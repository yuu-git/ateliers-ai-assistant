using Microsoft.UI.Xaml;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Application.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Desktop.Images;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace Ateliers.Ai.Assistant.Desktop;

/// <summary>チャットとモデル管理の画面を接続し、期限付きの非同期終了を調整するウィンドウ。</summary>
public sealed partial class MainWindow : Window
{
    /// <summary>チャット画面のバインド先を取得する。</summary>
    public ChatViewModel ViewModel { get; }
    /// <summary>モデル管理画面のバインド先を取得する。</summary>
    public ModelManagementViewModel ModelViewModel { get; }
    /// <summary>固定保存先とユーザー設定の表示・操作。</summary>
    public SettingsViewModel SettingsViewModel { get; }
    /// <summary>Web 収集・巡回履歴・通知のバインド先。既存診断ホストでは省略できる。</summary>
    public WebMonitoringViewModel? WebViewModel { get; }
    /// <summary>ユーザーセッション Worker の設定と状態。</summary>
    public WorkerViewModel? WorkerViewModel { get; }
    /// <summary>Worker 停止中も利用できる保存済みログ。</summary>
    public LogsViewModel? LogsViewModel { get; }
    /// <summary>手動キャプチャと画像一枚の解析。通常ホストだけで構成する。</summary>
    public ScreenAnalysisViewModel? ScreenViewModel { get; private set; }
    /// <summary>Worker 所有の継続監視。UI を閉じても停止しない。</summary>
    public ScreenMonitorViewModel? MonitorViewModel { get; private set; }
    /// <summary>監視表示だけを接続する。終了時は表示資源を解放する。</summary>
    public void InitializeScreenMonitor(ScreenMonitorViewModel viewModel)
    {
        MonitorViewModel = viewModel; MonitorPanel.Initialize(viewModel);
        Closed += (_, _) => { viewModel.Dispose(); MonitorPanel.Dispose(); };
    }
    /// <summary>Host が所有する一時データを通常終了前に解放する。清掃失敗で終了を妨げない。</summary>
    public Func<Task>? ReleaseTemporaryDataAsync { get; set; }
    /// <summary>ウィンドウ生成後に HWND を必要とするキャプチャサービスを接続する。</summary>
    public void InitializeScreenAnalysis(ScreenAnalysisViewModel viewModel)
    {
        ScreenViewModel = viewModel; ScreenPanel.Initialize(viewModel);
        if (WorkerViewModel is not null) WorkerViewModel.PropertyChanged += WorkerAvailabilityChanged;
        WorkerAvailabilityChanged(this, new PropertyChangedEventArgs(null));
        Closed += (_, _) => { if (WorkerViewModel is not null) WorkerViewModel.PropertyChanged -= WorkerAvailabilityChanged; ScreenPanel.Dispose(); viewModel.Dispose(); };
    }
    private void WorkerAvailabilityChanged(object? sender, PropertyChangedEventArgs args) =>
        ScreenViewModel?.UpdateAvailability(WorkerViewModel is { Connected: true, ActualAiPaused: false });
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _workerTimer;
    private readonly IModelLifecycleService _lifecycle;
    private bool _closing;
    private bool _allowClose;
    private readonly ClipboardImageController _images;
    private bool _navigationBusy;
    private bool _initializing = true;
    private bool _articleDeletionDialogOpen;
    private object? _previousNavigation;
#if DEBUG
    private readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>実画面の診断で初期化完了を待つ。</summary>
    internal Task InitializedForVerification => _initialized.Task;
    /// <summary>UI 整理の実画面検証用。</summary>
    internal Controls.WebMonitoringView WebForVerification => ModernWeb;
    /// <summary>固定フッターの実バインドを検証する。</summary>
    internal Controls.WorkerStatusView GlobalStatusForVerification => GlobalWorkerStatus;
    /// <summary>全通知を集計した実ナビゲーションバッジ。</summary>
    internal int UnreadCountForVerification => UnreadBadge.Visibility == Visibility.Visible ? UnreadBadge.Value : -1;
    /// <summary>既存の ID による画面遷移を検証する。</summary>
    internal void NavigateForVerification(string tag) => MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>().First(item => (string?)item.Tag == tag);
    /// <summary>編集可能な実設定コントロール。</summary>
    internal Controls.WorkerOptionsView WorkerOptionsForVerification => WorkerOptions;
    /// <summary>保存先の表示を含め、設定画面の縦スクロールを検証する。</summary>
    internal void ScrollSettingsForVerification(bool bottom) => SettingsPanel.ChangeView(null, bottom ? double.MaxValue : 0, null, true);
    /// <summary>実画面の診断で Markdown と画像の DOM を確認する。</summary>
    internal Controls.ChatTranscriptView TranscriptForVerification => Transcript;
    /// <summary>継続監視の実表示を通常終了処理と合わせて検証する。</summary>
    internal Controls.ScreenMonitorView MonitorForVerification => MonitorPanel;
    /// <summary>通知の原文・安全化・選択変更を実 WebView2 で検証する。</summary>
    internal Controls.ChatTranscriptView NotificationPreviewForVerification => WorkerViewModel is null ? NotificationPreview : ModernWeb.PreviewForVerification;
    /// <summary>実 ListView の選択を通して通知本文の表示を確認する。</summary>
    internal void SelectNotificationForVerification(int index) { if (WorkerViewModel is null) WebNotificationList.SelectedIndex = index; else ModernWeb.SelectForVerification(index); }
    /// <summary>隔離した実画面検証で Web 画面を表示する。</summary>
    internal void ShowWebForVerification() => MainNavigation.SelectedItem = WebNavigationItem;
    /// <summary>Worker 接続・周期入力の実コントロールを検証する。</summary>
    internal Task ConfigureSourceForVerificationAsync(bool github = false) => ModernWeb.ConfigureForVerificationAsync(github);
    /// <summary>ログ画面と実リストの複数選択を検証する。</summary>
    internal void ShowLogsForVerification() => MainNavigation.SelectedItem = LogsNavigationItem;
    /// <summary>実 ListView の選択から詳細表示・コピーを検証する。</summary>
    internal void SelectLogsForVerification()
    { LogList.SelectedItems.Clear(); foreach (var item in LogsViewModel!.Entries.Take(2)) LogList.SelectedItems.Add(item); }
    /// <summary>描画済みログ件数と詳細。</summary>
    internal (int Count, string Detail) LogsForVerification => (LogList.Items.Count, LogDetails.Text);
    /// <summary>入力の TwoWay バインディングを検証する。</summary>
    internal void SetWebInputForVerification(string url, string model) { WebFeedUrlInput.Text = url; WebModelInput.SelectedItem = model; }
    /// <summary>実際の ListView に反映された要素数を返す。</summary>
    internal (int Runs, int Notifications) WebCountsForVerification => (WebRunList.Items.Count, WebNotificationList.Items.Count);
#endif

    /// <summary>注入された ViewModel とモデル管理サービスを使って画面を初期化する。</summary>
    /// <param name="viewModel">入力・添付の状態を保持するチャット ViewModel。</param>
    /// <param name="modelViewModel">ロード一覧と手動停止の状態を保持する ViewModel。</param>
    /// <param name="lifecycle">チャットとモデル管理で共有し、終了時に解放するサービス。</param>
    /// <param name="settingsViewModel">固定保存先の表示とモデル選択設定を担当する ViewModel。</param>
    /// <param name="webViewModel">Web 情報収集の操作と結果表示。既存の隔離検証では省略できる。</param>
    /// <param name="workerViewModel">独立 Worker の運転設定。旧表示の隔離検証では省略する。</param>
    /// <param name="logsViewModel">API から独立した永続ログの表示とコピー。</param>
    public MainWindow(ChatViewModel viewModel, ModelManagementViewModel modelViewModel, IModelLifecycleService lifecycle,
        SettingsViewModel settingsViewModel, WebMonitoringViewModel? webViewModel = null, WorkerViewModel? workerViewModel = null, LogsViewModel? logsViewModel = null)
    {
        ViewModel = viewModel;
        ModelViewModel = modelViewModel;
        _lifecycle = lifecycle;
        SettingsViewModel = settingsViewModel;
        WebViewModel = webViewModel;
        WorkerViewModel = workerViewModel; LogsViewModel = logsViewModel;
        InitializeComponent();
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Branding", "app.ico"));
        if (SettingsViewModel.Paths.DataEnvironment == Ateliers.Ai.Assistant.Application.Storage.AppDataEnvironment.Debug)
            Title = "Ateliers AI Assistant (Debug)";
        DataStorage.Initialize(SettingsViewModel);
        _previousNavigation = ChatNavigationItem;
        ViewModel.ConfirmLeaveAsync = ConfirmLeaveAsync;
        ViewModel.RequestConversationTitleAsync = RequestTitleAsync;
        ViewModel.ConfirmDeleteConversationAsync = ConfirmDeleteAsync;
        SettingsViewModel.ApplySelectedModelAsync = ViewModel.RestoreSelectedModelAsync;
        if (WebViewModel is not null && WorkerViewModel is null) SettingsViewModel.ApplyWebSettings = WebViewModel.ApplySettings;
        if (WebViewModel is not null) { WebViewModel.RequestArticleDeletionAsync = SelectArticlesToDeleteAsync; WebViewModel.ConfirmSingleArticleDeletionAsync = ConfirmSingleArticleDeletionAsync; WebViewModel.PropertyChanged += NotificationPropertyChanged; }
        if (WorkerViewModel is not null)
        {
            WorkerViewModel.ConfirmDeleteSourceAsync = name => ConfirmDeleteSourceAsync(name);
            WorkerViewModel.ConfirmDiscardEditsAsync = async () => await new ContentDialog { XamlRoot = ContentRoot.XamlRoot,
                Title = "設定を再読み込み", Content = "未保存の情報源・AI 設定の編集値を保存済みの値へ戻しますか？", PrimaryButtonText = "再読み込み", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary;
            WorkerOptions.DataContext = WorkerViewModel;
            GlobalWorkerStatus.Initialize(WorkerViewModel, SettingsViewModel.DataEnvironmentText); GlobalWorkerStatus.Visibility = Visibility.Visible;
            WorkerOptions.Visibility = ModernWeb.Visibility = Visibility.Visible;
            LegacyWebPanel.Visibility = Visibility.Collapsed;
            // 非表示の旧一覧が ItemsSource 更新で現行画面の選択を消さないよう切り離す。
            WebNotificationList.ClearValue(ListView.SelectedItemProperty);
            WebNotificationList.SelectionMode = ListViewSelectionMode.None;
            NotificationPreview.RenderingEnabled = false;
            if (WebViewModel is not null)
            {
                ModernWeb.Initialize(WebViewModel, WorkerViewModel);
                WorkerViewModel.SourcesUpdated = WebViewModel.UpdateSources;
                ModernWeb.NavigateToSettings = () => MainNavigation.SelectedItem = MainNavigation.MenuItems.OfType<NavigationViewItem>().First(item => (string?)item.Tag == "Settings");
            }
            _workerTimer = DispatcherQueue.CreateTimer(); _workerTimer.Interval = TimeSpan.FromSeconds(5);
            _workerTimer.Tick += async (_, _) => { await WorkerViewModel.PollAsync(); if (MonitorViewModel is not null) await MonitorViewModel.PollAsync(); };
        }
        else ModernWeb.PreviewControl.RenderingEnabled = false;
        ViewModel.PropertyChanged += ChatPropertyChanged;
        _images = new ClipboardImageController(ChatInput, AttachmentPreview, ImagePreviewPanel, ImagePasteError, ViewModel);
        AppWindow.Closing += Window_Closing;
        Closed += (_, _) => { _workerTimer?.Stop(); WorkerViewModel?.Dispose(); ViewModel.PropertyChanged -= ChatPropertyChanged; if (WebViewModel is not null) WebViewModel.PropertyChanged -= NotificationPropertyChanged; _images.Dispose(); ViewModel.Dispose(); ModelViewModel.Dispose(); WebViewModel?.Dispose(); Transcript.Dispose(); NotificationPreview?.Dispose(); ModernWeb?.Dispose(); };
    }

    /// <summary>一度閉じる操作を保留し、モデル解放を期限付きで待ってから閉じ直す。</summary>
    /// <param name="sender">通知元のオブジェクト。</param>
    /// <param name="args">イベントまたは起動の引数。</param>
    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_articleDeletionDialogOpen) return;
        if (_closing) return;
        _closing = true;
        try
        {
            await Task.Yield(); // Closing の通知を抜けてから確認ダイアログを開く。
            MainNavigation.IsEnabled = false;
            if (!await ViewModel.PrepareToLeaveConversationAsync()) return;
            if (ScreenViewModel is not null) await ScreenViewModel.StopAsync();
            if (WebViewModel is not null) await WebViewModel.StopAsync();
            await SettingsViewModel.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            _images.Dispose();
            ViewModel.Dispose();
            ModelViewModel.Dispose();
            if (ReleaseTemporaryDataAsync is not null)
            {
                try { await ReleaseTemporaryDataAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception exception) { ViewModel.ReportStorageFailure("temp.shutdown", exception); }
            }
            // ウィンドウを残して非同期終了を待つ。Closed イベント内の await に依存しない。
            await _lifecycle.ShutdownAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(6));
            _allowClose = true;
        }
        catch (Exception exception) { ViewModel.ReportStorageFailure("window.close", exception); }
        finally
        {
            _closing = false;
            if (_allowClose || _lifecycle.IsClosing) { _allowClose = true; Close(); }
            else MainNavigation.IsEnabled = !_navigationBusy;
        }
    }

    /// <summary>チャットとモデル管理を切り替え、モデル管理表示時に一覧を更新する。</summary>
    /// <param name="sender">通知元のオブジェクト。</param>
    /// <param name="args">イベントまたは起動の引数。</param>
    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (ChatPanel is null || ModelPanel is null || SettingsPanel is null || WebPanel is null || LogsPanel is null || _navigationBusy || _closing) return;
        _navigationBusy = true;
        try
        {
            await Task.Yield();
            MainNavigation.IsEnabled = false;
            if (ChatPanel.Visibility == Visibility.Visible && !ReferenceEquals(args.SelectedItem, ChatNavigationItem) &&
                !await ViewModel.PrepareToLeaveConversationAsync())
            { MainNavigation.SelectedItem = _previousNavigation; return; }
            var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
            if (WebPanel.Visibility == Visibility.Visible && tag != "Web" && WebViewModel is not null) await WebViewModel.EndReadingAsync();
            ChatPanel.Visibility = tag == "Chat" ? Visibility.Visible : Visibility.Collapsed;
            ModelPanel.Visibility = tag == "Models" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanel.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            WebPanel.Visibility = tag == "Web" ? Visibility.Visible : Visibility.Collapsed;
            LogsPanel.Visibility = tag == "Logs" ? Visibility.Visible : Visibility.Collapsed;
            ScreenPanel.Visibility = tag == "Screen" ? Visibility.Visible : Visibility.Collapsed;
            MonitorPanel.Visibility = tag == "Monitor" ? Visibility.Visible : Visibility.Collapsed;
            _previousNavigation = args.SelectedItem;
            if (tag == "Models") await ModelViewModel.RefreshCommand.ExecuteAsync();
            if (tag == "Screen" && ScreenViewModel is not null && ScreenViewModel.Models.Count == 0) await ScreenViewModel.RefreshModelsCommand.ExecuteAsync();
            if (tag == "Monitor" && MonitorViewModel is not null) await MonitorViewModel.RefreshCommand.ExecuteAsync();
            if (tag == "Settings" && WorkerViewModel is not null) await WorkerViewModel.PollAsync();
            if (tag == "Web" && WebViewModel is not null)
            { if (WorkerViewModel is null) await WebViewModel.RefreshCommand.ExecuteAsync(); else await WorkerViewModel.PollAsync(); }
            if (tag == "Logs" && LogsViewModel is not null) await LogsViewModel.RefreshAsync();
        }
        catch (Exception exception) { MainNavigation.SelectedItem = _previousNavigation; ViewModel.ReportStorageFailure("window.navigation", exception); }
        finally { _navigationBusy = false; if (!_closing) MainNavigation.IsEnabled = true; }
    }

    /// <summary>最初の表示時に一度だけチャットのモデル一覧を取得する。</summary>
    /// <param name="sender">通知元のオブジェクト。</param>
    /// <param name="e">イベントまたは起動の引数。</param>
    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)sender).Loaded -= Root_Loaded;
        await ViewModel.InitializeAsync();
        if (_closing || _allowClose) return;
        if (WebViewModel is not null && WorkerViewModel is null) await WebViewModel.RefreshAsync();
        await SettingsViewModel.InitializeAsync();
        await ViewModel.RefreshSavedConversationsAsync();
        if (WorkerViewModel is not null) { await WorkerViewModel.InitializeAsync(); _workerTimer?.Start(); }
        if (MonitorViewModel is not null) await MonitorViewModel.PollAsync();
        _initializing = false;
#if DEBUG
        _initialized.TrySetResult(true);
#endif
    }

    /// <summary>選択の復元・一覧再取得途中を除き、最後のモデル選択を保存する。</summary>
    private async void ChatPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_initializing && args.PropertyName == nameof(ChatViewModel.SelectedModel) && ViewModel.SelectedModel is not null)
            await SettingsViewModel.RememberModelAsync(ViewModel.SelectedModel);
    }
    /// <summary>未取得は非表示とし、DB 全通知の集計を得てから未読バッジを表示する。</summary>
    private void NotificationPropertyChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(WebMonitoringViewModel.HasUnreadCounts)) UnreadBadge.Visibility = WebViewModel?.HasUnreadCounts == true ? Visibility.Visible : Visibility.Collapsed; }

    /// <summary>未保存内容の保存・破棄・キャンセルを利用者に確認する。</summary>
    private async Task<ConversationLeaveDecision> ConfirmLeaveAsync()
    {
        var dialog = new ContentDialog { XamlRoot = ContentRoot.XamlRoot, Title = "未保存の会話があります",
            Content = "会話・未送信の入力・添付を保存しますか？ 保存せず破棄すると、最後の保存以降の変更が失われます。",
            PrimaryButtonText = "保存", SecondaryButtonText = "保存せず破棄", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() switch { ContentDialogResult.Primary => ConversationLeaveDecision.Save,
            ContentDialogResult.Secondary => ConversationLeaveDecision.Discard, _ => ConversationLeaveDecision.Cancel };
    }

    /// <summary>タイトルを入力する。空の名前を許可せず、利用者の変更を保存に渡す。</summary>
    private async Task<string?> RequestTitleAsync(string suggested)
    {
        var title = new TextBox { Text = suggested, MaxLength = 120, Header = "会話名（120文字以内）" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(title, "ConversationTitle");
        var dialog = new ContentDialog { XamlRoot = ContentRoot.XamlRoot, Title = "会話を保存", Content = title,
            PrimaryButtonText = "保存", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(title.Text)) { args.Cancel = true; title.Header = "会話名を入力してください"; } };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? title.Text.Trim() : null;
    }

    /// <summary>関連画像も含めたスレッド削除を明示確認する。</summary>
    private async Task<bool> ConfirmDeleteAsync(string title) => await new ContentDialog { XamlRoot = ContentRoot.XamlRoot,
        Title = "保存済みの会話を削除", Content = $"「{title}」と関連するメッセージ・画像を削除します。元に戻せません。",
        PrimaryButtonText = "削除", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary;

    /// <summary>単体削除の意図だけを返す。設定の保存は ViewModel が削除成功後に行う。</summary>
    private async Task<ArticleDeletionDecision> ConfirmSingleArticleDeletionAsync(WebNotificationViewModel article)
    {
        _articleDeletionDialogOpen = true;
        try
        {
            var skip = new CheckBox { Content = "次回以降は確認しない" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(skip, "SkipSingleArticleDeletionConfirmation");
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = article.Title, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
            content.Children.Add(new TextBlock { Text = "通知・要約・保存 Markdown・記事の整理情報を削除します。巡回履歴は残ります。次回取得の最新10件にあれば新規として再登録されます。", TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
            content.Children.Add(skip);
            var result = await new ContentDialog { XamlRoot = ContentRoot.XamlRoot, Title = "この記事を削除しますか？", Content = content,
                PrimaryButtonText = "この記事を削除", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close }.ShowAsync();
            return new(result == ContentDialogResult.Primary, result == ContentDialogResult.Primary && skip.IsChecked == true);
        }
        finally { _articleDeletionDialogOpen = false; }
    }
    /// <summary>削除専用の複数選択ダイアログ。全選択・解除と対象件数を表示し、確認した ID だけを返す。</summary>
    private async Task<IReadOnlyList<Guid>?> SelectArticlesToDeleteAsync(IReadOnlyList<WebNotificationViewModel> articles)
    {
        _articleDeletionDialogOpen = true;
        try
        {
            await Task.Yield();
            var list = new ListView { ItemsSource = articles, SelectionMode = ListViewSelectionMode.Multiple,
                ItemTemplate = WorkerViewModel is null ? WebNotificationList.ItemTemplate : ModernWeb.NotificationTemplate, MaxHeight = 320 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(list, "ArticleDeletionList");
            var count = new TextBlock { Text = $"選択 0件 / 表示中 {articles.Count}件" };
            var warning = new TextBlock { Text = "選択対象のブックマーク付き 0件・タグ付き 0件も削除します。", TextWrapping = TextWrapping.Wrap };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(warning, "ArticleDeletionWarning");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(count, "ArticleDeletionCount");
            var selectAll = new Button { Content = "すべて選択" };
            var clear = new Button { Content = "選択解除" };
            selectAll.Click += (_, _) => list.SelectAll();
            clear.Click += (_, _) => list.SelectedItems.Clear();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            buttons.Children.Add(selectAll); buttons.Children.Add(clear);
            var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
            panel.Children.Add(new TextBlock { Text = "選択した記事の通知・要約・保存 Markdown を削除します。\n次回取得の最新10件に含まれていれば、新規記事として再登録されます。巡回履歴は残ります。", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(buttons); panel.Children.Add(count); panel.Children.Add(warning); panel.Children.Add(list);
            var dialog = new ContentDialog { XamlRoot = ContentRoot.XamlRoot, Title = "削除する記事を選択",
                Content = panel, PrimaryButtonText = "選択した記事を削除", CloseButtonText = "キャンセル",
                DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false };
            list.SelectionChanged += (_, _) =>
            {
                var selected = list.SelectedItems.Cast<WebNotificationViewModel>().ToArray();
                count.Text = $"選択 {selected.Length}件 / 表示中 {articles.Count}件";
                warning.Text = $"選択対象のブックマーク付き {selected.Count(row => row.Item.IsBookmarked)}件・タグ付き {selected.Count(row => row.TagItems.Count != 0)}件も削除します。";
                dialog.IsPrimaryButtonEnabled = selected.Length != 0;
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            return list.SelectedItems.Cast<WebNotificationViewModel>().Select(item => item.Item.Notification.Id).ToArray();
        }
        finally { _articleDeletionDialogOpen = false; }
    }
    /// <summary>情報源の削除は取得を停止し、取得済み記事・履歴を保持する。</summary>
    private async Task<bool> ConfirmDeleteSourceAsync(string name) => await new ContentDialog { XamlRoot = ContentRoot.XamlRoot,
        Title = "情報源を削除", Content = $"「{name}」の巡回と待機記事の処理を停止します。既存の記事・通知・Markdown・実行履歴は残ります。",
        PrimaryButtonText = "情報源を削除", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary;
    /// <summary>ログの複数行選択を表示モデルへ反映する。コピーは明示ボタンからのみ行う。</summary>
    private void LogsSelectionChanged(object sender, SelectionChangedEventArgs args) => LogsViewModel?.Select(((ListView)sender).SelectedItems.Cast<LogEntryViewModel>().ToArray());

}
