package dream.engine.ui
{
    /**
     * 指针事件接收接口：CanvasSystem 命中分发时调用实现组件的元素。
     * x/y 为屏幕像素坐标（与 InputManager.mouseX/mouseY 同空间）。
     *
     * 任一 UI 元素被点击（按下在元素内抬起）时，其全部实现此接口的组件
     * 都会收到 onPointerDown/onPointerUp/onPointerClick。
     */
    public interface IPointerHandler
    {
        function onPointerDown(x:Number, y:Number):void;
        function onPointerUp(x:Number, y:Number):void;
        function onPointerClick(x:Number, y:Number):void;
    }
}
