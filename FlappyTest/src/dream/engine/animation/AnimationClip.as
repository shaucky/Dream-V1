package dream.engine.animation
{
    /**
     * 动画片段数据：解析 .dmclip（JSON）并提供按时间采样轨道值的接口。
     *
     * .dmclip 格式（与 Studio Clip 编辑器一致）：
     * {
     *   "name": "Move",
     *   "duration": 1.0,
     *   "loop": true,
     *   "tracks": [
     *     {
     *       "target": "",             // 可选：目标子元素名（沿 Transform 后代递归匹配）；
     *                                 // 空串 = 动画所在元素自身
     *       "componentType": "Transform",
     *       "field": "position",
     *       "fieldType": "vector2",
     *       "keys": [
     *         { "time": 0.0, "value": { "x": 0, "y": 0 } },
     *         { "time": 1.0, "value": { "x": 100, "y": 0 } }
     *       ]
     *     }
     *   ]
     * }
     *
     * fieldType 与 Inspector FieldInfo 一致：number | boolean | string | vector2 | color。
     * 采样规则：number/vector2/color 线性插值；boolean/string 步进（取前半段关键帧值）。
     */
    public final class AnimationClip
    {
        /** 片段名称。 */
        public var name:String = "Clip";

        /** 片段时长（秒）。 */
        public var duration:Number = 1;

        /** 是否循环。 */
        public var loop:Boolean = true;

        /**
         * 轨道数组。每项为 Object：
         *   target:String           —— 目标子元素名（可选，空串 = 自身；沿 Transform 后代递归匹配）
         *   componentType:String  —— 目标组件短类名（如 "Transform"）
         *   field:String          —— 目标字段名（与 Inspector FieldInfo 一致，如 "position"）
         *   fieldType:String      —— 字段类型（number/boolean/string/vector2/color）
         *   keys:Array            —— 关键帧 [{time:Number, value:*}]，按 time 升序
         */
        public var tracks:Array = [];

        public function AnimationClip()
        {
        }

        /** 从 JSON.parse 出的对象构造动画片段；缺失字段取默认值。 */
        public static function fromJson(data:Object):AnimationClip
        {
            var clip:AnimationClip = new AnimationClip();
            if (data == null) return clip;
            if (data.name != null) clip.name = String(data.name);
            if (data.duration != null) clip.duration = Number(data.duration);
            if (data.loop != null) clip.loop = Boolean(data.loop);

            if (data.tracks is Array)
            {
                for each (var raw:Object in data.tracks)
                {
                    if (raw == null) continue;
                    var track:Object = {
                        // 目标元素名（可选）：沿 Transform 后代按名称定位目标元素；
                        // 空串表示动画所在元素自身。控制子元素时记录子元素名。
                        target: raw.target != null ? String(raw.target) : "",
                        componentType: raw.componentType != null ? String(raw.componentType) : "",
                        field: raw.field != null ? String(raw.field) : "",
                        fieldType: raw.fieldType != null ? String(raw.fieldType) : "number",
                        keys: []
                    };
                    if (raw.keys is Array)
                    {
                        for each (var k:Object in raw.keys)
                        {
                            if (k == null) continue;
                            track.keys.push({ time: Number(k.time), value: k.value });
                        }
                    }
                    // 按时间升序（编辑器输出应有序，防御乱序）。
                    track.keys.sort(function(a:Object, b:Object):int
                    {
                        return a.time < b.time ? -1 : (a.time > b.time ? 1 : 0);
                    });
                    clip.tracks.push(track);
                }
            }
            return clip;
        }

        /** 将 t 折回 [0, duration]：loop 取模，否则 clamp 到边界。 */
        public function foldTime(t:Number):Number
        {
            if (duration <= 0) return 0;
            var ft:Number = loop ? t % duration : t;
            if (ft < 0) ft += duration;
            if (ft > duration) ft = duration;
            return ft;
        }

        /**
         * 采样指定时间点某条轨道的值。
         * 无关键帧返回 null；单关键帧返回该值（副本）。
         *
         * 插值类型（number/vector2/color）：相邻关键帧间线性插值。
         * 非插值类型（boolean/string 等）：向前追踪——取时间上最后一个
         * 不晚于采样时刻的关键帧值直接赋值（Unity 式步进）。
         */
        public function sampleTrack(track:Object, t:Number):*
        {
            if (track == null) return null;
            var keys:Array = track.keys as Array;
            if (keys == null || keys.length == 0) return null;
            if (keys.length == 1) return cloneValue(keys[0].value);

            var ft:Number = foldTime(t);
            var fieldType:String = track.fieldType;
            if (fieldType != "number" && fieldType != "vector2" && fieldType != "color")
            {
                // 非插值：向前追踪最后一个 time <= ft 的关键帧值。
                var last:Object = null;
                for each (var k0:Object in keys)
                {
                    if (k0.time <= ft) last = k0;
                    else break;
                }
                return cloneValue(last != null ? last.value : keys[0].value);
            }

            // 插值类型：区间定位，二分查找第一个 time > ft 的关键帧。
            var lo:int = 0;
            var hi:int = keys.length - 1;
            while (lo < hi)
            {
                var mid:int = (lo + hi) >> 1;
                if (keys[mid].time <= ft) lo = mid + 1;
                else hi = mid;
            }
            var b:Object = keys[lo];
            var a:Object = keys[lo - 1];
            if (lo == 0) return cloneValue(b.value);
            if (b.time <= a.time) return cloneValue(b.value);

            var u:Number = (ft - a.time) / (b.time - a.time);
            return interpolate(a.value, b.value, u, fieldType);
        }

        /** 两个关键帧值之间插值（按 fieldType）。 */
        private function interpolate(a:*, b:*, u:Number, fieldType:String):*
        {
            if (fieldType == "number")
                return Number(a) + (Number(b) - Number(a)) * u;
            if (fieldType == "vector2")
            {
                return {
                    x: lerp(Number(a.x), Number(b.x), u),
                    y: lerp(Number(a.y), Number(b.y), u)
                };
            }
            if (fieldType == "color")
            {
                var ca:uint = uint(a);
                var cb:uint = uint(b);
                var r:Number = ((ca >> 16) & 0xFF) * (1 - u) + ((cb >> 16) & 0xFF) * u;
                var g:Number = ((ca >> 8) & 0xFF) * (1 - u) + ((cb >> 8) & 0xFF) * u;
                var bl:Number = (ca & 0xFF) * (1 - u) + (cb & 0xFF) * u;
                return (Math.round(r) << 16) | (Math.round(g) << 8) | Math.round(bl);
            }
            // 未知/非插值类型兜底：向前追踪（取区间左端关键帧值）。
            return a;
        }

        private static function lerp(a:Number, b:Number, u:Number):Number
        {
            return a + (b - a) * u;
        }

        /** 返回值的副本：vector2 返回新对象避免别名；number/string/boolean 原样。 */
        private function cloneValue(v:*):*
        {
            if (v is Object && !(v is Number) && !(v is String) && !(v is Boolean) &&
                v.hasOwnProperty("x") && v.hasOwnProperty("y"))
            {
                return { x: Number(v.x), y: Number(v.y) };
            }
            return v;
        }
    }
}
