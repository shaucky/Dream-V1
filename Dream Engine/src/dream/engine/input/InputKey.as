package dream.engine.input
{
    /**
     * 引擎输入键码常量（数值与 flash.ui.Keyboard 一致）。
     *
     * 设计原则：用户代码用本类常量查询键盘状态，不直接接触底层平台
     * 类型（flash.ui.Keyboard），底层可替换而不影响引擎用户。
     */
    public final class InputKey
    {
        // 字母 A-Z
        public static const A:uint = 65;
        public static const B:uint = 66;
        public static const C:uint = 67;
        public static const D:uint = 68;
        public static const E:uint = 69;
        public static const F:uint = 70;
        public static const G:uint = 71;
        public static const H:uint = 72;
        public static const I:uint = 73;
        public static const J:uint = 74;
        public static const K:uint = 75;
        public static const L:uint = 76;
        public static const M:uint = 77;
        public static const N:uint = 78;
        public static const O:uint = 79;
        public static const P:uint = 80;
        public static const Q:uint = 81;
        public static const R:uint = 82;
        public static const S:uint = 83;
        public static const T:uint = 84;
        public static const U:uint = 85;
        public static const V:uint = 86;
        public static const W:uint = 87;
        public static const X:uint = 88;
        public static const Y:uint = 89;
        public static const Z:uint = 90;

        // 数字 0-9
        public static const NUMBER_0:uint = 48;
        public static const NUMBER_1:uint = 49;
        public static const NUMBER_2:uint = 50;
        public static const NUMBER_3:uint = 51;
        public static const NUMBER_4:uint = 52;
        public static const NUMBER_5:uint = 53;
        public static const NUMBER_6:uint = 54;
        public static const NUMBER_7:uint = 55;
        public static const NUMBER_8:uint = 56;
        public static const NUMBER_9:uint = 57;

        // 功能键
        public static const Enter:uint = 13;
        public static const Escape:uint = 27;
        public static const Space:uint = 32;
        public static const Tab:uint = 9;
        public static const Backspace:uint = 8;
        public static const Delete:uint = 46;
        public static const Shift:uint = 16;
        public static const Control:uint = 17;
        public static const Alt:uint = 18;

        // 方向键
        public static const ArrowLeft:uint = 37;
        public static const ArrowUp:uint = 38;
        public static const ArrowRight:uint = 39;
        public static const ArrowDown:uint = 40;
    }
}
