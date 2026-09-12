using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Dream.Studio.Engine.Comm
{
    /// <summary>
    /// TCP 监听器：Studio 作为宿主监听本地回环端口，接受 Engine 回连。
    /// 端口传 0 时由 OS 分配（避免固定端口冲突），实际端口通过 <see cref="Port"/> 暴露。
    /// </summary>
    internal sealed class TcpMessageServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        /// <summary>实际监听端口。</summary>
        public int Port { get; }

        public event EventHandler<TcpMessageChannel>? ChannelConnected;

        public TcpMessageServer(int port = 0)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public void Start() => _ = AcceptLoopAsync(_cts.Token);

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(ct); }
                catch { break; }
                var channel = new TcpMessageChannel(client);
                // 先触发事件让订阅者注册 MessageReceived，再启动接收循环。
                // 否则 Engine 在 ChannelConnected 回调里发的首批消息（如 Ready/Log）
                // 可能在订阅前到达而被丢弃。
                ChannelConnected?.Invoke(this, channel);
                channel.Start();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }
}
