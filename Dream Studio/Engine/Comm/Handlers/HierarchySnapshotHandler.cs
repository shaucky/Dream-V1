using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Dream.Studio.Engine.Comm.Handlers
{
    /// <summary>
    /// 接收引擎推送的 Hierarchy 元素树快照，反序列化后更新 HierarchyPanel。
    ///
    /// payload 格式：{ "nodes": [{id, name, parentId, enabled, components:[...]}, ...] }
    /// </summary>
    internal sealed class HierarchySnapshotHandler : IMessageHandler
    {
        private readonly Panels.Hierarchy.HierarchyPanel _panel;

        public HierarchySnapshotHandler(Panels.Hierarchy.HierarchyPanel panel) => _panel = panel;

        public string MessageType => MessageTypes.HierarchySnapshot;

        public Task HandleAsync(Message message, IMessageChannel channel, CancellationToken ct)
        {
            if (!message.Payload.HasValue) return Task.CompletedTask;

            try
            {
                var root = message.Payload.Value;
                if (!root.TryGetProperty("nodes", out var nodesElement)) return Task.CompletedTask;

                var nodes = JsonSerializer.Deserialize<List<Panels.Hierarchy.HierarchyNodeData>>(
                    nodesElement.GetRawText());
                if (nodes != null)
                    _panel.UpdateSnapshot(nodes);
            }
            catch { /* 反序列化失败静默，等下一帧快照 */ }

            return Task.CompletedTask;
        }
    }
}
