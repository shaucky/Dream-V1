using System.Threading;
using System.Threading.Tasks;

namespace Dream.Studio.Engine.Comm.Handlers
{
    /// <summary>
    /// 接收引擎的场景修改通知（scene.modified，无 payload）。
    /// 由 Gizmo 拖拽等引擎本地编辑触发，Studio 据此标记场景为未保存状态。
    /// </summary>
    internal sealed class SceneModifiedHandler : IMessageHandler
    {
        private readonly System.Action _onModified;

        public SceneModifiedHandler(System.Action onModified) => _onModified = onModified;

        public string MessageType => MessageTypes.SceneModified;

        public Task HandleAsync(Message message, IMessageChannel channel, CancellationToken ct)
        {
            _onModified?.Invoke();
            return Task.CompletedTask;
        }
    }
}
