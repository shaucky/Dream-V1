package dream.engine.ecs
{
    CONFIG::STUDIO
    {
        /**
         * 字段元数据：描述一个可被 Inspector 编辑的字段。
         *
         * 字段类型 type 取值：
         *   "number"  — 数值（int/Number）
         *   "string"  — 字符串
         *   "boolean" — 布尔
         *   "vector2" — 二维向量（x, y 两个 number 子字段）
         *
         * 可选 hint：
         *   min:Number  — 数值下限
         *   max:Number  — 数值上限
         *   step:Number — 数值步进
         *   readonly:Boolean — 只读（Inspector 显示但禁用编辑）
         *   options:Array — 受控取值列表（仅字符串字段）：Inspector 显示下拉框而非
         *                   自由文本框。AS3 无枚举类型，用此约束受限字符串常量
         *                   （如混合模式、对齐等）。
         */
        public final class FieldInfo
        {
            public var name:String;       // 字段名（与组件实例上的公开属性名一致）
            public var label:String;      // Inspector 中显示的标签
            public var type:String;       // "number" | "string" | "boolean" | "vector2" | "color" | "resource"
            public var value:*;           // 当前值（读取时快照，vector2 为 {x, y}）
            public var min:Number = NaN;
            public var max:Number = NaN;
            public var step:Number = NaN;
            public var readonly:Boolean = false;
            public var options:Array = null; // 受控取值列表（string 字段下拉用），null 表示自由输入

            public function FieldInfo(name:String, label:String, type:String, value:*)
            {
                this.name = name;
                this.label = label;
                this.type = type;
                this.value = value;
            }
        }
    }
}
