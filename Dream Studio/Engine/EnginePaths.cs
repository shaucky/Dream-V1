using System;
using System.IO;

namespace Dream.Studio.Engine
{
    /// <summary>
    /// 开发阶段桩：集中硬编码引擎子项目的相关路径。
    /// DreamRoot 由宿主程序所在目录上溯四级推断（bin\Debug\net9.0 → 仓库根），
    /// 保证仓库整体迁移后仍可用。
    /// </summary>
    internal static class EnginePaths
    {
        public static readonly string DreamRoot =
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        /// <summary>模板子项目源：启动时克隆进工作目录的项目。
        /// 开发阶段直接用 FlappyTest 示例工程当模板——Studio 每次启动都在一个干净的
        /// 游戏工程上调试引擎，而不是空引擎模板。</summary>
        public static readonly string TemplateSourcePath = Path.Combine(DreamRoot, "FlappyTest");

        /// <summary>工作子项目路径：模板复制目标，编译/运行的根目录。</summary>
        public static readonly string WorkingProjectPath = Path.Combine(DreamRoot, ".workspace", "Project");

        /// <summary>
        /// 项目内的派生数据目录名（对标 Godot 的 .godot/）：存放导入/合图等由资源派生的数据，
        /// 随时可删可重建。它不属于资源，扫描资源与生成 .meta 时必须跳过。
        /// </summary>
        public const string ProjectDataDirectoryName = ".dream";
    }
}
