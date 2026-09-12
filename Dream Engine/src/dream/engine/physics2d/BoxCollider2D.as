package dream.engine.physics2d
{
    import Box2D.Collision.Shapes.b2PolygonShape;
    import Box2D.Collision.Shapes.b2Shape;
    import Box2D.Common.Math.b2Vec2;

    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    /**
     * 矩形碰撞器 2D：轴对齐盒（AABB），支持本地偏移。
     * 尺寸字段为全宽全高（对齐 Unity BoxCollider2D.size）；形状半宽 = size/2。
     */
    public final class BoxCollider2D extends Collider2D
    {
        /** 全宽。 */
        public var sizeX:Number = 1;

        /** 全高。 */
        public var sizeY:Number = 1;

        public function BoxCollider2D()
        {
        }

        /**
         * 创建以 offset 为中心、半宽 size/2 的矩形几何。
         * 尺寸与偏移随宿主 Transform 缩放（负缩放取绝对值：物理形状不镜像）。
         */
        override protected function createShape(scaleX:Number, scaleY:Number):b2Shape
        {
            var s:b2PolygonShape = new b2PolygonShape();
            s.SetAsOrientedBox(sizeX * 0.5 * scaleX, sizeY * 0.5 * scaleY,
                               new b2Vec2(offsetX * scaleX, offsetY * scaleY), 0);
            return s;
        }

        // ── Inspector 反射（集成基类字段） ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            return super.getInspectableFields().concat([
                new FieldInfo("size", "Size", "vector2", {x: sizeX, y: sizeY}),
            ]);
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            if (fieldName == "size")
            {
                sizeX = Number(value.x);
                sizeY = Number(value.y);
                _dirty = true;
                return;
            }
            // 基类字段（offset/isSensor/材质/tag）统一由基类处理并标记 dirty。
            super.setFieldValue(fieldName, value);
        }
    }
}
