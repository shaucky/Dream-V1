using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Dream.Studio.Engine
{
    /// <summary>AS 编译器抽象。返回 true 表示编译成功，false 表示失败（错误已输出到 Console）。</summary>
    internal interface IAsCompiler
    {
        bool Compile(ActionScriptProject project);

        /// <summary>按显式选项编译（构建产物用：覆盖 define、关调试信息、输出到产物目录）。</summary>
        bool Compile(ActionScriptProject project, MxmlcOptions options);
    }

    /// <summary>
    /// 编译选项。默认值即编辑器开发构建：沿用 asconfig 的 define 与输出位置、带调试信息。
    /// </summary>
    internal sealed class MxmlcOptions
    {
        /// <summary>输出 SWF 的绝对路径；空则用 asconfig 的 compilerOptions.output。</summary>
        public string? OutputPath;

        /// <summary>按名覆盖/新增编译常量（键与 asconfig 的 define name 一致，如 CONFIG::STUDIO）。</summary>
        public Dictionary<string, string> DefineOverrides = new();

        /// <summary>是否生成调试信息（发布构建为 false）。</summary>
        public bool Debug = true;
    }

    /// <summary>
    /// 使用 AIR SDK 的 mxmlc 以 debug 模式编译子项目。
    /// 直接调用 java -jar mxmlc-cli.jar，复刻 mxmlc.bat 的启动参数，
    /// 避免 cmd /c 引号解析问题；输出捕获便于排错。
    /// 编译失败时不抛异常，而是将 mxmlc 输出写入 Console 面板并返回 false，
    /// 避免调试器在 first-chance 异常处中断。
    /// </summary>
    internal sealed class MxmlcCompiler : IAsCompiler
    {
        private readonly string _sdkPath;
        private readonly IEngineConsoleSink? _console;

        public MxmlcCompiler(string sdkPath, IEngineConsoleSink? console = null)
        {
            _sdkPath = sdkPath;
            _console = console;
        }

        public bool Compile(ActionScriptProject project) => Compile(project, new MxmlcOptions());

        public bool Compile(ActionScriptProject project, MxmlcOptions options)
        {
            // 重新加载 asconfig 以反映（可能的）模板复制后的内容。
            project.IsValid();

            var mainSource = project.FindMainSource();
            var jar = Path.Combine(_sdkPath, "lib", "mxmlc-cli.jar");
            var flexlib = Path.Combine(_sdkPath, "frameworks");

            if (string.IsNullOrWhiteSpace(_sdkPath) || !File.Exists(jar))
            {
                Emit($"Invalid AIR SDK path or missing mxmlc-cli.jar：{_sdkPath}", EngineConsoleLevel.Error);
                return false;
            }

            var output = options.OutputPath ?? project.GetOutputSwfPath();
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            var psi = new ProcessStartInfo
            {
                FileName = "java",
                WorkingDirectory = project.RootDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // 与 mxmlc.bat 一致的 JVM 参数。
            psi.ArgumentList.Add("-Dsun.io.useCanonCaches=false");
            psi.ArgumentList.Add("-Xms32m");
            psi.ArgumentList.Add("-Xmx512m");
            psi.ArgumentList.Add($"-Dflexlib={flexlib}");
            psi.ArgumentList.Add("-jar");
            psi.ArgumentList.Add(jar);

            foreach (var sp in project.GetSourcePaths())
            {
                if (Directory.Exists(Path.Combine(project.RootDir, sp)))
                    psi.ArgumentList.Add($"-source-path+={sp}");
            }
            foreach (var lp in project.GetLibraryPaths())
            {
                if (Directory.Exists(Path.Combine(project.RootDir, lp)))
                    psi.ArgumentList.Add($"-library-path+={lp}");
            }

            // asconfig compilerOptions.define → mxmlc -define=name,value
            // value 按 JSON ValueKind 转字面量：bool→true/false，string→'xxx'，number→原值。
            // 再按 options.DefineOverrides 覆盖同名项（发布构建把 CONFIG::STUDIO 置 false，
            // 必须替换而非追加——同名常量出现两次 mxmlc 会报重复定义）。
            var defines = new List<KeyValuePair<string, string>>();
            foreach (var d in project.GetDefines())
            {
                var name = d.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var v = d.Value.ValueKind switch
                {
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.String => "'" + d.Value.GetString() + "'",
                    JsonValueKind.Number => d.Value.GetRawText(),
                    _ => "false"
                };
                defines.Add(new KeyValuePair<string, string>(name, v));
            }
            foreach (var kv in options.DefineOverrides)
            {
                var entry = new KeyValuePair<string, string>(kv.Key, kv.Value);
                var idx = defines.FindIndex(d => string.Equals(d.Key, kv.Key, StringComparison.Ordinal));
                if (idx >= 0) defines[idx] = entry;
                else defines.Add(entry);
            }
            foreach (var d in defines)
                psi.ArgumentList.Add($"-define={d.Key},{d.Value}");

            psi.ArgumentList.Add(options.Debug ? "-debug=true" : "-debug=false");
            psi.ArgumentList.Add("-output");
            psi.ArgumentList.Add(output);
            psi.ArgumentList.Add(mainSource);

            // java 启动失败：输出错误到 Console 并返回 false，不抛异常。
            var proc = Process.Start(psi);
            if (proc == null)
            {
                Emit("Unable to start java (mxmlc), please confirm that AIR SDK and Java environment have been installed", EngineConsoleLevel.Error);
                return false;
            }
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                // 编译失败（文件名/类名不一致、语法错误等）：将 mxmlc 输出写入 Console，
                // 让用户看到错误详情。不抛异常，避免调试器 first-chance 中断。
                Emit($"Compile failed mxmlc (ExitCode={proc.ExitCode})。", EngineConsoleLevel.Error);
                if (!string.IsNullOrWhiteSpace(stdout))
                    Emit(stdout.Trim(), EngineConsoleLevel.Error);
                if (!string.IsNullOrWhiteSpace(stderr))
                    Emit(stderr.Trim(), EngineConsoleLevel.Error);
                return false;
            }

            return true;
        }

        private void Emit(string text, EngineConsoleLevel level)
        {
            try { _console?.WriteLine(text, level); } catch { }
        }
    }
}
