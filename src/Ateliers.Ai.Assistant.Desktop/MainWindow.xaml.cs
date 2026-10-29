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
    /// <summary>実画面の診断で Markdown と画像の DOM を確認する。</summary>
    internal Controls.ChatTranscriptView TranscriptForVerification => Transcript;
    /// <summary>通知の原文・安全化・選択変更を実 WebView2 で検証する。</summary>
    internal Controls.ChatTranscriptView NotificationPreviewForVerification => NotificationPreview;
    /// <summary>実 ListView の選択を通して通知本文の表示を確認する。</summary>
    internal void SelectNotificationForVerification(int index) => WebNotificationList.SelectedIndex = index;
    /// <summary>隔離した実画面検証で Web 画面を表示する。</summary>
    internal void ShowWebForVerification() => MainNavigation.SelectedItem = WebNavigationItem;
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
    public MainWindow(ChatViewModel viewModel, ModelManagementViewModel modelViewModel, IModelLifecycleService lifecycle,
        SettingsViewModel settingsViewModel, WebMonitoringViewModel? webViewModel = null)
    {
        ViewModel = viewModel;
        ModelViewModel = modelViewModel;
        _lifecycle = lifecycle;
        SettingsViewModel = settingsViewModel;
        WebViewModel = webViewModel;
        InitializeComponent();
        _previousNavigation = ChatNavigationItem;
        ViewModel.ConfirmLeaveAsync = ConfirmLeaveAsync;
        ViewModel.RequestConversationTitleAsync = RequestTitleAsync;
        ViewModel.ConfirmDeleteConversationAsync = ConfirmDeleteAsync;
        SettingsViewModel.ApplySelectedModelAsync = ViewModel.RestoreSelectedModelAsync;
        if (WebViewModel is not null) SettingsViewModel.ApplyWebSettings = WebViewModel.ApplySettings;
        if (WebViewModel is not null) WebViewModel.RequestArticleDeletionAsync = SelectArticlesToDeleteAsync;
        ViewModel.PropertyChanged += ChatPropertyChanged;
        _images = new ClipboardImageController(ChatInput, AttachmentPreview, ImagePreviewPanel, ImagePasteError, ViewModel);
        AppWindow.Closing += Window_Closing;
        Closed += (_, _) => { ViewModel.PropertyChanged -= ChatPropertyChanged; _images.Dispose(); ViewModel.Dispose(); ModelViewModel.Dispose(); WebViewModel?.Dispose(); Transcript.Dispose(); NotificationPreview.Dispose(); };
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
            if (WebViewModel is not null) await WebViewModel.StopAsync();
            await SettingsViewModel.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            _images.Dispose();
            ViewModel.Dispose();
            ModelViewModel.Dispose();
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
        if (ChatPanel is null || ModelPanel is null || SettingsPanel is null || _navigationBusy || _closing) return;
        _navigationBusy = true;
        try
        {
            await Task.Yield();
            MainNavigation.IsEnabled = false;
            if (ChatPanel.Visibility == Visibility.Visible && !ReferenceEquals(args.SelectedItem, ChatNavigationItem) &&
                !await ViewModel.PrepareToLeaveConversationAsync())
            { MainNavigation.SelectedItem = _previousNavigation; return; }
            var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
            ChatPanel.Visibility = tag == "Chat" ? Visibility.Visible : Visibility.Collapsed;
            ModelPanel.Visibility = tag == "Models" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanel.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            WebPanel.Visibility = tag == "Web" ? Visibility.Visible : Visibility.Collapsed;
            _previousNavigation = args.SelectedItem;
            if (tag == "Models") await ModelViewModel.RefreshCommand.ExecuteAsync();
            if (tag == "Web" && WebViewModel is not null) await WebViewModel.RefreshCommand.ExecuteAsync();
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
        if (WebViewModel is not null) await WebViewModel.RefreshAsync();
        await SettingsViewModel.InitializeAsync();
        await ViewModel.RefreshSavedConversationsAsync();
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

    /// <summary>削除専用の複数選択ダイアログ。全選択・解除と対象件数を表示し、確認した ID だけを返す。</summary>
    private async Task<IReadOnlyList<Guid>?> SelectArticlesToDeleteAsync(IReadOnlyList<WebNotificationViewModel> articles)
    {
        _articleDeletionDialogOpen = true;
        try
        {
            await Task.Yield();
            var list = new ListView { ItemsSource = articles, SelectionMode = ListViewSelectionMode.Multiple,
                ItemTemplate = WebNotificationList.ItemTemplate, MaxHeight = 320 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(list, "ArticleDeletionList");
            var count = new TextBlock { Text = $"選択 0件 / 表示中 {articles.Count}件" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(count, "ArticleDeletionCount");
            var selectAll = new Button { Content = "すべて選択" };
            var clear = new Button { Content = "選択解除" };
            selectAll.Click += (_, _) => list.SelectAll();
            clear.Click += (_, _) => list.SelectedItems.Clear();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            buttons.Children.Add(selectAll); buttons.Children.Add(clear);
            var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
            panel.Children.Add(new TextBlock { Text = "選択した記事の通知・要約・保存 Markdown を削除します。\n次回取得の最新10件に含まれていれば、新規記事として再登録されます。巡回履歴は残ります。", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(buttons); panel.Children.Add(count); panel.Children.Add(list);
            var dialog = new ContentDialog { XamlRoot = ContentRoot.XamlRoot, Title = "削除する記事を選択",
                Content = panel, PrimaryButtonText = "選択した記事を削除", CloseButtonText = "キャンセル",
                DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false };
            list.SelectionChanged += (_, _) => { count.Text = $"選択 {list.SelectedItems.Count}件 / 表示中 {articles.Count}件"; dialog.IsPrimaryButtonEnabled = list.SelectedItems.Count != 0; };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            return list.SelectedItems.Cast<WebNotificationViewModel>().Select(item => item.Item.Notification.Id).ToArray();
        }
        finally { _articleDeletionDialogOpen = false; }
    }

}
