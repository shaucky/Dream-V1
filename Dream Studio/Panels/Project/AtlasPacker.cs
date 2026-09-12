using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Dream.Studio.Panels.Project
{
    /// <summary>
    /// 图集打包器：把一组精灵（各自「源纹理 + 像素矩形」）合成一张图集纹理，
    /// 并给出各精灵在图集中的像素位置。
    ///
    /// 产线位置：精灵（已切分，各自带 GUID）→ [本类] → 图集 PNG + 精灵位置 →
    /// 由 ProjectPanel 写成 .dmatlas 并推送给引擎。引擎侧组件仍只引用精灵 GUID；
    /// 图集只是可选覆盖层，删掉图集时引用不受影响。
    ///
    /// 算法：MaxRects（Best Short Side Fit）+ 自由矩形分裂与包含剪枝。
    /// 图集尺寸按 2 的幂从小到大试（256 → 4096），首个装得下全部精灵的尺寸即采用。
    /// 精灵间留 padding 像素间隔，避免相邻精灵在双线性采样时相互渗色。
    ///
    /// 已知边界：不做旋转、不做缩放下采样——装不下 4096 图集时直接失败，而非静默改尺寸。
    /// </summary>
    internal static class AtlasPacker
    {
        /// <summary>图集允许的边长（2 的幂，从小到大尝试）。</summary>
        private static readonly int[] CandidateSizes = { 256, 512, 1024, 2048, 4096 };

        /// <summary>待打包的一个精灵：源纹理路径 + 该纹理内的像素矩形。</summary>
        internal sealed record PackItem(string SpriteGuid, string Name, string SourcePath, SliceRect Region);

        /// <summary>单个精灵在图集中的位置（图集像素坐标，不含间隔）。</summary>
        internal sealed record Frame(string SpriteGuid, string Name, int X, int Y, int W, int H);

        /// <summary>打包结果：图集写出路径 + 图集尺寸 + 各精灵位置。</summary>
        internal sealed record PackResult(string AtlasPath, int AtlasWidth, int AtlasHeight, IReadOnlyList<Frame> Frames);

        /// <summary>
        /// 把 <paramref name="items"/> 打包到 <paramref name="atlasPath"/>。
        /// 图集边长不超过 <paramref name="maxSize"/>。
        /// 失败返回 null 并给出 <paramref name="error"/>（无精灵 / 解码失败 / 装不下）。
        /// </summary>
        public static PackResult? Pack(IReadOnlyList<PackItem> items, string atlasPath, int padding,
                                       int maxSize, out string error)
        {
            error = "";
            if (items.Count == 0) { error = "no sprites to pack"; return null; }

            // 同一张源纹理可能切出多个精灵：按路径复用已解码位图，避免重复解码。
            var opened = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var sources = new List<Source>(items.Count);
                foreach (var item in items)
                {
                    var src = Source.TryLoad(opened, item);
                    if (src == null)
                    {
                        error = "failed to decode: " + Path.GetFileName(item.SourcePath);
                        return null;
                    }
                    sources.Add(src);
                }

                foreach (var size in CandidateSizes)
                {
                    if (size > maxSize) break;
                    if (TryCompose(size, sources, items, padding, atlasPath, out var result)) return result;
                }

                error = $"does not fit in {maxSize}x{maxSize}";
                return null;
            }
            finally
            {
                foreach (var bmp in opened.Values) bmp.Dispose();
            }
        }

        /// <summary>在 size×size 图集内尝试容纳全部精灵，成功则写出 PNG 并返回结果。</summary>
        private static bool TryCompose(int size, List<Source> sources, IReadOnlyList<PackItem> items,
                                       int padding, string atlasPath, out PackResult? result)
        {
            result = null;
            var packer = new MaxRects(size, size);
            var placed = new IntRect[sources.Count];

            // 大图优先：先放尺寸大的，碎片更少（输出仍按输入顺序）。
            var order = new List<int>(sources.Count);
            for (int i = 0; i < sources.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                var sa = sources[a]; var sb = sources[b];
                return Math.Max(sb.W, sb.H).CompareTo(Math.Max(sa.W, sa.H));
            });

            foreach (var i in order)
            {
                var s = sources[i];
                // 间隔计入占位：右/下留 gutter，保证相邻精灵之间有 padding 像素。
                if (!packer.TryInsert(s.W + padding, s.H + padding, out var rect)) return false;
                placed[i] = new IntRect(rect.X, rect.Y, s.W, s.H);
            }

            WriteAtlas(size, sources, placed, atlasPath);

            var frames = new List<Frame>(sources.Count);
            for (int i = 0; i < sources.Count; i++)
                frames.Add(new Frame(items[i].SpriteGuid, items[i].Name,
                    placed[i].X, placed[i].Y, placed[i].W, placed[i].H));

            result = new PackResult(atlasPath, size, size, frames);
            return true;
        }

        /// <summary>把各精灵逐行拷贝到图集位图并保存为 PNG。</summary>
        private static void WriteAtlas(int size, List<Source> sources, IntRect[] placed, string atlasPath)
        {
            using var atlas = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96),
                PixelFormats.Bgra8888, AlphaFormat.Premul);
            using (var fb = atlas.Lock())
            {
                var dstBase = fb.Address;
                var dstStride = fb.RowBytes;
                for (int i = 0; i < sources.Count; i++)
                {
                    var s = sources[i];
                    var p = placed[i];
                    for (int y = 0; y < s.H; y++)
                    {
                        var dst = IntPtr.Add(dstBase, (p.Y + y) * dstStride + p.X * 4);
                        Marshal.Copy(s.Pixels, y * s.W * 4, dst, s.W * 4);
                    }
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(atlasPath)!);
            atlas.Save(atlasPath);
        }

        /// <summary>已解码的源图区域：名称（精灵名）+ 尺寸 + BGRA 像素。</summary>
        private sealed class Source
        {
            public string Name = "";
            public int W, H;
            public byte[] Pixels = Array.Empty<byte>();

            /// <param name="opened">源纹理路径 → 已解码位图（跨精灵复用，由调用方统一释放）。</param>
            public static Source? TryLoad(Dictionary<string, Bitmap> opened, PackItem item)
            {
                try
                {
                    if (!opened.TryGetValue(item.SourcePath, out var bmp))
                    {
                        using var stream = File.OpenRead(item.SourcePath);
                        bmp = new Bitmap(stream);
                        opened[item.SourcePath] = bmp;
                    }

                    var texW = bmp.PixelSize.Width;
                    var texH = bmp.PixelSize.Height;

                    // 精灵矩形裁剪到纹理范围内：切分参数与图片尺寸不符时不越界。
                    var region = item.Region;
                    int x = Math.Max(0, region.X);
                    int y = Math.Max(0, region.Y);
                    int w = Math.Min(region.W, texW - x);
                    int h = Math.Min(region.H, texH - y);
                    if (w <= 0 || h <= 0) return null;

                    var stride = w * 4;
                    var pixels = new byte[stride * h];
                    var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                    try
                    {
                        bmp.CopyPixels(new PixelRect(x, y, w, h), handle.AddrOfPinnedObject(), pixels.Length, stride);
                    }
                    finally { handle.Free(); }

                    // 解码格式非 BGRA8 时按通道序交换 R/B，保证写入 BGRA8 图集后颜色正确。
                    if (bmp.Format == PixelFormats.Rgba8888) SwapRedBlue(pixels);

                    return new Source { Name = item.Name, W = w, H = h, Pixels = pixels };
                }
                catch
                {
                    return null;
                }
            }

            private static void SwapRedBlue(byte[] bgra)
            {
                for (int i = 0; i + 3 < bgra.Length; i += 4)
                    (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            }
        }

        private readonly record struct IntRect(int X, int Y, int W, int H);

        /// <summary>
        /// MaxRects 装箱：维护一组自由矩形，每次插入选「最短边剩余最小」的位置，
        /// 插入后把与占用区相交的自由矩形切成至多 4 块，并剪掉被包含的碎片。
        /// </summary>
        private sealed class MaxRects
        {
            private readonly int _w, _h;
            private readonly List<IntRect> _free = new();

            public MaxRects(int w, int h)
            {
                _w = w; _h = h;
                _free.Add(new IntRect(0, 0, w, h));
            }

            public bool TryInsert(int w, int h, out IntRect placed)
            {
                placed = default;
                if (w <= 0 || h <= 0 || w > _w || h > _h) return false;

                var bestScore1 = int.MaxValue;
                var bestScore2 = int.MaxValue;
                var found = false;

                foreach (var fr in _free)
                {
                    if (w > fr.W || h > fr.H) continue;
                    var leftoverW = fr.W - w;
                    var leftoverH = fr.H - h;
                    var s1 = Math.Min(leftoverW, leftoverH);
                    var s2 = Math.Max(leftoverW, leftoverH);
                    if (s1 < bestScore1 || (s1 == bestScore1 && s2 < bestScore2))
                    {
                        bestScore1 = s1;
                        bestScore2 = s2;
                        placed = new IntRect(fr.X, fr.Y, w, h);
                        found = true;
                    }
                }

                if (!found) return false;
                Split(placed);
                Prune();
                return true;
            }

            /// <summary>把与已占用区相交的自由矩形替换为其未被覆盖的至多 4 块子区域。</summary>
            private void Split(IntRect used)
            {
                for (int i = _free.Count - 1; i >= 0; i--)
                {
                    var fr = _free[i];
                    if (!Intersects(fr, used)) continue;
                    _free.RemoveAt(i);

                    // 上下切分（仅当水平方向确有重叠）
                    if (used.X < fr.X + fr.W && used.X + used.W > fr.X)
                    {
                        if (used.Y > fr.Y)
                            Add(fr.X, fr.Y, fr.W, used.Y - fr.Y);
                        if (used.Y + used.H < fr.Y + fr.H)
                            Add(fr.X, used.Y + used.H, fr.W, fr.Y + fr.H - (used.Y + used.H));
                    }
                    // 左右切分（仅当垂直方向确有重叠）
                    if (used.Y < fr.Y + fr.H && used.Y + used.H > fr.Y)
                    {
                        if (used.X > fr.X)
                            Add(fr.X, fr.Y, used.X - fr.X, fr.H);
                        if (used.X + used.W < fr.X + fr.W)
                            Add(used.X + used.W, fr.Y, fr.X + fr.W - (used.X + used.W), fr.H);
                    }
                }
            }

            /// <summary>移除被其它自由矩形完全包含的碎片，避免自由列表无限膨胀。</summary>
            private void Prune()
            {
                for (int i = _free.Count - 1; i >= 0; i--)
                {
                    for (int j = 0; j < _free.Count; j++)
                    {
                        if (i == j) continue;
                        if (Contains(_free[j], _free[i])) { _free.RemoveAt(i); break; }
                    }
                }
            }

            private void Add(int x, int y, int w, int h)
            {
                if (w > 0 && h > 0) _free.Add(new IntRect(x, y, w, h));
            }

            private static bool Intersects(IntRect a, IntRect b)
                => a.X < b.X + b.W && a.X + a.W > b.X && a.Y < b.Y + b.H && a.Y + a.H > b.Y;

            private static bool Contains(IntRect outer, IntRect inner)
                => inner.X >= outer.X && inner.Y >= outer.Y
                   && inner.X + inner.W <= outer.X + outer.W
                   && inner.Y + inner.H <= outer.Y + outer.H;
        }
    }
}
