using Dream.Studio.Docking;

namespace Dream.Studio.Panels
{
    /// <summary>
    /// 描述一个可由菜单创建的面板。注册到 <see cref="PanelRegistry"/> 后，
    /// 菜单栏可枚举所有描述符并按需创建面板实例（开放封闭：新增面板类型无需修改菜单）。
    /// </summary>
    public interface IPanelDescriptor
    {
        string Id { get; }
        string Title { get; }
        DockPanel Create();
    }
}
