using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;

namespace Dream.Studio.Panels.Project
{
    /// <summary>
    /// 图标服务：优先返回自定义注册的图标（按扩展名 / 目录状态），
    /// 未定义时回退到 <see cref="IFileIconProvider"/>（通常为系统图标）。
    /// 自定义图标通过 <see cref="RegisterExtension"/> / <see cref="RegisterFolder"/> 注册，
    /// 支持未来为 .as、.json 等类型定义专属矢量图标。
    /// </summary>
    internal sealed class FileIconService : IFileIconProvider
    {
        private readonly Dictionary<string, IImage> _byExtension = new(StringComparer.OrdinalIgnoreCase);
        private IImage? _folderClosed;
        private IImage? _folderOpen;
        private readonly IFileIconProvider? _fallback;

        public FileIconService(IFileIconProvider? fallback = null) => _fallback = fallback;

        /// <summary>注册扩展名图标（如 ".as" → ActionScript 图标）。</summary>
        public void RegisterExtension(string extension, IImage icon)
            => _byExtension[extension] = icon;

        /// <summary>注册文件夹图标（收起 / 展开）。</summary>
        public void RegisterFolder(IImage closed, IImage open)
        {
            _folderClosed = closed;
            _folderOpen = open;
        }

        public IImage? GetIcon(string fullPath, bool isDirectory, bool isExpanded)
        {
            if (isDirectory)
            {
                if (isExpanded && _folderOpen != null) return _folderOpen;
                if (_folderClosed != null) return _folderClosed;
            }
            else
            {
                var ext = Path.GetExtension(fullPath);
                if (!string.IsNullOrEmpty(ext) && _byExtension.TryGetValue(ext, out var icon))
                    return icon;
            }

            return _fallback?.GetIcon(fullPath, isDirectory, isExpanded);
        }
    }
}
