using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dream.Studio.Panels.Inspector;

namespace Dream.Studio.Engine.Comm.Handlers
{
    /// <summary>
    /// 处理 Engine 返回的 Inspector 快照，反序列化后更新 Inspector 面板。
    /// 可选地把快照转发给额外订阅者（如 Clip 曲线编辑器的动态组件/字段候选）。
    /// </summary>
    internal sealed class InspectorSnapshotHandler : IMessageHandler
    {
        private readonly InspectorPanel _panel;
        private readonly Action<InspectorSnapshotData>? _forward;

        public InspectorSnapshotHandler(InspectorPanel panel, Action<InspectorSnapshotData>? forward = null)
        {
            _panel = panel;
            _forward = forward;
        }

        public string MessageType => MessageTypes.InspectorSnapshot;

        public Task HandleAsync(Message message, IMessageChannel channel, CancellationToken ct)
        {
            if (!message.Payload.HasValue) return Task.CompletedTask;
            var payload = message.Payload.Value;

            var snapshot = JsonSerializer.Deserialize<InspectorSnapshotData>(payload.GetRawText());
            if (snapshot != null)
            {
                _panel.UpdateSnapshot(snapshot);
                _forward?.Invoke(snapshot);
            }

            return Task.CompletedTask;
        }
    }
}
