package dream.engine.physics2d
{
    import Box2D.Common.Math.b2Vec2;
    import Box2D.Dynamics.b2Body;
    import Box2D.Dynamics.b2BodyDef;
    import Box2D.Dynamics.b2Fixture;
    import Box2D.Dynamics.b2World;
    import Box2D.Dynamics.Contacts.b2Contact;

    import dream.engine.ecs.Element;

    /**
     * 物理世界 2D：对第三方物理库（Box2D）的单例封装（包内类）。
     *
     * 职责：
     *   - 持有 b2World，管理刚体创建/销毁（internal 接口，包外不可见）
     *   - 固定步进：Step 前对 dynamic 刚体手动施加"重力 × 质量 × gravityScale"
     *     （b2World 内置重力无法按组件缩放，故关闭内置重力，自行施加）
     *   - 接触表访问与射线检测（供 PhysicsSystem2D / Physics2D 门面使用）
     *
     * 引擎用户不可见此类：所有第三方类型被隔离在本文件内。
     */
    internal final class PhysicsWorld2D
    {
        private var _world:b2World;
        private var _gravityX:Number = 0;
        private var _gravityY:Number = 10;

        public function PhysicsWorld2D()
        {
            // 内置重力置零：gravityScale 需要按刚体缩放重力，统一由 step 手动施加。
            _world = new b2World(new b2Vec2(0, 0), true);
        }

        // ── 重力 ──

        internal function get gravityX():Number { return _gravityX; }
        internal function get gravityY():Number { return _gravityY; }

        internal function setGravity(x:Number, y:Number):void
        {
            _gravityX = x;
            _gravityY = y;
        }

        // ── 刚体创建/销毁（PhysicsSystem2D 专用） ──

        internal function createBody(def:b2BodyDef):b2Body
        {
            return _world.CreateBody(def);
        }

        internal function destroyBody(b:b2Body):void
        {
            if (b != null) _world.DestroyBody(b);
        }

        // ── 步进 ──

        /**
         * 步进物理模拟一个固定时步。
         * 先对全部 dynamic 刚体施加缩放后的重力（kinematic/static 不受力），
         * Step 后 ClearForces 清掉残余力——否则力会逐帧累积，刚体加速失控
         * （越落越快，最终高速穿透碰撞体）。
         *
         * 力生命周期（每物理帧）：
         *   onEnterFrame 用户 applyForce → 本方法加重力 → Step 积分 → ClearForces。
         *   用户持续力需每帧调用 applyForce（与 Unity AddForce 语义一致）。
         */
        internal function step(dt:Number):void
        {
            if (dt <= 0) return;

            // 1. 施加本帧自定义重力（追加到用户力之后）。
            if (_gravityX != 0 || _gravityY != 0)
            {
                var b:b2Body = _world.GetBodyList();
                while (b != null)
                {
                    if (b.GetType() == b2Body.b2_dynamicBody && b.IsAwake())
                    {
                        var scale:Number = 1;
                        var el:Element = b.GetUserData() as Element;
                        if (el != null)
                        {
                            var rb:RigidBody2D = el.getComponent(RigidBody2D) as RigidBody2D;
                            if (rb != null) scale = rb.gravityScale;
                        }
                        if (scale != 0)
                        {
                            var mass:Number = b.GetMass();
                            b.ApplyForce(new b2Vec2(_gravityX * mass * scale, _gravityY * mass * scale),
                                         b.GetWorldCenter());
                        }
                    }
                    b = b.GetNext();
                }
            }

            // 2. 步进求解（积分本帧全部力）。
            _world.Step(dt, Physics2D.velocityIterations, Physics2D.positionIterations);

            // 3. 清除残余力，防止累积（下帧用户力/重力重新施加）。
            _world.ClearForces();
        }

        // ── 接触表（PhysicsSystem2D 碰撞事件轮询） ──

        /** 当前世界接触链表头；遍历用 b2Contact.GetNext()。仅含触碰中的接触。 */
        internal function getContacts():b2Contact
        {
            return _world.GetContactList();
        }

        // ── 射线检测 ──

        /** 返回从 (x1,y1) 到 (x2,y2) 的最近命中；未命中返回 null。 */
        internal function raycastClosest(x1:Number, y1:Number, x2:Number, y2:Number):RaycastHit2D
        {
            var result:RaycastHit2D = null;
            var bestFrac:Number = 1;
            var p1:b2Vec2 = new b2Vec2(x1, y1);
            var p2:b2Vec2 = new b2Vec2(x2, y2);
            var dx:Number = x2 - x1;
            var dy:Number = y2 - y1;
            var len:Number = Math.sqrt(dx * dx + dy * dy);

            _world.RayCast(
                function(fixture:b2Fixture, point:b2Vec2, normal:b2Vec2, fraction:Number):Number
                {
                    var collider:Collider2D = fixture.GetUserData() as Collider2D;
                    if (collider == null) return fraction; // 非物理 fixture，跳过
                    if (fraction < bestFrac)
                    {
                        bestFrac = fraction;
                        var hit:RaycastHit2D = new RaycastHit2D();
                        hit.point = { x: point.x, y: point.y };
                        hit.normal = { x: normal.x, y: normal.y };
                        hit.distance = fraction * len;
                        hit.collider = collider;
                        var el:Element = collider.owner;
                        hit.element = el;
                        if (el != null)
                            hit.rigidBody = el.getComponent(RigidBody2D) as RigidBody2D;
                        result = hit;
                    }
                    return fraction; // 截断后续探测到当前距离，最终得到最近命中
                },
                p1, p2);

            return result;
        }
    }
}
