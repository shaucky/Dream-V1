package dream.engine.ui
{
    import dream.engine.render.RenderEngine;
    import dream.engine.transform.Transform;

    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    /**
     * 滚动条组件（UI）：滑块视觉，位置与长度跟随目标 ScrollView。
     *
     * 宿主元素的 RectTransform 矩形即「轨道」；本组件的可绘制对象即「滑块」，
     * 由 CanvasSystem 每帧按目标 ScrollView 的滚动比例改写滑块矩形（覆盖元素矩形）。
     *
     * 目标解析：
     *   - scrollViewName 指定祖先元素名（该祖先需带 ScrollView）；
     *   - scrollViewName 为空时取最近带 ScrollView 的祖先。
     *
     * 拖拽：CanvasSystem 检测到按在滚动条上时，调用 dragBy(像素增量) 换算为滚动量。
     * autoHide 为 true 且内容不超过视口时，滑块长度收缩为 0（不可见）。
     */
    public final class Scrollbar extends UIDrawable
    {
        /** 目标 ScrollView 所在祖先元素名；空 = 最近祖先。 */
        public var scrollViewName:String = "";

        /** 方向：vertical | horizontal。 */
        public var direction:String = "vertical";

        /** 滑块最小长度（轨道方向）。 */
        public var minThumbLength:Number = 20;

        /** 滑块颜色。 */
        public var color:uint = 0x9AA0A6;

        /** 内容不超过视口时自动隐藏（滑块长度收缩为 0）。 */
        public var autoHide:Boolean = true;

        /** 轨道矩形缓存（CanvasSystem 放置时写入；dragBy 换算用）。 */
        internal var _trackRect:Object = null;

        public function Scrollbar()
        {
        }

        override protected function onLoad():void
        {
            var re:RenderEngine = RenderEngine.current;
            if (re == null) return;
            _drawable = re.createQuad(1, 1, color);
            _drawable.touchable = true;
        }

        /** 沿 Transform 祖先链解析目标 ScrollView；无则 null。 */
        public function resolveScrollView():ScrollView
        {
            if (owner == null) return null;
            var t:Transform = owner.getComponent(Transform) as Transform;
            if (t != null) t = t.parent;
            var fallback:ScrollView = null;
            while (t != null && t.owner != null)
            {
                var sv:ScrollView = t.owner.getComponent(ScrollView) as ScrollView;
                if (sv != null)
                {
                    if (fallback == null) fallback = sv;
                    if (scrollViewName != null && scrollViewName.length > 0
                        && t.owner.name == scrollViewName)
                        return sv;
                }
                t = t.parent;
            }
            return fallback;
        }

        /** 依据轨道矩形与目标滚动状态计算滑块矩形 {x,y,w,h}（autoHide 时可能为 0 长）。 */
        public function thumbRect(trackRect:Object):Object
        {
            var x:Number = trackRect.x;
            var y:Number = trackRect.y;
            var w:Number = trackRect.w;
            var h:Number = trackRect.h;

            var sv:ScrollView = resolveScrollView();
            if (sv == null) return { x: x, y: y, w: w, h: h };

            if (direction == "horizontal")
            {
                if (sv.contentWidth <= 0 || sv.contentWidth <= sv.viewportWidth)
                    return autoHide ? { x: x, y: y, w: 0, h: h } : { x: x, y: y, w: w, h: h };
                var ratioW:Number = sv.viewportWidth / sv.contentWidth;
                var lenW:Number = Math.max(minThumbLength, w * ratioW);
                var tw:Number = (sv.maxScrollX > 0) ? (sv.scrollX / sv.maxScrollX) : 0;
                return { x: x + tw * (w - lenW), y: y, w: lenW, h: h };
            }

            if (sv.contentHeight <= 0 || sv.contentHeight <= sv.viewportHeight)
                return autoHide ? { x: x, y: y, w: w, h: 0 } : { x: x, y: y, w: w, h: h };
            var ratioH:Number = sv.viewportHeight / sv.contentHeight;
            var lenH:Number = Math.max(minThumbLength, h * ratioH);
            var th:Number = (sv.maxScrollY > 0) ? (sv.scrollY / sv.maxScrollY) : 0;
            return { x: x, y: y + th * (h - lenH), w: w, h: lenH };
        }

        /** 拖动滑块 delta 像素 → 换算为对应滚动量并写入目标 ScrollView。 */
        public function dragBy(delta:Number):void
        {
            var sv:ScrollView = resolveScrollView();
            if (sv == null || _trackRect == null) return;

            if (direction == "horizontal")
            {
                var ratioW:Number = (sv.contentWidth > 0) ? (sv.viewportWidth / sv.contentWidth) : 1;
                var thumbW:Number = Math.max(minThumbLength, _trackRect.w * ratioW);
                var spanW:Number = _trackRect.w - thumbW;
                if (spanW <= 0) return;
                sv.scrollBy(delta / spanW * sv.maxScrollX, 0);
            }
            else
            {
                var ratioH:Number = (sv.contentHeight > 0) ? (sv.viewportHeight / sv.contentHeight) : 1;
                var thumbH:Number = Math.max(minThumbLength, _trackRect.h * ratioH);
                var spanH:Number = _trackRect.h - thumbH;
                if (spanH <= 0) return;
                sv.scrollBy(0, delta / spanH * sv.maxScrollY);
            }
        }

        // ── Inspector 反射 ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            var dirFi:FieldInfo = new FieldInfo("direction", "Direction", "string", direction);
            dirFi.options = ["vertical", "horizontal"];
            return [
                sortingOrderFieldInfo(),
                new FieldInfo("scrollViewName", "Target ScrollView", "string", scrollViewName),
                dirFi,
                new FieldInfo("minThumbLength", "Min Thumb Length", "number", minThumbLength),
                new FieldInfo("color", "Color", "color", color),
                new FieldInfo("autoHide", "Auto Hide", "boolean", autoHide)
            ];
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            if (applySortingOrderField(fieldName, value)) return;
            switch (fieldName)
            {
                case "scrollViewName":
                    scrollViewName = (value == null) ? "" : String(value);
                    break;
                case "direction":
                    if (value != null) direction = String(value);
                    break;
                case "minThumbLength":
                    minThumbLength = Math.max(0, Number(value));
                    break;
                case "color":
                    if (value != null)
                    {
                        color = uint(value);
                        if (_drawable != null) _drawable.color = color;
                    }
                    break;
                case "autoHide":
                    autoHide = Boolean(value);
                    break;
            }
        }
    }
}
