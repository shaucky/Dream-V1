using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Dream.Studio.Engine.Comm.Handlers
{
    /// <summary>处理 Engine 回传的 pong，记录到 engine.log 以验证往返。</summary>
    internal sealed class PongHandler : IMessageHandler
    {
        public string MessageType => MessageTypes.Pong;

        public Task HandleAsync(Message message, IMessageChannel channel, CancellationToken ct)
        {
            Log("[comm] pong received");
            return Task.CompletedTask;
        }

        private static void Log(string text)
        {
            try
            {
                File.AppendAllText(Path.Combine(EnginePaths.DreamRoot, "engine.log"), text + "\n");
            }
            catch { }
        }
    }
}
