using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Desktop.Presentation;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Launcher = Windows.System.Launcher;

namespace Ateliers.Ai.Assistant.Desktop.Controls;

/// <summary>会話表示専用。WebView2 はこのコントロールに一つだけ保持する。</summary>
public sealed partial class ChatTranscriptView : UserControl, IDisposable
{
    /// <summary>表示対象のメッセージ一覧をバインドする依存関係プロパティ。</summary>
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource),
        typeof(IEnumerable<ChatMessageViewModel>), typeof(ChatTranscriptView),
        new PropertyMetadata(null, (owner, _) => ((ChatTranscriptView)owner).ChangeSource()));
    /// <summary>変更通知を監視して表示するメッセージ一覧を取得または設定する。</summary>
    public IEnumerable<ChatMessageViewModel>? ItemsSource
    {
        get => (IEnumerable<ChatMessageViewModel>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    private readonly Dictionary<ChatMessageViewModel, string> _ids = new();
    private readonly TranscriptProjection _projection = new();
    private readonly TranscriptRenderCache _cache = new();
    private readonly TaskCompletionSource<bool> _initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherQueueTimer _timer;
    private INotifyCollectionChanged? _source;
    private long _revision;
    private bool _started;
    private bool _ready;
    private bool _dirty;
    private bool _rendering;
    private bool _urgent;
    private bool _disposed;

    /// <summary>会話全体で使う WebView2 と100msの更新集約タイマーを初期化する。</summary>
    public ChatTranscriptView()
    {
        InitializeComponent();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _ = RenderAsync();
        Loaded += async (_, _) => await InitializeBrowserAsync();
        ActualThemeChanged += (_, _) => Invalidate(urgent: true);
    }

    /// <summary>ページ準備完了なら true、初期化失敗・準備前の破棄なら false で完了するタスクを取得する。</summary>
    internal Task<bool> Ready => _initialization.Task;
#if DEBUG
    /// <summary>初期化後のブラウザーを、Debug の実画面検証に公開する。</summary>
    internal CoreWebView2 CoreForVerification => Browser.CoreWebView2;
    /// <summary>既定ブラウザー起動を置き換える Debug 専用の検証用デリゲートを取得または設定する。</summary>
    internal Func<Uri, Task<bool>>? VerificationLinkLauncher { get; set; }
#endif

    /// <summary>旧一覧の購読を解除し、新しい一覧の監視と会話世代のリセットを行う。</summary>
    private void ChangeSource()
    {
        if (_source is not null) _source.CollectionChanged -= CollectionChanged;
        _source = ItemsSource as INotifyCollectionChanged;
        if (_source is not null) _source.CollectionChanged += CollectionChanged;
        ReconcileSubscriptions();
        ResetConversation();
    }
    /// <summary>一覧変更に合わせて購読を更新し、リセットまたは優先描画を要求する。</summary>
    /// <param name="sender">通知元のオブジェクト。</param>
    /// <param name="args">イベントまたは起動の引数。</param>
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        ReconcileSubscriptions();
        if (args.Action == NotifyCollectionChangedAction.Reset) ResetConversation();
        else Invalidate(urgent: true);
    }
    /// <summary>現在の一覧との差分に応じて、各メッセージの変更通知を購読・解除する。</summary>
    private void ReconcileSubscriptions()
    {
        var current = (ItemsSource ?? Array.Empty<ChatMessageViewModel>()).ToHashSet();
        foreach (var message in _ids.Keys.Where(message => !current.Contains(message)).ToArray())
        {
            message.PropertyChanged -= MessageChanged;
            _ids.Remove(message);
        }
        foreach (var message in current.Where(message => !_ids.ContainsKey(message)))
        {
            _ids.Add(message, Guid.NewGuid().ToString("N"));
            message.PropertyChanged += MessageChanged;
        }
    }
    /// <summary>本文更新を集約し、状態変更を優先して表示へ反映する。</summary>
    /// <param name="sender">通知元のオブジェクト。</param>
    /// <param name="args">イベントまたは起動の引数。</param>
    private void MessageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ChatMessageViewModel.Content) or nameof(ChatMessageViewModel.StatusText) or nameof(ChatMessageViewModel.DetailText))
            Invalidate(urgent: args.PropertyName != nameof(ChatMessageViewModel.Content));
    }
    /// <summary>会話の世代を進めて旧会話の変換結果を無効にし、最新内容を描画する。</summary>
    private void ResetConversation()
    {
        _projection.Reset();
        if (_ready) Publish(_projection.CreatePatch(_projection.Generation, ++_revision,
            Array.Empty<RenderedMessage>(), ActualTheme == ElementTheme.Dark));
        Invalidate(urgent: true);
    }
    /// <summary>未反映状態を記録し、ページ準備後の描画を即時またはタイマーで予約する。</summary>
    /// <param name="urgent">100ms の集約待ちを省いて更新を要求する場合は true。</param>
    private void Invalidate(bool urgent)
    {
        if (_disposed) return;
        ++_revision;
        _dirty = true;
        _urgent |= urgent;
        if (FallbackText.Visibility == Visibility.Visible) UpdateFallback();
        if (!_ready) return; // ready の受信時に最新スナップショットを反映する。
        if (urgent) { _timer.Stop(); _ = RenderAsync(); }
        else if (!_timer.IsRunning) _timer.Start();
    }
    /// <summary>UI 上で取得したスナップショットを背景で変換し、世代を確認して差分を送る。</summary>
    /// <remarks>変換を同時実行しない。初期化前の変更は保持し、変換失敗時は原文表示へ移行する。</remarks>
    /// <returns>操作の完了を表すタスク。</returns>
    private async Task RenderAsync()
    {
        if (_disposed || !_ready || _rendering || !_dirty) return;
        _rendering = true;
        _dirty = false;
        _urgent = false;
        var generation = _projection.Generation;
        var revision = _revision;
        var snapshot = (ItemsSource ?? Array.Empty<ChatMessageViewModel>()).Select(message => new MessageSnapshot(
            _ids[message], message.Content, message.Speaker, message.DetailText, message.IsUser,
            message.StatusText is not ("応答待ち…" or "受信中…"), message.Image)).ToArray();
        try
        {
            // 単一の worker だけがキャッシュに触れる。解析・安全化で UI スレッドを占有しない。
            var rendered = await Task.Run(() => _cache.Render(snapshot));
            if (!_disposed && _ready) Publish(_projection.CreatePatch(generation, revision, rendered, ActualTheme == ElementTheme.Dark));
        }
        catch (Exception exception) { ShowFailure($"Markdown 表示に失敗したため原文を表示します。{exception.Message}"); }
        finally
        {
            _rendering = false;
            if (_dirty && !_disposed && _ready)
            {
                if (_urgent) _ = RenderAsync();
                else if (!_timer.IsRunning) _timer.Start();
            }
        }
    }
    /// <summary>有効な表示差分を JSON として送信し、モデル出力をスクリプトへ連結しない。</summary>
    /// <param name="patch">送信する差分。古い結果が拒否された場合は null。</param>
    private void Publish(TranscriptPatch? patch)
    {
        if (patch is null || !_ready || _disposed) return;
        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(patch, TranscriptJsonContext.Default.TranscriptPatch));
    }
    /// <summary>ブラウザーを一度だけ初期化し、同梱資産と許可した操作だけに表示を制限する。</summary>
    /// <returns>操作の完了を表すタスク。</returns>
    private async Task InitializeBrowserAsync()
    {
        if (_started || _disposed) return;
        _started = true;
        try
        {
            await Browser.EnsureCoreWebView2Async();
            if (_disposed) return;
            var core = Browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.NavigationStarting += (_, args) => args.Cancel = args.Uri != TranscriptProtocol.Document;
            core.FrameNavigationStarting += (_, args) => args.Cancel = true;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (!TranscriptProtocol.IsAllowedResource(args.Request.Uri) || args.Request.Method != "GET")
                    args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "Content-Type: text/plain");
            };
            core.WebMessageReceived += WebMessageReceived;
            core.ProcessFailed += (_, _) => ShowFailure("WebView2 の表示プロセスが終了しました。原文表示で継続できます。");
            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    ShowFailure($"チャット表示ページを読み込めませんでした ({args.WebErrorStatus})。");
            };
            core.SetVirtualHostNameToFolderMapping(TranscriptProtocol.Host,
                Path.Combine(AppContext.BaseDirectory, "Assets", "ChatTranscript"), CoreWebView2HostResourceAccessKind.Deny);
            core.Navigate(TranscriptProtocol.Document);
            await Ready.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception exception) { ShowFailure($"WebView2 を初期化できません。Runtime の導入状況を確認してください。{exception.Message}"); }
    }
    /// <summary>送信元・内容を検証し、ページ準備通知または明示クリックのリンクを処理する。</summary>
    /// <param name="sender">通知元のオブジェクト。</param>
    /// <param name="args">イベントまたは起動の引数。</param>
    private async void WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_disposed || args.Source != TranscriptProtocol.Document || sender.Source != TranscriptProtocol.Document) return;
        try
        {
            if (args.WebMessageAsJson.Length > 8192) return;
            var message = JsonSerializer.Deserialize(args.WebMessageAsJson, TranscriptJsonContext.Default.TranscriptWebMessage);
            if (message?.Type == "ready")
            {
                _ready = true;
                _initialization.TrySetResult(true);
                ResetConversation();
            }
            else if (message?.Type == "link" && ExternalLinkPolicy.TryGetUri(message.Href, out var uri))
            {
                var launched = false;
#if DEBUG
                if (VerificationLinkLauncher is not null) launched = await VerificationLinkLauncher(uri!);
                else
#endif
                    launched = await Launcher.LaunchUriAsync(uri!);
                if (!launched)
                {
                    DisplayError.Message = "外部リンクを開けませんでした。";
                    DisplayError.IsOpen = true;
                }
            }
        }
        catch (JsonException) { /* 想定外の Web メッセージは実行しない。 */ }
        catch (Exception exception)
        {
            DisplayError.Message = $"表示からの操作に失敗しました。{exception.Message}";
            DisplayError.IsOpen = true;
        }
    }
    /// <summary>Markdown 表示を停止し、エラーと原文フォールバックを表示する。</summary>
    /// <param name="message">画面へ表示する説明。</param>
    private void ShowFailure(string message)
    {
        if (_disposed) return;
        _ready = false;
        _timer.Stop();
        _initialization.TrySetResult(false);
        DisplayError.Message = message;
        DisplayError.IsOpen = true;
        Browser.Visibility = Visibility.Collapsed;
        FallbackText.Visibility = Visibility.Visible;
        UpdateFallback();
    }
    /// <summary>会話原文と添付サイズをネイティブのフォールバック表示へ反映する。</summary>
    private void UpdateFallback() => FallbackText.Text = string.Join("\n\n", (ItemsSource ?? Array.Empty<ChatMessageViewModel>())
        .Select(message => $"{message.Speaker}\n{(message.Image is null ? "" : $"[添付画像 {message.Image.Width} × {message.Image.Height} px]\n")}{message.Content}\n{message.DetailText}"));

    /// <summary>変更通知とタイマーを解除し、WebView2 を閉じて旧非同期結果を無効にする。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready = false;
        _timer.Stop();
        if (_source is not null) _source.CollectionChanged -= CollectionChanged;
        foreach (var message in _ids.Keys) message.PropertyChanged -= MessageChanged;
        _ids.Clear();
        _initialization.TrySetResult(false);
        Browser.Close();
    }
}
