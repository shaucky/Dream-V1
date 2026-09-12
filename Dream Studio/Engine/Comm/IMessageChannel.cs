using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dream.Studio.Engine.Comm
{
    /// <summary>双向消息通道抽象：发送、接收、连接生命周期。</summary>
    internal interface IMessageChannel : IAsyncDisposable
    {
        bool IsConnected { get; }
        Task SendAsync(Message message, CancellationToken ct = default);
        event EventHandler<Message>? MessageReceived;
        event EventHandler? Closed;
    }
}
