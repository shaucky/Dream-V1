package dream.engine.render
{
    import flash.geom.Rectangle;

    /** 单个精灵定义：guid 为组件的引用目标，rect 为源纹理内的像素矩形。 */
    internal final class SpriteDef
    {
        public var guid:String;
        public var name:String;
        public var rect:Rectangle;
    }
}
