using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Dream.Studio.Panels.Project
{
    /// <summary>
    /// 系统文件图标提供者。Windows 下通过 SHGetFileInfo 提取系统关联图标，
    /// 按"扩展名 / 目录状态"缓存，避免重复 P/Invoke。非 Windows 平台返回 null
    /// （由上层自定义图标覆盖，或显示占位）。
    /// </summary>
    internal sealed class SystemFileIconProvider : IFileIconProvider
    {
        private readonly Dictionary<string, IImage?> _cache = new(StringComparer.OrdinalIgnoreCase);

#if WINDOWS
        public IImage? GetIcon(string fullPath, bool isDirectory, bool isExpanded)
        {
            var key = isDirectory
                ? (isExpanded ? "dir:open" : "dir:closed")
                : "ext:" + Path.GetExtension(fullPath).ToLowerInvariant();

            if (_cache.TryGetValue(key, out var cached)) return cached;

            IImage? icon = null;
            try
            {
                if (isDirectory)
                {
                    uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;
                    if (isExpanded) flags |= SHGFI_OPENICON;
                    icon = Extract("folder", FILE_ATTRIBUTE_DIRECTORY, flags);
                }
                else
                {
                    var ext = Path.GetExtension(fullPath);
                    var dummy = string.IsNullOrEmpty(ext) ? "file" : "dummy" + ext;
                    icon = Extract(dummy, FILE_ATTRIBUTE_NORMAL,
                        SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
                }
            }
            catch { /* 忽略：图标提取失败不应中断面板渲染 */ }

            _cache[key] = icon;
            return icon;
        }

        private static IImage? Extract(string path, uint fileAttributes, uint flags)
        {
            var shfi = new SHFILEINFO();
            SHGetFileInfo(path, fileAttributes, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (shfi.hIcon == IntPtr.Zero) return null;
            try { return HIconToBitmap(shfi.hIcon); }
            finally { DestroyIcon(shfi.hIcon); }
        }

        /// <summary>将 HICON 转换为 Avalonia <see cref="Bitmap"/>（32bit BGRA）。</summary>
        private static Bitmap? HIconToBitmap(IntPtr hIcon)
        {
            if (!GetIconInfo(hIcon, out var ii)) return null;
            try
            {
                IntPtr hbmp = ii.hbmColor != IntPtr.Zero ? ii.hbmColor : ii.hbmMask;
                if (hbmp == IntPtr.Zero) return null;

                GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bmp);
                if (bmp.bmWidth <= 0 || bmp.bmHeight <= 0) return null;

                int w = bmp.bmWidth;
                // 若仅有 mask（无 color bitmap），高度是 2×（mask + XOR），取一半。
                int h = ii.hbmColor != IntPtr.Zero ? bmp.bmHeight : bmp.bmHeight / 2;
                if (h <= 0) return null;

                var bi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = w,
                        biHeight = -h,       // top-down，省去翻转
                        biPlanes = 1,
                        biBitCount = 32,
                    }
                };

                var pixels = new byte[w * h * 4];
                IntPtr hdc = GetDC(IntPtr.Zero);
                try { GetDIBits(hdc, hbmp, 0, (uint)h, pixels, ref bi, DIB_RGB_COLORS); }
                finally { ReleaseDC(IntPtr.Zero, hdc); }

                // 若 alpha 全为 0（非 32bit 图标），设为完全不透明。
                bool hasAlpha = false;
                for (int i = 3; i < pixels.Length; i += 4)
                    if (pixels[i] != 0) { hasAlpha = true; break; }
                if (!hasAlpha)
                    for (int i = 3; i < pixels.Length; i += 4)
                        pixels[i] = 255;

                var result = new WriteableBitmap(
                    new PixelSize(w, h), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Unpremul);
                using (var l = result.Lock())
                    Marshal.Copy(pixels, 0, l.Address, pixels.Length);
                return result;
            }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            }
        }

        #region P/Invoke

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_OPENICON = 0x000000002;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const uint DIB_RGB_COLORS = 0;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
            [Out] byte[] lpvBits, ref BITMAPINFO lpbmi, uint uUsage);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
        }

        #endregion
#else
        public IImage? GetIcon(string fullPath, bool isDirectory, bool isExpanded) => null;
#endif
    }
}
