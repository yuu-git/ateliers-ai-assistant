using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ateliers.Ai.Assistant.Desktop.Controls;

/// <summary>画面によらず同じ Worker ViewModel の実状態と操作を提供する固定領域。</summary>
public sealed partial class WorkerStatusView : UserControl
{
    /// <summary>表示を初期化する。</summary>
    public WorkerStatusView() => InitializeComponent();
    /// <summary>保存環境と既存コマンドをホストから受け取る。</summary>
    public void Initialize(WorkerViewModel worker, string environment)
    { DataContext = worker; EnvironmentText.Text = environment.StartsWith("Debug") ? "Debug" : "Release"; }
    private void StatusFlyoutOpening(object sender, object args)
    { if (sender is Flyout { Content: FrameworkElement content }) content.DataContext = DataContext; }
    private void StopMenuOpening(object sender, object args)
    { StopWorkerAction.DataContext = DataContext; }
    private void StatusSizeChanged(object sender, SizeChangedEventArgs args)
    { ExpandedStatus.Visibility = ActualWidth >= 150 ? Visibility.Visible : Visibility.Collapsed; CompactStatus.Visibility = ActualWidth < 150 ? Visibility.Visible : Visibility.Collapsed; }
#if DEBUG
    /// <summary>フッターの実ボタンが共有コマンドを使用することを確認して操作する。</summary>
    internal async System.Threading.Tasks.Task ToggleAiForVerificationAsync()
    {
        StatusButton.Flyout.ShowAt(StatusButton); await System.Threading.Tasks.Task.Delay(150);
        if (DataContext is not WorkerViewModel worker || !object.ReferenceEquals(PauseAiAction.Command, worker.ToggleAiPauseCommand)) throw new System.InvalidOperationException("Footer command binding failed");
        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(PauseAiAction);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await System.Threading.Tasks.Task.Delay(250); StatusButton.Flyout.Hide();
    }
#endif
}
