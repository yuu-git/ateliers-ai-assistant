using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace Ateliers.Ai.Assistant.Desktop.Controls;

/// <summary>通知閲覧の配置とダイアログを担当する。取得・保存・絞込の処理は ViewModel に委譲する。</summary>
public sealed partial class WebMonitoringView : UserControl, IDisposable
{
    private WebMonitoringViewModel? _web;
    private WorkerViewModel? _worker;
    private bool _detailRequested, _dialogOpen;
    private ContentDialog? _managerDialog;
    private readonly WorkerConsoleView _editor = new();
    /// <summary>設定画面への表示遷移をホストに依頼する。</summary>
    public Action? NavigateToSettings { get; set; }
    /// <summary>削除対象の選択に専念できる、記事を開く操作を持たない行テンプレート。</summary>
    public DataTemplate NotificationTemplate => (DataTemplate)Resources["ArticleDeletionTemplate"];
    /// <summary>ホストが表示寿命を管理する単一の通知ビュー。</summary>
    internal ChatTranscriptView PreviewControl => Preview;
    /// <summary>表示を初期化する。</summary>
    public WebMonitoringView() => InitializeComponent();
    /// <summary>既存の表示・操作 ViewModel を接続する。</summary>
    public void Initialize(WebMonitoringViewModel web, WorkerViewModel worker)
    {
        _web = web; _worker = worker; DataContext = web; WorkerStatus.DataContext = worker; _editor.DataContext = worker;
        var grouped = (CollectionViewSource)Resources["NotificationGroupsView"];
        grouped.Source = web.NotificationGroups; NotificationList.ItemsSource = grouped.View;
        web.PropertyChanged += WebChanged;
    }
    /// <summary>同じ記事の更新では詳細表示状態を変更しない。</summary>
    private void WebChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(WebMonitoringViewModel.SelectedNotification))
        { _detailRequested = _web?.SelectedNotification is not null; UpdateLayoutMode(); }
    }
    private void LayoutSizeChanged(object sender, SizeChangedEventArgs args) => UpdateLayoutMode();
    private void UpdateLayoutMode()
    {
        if (ReadingLayout is null) return;
        var wide = ActualWidth >= 1200; var narrow = ActualWidth < 820;
        SourcePane.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        CompactNavigation.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        SourcesColumn.Width = new GridLength(wide ? 210 : 0);
        var showDetail = !narrow || _detailRequested;
        ListColumn.Width = narrow && showDetail ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        DetailColumn.Width = showDetail ? new GridLength(1.25, GridUnitType.Star) : new GridLength(0);
        ListPane.Visibility = narrow && showDetail ? Visibility.Collapsed : Visibility.Visible;
        DetailPane.Visibility = showDetail ? Visibility.Visible : Visibility.Collapsed;
        BackToList.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        ArticleActions.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
    }
    private async void BackClicked(object sender, RoutedEventArgs args) { if (_web is not null) await _web.EndReadingAsync(); _detailRequested = false; UpdateLayoutMode(); }
    private async void OpenArticleClicked(object sender, RoutedEventArgs args)
    { if (_web is not null && (sender as FrameworkElement)?.DataContext is WebNotificationViewModel row) { _detailRequested = true; await _web.OpenArticleAsync(row); UpdateLayoutMode(); } }
    private async void BookmarkClicked(object sender, RoutedEventArgs args)
    { if (_web is not null && (sender as FrameworkElement)?.DataContext is WebNotificationViewModel row) { await _web.ToggleBookmarkAsync(row); } }
    private async void DeleteArticleClicked(object sender, RoutedEventArgs args)
    { if (_web is not null && (sender as FrameworkElement)?.DataContext is WebNotificationViewModel row) { await _web.DeleteArticleAsync(row); } }
    private async void TagFilterClicked(object sender, RoutedEventArgs args)
    { if (_web is not null && (sender as FrameworkElement)?.DataContext is ArticleTag tag) { await _web.FilterByTagAsync(tag); } }
    private void SettingsClicked(object sender, RoutedEventArgs args) => NavigateToSettings?.Invoke();
    private async void ResultTabChanged(object sender, SelectionChangedEventArgs args)
    {
        if (NotificationsPane is null) return;
        if (ResultTab.SelectedIndex == 1 && _web is not null) await _web.EndReadingAsync();
        NotificationsPane.Visibility = ResultTab.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        RunList.Visibility = ResultTab.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void FetchClicked(object sender, RoutedEventArgs args)
    {
        if (_worker is null || _web is null || _dialogOpen) return;
        if (_web.SelectedFeedSource?.Id is not { } id || id == Guid.Empty)
        {
            _dialogOpen = true;
            try { await new ContentDialog { XamlRoot = XamlRoot, Title = "巡回する情報源を選択", Content = "「すべて」は閲覧用です。登録済みの情報源を選択してから手動巡回してください。", CloseButtonText = "閉じる" }.ShowAsync(); }
            finally { _dialogOpen = false; }
            return;
        }
        await _worker.FetchSourceAsync(id);
    }
    private async void ManageClicked(object sender, RoutedEventArgs args) => await ShowSourceManagerAsync();
    /// <summary>既存の保存・削除コマンドを管理ダイアログで利用する。</summary>
    public async Task ShowSourceManagerAsync()
    {
        if (_dialogOpen || _worker is null) return;
        _dialogOpen = true;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "情報源設定", Content = _editor, CloseButtonText = "閉じる", DefaultButton = ContentDialogButton.Close };
        _managerDialog = dialog;
        // 削除確認を別の ContentDialog に重ねず、同じ管理画面内で確認する。
        var original = _worker.ConfirmDeleteSourceAsync;
        _worker.ConfirmDeleteSourceAsync = _editor.ConfirmDeleteAsync;
        try { await dialog.ShowAsync(); }
        finally { _worker.ConfirmDeleteSourceAsync = original; dialog.Content = null; _managerDialog = null; _dialogOpen = false; }
    }
    /// <summary>WebView2 と変更購読を解放する。</summary>
    public void Dispose() { if (_web is not null) _web.PropertyChanged -= WebChanged; Preview.Dispose(); }
#if DEBUG
    /// <summary>実現済み行の閲覧表示が、詳細の選択と一致することを確認する。</summary>
    internal void VerifyCurrentArticleForVerification()
    {
        if (_web is null) throw new InvalidOperationException("ViewModel is missing");
        foreach (var row in _web.VisibleNotifications)
        {
            var expected = ReferenceEquals(row, _web.SelectedNotification);
            if (row.IsCurrent != expected) throw new InvalidOperationException("Current article state differs from detail");
            if (NotificationList.ContainerFromItem(row) is not ListViewItem { ContentTemplateRoot: FrameworkElement content }) continue;
            var visibility = expected ? Visibility.Visible : Visibility.Collapsed;
            if ((content.FindName("CurrentArticleHighlight") as Border)?.Visibility != visibility)
                throw new InvalidOperationException("Current article highlight differs from detail");
        }
    }
    /// <summary>実タグ入力・コマンド結合を検証する。</summary>
    internal async Task AddTagForVerificationAsync(string name)
    {
        ArticleOperations.IsExpanded = true; TagEditor.IsExpanded = true; await Task.Delay(250);
        TagNameInput.Text = name; await Task.Delay(50);
        for (var i = 0; i < 100 && !_web!.AddTagCommand.CanExecute(null); i++) await Task.Delay(50);
        if (!_web!.AddTagCommand.CanExecute(null)) throw new InvalidOperationException("Tag command remained busy: " + _web.ErrorMessage);
        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(AddTagAction);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        var key = TagNameRules.Normalize(name).Key;
        for (var i = 0; i < 200; i++)
        {
            if (_web.CanManageNotification && _web.SelectedNotification?.TagItems.Any(tag => TagNameRules.Normalize(tag.Name).Key == key) == true) { ArticleOperations.IsExpanded = false; return; }
            await Task.Delay(50);
        }
        throw new InvalidOperationException("Native tag update did not complete: " + _web.ErrorMessage);
    }
    /// <summary>実テンプレートの独立した記事／ブックマークボタンを UI Automation パターンで押す。</summary>
    internal async Task InvokeArticleForVerificationAsync(int index, bool bookmark, bool delete = false)
    {
        if (_web is null) throw new InvalidOperationException("ViewModel is missing");
        var row = _web.VisibleNotifications[index]; NotificationList.ScrollIntoView(row); await Task.Delay(150);
        var container = NotificationList.ContainerFromItem(row) as ListViewItem;
        var content = container?.ContentTemplateRoot as FrameworkElement;
        var button = content?.FindName(delete ? "DeleteArticleAction" : bookmark ? "BookmarkAction" : "OpenArticleAction") as Button ?? throw new InvalidOperationException("Native item button unavailable");
        if (!button.IsEnabled) throw new InvalidOperationException("Native item action is unexpectedly disabled");
        if (bookmark && ((content?.FindName("BookmarkGlyph") as TextBlock)?.Text != row.BookmarkIcon || Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) != row.BookmarkActionName || (content?.FindName("BookmarkedGlyph") as TextBlock)?.Visibility != (row.Item.IsBookmarked ? Visibility.Visible : Visibility.Collapsed))) throw new InvalidOperationException("Icon, highlight or accessible bookmark name differs");
        if (bookmark && row.Item.IsBookmarked && !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            var color = ((content!.FindName("BookmarkedGlyph") as TextBlock)!.Foreground as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color;
            if (color is null || color.Value.R < color.Value.G || color.Value.G <= color.Value.B) throw new InvalidOperationException("Bookmarked star is not yellow");
        }
        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Task.Delay(150);
    }
    /// <summary>情報源の実選択を検証する。</summary>
    internal void SelectSourceForVerification(int index) { if (SourcePane.Visibility == Visibility.Visible) SourceList.SelectedIndex = index; else SourcePicker.SelectedIndex = index; }
    /// <summary>幅ごとの実ナビゲーションで表示条件を切り替える。</summary>
    internal void SelectViewForVerification(int index) { if (SourcePane.Visibility == Visibility.Visible) ViewList.SelectedIndex = index; else ViewPicker.SelectedIndex = index; }
    /// <summary>両方の幅の表示切替が同じフィルター値に同期することを確認する。</summary>
    internal bool ViewSelectionForVerification(int index) => ViewList.SelectedIndex == index && ViewPicker.SelectedIndex == index && _web?.FilterIndex == index;
    /// <summary>実際のペイン幅と表示モード。</summary>
    internal (double List, double Detail, bool Back, bool Sources) LayoutForVerification => (ListPane.ActualWidth, DetailPane.ActualWidth, BackToList.Visibility == Visibility.Visible, SourcePane.Visibility == Visibility.Visible);
    /// <summary>履歴切替と戻る操作。</summary>
    internal void HistoryForVerification(bool show) => ResultTab.SelectedIndex = show ? 1 : 0;
    /// <summary>狭幅で一覧へ戻る。</summary>
    internal void BackForVerification() => BackClicked(this, new());
    /// <summary>情報源管理を開き、実画面の画像を採取する。</summary>
    internal async Task CaptureManagerForVerificationAsync(Func<Task> capture)
    { var showing = ShowSourceManagerAsync(); await Task.Delay(300); await capture(); _managerDialog?.Hide(); await showing; }
    /// <summary>既存の実画面検証で参照する Markdown コントロール。</summary>
    internal ChatTranscriptView PreviewForVerification => Preview;
    /// <summary>実選択による詳細表示。</summary>
    internal void SelectForVerification(int index) { if (_web is not null) _web.SelectedNotification = index >= 0 ? _web.VisibleNotifications[index] : null; }
    /// <summary>管理ダイアログの実入力を検証する。</summary>
    internal async Task ConfigureForVerificationAsync(bool github = false)
    {
        var showing = ShowSourceManagerAsync(); await Task.Delay(300);
        await _editor.SetInputsAsync("実プロセスの情報源", github ? "verification/project" : "https://example.com/feed", 5, github);
        await _worker!.PollAsync();
        if (_worker.Name != "実プロセスの情報源" || _worker.IntervalMinutes != 5 || !_worker.Automatic) throw new InvalidOperationException("Polling replaced native source inputs");
        // 入力後に閉じても未保存値を勝手に破棄しない。
        _managerDialog?.Hide();
        await showing;
    }
#endif
}
