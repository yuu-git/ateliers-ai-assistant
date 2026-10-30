using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.ScreenAnalysis;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Core.Chat;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>一枚のキャプチャと一回の解析だけをメモリ上に保持する。Windows・HTTP・DB に依存しない。</summary>
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public sealed partial class ScreenAnalysisViewModel : ObservableObject, IDisposable
{
    private readonly IScreenCaptureService _capture;
    private readonly ScreenAnalysisService _analysis;
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _operation;
    private Task _running = Task.CompletedTask;
    private bool _busy, _analyzing, _disposed, _allowed, _previewReady;
    private string _prompt = "この画面の内容を日本語で説明し、気付いた点を教えてください。";
    private string? _model;
    private string _status = "対象を選択し、手動でキャプチャしてください。", _error = "";
    private ChatImage? _image;
    /// <summary>ポートと UI スレッドへの移送方法を接続する。</summary>
    public ScreenAnalysisViewModel(IScreenCaptureService capture, ScreenAnalysisService analysis, Action<Action> dispatch)
    {
        _capture = capture; _analysis = analysis; _dispatch = dispatch;
        PickCommand = new(() => RunAsync(PickAsync), () => CanEdit);
        CaptureCommand = new(() => RunAsync(CaptureAsync), () => CanEdit && _capture.TargetName is not null);
        RefreshModelsCommand = new(() => RunAsync(RefreshModelsAsync), () => CanEdit);
        AnalyzeCommand = new(() => RunAsync(AnalyzeAsync), () => CanEdit && _allowed && _previewReady && Image is not null && !string.IsNullOrWhiteSpace(SelectedModel) && !string.IsNullOrWhiteSpace(Prompt));
        CancelCommand = new(() => { _operation?.Cancel(); return Task.CompletedTask; }, () => IsBusy);
        ClearCommand = new(() => { Image = null; Results.Clear(); Status = "画像と解析結果を破棄しました。"; return Task.CompletedTask; }, () => CanEdit && (Image is not null || Results.Count > 0));
        _capture.TargetClosed += TargetClosed;
    }
    /// <summary>対象選択。</summary>
    public AsyncCommand PickCommand { get; }
    /// <summary>選択対象から一枚取得。</summary>
    public AsyncCommand CaptureCommand { get; }
    /// <summary>インストール済みモデル一覧を再取得。</summary>
    public AsyncCommand RefreshModelsCommand { get; }
    /// <summary>プレビュー中の画像だけを解析。</summary>
    public AsyncCommand AnalyzeCommand { get; }
    /// <summary>進行中操作を中止。</summary>
    public AsyncCommand CancelCommand { get; }
    /// <summary>メモリ内の画像・結果を破棄。</summary>
    public AsyncCommand ClearCommand { get; }
    /// <summary>モデルの能力は名前で判定しない。非対応時は API エラーを表示。</summary>
    public ObservableCollection<string> Models { get; } = [];
    /// <summary>履歴として蓄積しない今回の解析結果。既存 Markdown コントロールへ渡す。</summary>
    public ObservableCollection<ChatMessageViewModel> Results { get; } = [];
    /// <summary>OS が公開する対象名。種類は推測しない。</summary>
    public string TargetName => _capture.TargetName ?? "対象未選択";
    /// <summary>キャプチャの進行を含む操作中表示。</summary>
    public bool IsBusy => _busy;
    /// <summary>編集中の値・対象を実行中に変更させない。</summary>
    public bool CanEdit => !_busy && !_disposed;
    /// <summary>画面に示す進行状態。</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    /// <summary>利用者向け失敗理由。画像・プロンプトはログへ送らない。</summary>
    public string Error { get => _error; private set { SetProperty(ref _error, value); OnPropertyChanged(nameof(HasError)); } }
    /// <summary>エラーの表示要否。</summary>
    public bool HasError => Error.Length > 0;
    /// <summary>最後に取得した Worker の実状態に基づく案内。</summary>
    public string AiAvailability => _allowed ? "解析操作で選択モデルをロードする場合があります。" : "解析には Worker への接続と AI の再開が必要です。キャプチャは利用できます。";
    /// <summary>毎回送信する一般的な画面説明の依頼。</summary>
    public string Prompt { get => _prompt; set { if (SetProperty(ref _prompt, value)) Notify(); } }
    /// <summary>チャットとは独立したモデル選択。選択だけではロード・停止しない。</summary>
    public string? SelectedModel { get => _model; set { if (SetProperty(ref _model, value)) Notify(); } }
    /// <summary>プレビューと送信で同一参照を使う正規化 PNG。</summary>
    public ChatImage? Image { get => _image; private set { if (ReferenceEquals(_image, value)) return; _previewReady = false; if (SetProperty(ref _image, value)) { OnPropertyChanged(nameof(ImageInfo)); Notify(); } } }
    /// <summary>送信する画像の実寸。</summary>
    public string ImageInfo => Image is { } image ? $"送信画像: {image.Width} × {image.Height} px / {image.ByteLength / 1024:N0} KiB（メモリ内のみ）" : "画像未取得";
    /// <summary>表示層が同じ画像のデコード完了を通知する。表示できなければ解析を許可しない。</summary>
    public void CompletePreview(ChatImage image, bool succeeded)
    {
        if (_disposed || !ReferenceEquals(image, Image)) return;
        _previewReady = succeeded;
        if (!succeeded) Error = "プレビューを表示できませんでした。もう一度キャプチャしてください。";
        Notify();
    }
    /// <summary>既存ポーリングが得た状態を反映する。編集中入力は上書きしない。</summary>
    public void UpdateAvailability(bool allowed)
    {
        _allowed = allowed;
        if (!allowed && _analyzing) _operation?.Cancel();
        OnPropertyChanged(nameof(AiAvailability)); Notify();
    }
    /// <summary>取得済み画像と現在対象を区別し、対象終了を UI スレッドへ通知する。</summary>
    private void TargetClosed(object? sender, EventArgs args) => _dispatch(() =>
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(TargetName));
        // 取得済み画像の解析は対象の生存に依存しない。取得中だけ中止する。
        if (!_analyzing) _operation?.Cancel();
        Error = "対象が閉じられました。必要なら選択し直してください。取得済み画像は保持しています。"; Notify();
    });
    /// <summary>終了処理から待機できる現在の操作タスクを記録する。</summary>
    private Task RunAsync(Func<CancellationToken, Task> work) => _running = RunCoreAsync(work);
    /// <summary>操作の排他・中止・失敗表示を統一し、画像や入力内容をログへ送らない。</summary>
    private async Task RunCoreAsync(Func<CancellationToken, Task> work)
    {
        if (!CanEdit) return;
        _busy = true; Error = ""; using var cancel = new CancellationTokenSource(); _operation = cancel; Notify();
        try { await work(cancel.Token); }
        catch (OperationCanceledException) { if (!_disposed) Status = "中止しました。途中の解析結果は保持しています。"; }
        catch (Exception exception)
        {
            if (!_disposed) { Status = "失敗しました。"; Error = exception is ChatServiceException or WorkerException or InvalidOperationException ? exception.Message : "操作に失敗しました。対象・OS の制限と接続状態を確認してください。"; }
        }
        finally { _operation = null; if (_analyzing && Results.Count > 0) Results[0].StatusText = Status; _analyzing = false; _busy = false; Notify(); }
    }
    /// <summary>選択を確定した時だけ前の画像・結果を破棄する。</summary>
    private async Task PickAsync(CancellationToken token)
    {
        Status = "対象を選択しています。";
        if (await _capture.PickAsync(token)) { Image = null; Results.Clear(); OnPropertyChanged(nameof(TargetName)); Status = "対象を選択しました。「キャプチャ」で取得してください。"; }
        else Status = "対象の選択をキャンセルしました。";
    }
    /// <summary>取得成功時だけ前の画像と交換する。</summary>
    private async Task CaptureAsync(CancellationToken token)
    {
        Status = "キャプチャ中…";
        var image = await _capture.CaptureAsync(token); token.ThrowIfCancellationRequested();
        if (_disposed) return;
        Image = image; Results.Clear(); Status = "画像を確認してから解析してください。";
    }
    /// <summary>一覧更新後も存在する選択を維持し、自動ロードしない。</summary>
    private async Task RefreshModelsAsync(CancellationToken token)
    {
        Status = "モデル一覧を取得中…";
        var models = await _analysis.GetModelsAsync(token); token.ThrowIfCancellationRequested();
        if (_disposed) return;
        var selected = SelectedModel; Models.Clear(); foreach (var model in models) Models.Add(model);
        SelectedModel = selected is not null && Models.Contains(selected) ? selected : null;
        Status = Models.Count > 0 ? "画像入力に対応するモデルを選択してください。能力の自動判定は行いません。" : "モデルがありません。Ollama 側で用意してください。";
    }
    /// <summary>今回の入力を固定して実行し、古いコールバックの混入を防いで最終内容を反映する。</summary>
    private async Task AnalyzeAsync(CancellationToken token)
    {
        var image = Image!; var model = SelectedModel!; var prompt = Prompt;
        _analyzing = true; Status = "解析中…";
        Results.Clear(); var result = new ChatMessageViewModel(new(ChatRole.Assistant, ""), model, "受信中…"); Results.Add(result);
        // 内容は同じ結果オブジェクトへ追記する。次の解析へ遅いコールバックを混入させない。
        var sync = new object(); var buffer = ""; var finished = false;
        void Flush()
        {
            lock (sync)
            {
                if (_disposed || finished || !Results.Contains(result)) return;
                if (buffer.Length > result.Content.Length) result.Append(buffer[result.Content.Length..]);
            }
        }
        try { await _analysis.AnalyzeAsync(model, prompt, image, chunk => { lock (sync) buffer += chunk; _dispatch(Flush); }, token); Status = "解析が完了しました。"; }
        finally { Flush(); lock (sync) finished = true; }
    }
    /// <summary>終了前に中止して完了を待つ。通常終了ではリソース解放より先に呼ぶ。</summary>
    public async Task StopAsync() { _operation?.Cancel(); await _running.WaitAsync(TimeSpan.FromSeconds(12)); }
    /// <summary>編集中・実行中の派生表示と各操作の実行可否を更新する。</summary>
    private void Notify()
    {
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanEdit));
        PickCommand.NotifyCanExecuteChanged(); CaptureCommand.NotifyCanExecuteChanged(); RefreshModelsCommand.NotifyCanExecuteChanged();
        AnalyzeCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); ClearCommand.NotifyCanExecuteChanged();
    }
    /// <inheritdoc/>
    public void Dispose() { if (_disposed) return; _disposed = true; _operation?.Cancel(); _capture.TargetClosed -= TargetClosed; _capture.Dispose(); Image = null; Results.Clear(); }
}
