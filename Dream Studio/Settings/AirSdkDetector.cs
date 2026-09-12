using System;
using System.IO;
using System.Xml.Linq;

namespace Dream.Studio.Settings
{
    /// <summary>
    /// AIR SDK 路径检测：检查目录是否存在关键文件，并尝试从 air-sdk-description.xml 读取版本。
    /// </summary>
    internal static class AirSdkDetector
    {
        /// <summary>允许的最低 AIR SDK 主版本号：低于此版本视为无效 SDK（构建可能不兼容）。</summary>
        public const int MinMajorVersion = 50;

        /// <summary>检测指定路径是否为可用的 AIR SDK，并返回版本号（如 "51.3.1"）。
        /// 主版本低于 MinMajorVersion 时返回 false（视为不可用）。</summary>
        public static bool TryDetect(string path, out string version)
        {
            version = "";
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (!Directory.Exists(path)) return false;

            // 关键文件：mxmlc-cli.jar 与 adl.exe/adl（跨平台）。
            var mxmlcJar = Path.Combine(path, "lib", "mxmlc-cli.jar");
            var adlExe = Path.Combine(path, "bin", "adl.exe");
            var adlUnix = Path.Combine(path, "bin", "adl");
            if (!File.Exists(mxmlcJar)) return false;
            if (!File.Exists(adlExe) && !File.Exists(adlUnix)) return false;

            var descriptionPath = Path.Combine(path, "air-sdk-description.xml");
            if (File.Exists(descriptionPath))
            {
                try
                {
                    var doc = XDocument.Load(descriptionPath);
                    var versionElement = doc.Root?.Element("version");
                    var buildElement = doc.Root?.Element("build");
                    if (versionElement != null)
                    {
                        var v = versionElement.Value.Trim();
                        var b = buildElement?.Value.Trim() ?? "";
                        if (!string.IsNullOrEmpty(v))
                        {
                            version = string.IsNullOrEmpty(b) ? v : $"{v}.{b}";
                            // 主版本号过低：SDK 太旧，视为不可用。
                            var majorStr = v.Split('.')[0];
                            if (int.TryParse(majorStr, out var major) && major < MinMajorVersion)
                                return false;
                        }
                    }
                }
                catch { /* 解析失败仍视为可用，只是不显示版本 */ }
            }

            return true;
        }
    }
}
