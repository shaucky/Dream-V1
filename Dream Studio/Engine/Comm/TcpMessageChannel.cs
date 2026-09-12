using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Dream.Studio.Engine.Comm
{
    /// <summary>
    /// 基于 TcpClient 的消息通道实现：4 字节大端长度前缀 + UTF-8 JSON 帧。
    /// 接收在后台循环读取完整帧并触发 MessageReceived；发送加锁串行写入。
    /// </summary>
    internal sealed class TcpMessageChannel : IMessageChannel
    {
        private readonly TcpClient _client;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public bool IsConnected => _client.Connected;
        public event EventHandler<Message>? MessageReceived;
        public event EventHandler? Closed;

        public TcpMessageChannel(TcpClient client) => _client = client;

        /// <summary>启动后台接收循环。应在 channel 交付使用前调用一次。</summary>
        public void Start() => _ = ReceiveLoopAsync(_cts.Token);

        public async Task SendAsync(Message message, CancellationToken ct = default)
        {
            if (!IsConnected) return;
            var json = JsonSerializer.SerializeToUtf8Bytes(message);
            await _sendLock.WaitAsync(ct);
            try
            {
                var stream = _client.GetStream();
                var lenBuf = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(lenBuf, (uint)json.Length);
                await stream.WriteAsync(lenBuf, ct);
                await stream.WriteAsync(json, ct);
                await stream.FlushAsync(ct);
            }
            finally { _sendLock.Release(); }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                var stream = _client.GetStream();
                while (!ct.IsCancellationRequested)
                {
                    var len = await ReadUInt32BigEndianAsync(stream, ct);
                    if (len == 0) break;
                    var body = new byte[len];
                    if (!await ReadExactAsync(stream, body, ct)) break;
                    var msg = JsonSerializer.Deserialize<Message>(body);
                    if (msg != null) MessageReceived?.Invoke(this, msg);
                }
            }
            catch { }
            finally { Closed?.Invoke(this, EventArgs.Empty); }
        }

        private static async Task<uint> ReadUInt32BigEndianAsync(Stream s, CancellationToken ct)
        {
            var buf = new byte[4];
            if (!await ReadExactAsync(s, buf, ct)) return 0;
            return BinaryPrimitives.ReadUInt32BigEndian(buf);
        }

        private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
        {
            var off = 0;
            while (off < buf.Length)
            {
                var n = await s.ReadAsync(buf.AsMemory(off), ct);
                if (n == 0) return false;
                off += n;
            }
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _sendLock.Dispose();
            _client.Dispose();
            await ValueTask.CompletedTask;
        }
    }
}
