package dream.engine.ui
{
    import dream.engine.ecs.DreamComponent;
    import dream.engine.ecs.Element;
    import dream.engine.render.Drawable;
    import dream.engine.render.DrawableContainer;
    import dream.engine.transform.Transform;

    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    /**
     * UI 可绘制组件基类：持有在画布屏幕空间绘制的 Drawable。
     *
     * 职责：
     *   - drawable 生命周期（onEnable/onDisable 可见性、onDestroy 清理）
     *   - attachTo 幂等挂载到画布层（CanvasSystem 调用）
     *   - swapDrawable 换图时复位挂载状态
     *
     * 尺寸与位置由 CanvasSystem 按 RectTransform/Layout 逐帧写入 drawable
     * （x/y/width/height，无旋转，Starling width/height setter 安全）。
     * 子类负责创建/替换 drawable 与暴露可编辑字段。
     */
    public class UIDrawable extends DreamComponent
    {
        protected var _drawable:Drawable = null;
        protected var _layer:DrawableContainer = null;
        protected var _attached:Boolean = false;

        /**
         * 交互缩放（Button 按下等）：CanvasSystem 放置时围绕矩形中心乘以此值。
         * 默认 1（无缩放）；由交互组件临时设置并还原，不参与序列化。
         */
        public var interactionScale:Number = 1;

        /**
         * UI 叠放序号：数值越大越靠上层。同为 0（默认）时按层级顺序叠放
         * （Hierarchy 中靠后的元素画在上层）。CanvasSystem 每帧按
         * (sortingOrder, 层级序号) 排序并可绘制对象子级顺序。
         */
        public var sortingOrder:int = 0;

        /** 当前可绘制对象（CanvasSystem 布局/命中用）；创建中可能为 null。 */
        public function get drawable():Drawable { return _drawable; }

        /**
         * 所属画布的「纹理像素 → UI 单位」换算基准（CanvasScaler.referencePixelsPerUnit）。
         * 沿 Transform 父链查找最近的上层 CanvasScaler；无画布/未配置时返回 100（Unity 默认）。
         * 按需查找（仅在 setNativeSize 等场景调用），不缓存。
         */
        public function get referencePixelsPerUnit():Number
        {
            if (owner == null) return 100;
            var t:Transform = owner.getComponent(Transform) as Transform;
            while (t != null)
            {
                var o:Element = t.owner;
                if (o != null)
                {
                    var scaler:CanvasScaler = o.getComponent(CanvasScaler) as CanvasScaler;
                    if (scaler != null)
                        return scaler.referencePixelsPerUnit > 0 ? scaler.referencePixelsPerUnit : 100;
                    if (o.getComponent(Canvas) != null) return 100; // 画布根上无缩放器
                }
                t = t.parent;
            }
            return 100;
        }

        public function UIDrawable()
        {
        }

        // ── 生命周期 ──

        override protected function onEnable():void
        {
            if (_drawable) _drawable.visible = true;
        }

        override protected function onDisable():void
        {
            if (_drawable) _drawable.visible = false;
        }

        override protected function onDestroy():void
        {
            if (_drawable != null)
            {
                _drawable.removeFromParent();
                _drawable = null;
            }
            _attached = false;
            _layer = null;
        }

        /**
         * 挂到指定容器（CanvasSystem 调用）。
         * 已挂到同一容器时幂等返回；目标容器变化时（元素在分组容器间移动、
         * 新增/移除 Mask 或 ScrollView）自动改挂到新容器。
         */
        internal function attachTo(layer:DrawableContainer):void
        {
            if (_attached && _layer === layer) return;
            if (_drawable != null) _drawable.removeFromParent();
            _layer = layer;
            if (_drawable != null) layer.addChild(_drawable);
            // 初始可见性跟随运行状态：层级无效时 onEnable 未触发，直接隐藏。
            if (_drawable) _drawable.visible = isRunning;
            _attached = true;
        }

        /** 清除当前可绘制对象并复位挂载状态（换图重建时用）。 */
        protected function swapDrawable():void
        {
            if (_drawable != null)
            {
                _drawable.removeFromParent();
                _drawable = null;
            }
            _attached = false;
        }

        // ── Inspector 反射（子类字段列表中插入排序字段用） ──

        /** 构造父类排序字段的 Inspector 描述，供子类 getInspectableFields 加入列表。 */
        CONFIG::STUDIO
        protected function sortingOrderFieldInfo():FieldInfo
        {
            return new FieldInfo("sortingOrder", "Sorting Order", "number", sortingOrder);
        }

        /** 处理父类排序字段写入；字段名匹配返回 true（子类 setFieldValue 首行调用）。 */
        protected function applySortingOrderField(fieldName:String, value:*):Boolean
        {
            if (fieldName != "sortingOrder") return false;
            sortingOrder = (value != null) ? int(value) : 0;
            return true;
        }
    }
}
