using System;
using System.Collections.Generic;
using System.IO;

namespace Dream.Studio.Engine
{
    /// <summary>
    /// 表示一个 ActionScript (AIR) 子项目：解析 asconfig.json 并校验结构完整性。
    /// 仅承载“项目模型”职责（SRP），不关心编译/运行/嵌入。
    /// </summary>
    internal sealed class ActionScriptProject
    {
        public string RootDir { get; }
        public string AsconfigPath { get; }
        public Asconfig Config { get; private set; }

        public string? AppXmlRelative => Config.Application;
        public string? MainClass => Config.MainClass;

        public ActionScriptProject(string rootDir)
        {
            RootDir = rootDir;
            AsconfigPath = Path.Combine(rootDir, "asconfig.json");
            Config = new Asconfig();
        }

        /// <summary>判定目录是否符合 ActionScript Project 结构：
        /// 存在 asconfig.json、application 描述符与主类源文件三者齐备。</summary>
        public bool IsValid()
        {
            if (!File.Exists(AsconfigPath)) return false;
            try { Config = Asconfig.Load(AsconfigPath); }
            catch { return false; }

            if (string.IsNullOrWhiteSpace(AppXmlRelative)) return false;
            if (!File.Exists(Path.Combine(RootDir, AppXmlRelative))) return false;

            return File.Exists(FindMainSource());
        }

        public string GetAppXmlPath() => Path.Combine(RootDir, AppXmlRelative!);

        /// <summary>输出 SWF 路径（asconfig compilerOptions.output，缺省回退到 bin/{MainClass}.swf）。</summary>
        public string GetOutputSwfPath()
        {
            var rel = Config.CompilerOptions?.Output;
            if (string.IsNullOrWhiteSpace(rel))
            {
                var main = (MainClass ?? "Main").Replace('.', Path.DirectorySeparatorChar);
                rel = Path.Combine("bin", main + ".swf");
            }
            return Path.Combine(RootDir, rel);
        }

        /// <summary>主类源文件：在 source-path 中查找 {mainClass}.as。</summary>
        public string FindMainSource()
        {
            var main = MainClass ?? string.Empty;
            var rel = main.Replace('.', Path.DirectorySeparatorChar) + ".as";
            foreach (var sp in GetSourcePaths())
            {
                var candidate = Path.Combine(RootDir, sp, rel);
                if (File.Exists(candidate)) return candidate;
            }
            return Path.Combine(RootDir, "src", rel);
        }

        public IReadOnlyList<string> GetSourcePaths()
            => Config.CompilerOptions?.SourcePath ?? new List<string> { "src" };

        /// <summary>保存对话框的首选目录：asconfig source-path 中首个可匹配（解析成功且存在）的目录；
        /// 无 asconfig / 无匹配 / 解析失败均回退到项目根目录。</summary>
        public static string GetDefaultSaveDirectory(string rootDir)
        {
            if (string.IsNullOrWhiteSpace(rootDir)) return rootDir;
            try
            {
                var asconfigPath = Path.Combine(rootDir, "asconfig.json");
                if (File.Exists(asconfigPath))
                {
                    var cfg = Asconfig.Load(asconfigPath);
                    if (cfg.CompilerOptions?.SourcePath is { } paths)
                    {
                        foreach (var sp in paths)
                        {
                            if (string.IsNullOrWhiteSpace(sp)) continue;
                            var abs = Path.GetFullPath(Path.Combine(rootDir, sp));
                            if (Directory.Exists(abs)) return abs;
                        }
                    }
                }
            }
            catch { /* 配置解析失败：回退项目根 */ }
            return rootDir;
        }

        public IReadOnlyList<string> GetLibraryPaths()
        {
            if (Config.CompilerOptions?.LibraryPath is { } paths) return paths;
            return Array.Empty<string>();
        }

        /// <summary>asconfig compilerOptions.define 条件编译常量（可能为空）。</summary>
        public IReadOnlyList<AsconfigDefine> GetDefines()
            => (IReadOnlyList<AsconfigDefine>?)Config.CompilerOptions?.Define
               ?? Array.Empty<AsconfigDefine>();
    }
}
