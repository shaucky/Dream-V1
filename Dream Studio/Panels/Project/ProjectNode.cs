using System.Collections.Generic;

namespace Dream.Studio.Panels.Project
{
    /// <summary>
    /// 项目树节点：文件、目录，或精灵表中的单个精灵。
    /// 子级在首次展开时由 <see cref="ProjectPanel"/> 懒加载。
    /// <see cref="IndentLevel"/> 由面板在扁平化时赋值，用于渲染缩进。
    /// </summary>
    internal sealed class ProjectNode
    {
        public string Name { get; internal set; }
        public string FullPath { get; internal set; }
        public bool IsDirectory { get; }

        /// <summary>是否可展开出子级：目录（文件系统子项）或精灵表（表内精灵）。</summary>
        public bool IsExpandable { get; internal set; }

        /// <summary>精灵子节点的精灵 GUID；非精灵节点为 null。</summary>
        public string? SpriteGuid { get; internal set; }

        public int IndentLevel { get; set; }
        public bool IsExpanded { get; set; }
        public bool ChildrenLoaded { get; set; }
        public List<ProjectNode> Children { get; } = new();

        public ProjectNode(string name, string fullPath, bool isDirectory)
        {
            Name = name;
            FullPath = fullPath;
            IsDirectory = isDirectory;
            IsExpandable = isDirectory;
        }

        /// <summary>是否为精灵表内的精灵节点。</summary>
        public bool IsSprite => SpriteGuid != null;
    }
}
