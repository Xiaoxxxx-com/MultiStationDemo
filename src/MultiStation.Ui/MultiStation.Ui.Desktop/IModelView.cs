namespace MultiStation.Ui.Desktop;

/// <summary>机种 UI 视图：外壳只保留标题栏与切换入口，主体界面由各机种独立实现。</summary>
public interface IModelView : IDisposable
{
    /// <summary>切换机种或关闭窗口前优雅停机。</summary>
    Task ShutdownAsync();
}
