using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Dream.Studio.Engine
{
    /// <summary>
    /// 组件索引生成抽象：编译前扫描源码，生成 ComponentIndex.as 引用所有
    /// DreamComponent 子类，供 ComponentFactory 运行时反射注册。
    /// </summary>
    internal interface IComponentIndexer
    {
        void Generate(ActionScriptProject project);
    }

    /// <summary>
    /// 编译前组件索引生成器：扫描 asconfig 配置的 source-path 下所有 .as 文件，
    /// 解析 package/import/class 声明，构建类继承图，找出所有 dream.engine.ecs.DreamComponent
    /// 的传递子类，生成 ComponentIndex.as（引用全部子类，触发类链接）。
    ///
    /// 生成的文件位于 src/dream/engine/ecs/ComponentIndex.as，由 mxmlc 通过
    /// -source-path+=src 自动发现。每次编译前重新生成（TemplateCloner 已清空工作目录）。
    ///
    /// 这是 AS3 缺乏运行时类枚举能力的补偿：通过编译前静态扫描 + 生成引用代码，
    /// 实现"新增组件只需写类文件，无需手写注册"的反射式注册。
    /// </summary>
    internal sealed class ComponentIndexGenerator : IComponentIndexer
    {
        // DreamComponent 基类的全限定名（引擎 ECS 基类）。
        private const string DreamComponentFqcn = "dream.engine.ecs.DreamComponent";

        // 生成文件的包与路径（位于 source-path src 下，mxmlc 自动发现）。
        private const string GeneratedPackage = "dream.engine.ecs";
        private const string GeneratedRelativePath = "src/dream/engine/ecs/ComponentIndex.as";

        // ── 正则：解析 AS3 源码 ──

        // package dream.engine.ecs.components {
        private static readonly Regex PackageRegex =
            new(@"package\s+([\w.]+)\s*\{", RegexOptions.Compiled);

        // import dream.engine.ecs.DreamComponent;
        private static readonly Regex ImportRegex =
            new(@"import\s+([\w.]+)\s*;", RegexOptions.Compiled);

        // public final class DisplayComponent extends DreamComponent
        // public class Rotator extends DreamComponent
        // internal class Helper extends Foo
        private static readonly Regex ClassRegex =
            new(@"(?:public|internal)\s+(?:(?:final|dynamic)\s+)*class\s+(\w+)\s+extends\s+([\w.]+)",
                RegexOptions.Compiled);

        public void Generate(ActionScriptProject project)
        {
            var fqcns = CollectComponentSubclasses(project);
            WriteIndexFile(project, fqcns);
        }

        /// <summary>扫描源码，返回所有 DreamComponent 传递子类的全限定名（按字母序，稳定输出）。</summary>
        private List<string> CollectComponentSubclasses(ActionScriptProject project)
        {
            // fqcn → {shortName, parentRaw, imports}
            var classes = new Dictionary<string, ClassInfo>(StringComparer.Ordinal);

            foreach (var sourcePath in project.GetSourcePaths())
            {
                var absSource = Path.Combine(project.RootDir, sourcePath);
                if (!Directory.Exists(absSource)) continue;

                foreach (var file in Directory.EnumerateFiles(absSource, "*.as", SearchOption.AllDirectories))
                {
                    var text = File.ReadAllText(file);
                    ParseFile(text, classes);
                }
            }

            // 解析每个类的 parentRaw → parentFqcn
            var parentMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in classes)
            {
                var parentFqcn = ResolveParentFqcn(kv.Value);
                if (parentFqcn != null)
                    parentMap[kv.Key] = parentFqcn;
            }

            // BFS：从 DreamComponent 出发收集所有传递子类
            var descendants = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(DreamComponentFqcn);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var kv in parentMap)
                {
                    if (kv.Value == current && descendants.Add(kv.Key))
                        queue.Enqueue(kv.Key);
                }
            }

            return descendants.OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        /// <summary>解析单个文件，提取 package、imports、class 声明，填充 classes 字典。</summary>
        private static void ParseFile(string text, Dictionary<string, ClassInfo> classes)
        {
            // 默认包（无 package 声明或 package {}）
            var pkg = "";
            var pkgMatch = PackageRegex.Match(text);
            if (pkgMatch.Success)
                pkg = pkgMatch.Groups[1].Value;

            // imports：short name → fqcn
            var imports = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in ImportRegex.Matches(text))
            {
                var fqcn = m.Groups[1].Value;
                var shortName = fqcn.Substring(fqcn.LastIndexOf('.') + 1);
                imports[shortName] = fqcn;
            }

            // class 声明
            foreach (Match m in ClassRegex.Matches(text))
            {
                var className = m.Groups[1].Value;
                var parentRaw = m.Groups[2].Value;
                var fqcn = string.IsNullOrEmpty(pkg) ? className : pkg + "." + className;
                classes[fqcn] = new ClassInfo(className, pkg, parentRaw, imports);
            }
        }

        /// <summary>将类的 parentRaw（可能是短名或 FQCN）解析为全限定名。</summary>
        private static string? ResolveParentFqcn(ClassInfo info)
        {
            var parent = info.ParentRaw;
            if (string.IsNullOrEmpty(parent)) return null;

            // 含 "." 视为 FQCN（如 dream.engine.ecs.DreamComponent 或 dotted 包路径）
            if (parent.Contains('.'))
                return parent;

            // 短名：先查 imports，再回退到同包
            if (info.Imports.TryGetValue(parent, out var fqcn))
                return fqcn;

            // 同包回退
            return string.IsNullOrEmpty(info.Package) ? parent : info.Package + "." + parent;
        }

        /// <summary>生成 ComponentIndex.as 文件。</summary>
        private static void WriteIndexFile(ActionScriptProject project, List<string> fqcns)
        {
            var path = Path.Combine(project.RootDir, GeneratedRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var sb = new StringBuilder();
            sb.AppendLine("// AUTO-GENERATED by Dream Studio. Do not edit.");
            sb.AppendLine("// 编译前扫描源码生成：引用所有 DreamComponent 子类，供 ComponentFactory 反射注册。");
            sb.AppendLine("package " + GeneratedPackage);
            sb.AppendLine("{");

            // imports
            foreach (var fqcn in fqcns)
                sb.AppendLine("\timport " + fqcn + ";");

            sb.AppendLine();
            sb.AppendLine("\t/**");
            sb.AppendLine("\t * 自动生成的组件索引：引用所有 DreamComponent 子类（触发类链接）。");
            sb.AppendLine("\t * ComponentFactory.ensureInitialized 遍历 ALL，用 describeType 反射");
            sb.AppendLine("\t * 构造函数签名，自动注册无参构造且非 EXCLUDED 的子类。");
            sb.AppendLine("\t */");
            sb.AppendLine("\tpublic final class ComponentIndex");
            sb.AppendLine("\t{");

            // ALL 数组：用短类名引用
            sb.AppendLine("\t\tpublic static const ALL:Array = [");
            for (int i = 0; i < fqcns.Count; i++)
            {
                var fqcn = fqcns[i];
                var shortName = fqcn.Substring(fqcn.LastIndexOf('.') + 1);
                var comma = i < fqcns.Count - 1 ? "," : "";
                sb.AppendLine("\t\t\t" + shortName + comma + "  // " + fqcn);
            }
            sb.AppendLine("\t\t];");

            sb.AppendLine("\t}");
            sb.AppendLine("}");

            File.WriteAllText(path, sb.ToString());
        }

        private sealed class ClassInfo
        {
            public string Name { get; }
            public string Package { get; }
            public string ParentRaw { get; }
            public Dictionary<string, string> Imports { get; }

            public ClassInfo(string name, string pkg, string parentRaw, Dictionary<string, string> imports)
            {
                Name = name;
                Package = pkg;
                ParentRaw = parentRaw;
                Imports = imports;
            }
        }
    }
}
