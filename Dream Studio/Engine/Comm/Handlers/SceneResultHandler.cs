using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Dream.Studio.Engine.Comm.Handlers
{
    /// <summary>
    /// 处理 Engine 回传的 scene.result（保存/加载结果），通过回调通知 EngineSession
    /// 完成对应的 SaveSceneAsync/LoadSceneAsync 等待。
    ///
    /// payload: {action:"save"|"load", success:true|false}
    /// </summary>
    internal sealed class SceneResultHandler : IMessageHandler
    {
        private readonly System.Action<bool, string> _callback;

        public SceneResultHandler(System.Action<bool, string> callback)
        {
            _callback = callback;
        }

        public string MessageType => MessageTypes.SceneResult;

        public Task HandleAsync(Message message, IMessageChannel channel, CancellationToken ct)
        {
            bool success = false;
            string action = "";
            if (message.Payload is { } p && p.ValueKind == JsonValueKind.Object)
            {
                if (p.TryGetProperty("success", out var s)) success = s.GetBoolean();
                if (p.TryGetProperty("action", out var a)) action = a.GetString() ?? "";
            }
            _callback(success, action);
            return Task.CompletedTask;
        }
    }
}
