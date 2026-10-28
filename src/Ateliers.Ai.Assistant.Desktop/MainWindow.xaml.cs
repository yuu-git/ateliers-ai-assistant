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
    private readonly IModelLifecycleService _lifecycle;
    private bool _closing;
    private bool _allowClose;
    private readonly ClipboardImageController _images;
    private bool _navigationBusy;
    private bool _initializing = true;
    private object? _previousNavigation;
#if DEBUG
    private readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>実画面の診断で初期化完了を待つ。</summary>
    internal Task InitializedForVerification => _initialized.Task;
    /// <summary>実画面の診断で Markdown と画像の DOM を確認する。</summary>
    internal Controls.ChatTranscriptView TranscriptForVerification => Transcript;
#endif

    /// <summary>注入された ViewModel とモデル管理サービスを使って画面を初期化する。</summary>
    /// <param name="viewModel">入力・添付の状態を保持するチャット ViewModel。</param>
    /// <param name="modelViewModel">ロード一覧と手動停止の状態を保持する ViewModel。</param>
    /// <param name="lifecycle">チャットとモデル管理で共有し、終了時に解放するサービス。</param>
    /// <param name="settingsViewModel">固定保存先の表示とモデル選択設定を担当する ViewModel。</param>
    public MainWindow(ChatViewModel viewModel, ModelManagementViewModel modelViewModel, IModelLifecycleService lifecycle,
        SettingsViewModel settingsViewModel)
    {
        ViewModel = viewModel;
        ModelViewModel = modelViewModel;
        _lifecycle = lifecycle;
        SettingsViewModel = settingsViewModel;
        InitializeComponent();
        _previousNavigation = ChatNavigationItem;
        ViewModel.ConfirmLeaveAsync = ConfirmLeaveAsync;
        ViewModel.RequestConversationTitleAsync = RequestTitleAsync;
        ViewModel.ConfirmDeleteConversationAsync = ConfirmDeleteAsync;
        SettingsViewModel.ApplySelectedModelAsync = ViewModel.RestoreSelectedModelAsync;
        ViewModel.PropertyChanged += ChatPropertyChanged;
        _images = new ClipboardImageController(ChatInput, AttachmentPreview, ImagePreviewPanel, ImagePasteError, ViewModel);
        AppWindow.Closing += Window_Closing;
        Closed += (_, _) => { ViewModel.PropertyChanged -= ChatPropertyChanged; _images.Dispose(); ViewModel.Dispose(); ModelViewModel.Dispose(); Transcript.Dispose(); };
    }

    /// <summary>一度閉じる操作を保留し、モデル解放を期限付きで待ってから閉じ直す。</summary>
    /// <param name="sender">通知元のオブジェクト。</param>
    /// <param name="args">イベントまたは起動の引数。</param>
    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        try
        {
            await Task.Yield(); // Closing の通知を抜けてから確認ダイアログを開く。
            MainNavigation.IsEnabled = false;
            if (!await ViewModel.PrepareToLeaveConversationAsync()) return;
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
            _previousNavigation = args.SelectedItem;
            if (tag == "Models") await ModelViewModel.RefreshCommand.ExecuteAsync();
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

}
