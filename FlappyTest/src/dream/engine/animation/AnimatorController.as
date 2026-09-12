package dream.engine.animation
{
    /**
     * 动画状态机控制器数据：解析 .dmanimator（JSON），提供状态/参数/过渡查询与评估。
     *
     * .dmanimator 格式（与 Studio Animator 编辑器一致）：
     * {
     *   "name": "PlayerAnim",
     *   "defaultState": "Idle",
     *   "parameters": [ { "name": "Speed", "type": "float", "value": 0 } ],
     *   "states": [ { "name": "Idle", "clipGuid": "..." } ],
     *   "transitions": [
     *     {
     *       "from": "Idle", "to": "Run",
     *       "hasExitTime": true, "exitTime": 1.0,
     *       "conditions": [ { "param": "Speed", "op": ">", "value": 0.1 } ]
     *     }
     *   ]
     * }
     *
     * 参数类型：boolean | float | int | trigger。
     * 过渡条件 op：== | != | > | < | >= | <=（boolean 仅 ==/!=；trigger 触发即满足）。
     * 过渡语义：hasExitTime 时先等待状态播放满 exitTime（秒），随后所有条件满足才切换；
     * 无条件且无 Exit Time 的过渡立即满足。
     */
    public final class AnimatorController
    {
        /** 控制器名称。 */
        public var name:String = "Controller";

        /** 进入控制器时播放的默认状态名。 */
        public var defaultState:String = "";

        /** 状态数组。每项 { name:String, clipGuid:String }。 */
        public var states:Array = [];

        /** 参数数组。每项 { name:String, type:String, value:Number }（type 见类注释）。 */
        public var parameters:Array = [];

        /** 过渡数组。每项 { from, to, hasExitTime, exitTime, conditions:[{param,op,value}] }。 */
        public var transitions:Array = [];

        public function AnimatorController()
        {
        }

        /** 从 JSON.parse 出的对象构造控制器；缺失字段取默认值。 */
        public static function fromJson(data:Object):AnimatorController
        {
            var c:AnimatorController = new AnimatorController();
            if (data == null) return c;
            if (data.name != null) c.name = String(data.name);
            if (data.defaultState != null) c.defaultState = String(data.defaultState);

            if (data.parameters is Array)
            {
                for each (var rawP:Object in data.parameters)
                {
                    if (rawP == null) continue;
                    c.parameters.push({
                        name: rawP.name != null ? String(rawP.name) : "",
                        type: rawP.type != null ? String(rawP.type) : "float",
                        value: rawP.value != null ? Number(rawP.value) : 0
                    });
                }
            }

            if (data.states is Array)
            {
                for each (var rawS:Object in data.states)
                {
                    if (rawS == null) continue;
                    c.states.push({
                        name: rawS.name != null ? String(rawS.name) : "",
                        clipGuid: rawS.clipGuid != null ? String(rawS.clipGuid) : ""
                    });
                }
            }

            if (data.transitions is Array)
            {
                for each (var rawT:Object in data.transitions)
                {
                    if (rawT == null) continue;
                    var conds:Array = [];
                    if (rawT.conditions is Array)
                    {
                        for each (var rawC:Object in rawT.conditions)
                        {
                            if (rawC == null) continue;
                            conds.push({
                                param: rawC.param != null ? String(rawC.param) : "",
                                op: rawC.op != null ? String(rawC.op) : "==",
                                value: rawC.value != null ? Number(rawC.value) : 0
                            });
                        }
                    }
                    c.transitions.push({
                        from: rawT.from != null ? String(rawT.from) : "",
                        to: rawT.to != null ? String(rawT.to) : "",
                        hasExitTime: rawT.hasExitTime != null ? Boolean(rawT.hasExitTime) : false,
                        exitTime: rawT.exitTime != null ? Number(rawT.exitTime) : 0,
                        conditions: conds
                    });
                }
            }
            return c;
        }

        /** 按名称查找状态；未找到返回 null。 */
        public function findState(name:String):Object
        {
            for each (var s:Object in states)
                if (s.name == name) return s;
            return null;
        }

        /** 按名称查找参数；未找到返回 null。 */
        public function findParameter(name:String):Object
        {
            for each (var p:Object in parameters)
                if (p.name == name) return p;
            return null;
        }

        /** 按名称查找参数类型；未找到返回 ""。 */
        public function parameterType(name:String):String
        {
            var p:Object = findParameter(name);
            return p != null ? String(p.type) : "";
        }

        /**
         * 评估从 stateName 出发的过渡。
         * @param paramValues 参数值表（name → 值，trigger 为一次性 true/false）
         * @param timeInState 当前状态已播放时长（秒），用于 Exit Time
         * @return 满足条件的过渡目标 { to:String, consumed:Array }；无则 null。
         *         consumed 为本次被消费的 trigger 参数名，调用方负责复位。
         */
        public function evaluateTransition(stateName:String, paramValues:Object, timeInState:Number):Object
        {
            for each (var t:Object in transitions)
            {
                if (t.from != stateName) continue;

                // Exit Time 前提：需先播放满指定时长。
                if (t.hasExitTime && timeInState < Number(t.exitTime)) continue;

                var consumed:Array = [];
                var ok:Boolean = true;
                var conds:Array = t.conditions as Array;
                if (conds != null)
                {
                    for each (var c:Object in conds)
                    {
                        if (!conditionSatisfied(c, paramValues, consumed))
                        {
                            ok = false;
                            break;
                        }
                    }
                }
                if (ok) return { to: String(t.to), consumed: consumed };
            }
            return null;
        }

        /** 单条条件评估。trigger 满足时加入 consumed。 */
        private function conditionSatisfied(cond:Object, paramValues:Object, consumed:Array):Boolean
        {
            var pname:String = String(cond.param);
            var op:String = String(cond.op);
            var target:Number = Number(cond.value);
            var v:* = paramValues[pname];
            if (v == undefined) return false;

            var type:String = parameterType(pname);
            if (type == "trigger")
            {
                // 触发即满足，消费后由调用方复位。
                if (v === true) { consumed.push(pname); return true; }
                return false;
            }

            if (type == "boolean")
            {
                var b:Boolean = (v === true);
                var tb:Boolean = (target != 0);
                if (op == "==") return b == tb;
                if (op == "!=") return b != tb;
                return false; // boolean 不支持大小比较
            }

            // float / int：数值比较。
            var n:Number = Number(v);
            switch (op)
            {
                case "==": return n == target;
                case "!=": return n != target;
                case ">":  return n > target;
                case "<":  return n < target;
                case ">=": return n >= target;
                case "<=": return n <= target;
            }
            return false;
        }
    }
}
