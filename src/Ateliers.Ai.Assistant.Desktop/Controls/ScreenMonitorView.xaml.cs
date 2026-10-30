using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System;
using Ateliers.Ai.Assistant.Desktop.ViewModels;

namespace Ateliers.Ai.Assistant.Desktop.Controls;

/// <summary>監視入力と既存 Markdown 表示を配置する。監視処理は Worker に委譲する。</summary>
public sealed partial class ScreenMonitorView : UserControl, System.IDisposable
{
    private bool? _narrow;
    /// <summary>XAML を構築する。</summary>
    public ScreenMonitorView() => InitializeComponent();
    /// <summary>Host が生成した表示モデルを設定する。</summary>
    public void Initialize(ScreenMonitorViewModel viewModel)
    {
        DataContext = viewModel;
        viewModel.ConfirmDeleteAsync = async name => await new ContentDialog { XamlRoot = XamlRoot, Title = "監視を削除", Content = $"「{name}」を停止し、設定とメモリ内コメントを削除します。", PrimaryButtonText = "削除", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary;
    }
    private void LayoutChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < 900;
        if (_narrow != narrow) { _narrow = narrow; SettingsExpander.IsExpanded = !narrow; }
        ListColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(260);
        DetailColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ListRow.Height = narrow ? new GridLength(170) : new GridLength(1, GridUnitType.Star);
        DetailRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(Details, narrow ? 0 : 1); Grid.SetRow(Details, narrow ? 1 : 0);
    }
#if DEBUG
    /// <summary>実機試験で Markdown の DOM 反映を確認する。</summary>
    internal ChatTranscriptView TranscriptForVerification => Transcript;
#endif
    /// <summary>表示資源だけを解放する。</summary>
    public void Dispose() => Transcript.Dispose();
}
