using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Ateliers.Ai.Assistant.Desktop.Controls;

/// <summary>解析画面の配置・メモリ内 PNG のプレビュー。取得・解析は ViewModel へ委譲する。</summary>
public sealed partial class ScreenAnalysisView : UserControl, IDisposable
{
    private ScreenAnalysisViewModel? _viewModel;
    private long _previewRevision;
    private bool _disposed;
    /// <summary>単一の Markdown コントロールを初期化する。診断ホストは接続するまで起動しない。</summary>
    public ScreenAnalysisView() { InitializeComponent(); Transcript.RenderingEnabled = false; }
    /// <summary>UI 非依存のモデルに接続する。</summary>
    public void Initialize(ScreenAnalysisViewModel viewModel)
    { _viewModel = viewModel; DataContext = viewModel; Transcript.RenderingEnabled = true; viewModel.PropertyChanged += Changed; }
    private async void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ScreenAnalysisViewModel.Image)) return;
        var revision = ++_previewRevision; PreviewImage.Source = null;
        var image = _viewModel?.Image;
        if (image is null) return;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream)) { writer.WriteBytes(image.GetPngBytes()); await writer.StoreAsync(); writer.DetachStream(); }
            stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
            if (!_disposed && revision == _previewRevision) { PreviewImage.Source = bitmap; _viewModel?.CompletePreview(image, true); }
        }
        catch { if (!_disposed && revision == _previewRevision) { PreviewImage.Source = null; _viewModel?.CompletePreview(image, false); } }
    }
    private void LayoutChanged(object sender, SizeChangedEventArgs args)
    {
        if (Panes is null) return;
        var narrow = ActualWidth < 900;
        InputColumn.Width = new GridLength(1, GridUnitType.Star); ResultColumn.Width = narrow ? new GridLength(0) : new GridLength(1.2, GridUnitType.Star);
        InputRow.Height = narrow ? new GridLength(0.55, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        ResultRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(ResultPane, narrow ? 0 : 1); Grid.SetRow(ResultPane, narrow ? 1 : 0);
    }
    /// <inheritdoc/>
    public void Dispose() { _disposed = true; _previewRevision++; if (_viewModel is not null) _viewModel.PropertyChanged -= Changed; PreviewImage.Source = null; Transcript.Dispose(); }
#if DEBUG
    /// <summary>実 WinUI のプレビューのデコード完了。</summary>
    internal bool HasPreviewForVerification => PreviewImage.Source is BitmapImage;
    /// <summary>狭幅での縦配置。</summary>
    internal bool IsStackedForVerification => Grid.GetRow(ResultPane) == 1 && ResultPane.ActualHeight > 100;
    /// <summary>既存 WebView2 で解析結果が変換・着色されたことを確認する。</summary>
    internal async Task VerifyMarkdownForVerificationAsync()
    {
        if (!await Transcript.Ready.WaitAsync(TimeSpan.FromSeconds(20))) throw new InvalidOperationException("Markdown view unavailable");
        for (var i = 0; i < 100; i++)
        {
            if (await Transcript.CoreForVerification.ExecuteScriptAsync("!!document.querySelector('h1') && !!document.querySelector('code.hljs')") == "true") return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Streaming Markdown/code did not render");
    }
#endif
}
