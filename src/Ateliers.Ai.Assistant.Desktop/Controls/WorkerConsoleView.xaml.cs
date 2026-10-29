using Microsoft.UI.Xaml.Controls;

namespace Ateliers.Ai.Assistant.Desktop.Controls;

/// <summary>Worker 操作の表示。ユースケースや HTTP・DB は ViewModel のポートへ委譲する。</summary>
public sealed partial class WorkerConsoleView : UserControl
{
    private System.Threading.Tasks.TaskCompletionSource<bool>? _deleteConfirmation;
    /// <summary>表示資産を初期化する。</summary>
    public WorkerConsoleView()
    {
        InitializeComponent(); Unloaded += (_, _) => CompleteDelete(false);
    }
    /// <summary>管理ダイアログ内で削除を確認する。ContentDialog を重ねない。</summary>
    public System.Threading.Tasks.Task<bool> ConfirmDeleteAsync(string name)
    {
        _deleteConfirmation = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        DeleteQuestion.Text = $"「{name}」を情報源から削除しますか？";
        DeleteConfirmation.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        DeleteConfirmation.StartBringIntoView();
        return _deleteConfirmation.Task;
    }
    private void ConfirmClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs args) => CompleteDelete(true);
    private void CancelClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs args) => CompleteDelete(false);
    private void CompleteDelete(bool confirmed)
    { DeleteConfirmation.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed; _deleteConfirmation?.TrySetResult(confirmed); _deleteConfirmation = null; }
#if DEBUG
    /// <summary>実コントロールの TwoWay 入力を検証する。</summary>
    internal async System.Threading.Tasks.Task SetInputsAsync(string name, string url, int interval)
    {
        SourceEditor.IsExpanded = true;
        await System.Threading.Tasks.Task.Delay(200); // Expander の内容が読み込まれてから入力する。
        if (SourceName.DataContext is not ViewModels.WorkerViewModel) throw new System.InvalidOperationException("Unexpected source DataContext: " + SourceName.DataContext?.GetType().FullName);
        SourceName.Text = name; SourceUrl.Text = url; SourceInterval.Value = interval; SourceAutomatic.IsChecked = true;
    }
#endif
}
