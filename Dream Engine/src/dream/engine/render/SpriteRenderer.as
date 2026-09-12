package dream.engine.render
{
    import dream.engine.ecs.DreamComponent;
    import dream.engine.ecs.Element;
    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    import dream.engine.render.RenderEngine;
    import dream.engine.render.ResourceManager;
    import dream.engine.render.Texture2D;

    import flash.geom.Rectangle;

    /**
     * 精灵渲染组件：DisplayComponent 的贴图版本。
     *
     * 设置 textureGuid 后异步加载图片并创建 Image Drawable。
     * 未设置 textureGuid 时回退为纯色 Quad（与 DisplayComponent 行为一致）。
     *
     * 两种贴图引用方式（spriteGuid 优先）：
     *   - spriteGuid：引用精灵资源——常规用法。精灵自带源纹理与像素矩形；
     *     若该精灵被图集打包，解析时透明改用图集纹理，场景数据不变。
     *   - textureGuid（可配 frameX/Y/W/H）：直接引用整张纹理，帧矩形需手填，
     *     供调试/特殊场景使用；宽或高 ≤ 0 时使用整张纹理。
     *
     * 可通过 Inspector "Add Component" 无参创建：
     *   构造时不传参数，onLoad 时无 textureGuid 则创建默认 Quad。
     *
     * 也可由 Project 面板拖拽图片到 Hierarchy 自动创建：
     *   构造时传入 textureGuid，onLoad 时异步加载纹理。
     *
     * 纹理异步加载完成后，displayObject 被设置为 Image，
     * RenderSystem 下一帧自动 attachTo 到根容器。
     */
    public class SpriteRenderer extends DisplayComponent
    {
        private var _textureGuid:String;

        // 已加载纹理：帧矩形变更时复用，无需重新走资源加载。
        private var _texture:Texture2D;

        // 精灵资源 GUID；设置后按精灵解析（图集覆盖优先，未命中的回落源纹理）。
        private var _spriteGuid:String;
        // 精灵解析得到的纹理内像素矩形（null = 覆盖整张纹理）。
        private var _spriteRect:Rectangle;

        /** 帧矩形（纹理像素）。宽或高 ≤ 0 → 使用整张纹理。
         *  仅在直接引用纹理（未设 spriteGuid）时生效。 */
        public var frameX:Number = 0;
        public var frameY:Number = 0;
        public var frameW:Number = 0;
        public var frameH:Number = 0;

        /**
         * @param textureGuid 资源 GUID。设置后 onLoad 时异步解析为纹理 Image。
         *                    缺省时回退为纯色 Quad。
         */
        public function SpriteRenderer(textureGuid:String = null)
        {
            _textureGuid = textureGuid;
            // displayObject 在 onLoad 时创建（与 DisplayComponent 一致）。
        }

        override protected function onLoad():void
        {
            // 精灵引用优先：源纹理与帧矩形都由精灵解析得出。
            if (_spriteGuid != null && _spriteGuid.length > 0)
                loadSprite();
            else if (_textureGuid != null && _textureGuid.length > 0)
                loadTexture();
            else
                super.onLoad(); // 无贴图：创建默认 Quad
        }

        /** 解析精灵：命中图集覆盖则用图集纹理，未命中回落精灵自身的源纹理。 */
        private function loadSprite():void
        {
            var rm:ResourceManager = ResourceManager.current;
            if (rm == null || RenderEngine.current == null)
            {
                super.onLoad(); // 回退为 Quad
                return;
            }
            // 订阅解析结果：Studio 重新合图后图集覆盖变化，引擎热切换纹理而不必重载场景。
            rm.watchSprite(_spriteGuid, onSpriteLoaded);
            rm.requestSpriteByGuid(_spriteGuid, onSpriteLoaded);
        }

        /** 解除精灵订阅。清除引用或组件销毁时调用，否则 ResourceManager 会持有已失效的组件。 */
        private function unwatchSprite():void
        {
            var rm:ResourceManager = ResourceManager.current;
            if (rm == null || _spriteGuid == null) return;
            rm.unwatchSprite(_spriteGuid, onSpriteLoaded);
        }

        override protected function onDestroy():void
        {
            unwatchSprite();
            super.onDestroy();
        }

        /** 精灵解析完成：用得到的纹理与矩形创建显示对象。 */
        private function onSpriteLoaded(tex:Texture2D, rect:Rectangle):void
        {
            if (tex == null)
            {
                // 首次解析失败：回退为 Quad。热切换失败时保留原显示对象，避免把精灵降级成白块。
                if (_texture == null) super.onLoad();
                return;
            }
            _texture = tex;
            _spriteRect = rect;
            rebuildDisplayObject();
        }

        /** 通过 ResourceManager 异步加载纹理。 */
        private function loadTexture():void
        {
            var rm:ResourceManager = ResourceManager.current;
            if (rm == null || RenderEngine.current == null)
            {
                super.onLoad(); // 回退为 Quad
                return;
            }
            rm.requestTextureByGuid(_textureGuid, onTextureLoaded);
        }

        /** 纹理加载完成回调：创建 Image Drawable。 */
        private function onTextureLoaded(tex:Texture2D):void
        {
            if (tex == null)
            {
                // 加载失败：回退为 Quad
                super.onLoad();
                return;
            }
            _texture = tex;
            rebuildDisplayObject();
        }

        /** 用当前纹理 + 帧矩形（重新）创建显示对象。 */
        private function rebuildDisplayObject():void
        {
            var re:RenderEngine = RenderEngine.current;
            if (_texture == null || re == null) return;
            // 清理旧显示对象（若纹理更换前已挂载），复位挂载状态，下一帧 attachTo 新 Image。
            swapDisplayObject();
            displayObject = re.createImage(_texture, frameRect());
            // 应用组件级外观（颜色/混合模式），避免替换显示对象后丢失。
            applyAppearance();
            // _attached 保持 false，RenderSystem 下一帧自动 attachTo。
        }

        /** 帧矩形：精灵解析结果优先，否则用 frameX/Y/W/H；
         *  两者都没有返回 null → 整张纹理。 */
        private function frameRect():Rectangle
        {
            if (_spriteRect != null) return _spriteRect.clone();
            if (frameW <= 0 || frameH <= 0) return null;
            return new Rectangle(frameX, frameY, frameW, frameH);
        }

        /** 清除当前 displayObject 并重置挂载状态，用于切换纹理。 */
        private function swapDisplayObject():void
        {
            if (displayObject != null)
            {
                displayObject.removeFromParent();
                displayObject = null;
            }
            _attached = false;
            _root = null;
        }

        // ── Inspector 反射 ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            // 几何尺寸不序列化（由 Transform Scale 控制），直接透传基类字段。
            return super.getInspectableFields().concat([
                new FieldInfo("spriteGuid", "Sprite", "resource", _spriteGuid),
                new FieldInfo("textureGuid", "Texture (no sprite)", "resource", _textureGuid),
                new FieldInfo("frameX", "Frame X", "number", frameX),
                new FieldInfo("frameY", "Frame Y", "number", frameY),
                new FieldInfo("frameW", "Frame W", "number", frameW),
                new FieldInfo("frameH", "Frame H", "number", frameH),
            ]);
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            if (fieldName == "spriteGuid")
            {
                // 空串 → 清除精灵引用，回退到整图纹理或纯色 Quad。
                var sid:String = (value == null) ? null : String(value);
                unwatchSprite(); // 先按旧 GUID 解除订阅，再改引用
                _spriteGuid = (sid == null || sid.length == 0) ? null : sid;
                _spriteRect = null;
                _texture = null;
                swapDisplayObject();
                if (_spriteGuid != null)
                    loadSprite();
                else if (_textureGuid != null && _textureGuid.length > 0)
                    loadTexture();
                else
                    super.onLoad(); // 无贴图：回退为 Quad
            }
            else if (fieldName == "textureGuid")
            {
                // null/空串 → 无纹理：清空 GUID 并回退默认 Quad。
                // 注意 String(null) 返回 "null"，必须显式判空，否则会误用 "null" GUID 发起加载。
                var effective:String = (value == null) ? null : String(value);
                _textureGuid = (effective == null || effective.length == 0) ? null : effective;
                // 清除旧 displayObject 与旧纹理，重新加载
                _texture = null;
                swapDisplayObject();
                if (_textureGuid != null && _textureGuid.length > 0)
                    loadTexture();
                else
                    super.onLoad(); // 无贴图：回退为 Quad
            }
            else if (fieldName == "frameX" || fieldName == "frameY" ||
                     fieldName == "frameW" || fieldName == "frameH")
            {
                var n:Number = (value == null) ? 0 : Number(value);
                if (fieldName == "frameX") frameX = n;
                else if (fieldName == "frameY") frameY = n;
                else if (fieldName == "frameW") frameW = n;
                else frameH = n;
                rebuildDisplayObject(); // 纹理已缓存：直接按新帧矩形重建
            }
            else if (fieldName == "pivot")
            {
                if (value == null)
                {
                    // 恢复自动居中：pivot 跟随显示对象本地尺寸的一半。
                    pivotX = Number.NaN;
                    pivotY = Number.NaN;
                }
                else
                {
                    pivotX = Number(value.x);
                    pivotY = Number(value.y);
                }
            }
            else if (fieldName == "color")
            {
                if (value != null) color = uint(value);
            }
            else if (fieldName == "blendMode")
            {
                if (value != null) blendMode = String(value);
            }
            else
            {
                // 基类字段（Sorting Order 等）下沉给 DisplayComponent：本类重写了 setFieldValue，
                // 不透传的话这些字段在 Inspector 里可编辑却写不回去，输入后数值立刻弹回。
                super.setFieldValue(fieldName, value);
            }
        }
    }
}
