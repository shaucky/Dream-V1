using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Dream.Studio.Engine.Comm.Handlers
{
    /// <summary>
    /// 接收引擎窗口的编辑器快捷键请求（engine.shortcut，payload.action: "save"/"saveAs"）。
    /// 焦点在嵌入的 Engine 窗口时 Studio 收不到键盘事件，引擎发此消息请求 Studio 执行
    /// 依赖文件对话框的命令（保存/另存为）；与引擎侧 DreamEngine.initEditorShortcuts 对应。
    /// </summary>
    internal sealed class EngineShortcutHandler : IMessageHandler
    {
        private readonly Action<string> _onShortcut;

        public EngineShortcutHandler(Action<string> onShortcut) => _onShortcut = onShortcut;

        public string MessageType => MessageTypes.EngineShortcut;

        public Task HandleAsync(Message message, IMessageChannel channel, CancellationToken ct)
        {
            var action = "";
            if (message.Payload is { } p && p.ValueKind == JsonValueKind.Object &&
                p.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String)
                action = a.GetString() ?? "";
            _onShortcut?.Invoke(action);
            return Task.CompletedTask;
        }
    }
}
