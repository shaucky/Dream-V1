namespace Dream.Studio.Engine
{
    /// <summary>控制台输出级别（便于后续按级别着色/过滤）。</summary>
    internal enum EngineConsoleLevel
    {
        Info = 0,
        Warning = 1,
        Error = 2,
    }

    /// <summary>
    /// 引擎控制台输出接收者抽象（DIP）：Engine 层（AdlRunner / 消息处理器）依赖此抽象，
    /// 而非具体的 Avalonia 控件，使 Engine 层可独立测试且不耦合 UI。
    /// 实现方负责线程安全（实现可从任意线程调用）。
    /// </summary>
    internal interface IEngineConsoleSink
    {
        /// <summary>写入一行控制台输出。</summary>
        void WriteLine(string text, EngineConsoleLevel level = EngineConsoleLevel.Info);
    }
}
