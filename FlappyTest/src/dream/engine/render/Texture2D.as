package dream.engine.render
{
    import flash.display.BitmapData;
    import flash.geom.Rectangle;

    import starling.textures.SubTexture;
    import starling.textures.Texture;

    /**
     * 引擎纹理：对 Starling Texture 的内联封装。
     * 用户只面对此类，不直接接触 starling.* 类型。
     *
     * RenderEngine.createTextureFromBitmapData 返回此类型，
     * RenderEngine.createImage 接受此类型创建带纹理的 DisplayNode。
     *
     * 命名说明：与 DisplayNode 同理，避免与 starling.textures.Texture 同名，
     * 以使用强类型 _impl。Texture2D 也是游戏引擎常见命名（如 Unity）。
     *
     * 未来若替换 Starling，只需改本类内部实现，对外 API 不变。
     */
    public class Texture2D
    {
        internal var _impl:Texture;

        // 源位图引用：用于精确拾取的像素 alpha 采样。
        // 若调用方随后 dispose 了该 BitmapData，采样时会容错跳过（不阻断命中）。
        internal var _sourceBitmapData:BitmapData;

        // 本纹理在 _sourceBitmapData 中的像素区域（子纹理时非零）。
        internal var _sourceRect:Rectangle;

        // 派生（子）纹理与其源纹理共享 GPU 纹理与采样位图，dispose 时不释放共享资源。
        internal var _derived:Boolean = false;

        /**
         * 由静态工厂 fromBitmapData 创建。参数为底层纹理——弱类型避免公共签名
         * 暴露 Starling 类型（引擎用户通过 RenderEngine 工厂/ResourceManager 获取）。
         */
        public function Texture2D(impl:*)
        {
            _impl = impl as Texture;
        }

        /** 纹理宽度（像素）。 */
        public function get width():Number { return _impl.width; }

        /** 纹理高度（像素）。 */
        public function get height():Number { return _impl.height; }

        /** 本纹理在其源位图中的像素区域（图集/精灵表单帧即为帧矩形）。 */
        public function get frame():Rectangle
        {
            if (_sourceRect != null) return _sourceRect.clone();
            return new Rectangle(0, 0, _impl.width, _impl.height);
        }

        /** 释放底层 GPU 资源（连同精确拾取的 alpha 采样位图）。
         *  派生纹理与源纹理共享资源，此处只释放自身引用。 */
        public function dispose():void
        {
            if (!_derived && _sourceBitmapData != null)
            {
                _sourceBitmapData.dispose();
                _sourceBitmapData = null;
            }
            _impl.dispose();
        }

        /** 从 BitmapData 创建引擎纹理。调用方负责 dispose 传入的 BitmapData。
         *  内部克隆一份作为精确拾取的像素 alpha 采样源——原 bmd 可能被调用方
         *  （如 ResourceManager 加载器）立即 dispose，克隆不受影响。 */
        public static function fromBitmapData(bmd:BitmapData):Texture2D
        {
            var t:Texture2D = new Texture2D(Texture.fromBitmapData(bmd));
            t._sourceBitmapData = bmd.clone();
            t._sourceRect = new Rectangle(0, 0, bmd.width, bmd.height);
            return t;
        }

        /**
         * 创建本纹理的子纹理（图集 / 精灵表的单帧）。
         *
         * region 为本纹理内的像素区域（整数化并裁剪到纹理范围，避免采样渗色）。
         * 子纹理共享底层 GPU 纹理与像素采样位图，创建开销极小；其 dispose()
         * 不会释放共享资源。
         */
        public function subTexture(region:Rectangle):Texture2D
        {
            var base:Rectangle = frame; // 本纹理在源位图中的绝对区域
            var x:Number = Math.max(0, Math.min(Math.floor(region.x), base.width - 1));
            var y:Number = Math.max(0, Math.min(Math.floor(region.y), base.height - 1));
            var w:Number = Math.max(1, Math.min(Math.floor(region.width), base.width - x));
            var h:Number = Math.max(1, Math.min(Math.floor(region.height), base.height - y));

            // 相对当前纹理的区域 → Starling 允许 SubTexture 嵌套。
            var impl:SubTexture = new SubTexture(_impl, new Rectangle(x, y, w, h), false);
            var sub:Texture2D = new Texture2D(impl);
            sub._sourceBitmapData = _sourceBitmapData;
            sub._sourceRect = new Rectangle(base.x + x, base.y + y, w, h);
            sub._derived = true;
            return sub;
        }
    }
}
