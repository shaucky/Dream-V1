using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Dream.Studio.Engine.Comm
{
    /// <summary>单类型消息处理器（OCP：新增消息只需新增处理器并在 dispatcher 注册）。</summary>
    internal interface IMessageHandler
    {
        string MessageType { get; }
        Task HandleAsync(Message message, IMessageChannel channel, CancellationToken ct);
    }

    /// <summary>按消息类型分发到已注册处理器；未注册类型静默忽略。</summary>
    internal sealed class MessageDispatcher
    {
        private readonly Dictionary<string, IMessageHandler> _handlers = new();

        public MessageDispatcher Add(IMessageHandler handler)
        {
            _handlers[handler.MessageType] = handler;
            return this;
        }

        public Task DispatchAsync(Message message, IMessageChannel channel, CancellationToken ct = default)
            => _handlers.TryGetValue(message.Type, out var handler)
                ? handler.HandleAsync(message, channel, ct)
                : Task.CompletedTask;
    }
}
