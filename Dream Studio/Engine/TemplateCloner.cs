using System;
using System.IO;

namespace Dream.Studio.Engine
{
    /// <summary>模板克隆抽象：当子项目缺失或结构非法时，把模板项目复制到工作路径。</summary>
    internal interface ITemplateCloner
    {
        void Ensure(ActionScriptProject project);
    }

    /// <summary>
    /// 文件系统模板克隆器：每次清空工作路径后递归复制模板目录，
    /// 跳过 bin/obj 等构建产物目录。确保工作目录始终与模板同步，
    /// 模板更新后不残留旧文件（避免条件编译常量等变更被忽略）。
    /// </summary>
    internal sealed class TemplateCloner : ITemplateCloner
    {
        private readonly string _templateSource;

        public TemplateCloner(string templateSource) => _templateSource = templateSource;

        public void Ensure(ActionScriptProject project)
        {
            if (!Directory.Exists(_templateSource))
                throw new DirectoryNotFoundException($"模板源不存在：{_templateSource}");

            // 每次清空工作目录再复制：保证与模板完全一致，模板更新不被旧文件遮蔽。
            if (Directory.Exists(project.RootDir))
                Directory.Delete(project.RootDir, recursive: true);
            Directory.CreateDirectory(project.RootDir);
            CopyDirectory(_templateSource, project.RootDir);

            // 复制后重新加载 asconfig，使后续编译能读到最新的 compilerOptions。
            project.IsValid();
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(source, file);
                var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (Array.IndexOf(parts, "bin") >= 0 || Array.IndexOf(parts, "obj") >= 0) continue;

                var dst = Path.Combine(destination, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(file, dst, overwrite: true);
            }
        }
    }
}
