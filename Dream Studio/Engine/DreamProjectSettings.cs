using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Dream.Studio.Engine
{
    /// <summary>
    /// 项目设置（&lt;项目根&gt;/dream.project.json）。
    ///
    /// 与 asconfig.json 分工明确：asconfig 描述 AS3 侧怎么编译（source-path / 主类 / 输出），
    /// 本文件描述 Dream 特有的项目语义——构建产物用哪个场景作入口、按平台怎么打。
    /// 两者都放在项目根，互不侵入。文件缺失时全部取默认值（见 <see cref="Load"/>）。
    /// </summary>
    internal sealed class DreamProjectSettings
    {
        public const string FileName = "dream.project.json";

        /// <summary>启动场景：相对项目根的场景文件路径，构建产物以此为入口场景。空 = 未指定。</summary>
        public string StartupScene = "";

        /// <summary>
        /// 构建设置。应用身份（id / filename / versionNumber / name）不在此重复——
        /// 它们本就在项目的 app 描述符里，由 <see cref="AppDescriptor"/> 直接读写。
        /// </summary>
        public BuildSettings Build = new();

        public static string GetPath(string projectRoot) => Path.Combine(projectRoot, FileName);

        /// <summary>读取项目设置；文件缺失或解析失败时返回默认值（不抛异常）。</summary>
        public static DreamProjectSettings Load(string projectRoot)
        {
            var settings = new DreamProjectSettings();
            try
            {
                var path = GetPath(projectRoot);
                if (!File.Exists(path)) return settings;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                settings.StartupScene = Str(root, "startupScene");

                if (root.TryGetProperty("build", out var build) && build.ValueKind == JsonValueKind.Object)
                {
                    settings.Build.Platform = Str(build, "platform");
                    settings.Build.OutputDirectory = Str(build, "outputDirectory");
                    if (build.TryGetProperty("platforms", out var platforms)
                        && platforms.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var entry in platforms.EnumerateObject())
                        {
                            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                            var ps = new PlatformSettings
                            {
                                Architecture = Str(entry.Value, "architecture"),
                                Target = Str(entry.Value, "target"),
                            };
                            if (entry.Value.TryGetProperty("certificate", out var cert)
                                && cert.ValueKind == JsonValueKind.Object)
                            {
                                ps.Certificate = new CertificateSettings
                                {
                                    Keystore = Str(cert, "keystore"),
                                    StorePass = Str(cert, "storepass"),
                                    Alias = Str(cert, "alias"),
                                    KeyPass = Str(cert, "keypass"),
                                    TimestampUrl = Str(cert, "tsa"),
                                };
                            }
                            settings.Build.Platforms[entry.Name] = ps;
                        }
                    }

                    if (build.TryGetProperty("icon", out var icon) && icon.ValueKind == JsonValueKind.Object)
                    {
                        settings.Build.Icon.Source = Str(icon, "source");
                        if (icon.TryGetProperty("overrides", out var overrides)
                            && overrides.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var entry in overrides.EnumerateObject())
                            {
                                if (entry.Value.ValueKind != JsonValueKind.String) continue;
                                var size = entry.Name;
                                var iconPath = entry.Value.GetString() ?? "";
                                if (size.Length > 0 && iconPath.Length > 0)
                                    settings.Build.Icon.Overrides[size] = iconPath;
                            }
                        }
                    }
                }
            }
            catch { /* 解析失败：保留已读到的部分，其余取默认 */ }

            settings.Build.ApplyDefaults();
            return settings;
        }

        /// <summary>写出项目设置（字段名即文件格式约定）。</summary>
        public static void Write(string path, DreamProjectSettings settings)
        {
            var platforms = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in settings.Build.Platforms)
            {
                platforms[kv.Key] = new
                {
                    architecture = kv.Value.Architecture,
                    target = kv.Value.Target,
                    certificate = new
                    {
                        keystore = kv.Value.Certificate.Keystore,
                        storepass = kv.Value.Certificate.StorePass,
                        alias = kv.Value.Certificate.Alias,
                        keypass = kv.Value.Certificate.KeyPass,
                        tsa = kv.Value.Certificate.TimestampUrl,
                    },
                };
            }

            var payload = new
            {
                startupScene = settings.StartupScene,
                build = new
                {
                    platform = settings.Build.Platform,
                    outputDirectory = settings.Build.OutputDirectory,
                    platforms,
                    icon = new
                    {
                        source = settings.Build.Icon.Source,
                        overrides = settings.Build.Icon.Overrides,
                    },
                },
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            // 内容没变就不落盘：关闭构建面板也会走到这里，不必白白改动项目文件的时间戳。
            try
            {
                if (File.Exists(path) && File.ReadAllText(path) == json) return;
            }
            catch { }
            File.WriteAllText(path, json);
        }

        /// <summary>启动场景的绝对路径；未指定或文件不存在返回 null。</summary>
        public string? ResolveStartupScene(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(StartupScene)) return null;
            try
            {
                var abs = Path.GetFullPath(Path.Combine(projectRoot, StartupScene));
                return File.Exists(abs) ? abs : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 构建产物目录的顶层目录名（如 outputDirectory="build/win" → "build"）。
        /// 供资源扫描与资源树排除构建产物：它们不是资源，也不应生成 .meta。
        /// </summary>
        public static string ResolveOutputTopDirectoryName(string projectRoot)
        {
            var configured = Load(projectRoot).Build.OutputDirectory;
            if (string.IsNullOrWhiteSpace(configured)) configured = "build";
            var parts = configured.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : "";
        }

        private static string Str(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
    }

    /// <summary>
    /// 构建设置。
    ///
    /// 平台标识与架构刻意不用 C# 枚举约束：ADT 各版本的 target 与 -arch 取值会变，
    /// 可用取值应由实际安装的 SDK 探测得出，靠字符串传递才不会被 Studio 的硬编码挡住。
    /// </summary>
    internal sealed class BuildSettings
    {
        /// <summary>当前选中的目标平台（构建窗口默认标签页）。</summary>
        public string Platform = "";

        /// <summary>产物目录，相对项目根。</summary>
        public string OutputDirectory = "";

        /// <summary>按平台名索引的设置；键名即平台标识（windows / macos / linux / android / ios ...）。</summary>
        public Dictionary<string, PlatformSettings> Platforms = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 应用图标的**源**配置（生成结果记在 app 描述符的 &lt;icon&gt; 里，此处只记源）。
        /// 标准图标供所有未单独配置的尺寸缩放使用；某尺寸另有专用图时记在 Overrides 里。
        /// </summary>
        public IconSettings Icon = new();

        public PlatformSettings GetPlatform(string platform)
        {
            if (!Platforms.TryGetValue(platform, out var ps))
            {
                ps = new PlatformSettings();
                Platforms[platform] = ps;
            }
            return ps;
        }

        /// <summary>补齐空值：平台与输出目录给默认，便于新项目零配置可用。</summary>
        public void ApplyDefaults()
        {
            if (Platform.Length == 0) Platform = "windows";
            if (OutputDirectory.Length == 0) OutputDirectory = "build";
        }

        /// <summary>产物目录的绝对路径。</summary>
        public string ResolveOutputDirectory(string projectRoot)
        {
            var rel = string.IsNullOrWhiteSpace(OutputDirectory) ? "build" : OutputDirectory;
            try { return Path.GetFullPath(Path.Combine(projectRoot, rel)); }
            catch { return Path.Combine(projectRoot, rel); }
        }
    }

    /// <summary>应用图标的源配置。生成结果（每个尺寸的精确尺寸 PNG）记在 app 描述符的 &lt;icon&gt; 里。</summary>
    internal sealed class IconSettings
    {
        /// <summary>标准图标源图，相对项目根；未单独配置的尺寸都由它缩放而来。空 = 未设置。</summary>
        public string Source = "";

        /// <summary>某尺寸的专用源图（尺寸串 → 相对项目根的路径）；缺省用标准图标。</summary>
        public Dictionary<string, string> Overrides = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>单个平台的构建设置。</summary>
    internal sealed class PlatformSettings
    {
        /// <summary>目标架构（ADT 的 -arch 取值）；空表示交给 ADT 默认。</summary>
        public string Architecture = "";

        /// <summary>
        /// 该平台的 ADT 打包目标（-target 取值，如 bundle / apk-captive-runtime / aab）；
        /// 空表示用平台默认（见 <see cref="AdtPackageCommand.DefaultTarget"/>）。
        /// </summary>
        public string Target = "";

        public CertificateSettings Certificate = new();
    }

    /// <summary>
    /// 签名设置。ADT 的 bundle 目标强制要求签名，因此 Keystore 留空时由
    /// <see cref="DevCertificate"/> 自动生成一张自签名开发证书兜底；
    /// 正式发布应在此填自己的证书。密码为空时不会写进命令行——需要密码的目标
    /// 可在构建窗口的命令行标签页手工补齐。
    /// </summary>
    internal sealed class CertificateSettings
    {
        public string Keystore = "";
        public string StorePass = "";
        public string Alias = "";
        public string KeyPass = "";

        /// <summary>
        /// 时间戳服务器（ADT 的 -tsa）。留空 = 不下发该参数，由 ADT 用内置的默认 TSA；
        /// 若环境联不通外网会导致 "Could not generate timestamp" 而整包失败，
        /// 此时填 <c>none</c> 跳过时间戳（本地/内网构建的常规做法），或填自建 TSA 地址。
        /// Android 目标（apk / aab）不支持该参数，构建时不会下发。
        /// </summary>
        public string TimestampUrl = "";

        public bool IsEmpty => Keystore.Length == 0;
    }
}
