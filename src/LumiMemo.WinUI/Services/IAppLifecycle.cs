namespace LumiMemo.WinUI.Services;

/// <summary>应用级的生命周期动作，供界面触发。</summary>
/// <remarks>
/// 由 <c>App</c> 自己实现：收尾序列（保存便签、落盘布局、释放托盘与单实例守卫）
/// 只有它知道，界面不该也不需要一个「怎么退出」的副本。
/// </remarks>
public interface IAppLifecycle
{
    /// <summary>重启自己：先正常收尾，再拉起一个新进程。</summary>
    /// <remarks>
    /// 改存储位置之后走这条路——依赖笔记目录的服务全是启动时构造的，
    /// 换目录要重建一大片，重启是代价最小、也最不会丢数据的一条。
    /// </remarks>
    void Relaunch();
}
