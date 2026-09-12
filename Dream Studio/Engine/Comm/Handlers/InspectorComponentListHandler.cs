using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dream.Studio.Panels.Inspector;

namespace Dream.Studio.Engine.Comm.Handlers
{
    /// <summary>
    /// 处理 Engine 返回的可添加组件列表，转发到 Inspector 面板。
    /// payload 格式：{ "components": ["Rotator", ...] }
    /// </summary>
    internal sealed class InspectorComponentListHandler : IMessageHandler
    {
        private readonly InspectorPanel _panel;

        public InspectorComponentListHandler(InspectorPanel panel) => _panel = panel;

        public string MessageType => MessageTypes.InspectorComponentList;

        public Task HandleAsync(Message message, IMessageChannel channel, CancellationToken ct)
        {
            if (!message.Payload.HasValue) return Task.CompletedTask;
            try
            {
                var payload = message.Payload.Value;
                if (payload.TryGetProperty("components", out var arr))
                {
                    var names = JsonSerializer.Deserialize<List<string>>(arr.GetRawText());
                    _panel.UpdateComponentList(names ?? new List<string>());
                }
            }
            catch { /* 反序列化失败静默 */ }
            return Task.CompletedTask;
        }
    }
}
