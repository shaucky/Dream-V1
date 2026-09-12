using System;
using System.IO;

using Avalonia;
using Avalonia.Media.Imaging;

namespace Dream.Studio.Engine
{
    /// <summary>
    /// 应用图标落盘：把一张源图（"标准图标"或某尺寸的专用图）缩放成 ADT 要求的**精确尺寸** PNG。
    ///
    /// 为什么必须生成文件而不是直接引用源图：ADT 对图标做两项强校验——尺寸名要在该版本的
    /// 白名单里（否则 error 103），且图片实际像素必须与元素名完全一致（否则
    /// <c>error 201: Icon xxx.png is wrong size</c>）。所以描述符里引用的是这里生成的文件。
    ///
    /// 落盘位置是描述符同级目录下的 <c>icons/</c>（源项目的应用根目录），文件名按尺寸固定
    /// （<c>icon-16x16.png</c>），因此重复构建只会覆盖、不会堆积。描述符里写同名相对路径，
    /// 构建时再把这个目录原样复制进暂存目录——ADT 对描述符里的相对路径按应用根解析，
    /// 两边同路径即可同时满足源项目直接打包与 Studio 打包。
    /// </summary>
    internal static class AppIconWriter
    {
        /// <summary>图标目录名（位于应用根目录下）。</summary>
        public const string DirectoryName = "icons";

        /// <summary>图标目录的绝对路径。</summary>
        public static string DirectoryFor(string appRoot) => Path.Combine(appRoot, DirectoryName);

        /// <summary>某尺寸的图标在描述符里使用的相对路径（'/' 分隔，跨平台一致）。</summary>
        public static string RelativePathFor(string size) => DirectoryName + "/icon-" + size + ".png";

        /// <summary>该尺寸在当前配置下是否是"由标准图标生成"的（用于区分覆盖图与生成图）。</summary>
        public static bool IsGeneratedPath(string size, string path)
            => string.Equals(Normalize(path), Normalize(RelativePathFor(size)), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 由 <paramref name="sourcePath"/> 生成 <paramref name="size"/>（<c>NxN</c>）尺寸的图标。
        /// 成功返回描述符里应写的相对路径，失败返回 null 并说明原因。
        /// </summary>
        public static string? Write(string appRoot, string size, string sourcePath, IEngineConsoleSink? console)
        {
            try
            {
                if (!IconSizeCatalog.TryParseSize(size, out var width, out var height))
                {
                    console?.WriteLine("[icon] unsupported size: " + size, EngineConsoleLevel.Error);
                    return null;
                }
                if (!File.Exists(sourcePath))
                {
                    console?.WriteLine("[icon] source image not found: " + sourcePath, EngineConsoleLevel.Error);
                    return null;
                }

                var dir = DirectoryFor(appRoot);
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, "icon-" + size + ".png");

                using var source = new Bitmap(sourcePath);
                var pixels = source.PixelSize;
                // 每次都按目标尺寸重绘（单一代码路径）；仅在缺失或源图更新时落盘，避免每轮构建都改文件时间戳。
                if (!File.Exists(target) || IsStale(target, sourcePath))
                {
                    using var scaled = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
                    using (var context = scaled.CreateDrawingContext())
                    {
                        context.DrawImage(source,
                            new Rect(0, 0, pixels.Width, pixels.Height),
                            new Rect(0, 0, width, height));
                    }
                    scaled.Save(target);
                    console?.WriteLine($"[icon] {size}: written from {Path.GetFileName(sourcePath)}",
                        EngineConsoleLevel.Info);
                }
                return RelativePathFor(size);
            }
            catch (Exception ex)
            {
                console?.WriteLine("[icon] " + size + ": " + ex.Message, EngineConsoleLevel.Error);
                return null;
            }
        }

        /// <summary>源图比目标文件新（用户换了源图）时需要重新生成。</summary>
        private static bool IsStale(string target, string sourcePath)
        {
            try { return File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(target); }
            catch { return true; }
        }

        private static string Normalize(string path) => (path ?? "").Replace('\\', '/').Trim();
    }
}
