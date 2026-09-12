using System;
using System.Collections.Generic;
using System.Linq;
using Dream.Studio.Docking;

namespace Dream.Studio.Panels
{
    /// <summary>
    /// 面板注册表。集中管理可创建的面板类型并缓存已创建的实例（单一职责：注册与实例缓存，
    /// 不涉及布局操作）。"是否已打开"属于布局的实时状态，由调用方查询 workspace，
    /// 避免跟踪表与布局树不同步（如拖拽过程中面板暂时脱离所有管理器）。
    /// </summary>
    public sealed class PanelRegistry
    {
        private readonly Dictionary<string, IPanelDescriptor> _descriptors = new();
        private readonly Dictionary<string, DockPanel> _instances = new();

        public void Register(IPanelDescriptor descriptor)
            => _descriptors[descriptor.Id] = descriptor;

        public IEnumerable<IPanelDescriptor> Descriptors => _descriptors.Values;

        /// <summary>已创建的面板实例缓存（按描述符 Id 索引），用于复用而非跟踪布局状态。</summary>
        public IReadOnlyDictionary<string, DockPanel> Instances => _instances;

        /// <summary>获取或创建指定描述符的面板实例。已缓存的实例会被复用。</summary>
        public DockPanel GetOrCreate(string id)
        {
            if (_instances.TryGetValue(id, out var existing))
            {
                return existing;
            }

            if (!_descriptors.TryGetValue(id, out var desc))
            {
                throw new KeyNotFoundException($"未注册的面板 Id: {id}");
            }

            var panel = desc.Create();
            _instances[id] = panel;
            return panel;
        }

        /// <summary>判断指定描述符的缓存实例是否存在于给定的活跃面板集合中。</summary>
        public bool IsOpen(string id, IEnumerable<DockPanel> livePanels)
        {
            if (!_instances.TryGetValue(id, out var panel))
            {
                return false;
            }

            return livePanels.Contains(panel);
        }
    }
}

