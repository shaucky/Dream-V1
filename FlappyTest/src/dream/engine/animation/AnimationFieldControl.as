package dream.engine.animation
{
    /**
     * 动画字段独占注册表：动画播放期间，其轨道覆盖的字段禁止被外部写入
     * （如 Inspector 编辑、其它系统回写），避免与动画争抢导致抖动。
     *
     * 键为 "targetKey.componentType.field"（如 "e12.Transform.position"），
     * 与 .dmclip 轨道的 target/componentType/field 对应：targetKey 为目标元素
     * 的 "e+元素id"（轨道 target 为空时即动画所在元素自身）。按元素作用域化后，
     * 不同元素上同名字段互不干扰——播放 clip A 控制子元素 X、clip B 控制子元素 Y
     * 时各自独占自己的字段。由 AnimationPlayer/AnimStateMachine 播放时注册、
     * 停止/销毁时释放；InspectorEditHandler 在 setFieldValue 前检查此表并拦截
     * 被独占的字段。
     */
    public final class AnimationFieldControl
    {
        // "targetKey.componentType.field" -> true
        private static var _controlled:Object = {};

        /** 注册独占字段（动画播放期间外部不可写）。targetKey 形如 "e12"（元素 id）。 */
        public static function control(targetKey:String, componentType:String, field:String):void
        {
            if (componentType == null || componentType.length == 0 ||
                field == null || field.length == 0) return;
            _controlled[targetKey + "." + componentType + "." + field] = true;
        }

        /** 释放独占字段（动画停止/销毁时）。 */
        public static function release(targetKey:String, componentType:String, field:String):void
        {
            if (componentType == null || field == null) return;
            delete _controlled[targetKey + "." + componentType + "." + field];
        }

        /** 字段是否被动画独占（针对指定目标元素）。 */
        public static function isControlled(targetKey:String, componentType:String, field:String):Boolean
        {
            if (componentType == null || field == null) return false;
            return _controlled[targetKey + "." + componentType + "." + field] === true;
        }

        /** 清空全部独占注册（调试/场景切换用）。 */
        public static function clear():void
        {
            _controlled = {};
        }
    }
}
