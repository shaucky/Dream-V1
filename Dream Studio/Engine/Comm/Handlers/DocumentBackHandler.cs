using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dream.Studio.Engine.Comm.Handlers
{
    /// <summary>
    /// 接收引擎左上角返回按钮的点击（document.back，无 payload）。
    /// 引擎只发请求，文档栈与切换流程在 Studio 侧：MainWindow 记录进入预制体前的场景路径，
    /// 在此事件里切回去。与引擎侧 DreamEngine.onDocumentBack 对应。
    /// </summary>
    internal sealed class DocumentBackHandler : IMessageHandler
    {
        private readonly Action _onBack;

        public DocumentBackHandler(Action onBack) => _onBack = onBack;

        public string MessageType => MessageTypes.DocumentBack;

        public Task HandleAsync(Message message, IMessageChannel channel, CancellationToken ct)
        {
            _onBack?.Invoke();
            return Task.CompletedTask;
        }
    }
}
