package dream.engine.render
{
    import starling.display.DisplayObjectContainer;

    /**
     * 引擎可绘制对象容器：对 Starling DisplayObjectContainer 的内联封装。
     * RenderEngine.root 是此类型，Renderer 的可绘制对象挂载到 root 下渲染。
     *
     * 仅暴露 addChild/removeChild，不暴露 Starling 类型。
     *
     * 命名说明：与 Drawable 同理，避免与 starling.display.DisplayObjectContainer
     * 同名，以使用强类型 _impl。
     */
    public class DrawableContainer extends Drawable
    {
        public function DrawableContainer(impl:*)
        {
            super(impl);
        }

        /** 添加子可绘制对象（追加到末尾，即最上层）。 */
        public function addChild(child:Drawable):void
        {
            (_impl as DisplayObjectContainer).addChild(child._impl);
        }

        /** 在指定索引处插入子可绘制对象（索引越大越靠上层）。 */
        public function addChildAt(child:Drawable, index:int):void
        {
            (_impl as DisplayObjectContainer).addChildAt(child._impl, index);
        }

        /** 移除子可绘制对象。 */
        public function removeChild(child:Drawable):void
        {
            (_impl as DisplayObjectContainer).removeChild(child._impl);
        }

        /** 调整已挂载子对象的叠放索引（越大越靠上层）；非子对象忽略。 */
        public function setChildIndex(child:Drawable, index:int):void
        {
            var c:DisplayObjectContainer = _impl as DisplayObjectContainer;
            if (c == null || !c.contains(child._impl)) return;
            var n:int = c.numChildren;
            if (index < 0) index = 0; else if (index >= n) index = n - 1;
            c.setChildIndex(child._impl, index);
        }

        /** 子对象当前索引；非子对象返回 -1。 */
        public function getChildIndex(child:Drawable):int
        {
            var c:DisplayObjectContainer = _impl as DisplayObjectContainer;
            if (c == null || !c.contains(child._impl)) return -1;
            return c.getChildIndex(child._impl);
        }

        /** 子对象数量。 */
        public function get numChildren():int
        {
            return (_impl as DisplayObjectContainer).numChildren;
        }
    }
}
