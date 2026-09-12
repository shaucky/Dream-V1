using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Dream.Studio.Engine
{
    /// <summary>
    /// 开发用自签名证书。
    ///
    /// ADT 的 bundle 目标在「描述符 + 文件集」这种打包方式下强制要求签名：不给
    /// SIGNING_OPTIONS 会直接报 "Signing options required to package from descriptor and fileset"
    /// （帮助文本里那个 SIGNING_OPTIONS? 的 ? 是误导）。为了让新项目零配置也能打包，
    /// 用户没填证书时在此自动生成一张自签名 PKCS12。
    ///
    /// 证书落在派生数据目录 &lt;项目&gt;/.dream/build/ 下：它既不是资源、不进资源索引、
    /// 不生成 .meta，也不会跟着项目分发。要正式发布时在构建窗口填自己的证书即可覆盖。
    /// </summary>
    internal static class DevCertificate
    {
        /// <summary>证书文件名。</summary>
        public const string FileName = "dev-cert.p12";

        /// <summary>证书口令：开发用自签名证书，非机密；用户自备证书时以用户配置为准。</summary>
        public const string Password = "dreamdev";

        /// <summary>
        /// 取开发证书（不存在则生成）。失败返回 null，原因经 <paramref name="log"/> 报告。
        /// </summary>
        public static CertificateSettings? Ensure(string projectRoot, string sdkPath, Action<string> log)
        {
            var jar = Path.Combine(sdkPath, "lib", "adt.jar");
            if (!File.Exists(jar))
            {
                log("[build] cannot create a dev certificate: adt.jar not found (" + sdkPath + ")");
                return null;
            }

            var dir = Path.Combine(projectRoot, EnginePaths.ProjectDataDirectoryName, "build");
            var store = Path.Combine(dir, FileName);
            if (!File.Exists(store))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    log("[build] cannot create a dev certificate: " + ex.Message);
                    return null;
                }
                if (!Generate(jar, store, log)) return null;
            }

            // 时间戳对一次性的自签名证书没有意义，且 ADT 默认要去 DigiCert 取时间戳——
            // 内网/离线环境会因此整包失败。开发证书一律跳过时间戳。
            return new CertificateSettings { Keystore = store, StorePass = Password, TimestampUrl = "none" };
        }

        /// <summary>调用 adt -certificate 生成自签名证书（CN 固定，10 年有效期，2048 位 RSA）。</summary>
        private static bool Generate(string jar, string store, Action<string> log)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "java",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add("-jar");
                psi.ArgumentList.Add(jar);
                psi.ArgumentList.Add("-certificate");
                psi.ArgumentList.Add("-cn");
                psi.ArgumentList.Add("DreamStudioDev");
                psi.ArgumentList.Add("-ou");
                psi.ArgumentList.Add("Dream");
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add("Dream");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("CN");
                psi.ArgumentList.Add("-validityPeriod");
                psi.ArgumentList.Add("10");
                psi.ArgumentList.Add("2048-RSA");
                psi.ArgumentList.Add(store);
                psi.ArgumentList.Add(Password);

                using var proc = new Process { StartInfo = psi };
                var output = new StringBuilder();
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();

                if (proc.ExitCode == 0 && File.Exists(store)) return true;

                log($"[build] adt -certificate exited with code {proc.ExitCode}: {output.ToString().Trim()}");
                return false;
            }
            catch (Exception ex)
            {
                log("[build] cannot create a dev certificate: " + ex.Message);
                return false;
            }
        }
    }
}
