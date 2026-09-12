package dream.engine.ui
{
    import dream.engine.ecs.DreamComponent;
    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    /**
     * 按钮组件（UI）：通过 IPointerHandler 接收点击，按 transition 风格呈现按下反馈，
     * 抬起还原，点击触发 onClick。
     *
     * 用法：同一元素上搭配 Image（视觉）与 Button；onClick 由脚本赋值，不参与序列化
     * （加载后重新赋值，同 Unity 委托语义）。组件 enabled 关闭后不接收指针事件
     * （CanvasSystem 分发时按 enabled 过滤）。
     *
     * transition 过渡风格（单选，对标 Unity Button.Transition）：
     *   none   无反馈
     *   color  按下时 Image 颜色替换为 pressedTint，抬起还原
     *   scale  按下时 Image 围绕中心缩放到 pressScale，抬起还原
     *          （经 UIDrawable.interactionScale，由 CanvasSystem 每帧应用）
     *   sprite 按下时 Image 切换为 pressedTextureGuid 贴图，抬起切回原贴图
     */
    public final class Button extends DreamComponent implements IPointerHandler
    {
        /** 点击回调（脚本赋值），参数为点击屏幕坐标。 */
        public var onClick:Function = null;

        /** 过渡风格：none | color | scale | sprite。 */
        public var transition:String = "color";

        /** color 过渡：按下时对 Image 施加的色调。 */
        public var pressedTint:uint = 0x9AA0A6;

        /** scale 过渡：按下时 Image 的缩放比例（0~1 缩小）。 */
        public var pressScale:Number = 0.92;

        /** sprite 过渡：按下时切换的纹理 GUID（null → 纯色矩形）。 */
        public var pressedTextureGuid:String = null;

        private var _pressed:Boolean = false;
        /** sprite 过渡：按下前记录的原贴图 GUID（setTexture 会改写 textureGuid，需另存）。 */
        private var _originalTextureGuid:String = null;

        public function Button()
        {
        }

        override protected function onDisable():void
        {
            // 组件被禁用：还原按下视觉。
            _pressed = false;
            applyPress(false);
        }

        public function onPointerDown(x:Number, y:Number):void
        {
            _pressed = true;
            applyPress(true);
        }

        public function onPointerUp(x:Number, y:Number):void
        {
            _pressed = false;
            applyPress(false);
        }

        public function onPointerClick(x:Number, y:Number):void
        {
            if (onClick != null) onClick(x, y);
        }

        /** 按 transition 风格施加/还原按下反馈。 */
        private function applyPress(pressed:Boolean):void
        {
            var img:Image = (owner != null) ? owner.getComponent(Image) as Image : null;
            if (img == null) return;

            switch (transition)
            {
                case "scale":
                    img.interactionScale = pressed ? pressScale : 1;
                    break;
                case "sprite":
                    if (pressed)
                    {
                        // 首次按下：记录原贴图，之后换到按下贴图。
                        if (_originalTextureGuid == null) _originalTextureGuid = img.textureGuid;
                        img.setTexture(pressedTextureGuid);
                    }
                    else
                    {
                        img.setTexture(_originalTextureGuid);
                        _originalTextureGuid = null;
                    }
                    break;
                case "color":
                    if (img.drawable != null)
                        img.drawable.color = pressed ? pressedTint : img.color;
                    break;
                // "none" 及其他：无反馈。
            }
        }

        // ── Inspector 反射 ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            var transFi:FieldInfo = new FieldInfo("transition", "Transition", "string", transition);
            transFi.options = ["none", "color", "scale", "sprite"];
            return [
                transFi,
                new FieldInfo("pressedTint", "Pressed Tint", "color", pressedTint),
                new FieldInfo("pressScale", "Press Scale", "number", pressScale),
                new FieldInfo("pressedTextureGuid", "Pressed Sprite", "resource", pressedTextureGuid),
            ];
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            switch (fieldName)
            {
                case "transition":
                    if (value != null) transition = String(value);
                    break;
                case "pressedTint":
                    if (value != null)
                    {
                        pressedTint = uint(value);
                        if (_pressed && transition == "color") applyPress(true); // 按住中改动：立即生效
                    }
                    break;
                case "pressScale":
                    pressScale = Math.max(0.01, Math.min(1, Number(value)));
                    break;
                case "pressedTextureGuid":
                    // null/空串/"null" 字符串 → null（纯色矩形）。String(null) 兜底。
                    var g:String = (value == null) ? null : String(value);
                    g = (g == null || g.length == 0 || g == "null") ? null : g;
                    pressedTextureGuid = g;
                    break;
            }
        }
    }
}
