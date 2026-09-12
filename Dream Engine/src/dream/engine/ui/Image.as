package dream.engine.ui
{
    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    import dream.engine.render.RenderEngine;
    import dream.engine.render.ResourceManager;
    import dream.engine.render.Texture2D;

    import flash.geom.Rectangle;

    /**
     * 图像组件（UI）：在画布屏幕空间绘制矩形/纹理/九宫格。
     *
     * 生命周期（继承 UIDrawable）：
     *   onLoad        创建可绘制对象（无纹理 → 默认白色 1×1；有纹理 → 异步加载）
     *   onEnable/onDisable  显示/隐藏（跟随 activeInHierarchy）
     *   onDestroy     移除并释放
     *
     * 尺寸与位置由 CanvasSystem 按 RectTransform/Layout 逐帧写入。
     * border > 0 时启用九宫格（Starling scale9Grid，内缩纹理像素，四边等宽）。
     */
    public final class Image extends UIDrawable
    {
        /** 纹理资源 GUID；null/空串 → 纯色矩形。 */
        public var textureGuid:String = null;

        /** 精灵资源 GUID；设置后按精灵解析（图集覆盖优先，未命中的回落源纹理）。 */
        public var spriteGuid:String = null;

        /** 颜色（tint；纯色矩形即填充色）。 */
        public var color:uint = 0xFFFFFF;

        /** 九宫格内缩（纹理像素），0=关闭。 */
        public var border:Number = 0;

        /** 帧矩形（纹理像素，图集/精灵表单帧）。宽或高 ≤ 0 → 使用整张纹理。
         *  仅在直接引用纹理（未设 spriteGuid）时生效。 */
        public var frameX:Number = 0;
        public var frameY:Number = 0;
        public var frameW:Number = 0;
        public var frameH:Number = 0;

        // 已加载纹理：帧矩形变更时复用，无需重新走资源加载。
        private var _texture:Texture2D;

        // 精灵解析得到的纹理内像素矩形（null = 覆盖整张纹理）。
        private var _spriteRect:Rectangle;

        public function Image(textureGuid:String = null)
        {
            this.textureGuid = textureGuid;
        }

        // ── 生命周期 ──

        override protected function onLoad():void
        {
            // 精灵引用优先：源纹理与帧矩形都由精灵解析得出。
            if (spriteGuid != null && spriteGuid.length > 0)
                loadSprite();
            else if (textureGuid != null && textureGuid.length > 0)
                loadTexture();
            else
                ensureDrawable();
        }

        /** 解析精灵：命中图集覆盖则用图集纹理，未命中回落精灵自身的源纹理。 */
        private function loadSprite():void
        {
            var rm:ResourceManager = ResourceManager.current;
            if (rm == null || RenderEngine.current == null)
            {
                ensureDrawable(); // 资源系统未就绪：回退纯色矩形
                return;
            }
            // 订阅解析结果：Studio 重新合图后图集覆盖变化，引擎热切换纹理而不必重载场景。
            rm.watchSprite(spriteGuid, onSpriteLoaded);
            rm.requestSpriteByGuid(spriteGuid, onSpriteLoaded);
        }

        /** 解除精灵订阅。清除引用或组件销毁时调用，否则 ResourceManager 会持有已失效的组件。 */
        private function unwatchSprite():void
        {
            var rm:ResourceManager = ResourceManager.current;
            if (rm == null || spriteGuid == null) return;
            rm.unwatchSprite(spriteGuid, onSpriteLoaded);
        }

        override protected function onDestroy():void
        {
            unwatchSprite();
            super.onDestroy();
        }

        /** 精灵解析完成：用得到的纹理与矩形创建可绘制对象。 */
        private function onSpriteLoaded(tex:Texture2D, rect:Rectangle):void
        {
            if (tex == null)
            {
                // 首次解析失败：回退纯色矩形。热切换失败时保留原可绘制对象。
                if (_texture == null) ensureDrawable();
                return;
            }
            _texture = tex;
            _spriteRect = rect;
            rebuildDrawable();
        }

        // ── 内部 ──

        private function ensureDrawable():void
        {
            var re:RenderEngine = RenderEngine.current;
            if (re == null) return;
            _drawable = re.createQuad(1, 1, color);
            _drawable.touchable = true;
        }

        private function loadTexture():void
        {
            var rm:ResourceManager = ResourceManager.current;
            if (rm == null || RenderEngine.current == null)
            {
                ensureDrawable(); // 资源系统未就绪：回退纯色矩形
                return;
            }
            rm.requestTextureByGuid(textureGuid, onTextureLoaded);
        }

        private function onTextureLoaded(tex:Texture2D):void
        {
            var re:RenderEngine = RenderEngine.current;
            if (tex == null || re == null)
            {
                ensureDrawable();
                return;
            }
            _texture = tex;
            rebuildDrawable();
        }

        /** 用当前纹理 + 帧矩形重建可绘制对象（尺寸由 CanvasSystem 逐帧写入）。 */
        private function rebuildDrawable():void
        {
            var re:RenderEngine = RenderEngine.current;
            if (_texture == null || re == null)
            {
                ensureDrawable();
                return;
            }
            swapDrawable();
            _drawable = re.createImage(_texture, frameRect());
            _drawable.touchable = true;
            _drawable.color = color; // 纹理同样支持 tint
            applyBorder();
            // _attached 保持 false：CanvasSystem 下一帧重新挂载新可绘制对象。
        }

        /** 帧矩形：精灵解析结果优先，否则用 frameX/Y/W/H；
         *  两者都没有返回 null → 整张纹理。 */
        private function frameRect():Rectangle
        {
            if (_spriteRect != null) return _spriteRect.clone();
            if (frameW <= 0 || frameH <= 0) return null;
            return new Rectangle(frameX, frameY, frameW, frameH);
        }

        private function applyBorder():void
        {
            if (_drawable == null) return;
            if (border > 0)
            {
                var b:Rectangle = _drawable.localBounds;
                var w:Number = Math.max(1, b.width - border * 2);
                var h:Number = Math.max(1, b.height - border * 2);
                _drawable.scale9Grid = new Rectangle(border, border, w, h);
            }
            else
            {
                _drawable.scale9Grid = null;
            }
        }

        // ── 公开 API ──

        /**
         * 切换纹理（null/空串 → 纯色矩形）。供脚本与 Button 的 sprite 过渡动态换图。
         * 异步加载，加载完成后自动换可绘制对象并重新挂载。
         * 直接引用整张纹理，会清除精灵引用（两者互斥，精灵优先）。
         */
        public function setTexture(guid:String):void
        {
            var effective:String = (guid == null) ? null : String(guid);
            effective = (effective == null || effective.length == 0) ? null : effective;
            if (effective == textureGuid && spriteGuid == null && _drawable != null) return;
            unwatchSprite(); // 先按旧精灵 GUID 解除订阅，再清除引用
            textureGuid = effective;
            spriteGuid = null;
            _spriteRect = null;
            _texture = null;
            swapDrawable();
            if (textureGuid != null) loadTexture();
            else ensureDrawable();
        }

        /**
         * 按纹理原生尺寸适配矩形（对标 Unity SetNativeSize）。
         * 尺寸（UI 单位）= 纹理像素 ÷ 参考 PPU（所属画布 CanvasScaler.referencePixelsPerUnit，默认 100）。
         * 锚点收缩为点（尺寸完全由偏移决定），并保持当前矩形中心不变。
         */
        public function setNativeSize():void
        {
            if (_drawable == null || owner == null) return;
            var rt:RectTransform = owner.getComponent(RectTransform) as RectTransform;
            if (rt == null) return;

            var ppu:Number = referencePixelsPerUnit;
            if (ppu <= 0) ppu = 100;
            var b:Rectangle = _drawable.localBounds;
            var w:Number = b.width / ppu;
            var h:Number = b.height / ppu;

            var cx:Number = (rt.offsetMinX + rt.offsetMaxX) * 0.5;
            var cy:Number = (rt.offsetMinY + rt.offsetMaxY) * 0.5;
            rt.anchorMaxX = rt.anchorMinX;
            rt.anchorMaxY = rt.anchorMinY;
            rt.offsetMinX = cx - w * 0.5;
            rt.offsetMaxX = cx + w * 0.5;
            rt.offsetMinY = cy - h * 0.5;
            rt.offsetMaxY = cy + h * 0.5;
        }

        // ── Inspector 反射 ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            var fields:Array = [
                sortingOrderFieldInfo(),
                new FieldInfo("spriteGuid", "Sprite", "resource", spriteGuid),
                new FieldInfo("textureGuid", "Texture (no sprite)", "resource", textureGuid),
                new FieldInfo("color", "Color", "color", color),
                new FieldInfo("border", "Border (9-slice)", "number", border),
                new FieldInfo("frameX", "Frame X", "number", frameX),
                new FieldInfo("frameY", "Frame Y", "number", frameY),
                new FieldInfo("frameW", "Frame W", "number", frameW),
                new FieldInfo("frameH", "Frame H", "number", frameH),
                // 按钮：按纹理原生尺寸适配矩形（参考 PPU 换算；值忽略）。
                new FieldInfo("setNativeSize", "Fit to Texture", "action", null),
            ];
            return fields;
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            if (applySortingOrderField(fieldName, value)) return;
            switch (fieldName)
            {
                case "spriteGuid":
                    var sid:String = (value == null) ? null : String(value);
                    sid = (sid == null || sid.length == 0) ? null : sid;
                    if (sid == spriteGuid && _drawable != null) break;
                    unwatchSprite(); // 先按旧精灵 GUID 解除订阅，再改引用
                    spriteGuid = sid;
                    _spriteRect = null;
                    _texture = null;
                    swapDrawable();
                    if (spriteGuid != null) loadSprite();
                    else if (textureGuid != null && textureGuid.length > 0) loadTexture();
                    else ensureDrawable();
                    break;
                case "textureGuid":
                    setTexture((value == null) ? null : String(value));
                    break;
                case "color":
                    if (value != null)
                    {
                        color = uint(value);
                        if (_drawable) _drawable.color = color;
                    }
                    break;
                case "border":
                    if (value != null)
                    {
                        border = Math.max(0, Number(value));
                        applyBorder();
                    }
                    break;
                case "frameX":
                case "frameY":
                case "frameW":
                case "frameH":
                    var n:Number = (value == null) ? 0 : Number(value);
                    if (fieldName == "frameX") frameX = n;
                    else if (fieldName == "frameY") frameY = n;
                    else if (fieldName == "frameW") frameW = n;
                    else frameH = n;
                    if (_texture != null) rebuildDrawable(); // 纹理已缓存：直接按新帧矩形重建
                    break;
                case "setNativeSize":
                    setNativeSize();
                    break;
            }
        }
    }
}
