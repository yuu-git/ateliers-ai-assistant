using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Ateliers.Ai.Assistant.Desktop.Images;

/// <summary>入力欄のテキスト・画像貼り付けと画像プレビューを接続する View 側の処理。HTTP は扱わない。</summary>
public sealed class ClipboardImageController : IDisposable
{
    private readonly TextBox _input;
    private readonly Image _preview;
    private readonly FrameworkElement _panel;
    private readonly InfoBar _error;
    private readonly ChatViewModel _viewModel;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private int _previewVersion;
    private int _pendingPastes;
    private bool _pasteScheduled;
    private bool _processingPaste;

    /// <summary>入力欄・画像プレビュー・エラー表示と ViewModel の通知を接続する。</summary>
    /// <param name="input">貼り付け操作を受け付ける入力欄。</param>
    /// <param name="preview">送信前の添付画像を表示するコントロール。</param>
    /// <param name="panel">添付の有無に応じて表示を切り替えるコンテナー。</param>
    /// <param name="error">貼り付けとプレビューのエラー表示先。</param>
    /// <param name="viewModel">入力・添付の状態を保持するチャット ViewModel。</param>
    public ClipboardImageController(TextBox input, Image preview, FrameworkElement panel, InfoBar error, ChatViewModel viewModel)
    {
        _input = input;
        _preview = preview;
        _panel = panel;
        _error = error;
        _viewModel = viewModel;
        _input.Paste += Paste;
        _viewModel.PropertyChanged += ViewModelChanged;
    }

#if DEBUG
    /// <summary>読み取りが Paste イベント外で行われることを確認する Debug 専用コールバック。</summary>
    internal Action? BeforeClipboardReadForVerification { get; set; }
#endif

    /// <summary>既定の貼り付けを抑止し、クリップボードに触れずに要求を UI キューへ送る。</summary>
    /// <remarks>UI スレッド上でも同期実行しない。XAML の再入禁止区間での COM 呼び出しによるクラッシュを避ける。</remarks>
    /// <param name="sender">通知元のオブジェクト。</param>
    /// <param name="args">イベントまたは起動の引数。</param>
    private void Paste(object sender, TextControlPasteEventArgs args)
    {
        // Clipboard の COM 呼び出しはメッセージをポンプすることがある。
        // XAML の Paste イベント内では GetContent / Contains すら呼ばない。
        // try/catch で回復できない stowed exception を避けるため必ず次の UI キューへ送る。
        args.Handled = true;
        if (_disposed) return;
        _pendingPastes++;
        SchedulePaste();
    }

    /// <summary>未処理の要求を一つだけ UI キューへ予約し、貼り付け処理を直列化する。</summary>
    private void SchedulePaste()
    {
        if (_disposed || _pasteScheduled || _processingPaste || _pendingPastes == 0) return;
        _pasteScheduled = true;
        if (!_input.DispatcherQueue.TryEnqueue(() =>
        {
            _pasteScheduled = false;
            _ = ProcessNextPasteAsync();
        }))
        {
            _pasteScheduled = false;
            _pendingPastes = 0;
        }
    }

    /// <summary>画像またはテキストを読み取り、編集の競合と破棄状態を確認して反映する。</summary>
    /// <returns>操作の完了を表すタスク。</returns>
    private async Task ProcessNextPasteAsync()
    {
        if (_disposed || _processingPaste || _pendingPastes == 0) return;
        _pendingPastes--;
        _processingPaste = true;
        var preparingImage = false;
        try
        {
            if (!_viewModel.CanEdit || !_input.IsEnabled || _input.IsReadOnly) { _pendingPastes = 0; return; }
            var originalText = _input.Text;
            var selectionStart = _input.SelectionStart;
            var selectionLength = _input.SelectionLength;
#if DEBUG
            BeforeClipboardReadForVerification?.Invoke();
#endif
            var content = Clipboard.GetContent();
            if (_disposed || !_viewModel.CanEdit) return;
            _error.IsOpen = false;
            if (content.Contains(StandardDataFormats.Bitmap))
            {
                if (!_viewModel.TryBeginImagePaste()) return;
                preparingImage = true;
                var image = await ClipboardImageService.ReadAsync(content, _lifetime.Token);
                if (!_disposed) _viewModel.CompleteImagePaste(image);
            }
            else if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync().AsTask(_lifetime.Token);
                if (_disposed || !_viewModel.CanEdit || !_input.IsEnabled || _input.IsReadOnly) return;
                // 遅いクリップボード提供元を待つ間に利用者が編集した場合、その入力を上書きしない。
                if (_input.Text != originalText || _input.SelectionStart != selectionStart || _input.SelectionLength != selectionLength)
                {
                    _pendingPastes = 0;
                    ShowError("入力位置が変更されたため貼り付けを中止しました。もう一度貼り付けてください。");
                    return;
                }
                InsertText(text, originalText.Length, selectionStart, selectionLength);
            }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception)
        {
            Trace.TraceError("Clipboard paste failed: type={0}, HRESULT=0x{1:X8}\n{2}",
                exception.GetType().FullName, exception.HResult, exception.StackTrace);
            if (!_disposed) ShowError($"クリップボードの内容を貼り付けられませんでした。{exception.Message}");
        }
        finally
        {
            if (preparingImage && !_disposed) _viewModel.CompleteImagePaste(null);
            _processingPaste = false;
            SchedulePaste();
        }
    }

    /// <summary>選択範囲へテキストを挿入し、文字数上限・サロゲートペア・Undo・カーソルを扱う。</summary>
    /// <param name="text">選択範囲に挿入するテキスト。</param>
    /// <param name="previousLength">読み取り開始時の入力文字列の UTF-16 コード単位数。</param>
    /// <param name="selectionStart">置換する選択範囲の開始位置。UTF-16 コード単位で指定する。</param>
    /// <param name="selectionLength">置換する選択範囲の UTF-16 コード単位数。</param>
    private void InsertText(string text, int previousLength, int selectionStart, int selectionLength)
    {
        if (_input.MaxLength > 0)
        {
            var available = Math.Max(0, _input.MaxLength - (previousLength - selectionLength));
            if (text.Length > available)
            {
                if (available > 0 && char.IsHighSurrogate(text[available - 1])) available--;
                text = text[..available];
            }
        }
        // Text 全体の置き換えではなく選択範囲へ挿入し、Undo とカーソル位置を維持する。
        _input.SelectedText = text;
        var insertedLength = _input.Text.Length - (previousLength - selectionLength);
        _input.Select(Math.Clamp(selectionStart + insertedLength, 0, _input.Text.Length), 0);
    }

    /// <summary>添付変更に追従してプレビューを作り、古い画像の完了結果を破棄する。</summary>
    /// <param name="sender">通知元のオブジェクト。</param>
    /// <param name="args">イベントまたは起動の引数。</param>
    private async void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || args.PropertyName != nameof(ChatViewModel.PendingImage)) return;
        var version = ++_previewVersion;
        var image = _viewModel.PendingImage;
        try
        {
            _preview.Source = null;
            _panel.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
            if (image is null) return;
            var source = await ClipboardImageService.CreatePreviewAsync(image, _lifetime.Token);
            if (!_disposed && version == _previewVersion && ReferenceEquals(image, _viewModel.PendingImage)) _preview.Source = source;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception) { if (!_disposed) ShowError($"画像プレビューを表示できませんでした。{exception.Message}"); }
    }
    /// <summary>貼り付けまたはプレビューの失敗を入力欄付近へ通知する。</summary>
    /// <param name="message">画面へ表示する説明。</param>
    private void ShowError(string message) { _error.Message = message; _error.IsOpen = true; }
    /// <summary>通知購読を解除して画像処理を中止し、予約済みの貼り付けを無効にする。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pendingPastes = 0;
        _input.Paste -= Paste;
        _viewModel.PropertyChanged -= ViewModelChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _preview.Source = null;
    }
}
