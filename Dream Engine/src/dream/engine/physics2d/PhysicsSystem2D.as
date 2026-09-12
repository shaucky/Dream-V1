package dream.engine.physics2d
{
    import Box2D.Collision.b2WorldManifold;
    import Box2D.Common.Math.b2Vec2;
    import Box2D.Dynamics.b2Body;
    import Box2D.Dynamics.b2BodyDef;
    import Box2D.Dynamics.b2Fixture;
    import Box2D.Dynamics.Contacts.b2Contact;

    import dream.engine.ecs.DreamComponent;
    import dream.engine.ecs.DreamSystem;
    import dream.engine.ecs.Element;
    import dream.engine.ecs.World;
    import dream.engine.render.Drawable;
    import dream.engine.render.DrawableContainer;
    import dream.engine.render.RenderEngine;
    import dream.engine.transform.Transform;

    import flash.geom.Matrix;
    import flash.utils.Dictionary;

    /**
     * 物理系统 2D：ECS 与物理引擎之间的桥（Unity Physics2D 语义）。
     *
     * 每帧职责：
     *   1. reconcileBodies：按元素组件组合创建/重建/销毁底层刚体与夹具
     *      （RigidBody2D + Collider2D；仅有 Collider2D → 隐式静态刚体）
     *   2. simulate：Transform→刚体传送 → 固定步长步进（固定时间步+累积器）
     *      → 刚体→Transform 写回（dynamic 支持 interpolation 渲染插值）
     *   3. dispatchEvents：轮询接触表，对比上帧生成 enter/stay/exit，
     *      以组件方法（onCollision*2D / onTrigger*2D）形式分发
     *   4. 编辑器模式（world.running=false）：不步进，仅显示碰撞器线框 gizmo
     *
     * 注册顺序：InputSystem 之后、RenderSystem 之前。
     */
    public final class PhysicsSystem2D extends DreamSystem
    {
        /** 单帧最多物理步数：卡顿时丢弃多余累积，防止物理爆炸。 */
        private static const MAX_STEPS_PER_FRAME:int = 5;

        /** 元素 → 底层刚体。 */
        private var _bodyMap:Dictionary = new Dictionary();

        /** 元素 → 创建刚体时的 Transform 缩放（检测缩放变化以重建碰撞尺寸）。 */
        private var _scaleMap:Dictionary = new Dictionary();

        /** 上一帧触碰对（key "元素Aid|元素Bid"）→ ContactRecord。 */
        private var _prevContacts:Object = {};

        /** 固定步长累积器。 */
        private var _accumulator:Number = 0;

        // 编辑器碰撞线框 gizmo（仅 Studio 构建）。
        CONFIG::STUDIO
        private var _gizmoRoot:DrawableContainer = null;

        public function PhysicsSystem2D()
        {
            Physics2D.ensureWorld();
        }

        override public function update(world:World, dt:Number):void
        {
            reconcileBodies(world);
            CONFIG::STUDIO
            {
                // 编辑器模式：不步进不同步，仅可视化碰撞器。
                if (!world.running)
                {
                    updateGizmo(world);
                    return;
                }
                hideGizmo();
            }
            simulate(world, dt);
            dispatchEvents(world);
        }

        // ── 1. 刚体创建/重建/销毁 ──

        private function reconcileBodies(world:World):void
        {
            // 销毁：元素已不存活、层级无效（自身/祖先禁用，Unity：非 activeInHierarchy 不参与物理），
            // 或已无任何 Collider2D。
            var stale:Array = [];
            for (var k:* in _bodyMap)
            {
                var el:Element = k as Element;
                if (el == null || !el.alive || !el.activeInHierarchy || el.getComponent(Collider2D) == null)
                    stale.push(el);
            }
            for each (var se:Element in stale) destroyBodyFor(se);

            // 创建/重建：字段变化（_dirty）时重建，保证 Inspector 编辑即时生效。
            var list:Vector.<Element> = world.query(Transform, Collider2D);
            for each (var e:Element in list)
            {
                if (!e.activeInHierarchy) continue; // 层级无效：不建刚体（重新启用后自动重建）
                var body:b2Body = _bodyMap[e] as b2Body;
                if (body != null && !isDirty(e)) continue;
                destroyBodyFor(e);
                createBodyFor(e);
            }
        }

        /** 元素是否存在需要重建的字段变化（碰撞器/刚体被编辑或 Transform 缩放变化）。 */
        private function isDirty(e:Element):Boolean
        {
            var comps:Vector.<DreamComponent> = e.getAllComponents();
            for each (var c:DreamComponent in comps)
            {
                var col:Collider2D = c as Collider2D;
                if (col != null && col._dirty) return true;
                var rb:RigidBody2D = c as RigidBody2D;
                if (rb != null && rb._dirty) return true;
            }
            // Transform.Scale 变化：碰撞体尺寸随缩放（Unity 语义），需重建。
            var t:Transform = e.getComponent(Transform) as Transform;
            if (t != null)
            {
                var rec:Object = _scaleMap[e];
                if (rec != null &&
                    (Math.abs(rec.sx - t.scaleX) > 0.0001 || Math.abs(rec.sy - t.scaleY) > 0.0001))
                    return true;
            }
            return false;
        }

        private function createBodyFor(e:Element):void
        {
            var t:Transform = e.getComponent(Transform) as Transform;
            var wx:Number = 0, wy:Number = 0, wrot:Number = 0;
            if (t != null)
            {
                if (t.parent == null) { wx = t.x; wy = t.y; wrot = t.rotation; }
                else { var m:Matrix = t.worldMatrix; wx = m.tx; wy = m.ty; wrot = Math.atan2(m.b, m.a); }
            }

            var rb:RigidBody2D = e.getComponent(RigidBody2D) as RigidBody2D;
            var type:uint = b2Body.b2_staticBody;
            if (rb != null && rb.enabled) type = RigidBody2D.toBodyType(rb.bodyType);

            var def:b2BodyDef = new b2BodyDef();
            def.type = type;
            def.position.Set(wx, wy);
            def.angle = wrot;
            if (rb != null && rb.enabled)
            {
                def.linearDamping = rb.linearDamping;
                def.angularDamping = rb.angularDamping;
                def.fixedRotation = rb.fixedRotation;
                def.allowSleep = rb.allowSleep;
                def.bullet = rb.bullet;
            }

            var body:b2Body = Physics2D.world.createBody(def);
            body.SetUserData(e);
            _bodyMap[e] = body;

            // 碰撞尺寸随 Transform.Scale（Unity 语义）：取正并夹最小，负缩放不镜像。
            var sx:Number = Math.max(0.001, Math.abs(t != null ? t.scaleX : 1));
            var sy:Number = Math.max(0.001, Math.abs(t != null ? t.scaleY : 1));

            // 夹具：元素的全部启用 Collider2D。
            var comps:Vector.<DreamComponent> = e.getAllComponents();
            for each (var c:DreamComponent in comps)
            {
                var col:Collider2D = c as Collider2D;
                if (col != null && col.enabled)
                {
                    col.attach(body, sx, sy);
                    col._dirty = false;
                }
            }

            if (rb != null)
            {
                rb.onBodyAttached(body, wx, wy, wrot);
                rb._dirty = false;
            }

            // 记录创建时的缩放，供下一帧检测变化。
            if (t != null) _scaleMap[e] = { sx: t.scaleX, sy: t.scaleY };
        }

        private function destroyBodyFor(e:Element):void
        {
            var body:b2Body = _bodyMap[e] as b2Body;
            if (body == null) return;

            var comps:Vector.<DreamComponent> = e.getAllComponents();
            for each (var c:DreamComponent in comps)
            {
                var col:Collider2D = c as Collider2D;
                if (col != null) col.detach();
            }
            var rb:RigidBody2D = e.getComponent(RigidBody2D) as RigidBody2D;
            if (rb != null) rb.onBodyDetached();

            Physics2D.world.destroyBody(body);
            delete _bodyMap[e];
            delete _scaleMap[e];
        }

        // ── 2. 模拟（固定步长 + 同步） ──

        private function simulate(world:World, dt:Number):void
        {
            var list:Vector.<Element> = world.query(Transform, Collider2D);

            // 2a. Transform → 刚体（外部编辑传送）
            for each (var e:Element in list)
            {
                var rb:RigidBody2D = e.getComponent(RigidBody2D) as RigidBody2D;
                if (rb != null && rb.enabled) rb.pushTransformToBody();
            }

            // 2b. 固定步长步进
            var fdt:Number = Physics2D.fixedDeltaTime;
            _accumulator += dt;
            var steps:int = 0;
            while (_accumulator >= fdt && steps < MAX_STEPS_PER_FRAME)
            {
                Physics2D.world.step(fdt);
                for each (var e2:Element in list)
                {
                    var rb2:RigidBody2D = e2.getComponent(RigidBody2D) as RigidBody2D;
                    if (rb2 != null && rb2.enabled) rb2.captureBodyState();
                }
                _accumulator -= fdt;
                steps++;
            }
            if (_accumulator >= fdt) _accumulator = 0; // 卡顿：丢弃剩余累积

            // 2c. 刚体 → Transform（含 interpolation 渲染插值）
            var alpha:Number = _accumulator / fdt;
            for each (var e3:Element in list)
            {
                var rb3:RigidBody2D = e3.getComponent(RigidBody2D) as RigidBody2D;
                if (rb3 != null && rb3.enabled) rb3.pullBodyToTransform(alpha);
            }
        }

        // ── 3. 碰撞/触发事件分发 ──

        private function dispatchEvents(world:World):void
        {
            var cur:Object = {};
            var c:b2Contact = Physics2D.world.getContacts();
            while (c != null)
            {
                if (c.IsTouching())
                {
                    var fa:b2Fixture = c.GetFixtureA();
                    var fb:b2Fixture = c.GetFixtureB();
                    var ca:Collider2D = fa != null ? fa.GetUserData() as Collider2D : null;
                    var cb:Collider2D = fb != null ? fb.GetUserData() as Collider2D : null;
                    if (ca != null && cb != null && ca.owner != null && cb.owner != null
                        && ca.owner.alive && cb.owner.alive)
                    {
                        var ea:Element = ca.owner;
                        var eb:Element = cb.owner;
                        var key:String = ea.id < eb.id ? ea.id + "|" + eb.id : eb.id + "|" + ea.id;
                        if (cur[key] == null)
                        {
                            // 同对多接触点仅记录一条；数据用 Object 承载（无独立顶层类，
                            // 规避 AS3 包内多顶层类的可见性限制）。
                            var rec:Object = { a: ca, b: cb };
                            rec.contacts = [];
                            rec.relativeSpeed = 0;
                            buildCollisionData(rec, c);
                            cur[key] = rec;
                        }
                    }
                }
                c = c.GetNext();
            }

            // enter / stay
            for (var k1:String in cur)
            {
                var r:Object = cur[k1];
                var prev:Object = _prevContacts[k1];
                fireEvents(r, (prev == null) ? "Enter" : "Stay");
            }
            // exit（用上帧记录；接触对象可能已从世界移除，数据仍有效）
            for (var k2:String in _prevContacts)
            {
                if (cur[k2] == undefined)
                    fireEvents(_prevContacts[k2] as Object, "Exit");
            }
            _prevContacts = cur;
        }

        private static function buildCollisionData(rec:Object, c:b2Contact):void
        {
            var wm:b2WorldManifold = new b2WorldManifold();
            c.GetWorldManifold(wm);
            rec.contacts = [];
            if (wm.m_points != null)
            {
                for each (var p:b2Vec2 in wm.m_points)
                    rec.contacts.push({ x: p.x, y: p.y });
            }
            var va:b2Vec2 = c.GetFixtureA().GetBody().GetLinearVelocity();
            var vb:b2Vec2 = c.GetFixtureB().GetBody().GetLinearVelocity();
            var dvx:Number = va.x - vb.x;
            var dvy:Number = va.y - vb.y;
            rec.relativeSpeed = Math.sqrt(dvx * dvx + dvy * dvy);
        }

        /**
         * 向接触双方的组件分发回调（组件方法形式，无事件注册）。
         * 传感器（任一方 isSensor）→ onTrigger*2D；否则 → onCollision*2D。
         */
        private static function fireEvents(rec:Object, phase:String):void
        {
            var prefix:String = (rec.a.isSensor || rec.b.isSensor) ? "onTrigger" : "onCollision";
            fireToElement(rec.a.owner, rec.b, prefix, phase);
            fireToElement(rec.b.owner, rec.a, prefix, phase);
        }

        private static function fireToElement(el:Element, other:Collider2D, prefix:String, phase:String):void
        {
            if (el == null || !el.alive || !el.activeInHierarchy) return; // 层级无效：不派发回调（Unity 语义）

            // 回调数据：对方视角。
            var collision:Collision2D = new Collision2D();
            collision.collider = other;
            collision.element = other.owner;
            collision.rigidBody = (other.owner != null)
                ? other.owner.getComponent(RigidBody2D) as RigidBody2D : null;

            // 遍历元素全部组件调用对应虚方法（基类空实现，子类覆写生效）。
            var comps:Vector.<DreamComponent> = el.getAllComponents();
            var isTrigger:Boolean = (prefix == "onTrigger");
            for each (var comp:DreamComponent in comps)
            {
                if (isTrigger)
                {
                    switch (phase)
                    {
                        case "Enter": comp.onTriggerEnter2D(collision); break;
                        case "Stay":  comp.onTriggerStay2D(collision); break;
                        case "Exit":  comp.onTriggerExit2D(collision); break;
                    }
                }
                else
                {
                    switch (phase)
                    {
                        case "Enter": comp.onCollisionEnter2D(collision); break;
                        case "Stay":  comp.onCollisionStay2D(collision); break;
                        case "Exit":  comp.onCollisionExit2D(collision); break;
                    }
                }
            }
        }

        // ── 4. 编辑器碰撞器线框 ──

        CONFIG::STUDIO
        private function hideGizmo():void
        {
            if (_gizmoRoot != null) _gizmoRoot.visible = false;
        }

        CONFIG::STUDIO
        private function updateGizmo(world:World):void
        {
            var re:RenderEngine = RenderEngine.current;
            if (re == null || re.root == null) return;

            // 每帧重建容器：编辑期元素少，简单可靠（旧容器由 GC 回收）。
            if (_gizmoRoot != null) _gizmoRoot.removeFromParent();
            _gizmoRoot = re.createContainer();
            _gizmoRoot.touchable = false;
            re.root.addChild(_gizmoRoot);
            _gizmoRoot.visible = true;

            var lineWidth:Number = 2 / Math.max(0.01, re.cameraZoom);

            var list:Vector.<Element> = world.query(Transform, Collider2D);
            for each (var e:Element in list)
            {
                if (!e.activeInHierarchy) continue; // 禁用元素不显示 gizmo（Unity 语义）
                var t:Transform = e.getComponent(Transform) as Transform;
                if (t == null) continue;
                var comps:Vector.<DreamComponent> = e.getAllComponents();
                for each (var c:DreamComponent in comps)
                {
                    var col:Collider2D = c as Collider2D;
                    if (col == null || !col.enabled) continue;
                    var d:Drawable = createGizmoShape(col, lineWidth, t.scaleX, t.scaleY);
                    if (d == null) continue;
                    _gizmoRoot.addChild(d);
                    d.transformationMatrix = gizmoMatrix(t, col);
                }
            }
        }

        CONFIG::STUDIO
        private function createGizmoShape(col:Collider2D, lineWidth:Number,
                                          scaleX:Number, scaleY:Number):Drawable
        {
            var color:uint = col.isSensor ? 0x4FA3FF : gizmoBodyColor(col);
            var c:DrawableContainer = RenderEngine.current.createContainer();
            c.touchable = false;
            var box:BoxCollider2D = col as BoxCollider2D;
            if (box != null) buildWireBox(c, box.sizeX * Math.abs(scaleX), box.sizeY * Math.abs(scaleY),
                                          color, lineWidth);
            var circle:CircleCollider2D = col as CircleCollider2D;
            if (circle != null) buildWireCircle(c, circle.radius * Math.sqrt(Math.abs(scaleX) * Math.abs(scaleY)),
                                                color, lineWidth);
            return c;
        }

        /** 用细长 Quad 拼矩形线框（本地坐标原点在形状中心）。 */
        CONFIG::STUDIO
        private static function buildWireBox(c:DrawableContainer, w:Number, h:Number,
                                            color:uint, t:Number):void
        {
            var x0:Number = -w * 0.5;
            var y0:Number = -h * 0.5;
            addBar(c, x0, y0, w, t, color);          // 顶
            addBar(c, x0, h * 0.5 - t, w, t, color); // 底
            addBar(c, x0, y0, t, h, color);          // 左
            addBar(c, w * 0.5 - t, y0, t, h, color); // 右
        }

        CONFIG::STUDIO
        private static function addBar(c:DrawableContainer, x:Number, y:Number,
                                       w:Number, h:Number, color:uint):void
        {
            var d:Drawable = RenderEngine.current.createQuad(w, h, color);
            d.x = x;
            d.y = y;
            c.addChild(d);
        }

        /** 用 AudioGizmo 同款方式绘制圆形线框：1×1 Quad 缩放为切线向线段，段中心落在圆周点。
         *  使用标量 setter（scaleX/scaleY/rotation），绕开 Starling width/height/transformationMatrix
         *  对旋转对象的分解重组畸变。 */
        CONFIG::STUDIO
        private static function buildWireCircle(c:DrawableContainer, r:Number,
                                                color:uint, t:Number):void
        {
            const SEG:int = 48;
            for (var i:int = 0; i < SEG; i++)
            {
                var a:Number = i * (Math.PI * 2 / SEG);
                var len:Number = Math.max(Math.PI * 2 * r / SEG, t * 1.5); // 弧长，小半径时保证段相连
                var theta:Number = a + Math.PI / 2; // 切线方向
                var cosT:Number = Math.cos(theta);
                var sinT:Number = Math.sin(theta);
                var d:Drawable = RenderEngine.current.createQuad(1, 1, color);
                d.scaleX = len;
                d.scaleY = t;
                d.rotation = theta;
                // rotation 绕左上角旋转，故左上角 = 段中心 - R(θ)·(len/2, t/2)。
                d.x = r * Math.cos(a) - (cosT * len * 0.5 - sinT * t * 0.5);
                d.y = r * Math.sin(a) - (sinT * len * 0.5 + cosT * t * 0.5);
                c.addChild(d);
            }
        }

        CONFIG::STUDIO
        private static function gizmoBodyColor(col:Collider2D):uint
        {
            if (col.owner == null) return 0x4AE06A;
            var rb:RigidBody2D = col.owner.getComponent(RigidBody2D) as RigidBody2D;
            if (rb == null || !rb.enabled) return 0x4AE06A; // 静态（绿色）
            switch (rb.bodyType)
            {
                case "kinematic": return 0x4FB7E0;           // 运动（青色）
                case "dynamic":   return 0xE0C060;           // 动态（黄色）
                default:          return 0x4AE06A;
            }
        }

        /** 元素世界矩阵（忽略缩放）+ collider offset 的旋转平移，作为 gizmo 形状矩阵。 */
        CONFIG::STUDIO
        private static function gizmoMatrix(t:Transform, col:Collider2D):Matrix
        {
            var wm:Matrix = t.worldMatrix;
            var lenA:Number = Math.sqrt(wm.a * wm.a + wm.b * wm.b);
            var cosR:Number = lenA > 0 ? wm.a / lenA : 1;
            var sinR:Number = lenA > 0 ? wm.b / lenA : 0;
            var ox:Number = col.offsetX * t.scaleX;
            var oy:Number = col.offsetY * t.scaleY;
            var m:Matrix = new Matrix();
            m.a = cosR; m.b = sinR;
            m.c = -sinR; m.d = cosR;
            m.tx = wm.tx + cosR * ox - sinR * oy;
            m.ty = wm.ty + sinR * ox + cosR * oy;
            return m;
        }
    }
}
