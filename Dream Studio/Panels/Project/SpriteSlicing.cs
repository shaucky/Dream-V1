using System.Collections.Generic;

namespace Dream.Studio.Panels.Project
{
    /// <summary>源纹理内的一个像素矩形。</summary>
    internal readonly record struct SliceRect(int X, int Y, int W, int H);

    /// <summary>
    /// 切分设置：随 .dmsheet 一同序列化（"slice" 字段），使重新切分与新增切分方式
    /// 都能还原参数。Mode 为字符串（而非 enum），新增模式只需在 <see cref="SpriteSlicer"/>
    /// 里加一个分支，不必改动文件格式与调用方。
    /// 当前支持："grid"（行×列等分）。
    /// </summary>
    internal sealed class SliceSettings
    {
        public const string GridMode = "grid";

        public string Mode { get; init; } = GridMode;
        public int Rows { get; init; } = 1;
        public int Columns { get; init; } = 1;
    }

    /// <summary>
    /// 精灵切分器：把一张纹理按 <see cref="SliceSettings"/> 切成若干像素矩形。
    /// 返回顺序为「行优先」（先行后列），精灵命名沿用该顺序。
    /// </summary>
    internal static class SpriteSlicer
    {
        /// <summary>按设置切分；模式未知或尺寸非法时返回空列表。</summary>
        public static List<SliceRect> Slice(SliceSettings settings, int width, int height)
        {
            if (width <= 0 || height <= 0) return new List<SliceRect>();
            return settings.Mode switch
            {
                SliceSettings.GridMode => SliceGrid(settings.Rows, settings.Columns, width, height),
                _ => new List<SliceRect>(),
            };
        }

        /// <summary>
        /// 等分网格：第 i 格右边界取 (i+1)*宽/列数，余数因此均摊到各格
        /// （尺寸不能整除时不会丢像素，也不会让最后一格单独变宽）。
        /// </summary>
        private static List<SliceRect> SliceGrid(int rows, int columns, int width, int height)
        {
            var result = new List<SliceRect>();
            if (rows <= 0 || columns <= 0) return result;

            for (int r = 0; r < rows; r++)
            {
                int y0 = r * height / rows;
                int y1 = (r + 1) * height / rows;
                for (int c = 0; c < columns; c++)
                {
                    int x0 = c * width / columns;
                    int x1 = (c + 1) * width / columns;
                    if (x1 > x0 && y1 > y0)
                        result.Add(new SliceRect(x0, y0, x1 - x0, y1 - y0));
                }
            }
            return result;
        }
    }
}
