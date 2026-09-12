using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Dream.Studio.Engine.Comm.Handlers
{
    /// <summary>处理 Engine 上报的日志消息，写入 engine.log 并转发到 Console（DIP：依赖 sink 抽象）。
    /// payload 为字符串时直接取文本，否则取原始 JSON。</summary>
    internal sealed class EngineLogHandler : IMessageHandler
    {
        private readonly IEngineConsoleSink? _console;

        public EngineLogHandler(IEngineConsoleSink? console = null) => _console = console;

        public string MessageType => MessageTypes.Log;

        public Task HandleAsync(Message message, IMessageChannel channel, CancellationToken ct)
        {
            var text = ExtractText(message);
            Log("[engine] " + text);
            _console?.WriteLine("[engine] " + text, EngineConsoleLevel.Info);
            return Task.CompletedTask;
        }

        private static string ExtractText(Message message)
        {
            if (!message.Payload.HasValue) return "";
            var v = message.Payload.Value;
            return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
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
