using System;
using System.IO;
using System.Text.Json;
using Dream.Studio.Engine;

namespace Dream.Studio.Settings
{
    /// <summary>
    /// Studio 持久化设置。当前保存 AIR SDK 路径与上次打开的项目，供编译器、ADL 运行器与启动流程使用。
    ///
    /// 文件放在用户级应用数据目录（%LOCALAPPDATA%\Dream Studio\studio.settings.json），不放 exe 同级：
    /// 装机后安装目录通常不可写、多用户共用，而构建产物目录还会被 clean / 换 Debug·Release / 重新发布
    /// 覆盖掉，设置会跟着丢。SDK 路径、上次项目都属于"这台机器上是什么样"，放机器本地目录语义也最准。
    /// </summary>
    internal sealed class StudioSettings
    {
        private static readonly Lazy<StudioSettings> _instance = new(Load);

        public static StudioSettings Current => _instance.Value;

        private static string FilePath => Path.Combine(DataRoot, "studio.settings.json");

        /// <summary>用户级数据目录；系统未提供本地应用数据目录时退回仓库根（开发场景）。</summary>
        private static string DataRoot
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return string.IsNullOrEmpty(local)
                    ? EnginePaths.DreamRoot
                    : Path.Combine(local, "Dream Studio");
            }
        }

        /// <summary>AIR SDK 安装目录。为空或无效时编译/运行会失败。</summary>
        public string AirSdkPath { get; set; } = "";

        /// <summary>
        /// Android SDK 根目录。只有 Android App Bundle（aab）目标需要——ADT 打 aab 强制要求
        /// -platformsdk；apk 目标用的是 AIR SDK 自带的 aapt2 / d8 / apksigner，不需要它。
        /// </summary>
        public string AndroidSdkPath { get; set; } = "";

        /// <summary>
        /// 上次打开的项目目录（绝对路径，就地访问，不从模板克隆）。
        /// 为空表示使用默认开发工作目录（EnginePaths.WorkingProjectPath，启动时从模板克隆）。
        /// </summary>
        public string ProjectPath { get; set; } = "";

        /// <summary>从磁盘加载设置；文件不存在时返回默认实例（并尝试从旧的仓库根位置迁移一次）。</summary>
        private static StudioSettings Load()
        {
            try
            {
                var path = FilePath;
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(path));
                    if (loaded != null) return loaded;
                }

                // 旧位置（仓库根）：迁移一次，免得升级后还要重填 SDK 路径与上次项目。
                // 旧文件保留不动，万一迁移结果不对还能人工找回。
                var legacy = Path.Combine(EnginePaths.DreamRoot, "studio.settings.json");
                if (legacy != path && File.Exists(legacy))
                {
                    var migrated = JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(legacy));
                    if (migrated != null)
                    {
                        migrated.Save();
                        return migrated;
                    }
                }
            }
            catch { /* 加载失败时使用默认设置，避免启动崩溃 */ }
            return new StudioSettings();
        }

        /// <summary>将当前设置保存到磁盘。</summary>
        public void Save()
        {
            try
            {
                var path = FilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(this,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 保存失败静默 */ }
        }
    }
}
