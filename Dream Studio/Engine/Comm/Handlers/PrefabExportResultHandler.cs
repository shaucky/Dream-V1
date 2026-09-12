using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Dream.Studio.Engine.Comm.Handlers
{
    /// <summary>
    /// 处理 Engine 回传的 prefab.exportResult（子树导出结果），把 data 的原始 JSON 文本
    /// 交给回调，由 EngineSession 回填 ExportPrefabAsync 的等待句柄。
    ///
    /// payload: {action:"export", success:true|false, data:{elements:[...]}}
    /// </summary>
    internal sealed class PrefabExportResultHandler : IMessageHandler
    {
        private readonly Action<string?> _callback;

        public PrefabExportResultHandler(Action<string?> callback)
        {
            _callback = callback;
        }

        public string MessageType => MessageTypes.PrefabExportResult;

        public Task HandleAsync(Message message, IMessageChannel channel, CancellationToken ct)
        {
            string? data = null;
            if (message.Payload is { } p && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True
                && p.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                data = d.GetRawText();
            }
            _callback(data);
            return Task.CompletedTask;
        }
    }
}
