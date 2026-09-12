package dream.engine.input
{
    import flash.display.Stage;
    import flash.events.KeyboardEvent;
    import flash.events.MouseEvent;
    import flash.geom.Point;
    import flash.utils.Dictionary;

    import dream.engine.render.RenderEngine;

    /**
     * 输入管理器：统一封装键盘/鼠标/滚轮输入。
     *
     * 设计原则：底层事件（flash/Starling）完全封装在内部，对外只暴露统一查询
     * API（InputKey 常量、MouseButton 常量、Vector2 坐标），未来替换底层实现
     * （如 Web/其他平台）时仅需重写本类，引擎用户代码不变。
     *
     * 使用（游戏代码）：
     *   InputManager.current.isKeyDown(InputKey.W);
     *   InputManager.current.isMouseDown(InputManager.MouseButton.LEFT);
     *   var pos:Vector2 = InputManager.current.mouseWorld;
     *
     * 帧语义：isKeyDown/isMouseDown 为持续按住状态；isKeyPressed/isMousePressed
     * 为本帧刚按下（每帧一次）；isKeyReleased/isMouseReleased 为本帧刚抬起。
     * pressed/released 帧状态由 InputSystem 在每帧更新前重置。
     */
    public final class InputManager
    {
        /** 鼠标按钮常量（isMouseDown 等的参数）。 */
        public static const MouseButton:Object = { LEFT: 0, MIDDLE: 1, RIGHT: 2 };

        /** 当前实例（服务定位器），由 DreamEngine 初始化时创建。 */
        public static var current:InputManager;

        // 键盘状态
        private var _keysHeld:Dictionary = new Dictionary();
        private var _keysPressed:Array = [];
        private var _keysReleased:Array = [];

        // 鼠标状态（按 MouseButton 索引）
        private var _mouseHeld:Array = [false, false, false];
        private var _mousePressed:Array = [false, false, false];
        private var _mouseReleased:Array = [false, false, false];
        private var _mouseX:Number = 0;
        private var _mouseY:Number = 0;
        private var _wheelDelta:Number = 0;

        public function InputManager(stage:Stage)
        {
            current = this;

            stage.addEventListener(KeyboardEvent.KEY_DOWN, onKeyDown);
            stage.addEventListener(KeyboardEvent.KEY_UP, onKeyUp);
            // MouseEvent.button 在当前 AIR/SWF 配置下不可用，用事件名区分按键。
            stage.addEventListener(MouseEvent.MOUSE_DOWN, onMouseDown);
            stage.addEventListener(MouseEvent.MOUSE_UP, onMouseUp);
            stage.addEventListener(MouseEvent.MIDDLE_MOUSE_DOWN, onMiddleMouseDown);
            stage.addEventListener(MouseEvent.MIDDLE_MOUSE_UP, onMiddleMouseUp);
            stage.addEventListener(MouseEvent.RIGHT_MOUSE_DOWN, onRightMouseDown);
            stage.addEventListener(MouseEvent.RIGHT_MOUSE_UP, onRightMouseUp);
            stage.addEventListener(MouseEvent.MOUSE_MOVE, onMouseMove);
            stage.addEventListener(MouseEvent.MOUSE_WHEEL, onMouseWheel);
        }

        /** 帧刷新：重置本帧 pressed/released 与滚轮增量。由 InputSystem 每帧调用。 */
        public function update(dt:Number):void
        {
            _keysPressed.length = 0;
            _keysReleased.length = 0;
            _mousePressed[0] = false; _mousePressed[1] = false; _mousePressed[2] = false;
            _mouseReleased[0] = false; _mouseReleased[1] = false; _mouseReleased[2] = false;
            _wheelDelta = 0;
        }

        // ── 键盘查询 ──

        /** 按键是否按住。 */
        public function isKeyDown(code:uint):Boolean { return _keysHeld[code] == true; }

        /** 按键是否在本帧刚按下（仅一次）。 */
        public function isKeyPressed(code:uint):Boolean { return _keysPressed.indexOf(code) >= 0; }

        /** 按键是否在本帧刚抬起。 */
        public function isKeyReleased(code:uint):Boolean { return _keysReleased.indexOf(code) >= 0; }

        // ── 鼠标查询 ──

        /** 鼠标屏幕坐标 X（native 像素）。 */
        public function get mouseX():Number { return _mouseX; }

        /** 鼠标屏幕坐标 Y（native 像素）。 */
        public function get mouseY():Number { return _mouseY; }

        /** 鼠标世界坐标（渲染根局部空间）；渲染未就绪时返回 null。 */
        public function get mouseWorld():Point
        {
            return RenderEngine.current != null
                ? RenderEngine.current.screenToWorld(_mouseX, _mouseY)
                : null;
        }

        /** 本帧滚轮增量（向上为正）。 */
        public function get mouseWheelDelta():Number { return _wheelDelta; }

        /** 鼠标按钮是否按住（MouseButton.LEFT/MIDDLE/RIGHT）。 */
        public function isMouseDown(button:int):Boolean { return _mouseHeld[button] == true; }

        /** 鼠标按钮是否在本帧刚按下。 */
        public function isMousePressed(button:int):Boolean { return _mousePressed[button] == true; }

        /** 鼠标按钮是否在本帧刚抬起。 */
        public function isMouseReleased(button:int):Boolean { return _mouseReleased[button] == true; }

        // ── 底层事件处理（不对外暴露） ──

        private function onKeyDown(e:KeyboardEvent):void
        {
            if (_keysHeld[e.keyCode] != true)
            {
                _keysHeld[e.keyCode] = true;
                _keysPressed.push(e.keyCode);
            }
        }

        private function onKeyUp(e:KeyboardEvent):void
        {
            if (_keysHeld[e.keyCode] == true)
            {
                _keysHeld[e.keyCode] = false;
                _keysReleased.push(e.keyCode);
            }
        }

        private function onMouseDown(e:MouseEvent):void { setMouse(0, true, true); }
        private function onMouseUp(e:MouseEvent):void { setMouse(0, false, true); }
        private function onMiddleMouseDown(e:MouseEvent):void { setMouse(1, true, true); }
        private function onMiddleMouseUp(e:MouseEvent):void { setMouse(1, false, true); }
        private function onRightMouseDown(e:MouseEvent):void { setMouse(2, true, true); }
        private function onRightMouseUp(e:MouseEvent):void { setMouse(2, false, true); }

        private function setMouse(button:int, down:Boolean, edge:Boolean):void
        {
            _mouseHeld[button] = down;
            if (down) _mousePressed[button] = true;
            else _mouseReleased[button] = true;
        }

        private function onMouseMove(e:MouseEvent):void
        {
            _mouseX = e.stageX;
            _mouseY = e.stageY;
        }

        private function onMouseWheel(e:MouseEvent):void
        {
            _wheelDelta += e.delta;
        }
    }
}
