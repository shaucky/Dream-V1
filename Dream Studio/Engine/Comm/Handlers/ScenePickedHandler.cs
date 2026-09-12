using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Dream.Studio.Engine.Comm.Handlers
{
    /// <summary>
    /// 接收引擎视口拾取结果（scene.picked），同步 Hierarchy 选中。
    ///
    /// payload 格式：{ "elementIds": [0,1], "primaryId": 1 }；
    /// 兼容旧格式 { "elementId": 0 }（-1 表示点击空白处取消选中）。
    /// 选中同步复用 HierarchyPanel.SelectionChanged 链路：MainWindow 据此
    /// 更新 Inspector 并回发 scene.select 保持引擎高亮一致。
    /// </summary>
    internal sealed class ScenePickedHandler : IMessageHandler
    {
        private readonly Panels.Hierarchy.HierarchyPanel _hierarchy;

        public ScenePickedHandler(Panels.Hierarchy.HierarchyPanel hierarchy) => _hierarchy = hierarchy;

        public string MessageType => MessageTypes.ScenePicked;

        public Task HandleAsync(Message message, IMessageChannel channel, CancellationToken ct)
        {
            if (!message.Payload.HasValue) return Task.CompletedTask;

            try
            {
                var payload = message.Payload.Value;
                if (payload.TryGetProperty("elementIds", out var idsElem)
                    && idsElem.ValueKind == JsonValueKind.Array)
                {
                    var ids = new List<int>();
                    foreach (var id in idsElem.EnumerateArray())
                        if (id.TryGetInt32(out var v)) ids.Add(v);
                    var primary = payload.TryGetProperty("primaryId", out var p)
                        ? p.GetInt32() : (ids.Count > 0 ? ids[^1] : -1);
                    _hierarchy.SetSelection(ids, primary);
                }
                else
                {
                    var elementId = payload.GetProperty("elementId").GetInt32();
                    _hierarchy.SelectElementById(elementId);
                }
            }
            catch
            {
                // 解析失败静默，等待下一次拾取。
            }

            return Task.CompletedTask;
        }
    }
}
