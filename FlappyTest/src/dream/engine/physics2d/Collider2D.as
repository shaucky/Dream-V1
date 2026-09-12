package dream.engine.physics2d
{
    import Box2D.Collision.Shapes.b2Shape;
    import Box2D.Dynamics.b2Body;
    import Box2D.Dynamics.b2Fixture;
    import Box2D.Dynamics.b2FixtureDef;

    import dream.engine.ecs.DreamComponent;
    import dream.engine.render.DisplayComponent;

    import flash.geom.Rectangle;

    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    /**
     * 碰撞器 2D 基类：定义碰撞形状的材质与触发参数。
     *
     * 与 RigidBody2D 分离（Unity/Cocos 风格）：
     *   - 只有 Collider2D、无 RigidBody2D → 隐式静态碰撞体（地面/墙）
     *   - RigidBody2D + Collider2D → 按 bodyType 参与动力学
     *
     * 子类（BoxCollider2D / CircleCollider2D）实现 createShape() 返回几何，
     * 并集成基类反射：getInspectableFields() 用 super.concat 合并基类字段
     * （参考 SpriteRenderer 继承 DisplayComponent 的模式）。
     *
     * 字段变更（Inspector 编辑 / 脚本修改后）自动标记 _dirty，
     * PhysicsSystem2D 在下一帧据此重建刚体与夹具。
     */
    public class Collider2D extends DreamComponent
    {
        /** 形状中心相对宿主元素本地原点的偏移。 */
        public var offsetX:Number = 0;
        public var offsetY:Number = 0;

        /** 是否作为触发器（传感器）：不产生碰撞响应，只触发 onTrigger* 回调。 */
        public var isSensor:Boolean = false;

        /** 材质参数（Cocos 风格直接放在碰撞器上，不单独建物理材质资源）。 */
        public var density:Number = 1;
        public var friction:Number = 0.5;
        public var restitution:Number = 0;

        /** 碰撞标签：脚本在碰撞回调中区分碰撞对象用。 */
        public var tag:String = "";

        // ── 内部状态（PhysicsSystem2D 维护） ──

        /** 字段/启用状态变化标记；系统帧内据此重建。 */
        internal var _dirty:Boolean = true;

        /** 当前挂载的底层夹具（body 重建时自动替换）。 */
        internal var _fixture:b2Fixture = null;

        public function Collider2D()
        {
        }

        // ── 生命周期 ──

        override protected function onEnable():void
        {
            _dirty = true; // 启用恢复 → 系统重建夹具
        }

        override protected function onDisable():void
        {
            _dirty = true; // 停用 → 系统移除夹具
        }

        // ── 包内 API（PhysicsSystem2D 调用） ──

        /**
         * 子类实现：创建 body 本地坐标下的形状几何（含 offset）。
         * @param scaleX/scaleY 宿主元素 Transform 的缩放（已取正，>=0.001），
         *        物理形状尺寸与偏移随缩放（Unity 语义：Transform.Scale 影响碰撞体尺寸）。
         */
        protected function createShape(scaleX:Number, scaleY:Number):b2Shape
        {
            return null;
        }

        /** 把本碰撞器作为夹具挂到指定刚体上。 */
        internal function attach(body:b2Body, scaleX:Number, scaleY:Number):void
        {
            var shape:b2Shape = createShape(scaleX, scaleY);
            if (shape == null) return; // 基类无几何（误添加时无害）

            var fd:b2FixtureDef = new b2FixtureDef();
            fd.shape = shape;
            fd.isSensor = isSensor;
            // 传感器质量置零（Box2D 惯例），避免"触发器产生质量"。
            fd.density = isSensor ? 0 : density;
            fd.friction = friction;
            fd.restitution = restitution;
            _fixture = body.CreateFixture(fd);
            _fixture.SetUserData(this);
        }

        /** 断开夹具引用（body 重建前由系统调用）。 */
        internal function detach():void
        {
            _fixture = null;
        }

        /** 当前是否已挂载夹具（供系统判断 enabled 一致性）。 */
        internal function get attached():Boolean { return _fixture != null; }

        // ── 包围盒适配 ──

        /**
         * 把碰撞体尺寸/偏移适配到宿主元素的显示包围盒（DisplayComponent 本地几何）。
         * - BoxCollider2D：size = 包围盒宽高
         * - CircleCollider2D：radius = 内接圆半径（min(宽,高)/2）
         * - offset = 包围盒中心 − 锚点（pivot）：元素原点在 drawable 本地坐标的
         *   pivot 处（默认自动居中 = 几何中心，此时 offset 为 0），使碰撞体与可视对象重合。
         * 元素无显示组件或未就绪时静默返回。
         *
         * 由 Inspector 的 "Fit to Bounds" 按钮触发；添加碰撞器组件时自动调用。
         * 适配后置 _dirty，物理系统下一帧重建夹具。
         */
        public function fitToBounds():void
        {
            var dc:DisplayComponent = owner != null
                ? owner.getComponent(DisplayComponent) as DisplayComponent : null;
            var b:Rectangle = (dc != null && dc.displayObject != null)
                ? dc.displayObject.localBounds : null;
            if (b == null || b.width <= 0 || b.height <= 0) return;

            offsetX = b.x + b.width * 0.5 - dc.effectivePivotX;
            offsetY = b.y + b.height * 0.5 - dc.effectivePivotY;

            var box:BoxCollider2D = this as BoxCollider2D;
            if (box != null)
            {
                box.sizeX = b.width;
                box.sizeY = b.height;
            }
            var circle:CircleCollider2D = this as CircleCollider2D;
            if (circle != null)
            {
                // 内接圆：可整体滚入盒内，滚动贴合直觉。
                circle.radius = Math.min(b.width, b.height) * 0.5;
            }
            _dirty = true;
        }

        // ── Inspector 反射（子类用 super.concat 集成） ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            return [
                new FieldInfo("offset", "Offset", "vector2", {x: offsetX, y: offsetY}),
                new FieldInfo("isSensor", "Is Sensor", "boolean", isSensor),
                new FieldInfo("density", "Density", "number", density),
                new FieldInfo("friction", "Friction", "number", friction),
                new FieldInfo("restitution", "Restitution", "number", restitution),
                new FieldInfo("tag", "Tag", "string", tag),
                // 按钮：适配到宿主元素显示包围盒（值忽略）。
                new FieldInfo("fitToBounds", "Fit to Bounds", "action", null),
            ];
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            switch (fieldName)
            {
                case "offset":
                    offsetX = Number(value.x);
                    offsetY = Number(value.y);
                    break;
                case "isSensor":
                    isSensor = Boolean(value);
                    break;
                case "density":
                    density = Number(value);
                    break;
                case "friction":
                    friction = Number(value);
                    break;
                case "restitution":
                    restitution = Number(value);
                    break;
                case "tag":
                    tag = String(value);
                    break;
                case "fitToBounds":
                    fitToBounds();
                    return; // 内部已置 _dirty，不重复
                default:
                    return; // 未知字段交给子类处理
            }
            _dirty = true;
        }
    }
}
