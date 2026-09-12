package dream.engine.physics2d
{
    import Box2D.Collision.Shapes.b2CircleShape;
    import Box2D.Collision.Shapes.b2Shape;
    import Box2D.Common.Math.b2Vec2;

    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    /**
     * 圆形碰撞器 2D：圆盘形状，支持本地偏移。
     */
    public final class CircleCollider2D extends Collider2D
    {
        /** 半径。 */
        public var radius:Number = 0.5;

        public function CircleCollider2D()
        {
        }

        /**
         * 创建以 offset 为中心、给定半径的圆形几何。
         * 半径取 x/y 缩放的几何平均（Box2D 圆形不支持椭圆，非均匀缩放取近似）；
         * offset 随缩放。
         */
        override protected function createShape(scaleX:Number, scaleY:Number):b2Shape
        {
            var s:b2CircleShape = new b2CircleShape(radius * Math.sqrt(scaleX * scaleY));
            s.SetLocalPosition(new b2Vec2(offsetX * scaleX, offsetY * scaleY));
            return s;
        }

        // ── Inspector 反射（集成基类字段） ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            return super.getInspectableFields().concat([
                new FieldInfo("radius", "Radius", "number", radius),
            ]);
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            if (fieldName == "radius")
            {
                radius = Number(value);
                _dirty = true;
                return;
            }
            // 基类字段统一由基类处理并标记 dirty。
            super.setFieldValue(fieldName, value);
        }
    }
}
