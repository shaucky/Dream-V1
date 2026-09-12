using Avalonia.Media;

namespace Dream.Studio.Panels.Project
{
    /// <summary>
    /// 文件图标提供者：按路径与类型返回图标。
    /// 实现可基于自定义注册表或系统 API，由 <see cref="FileIconService"/> 组合使用。
    /// </summary>
    internal interface IFileIconProvider
    {
        /// <summary>
        /// 尝试获取图标。<paramref name="isExpanded"/> 仅对目录有意义（收起/展开可不同图标）。
        /// 返回 null 表示该提供者无法解析，由上层回退。
        /// </summary>
        IImage? GetIcon(string fullPath, bool isDirectory, bool isExpanded);
    }
}
