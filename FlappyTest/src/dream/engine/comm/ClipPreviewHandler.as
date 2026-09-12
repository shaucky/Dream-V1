package dream.engine.comm
{
    CONFIG::STUDIO
    {
        import dream.engine.animation.AnimationFieldControl;
        import dream.engine.ecs.DreamComponent;
        import dream.engine.ecs.Element;
        import dream.engine.ecs.World;
        import dream.engine.transform.Transform;

        /**
         * Clip 预览处理器：响应 Studio 动画预览的采样值写入。
         *
         * 与 inspector.edit 的区别：不记录撤销、不发送快照，仅把采样值
         * 直接写入组件字段，供 Clip 面板播放时实时驱动场景元素。
         * 动画独占字段（AnimationFieldControl）仍被拦截，避免与播放中的
         * 动画争抢采样值。
         *
         * payload 格式（与 inspector.edit 一致，额外 target）：
         *   {elementId:0, target:"Child", component:"Transform", field:"position", value:{x:10,y:20}}
         * target 为目标子元素名（空串 = elementId 元素自身），按 Transform 后代递归匹配。
         */
        public final class ClipPreviewHandler implements IMessageHandler
        {
            private var _world:World;

            public function ClipPreviewHandler(world:World)
            {
                _world = world;
            }

            public function get messageType():String
            {
                return MessageTypes.ClipPreviewApply;
            }

            public function handle(message:Message, channel:IChannel):void
            {
                var p:Object = message.payload;
                if (p == null) return;

                var elementId:int = int(p.elementId);
                var componentName:String = String(p.component);
                var fieldName:String = String(p.field);
                var value:* = p.value;
                var targetName:String = (p.target != null) ? String(p.target) : "";

                var e:Element = InspectorShared.findElement(_world, elementId);
                if (e == null) return;

                // 解析目标元素：target 非空时按路径匹配该元素的后代（"A/B/C" 逐段或单名递归）；空串 = 自身。
                var te:Element = e;
                if (targetName.length > 0)
                {
                    var t:Transform = e.getComponent(Transform) as Transform;
                    if (t == null) return;
                    var ct:Transform = t.findChildByPath(targetName);
                    if (ct == null || ct.owner == null) return;
                    te = ct.owner;
                }

                var c:DreamComponent = InspectorShared.findComponent(te, componentName);
                if (c == null) return;

                // 动画独占：播放期间动画轨道覆盖的字段禁止外部写入（按目标元素作用域）。
                if (AnimationFieldControl.isControlled("e" + te.id, componentName, fieldName)) return;

                c.setFieldValue(fieldName, value);
            }
        }
    }
}
