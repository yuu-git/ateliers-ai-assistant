using Microsoft.UI.Xaml;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Application.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Desktop.Images;

namespace Ateliers.Ai.Assistant.Desktop;

/// <summary>チャットとモデル管理の画面を接続し、期限付きの非同期終了を調整するウィンドウ。</summary>
public sealed partial class MainWindow : Window
{
    /// <summary>チャット画面のバインド先を取得する。</summary>
    public ChatViewModel ViewModel { get; }
    /// <summary>モデル管理画面のバインド先を取得する。</summary>
    public ModelManagementViewModel ModelViewModel { get; }
    private readonly IModelLifecycleService _lifecycle;
    private bool _closing;
    private bool _allowClose;
    private readonly ClipboardImageController _images;

    /// <summary>注入された ViewModel とモデル管理サービスを使って画面を初期化する。</summary>
    /// <param name="viewModel">入力・添付の状態を保持するチャット ViewModel。</param>
    /// <param name="modelViewModel">ロード一覧と手動停止の状態を保持する ViewModel。</param>
    /// <param name="lifecycle">チャットとモデル管理で共有し、終了時に解放するサービス。</param>
    public MainWindow(ChatViewModel viewModel, ModelManagementViewModel modelViewModel, IModelLifecycleService lifecycle)
    {
        ViewModel = viewModel;
        ModelViewModel = modelViewModel;
        _lifecycle = lifecycle;
        InitializeComponent();
        _images = new ClipboardImageController(ChatInput, AttachmentPreview, ImagePreviewPanel, ImagePasteError, ViewModel);
        AppWindow.Closing += Window_Closing;
        Closed += (_, _) => { _images.Dispose(); ViewModel.Dispose(); ModelViewModel.Dispose(); Transcript.Dispose(); };
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
        MainNavigation.IsEnabled = false;
        _images.Dispose();
        ViewModel.Dispose();
        ModelViewModel.Dispose();
        try
        {
            // ウィンドウを残して非同期終了を待つ。Closed イベント内の await に依存しない。
            await _lifecycle.ShutdownAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(6));
        }
        catch (Exception exception) { Trace.TraceError("Window model cleanup: {0}", exception); }
        finally { _allowClose = true; Close(); }
    }

    /// <summary>チャットとモデル管理を切り替え、モデル管理表示時に一覧を更新する。</summary>
    /// <param name="sender">通知元のオブジェクト。</param>
    /// <param name="args">イベントまたは起動の引数。</param>
    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (ChatPanel is null || ModelPanel is null) return;
        var models = (args.SelectedItem as NavigationViewItem)?.Tag as string == "Models";
        ChatPanel.Visibility = models ? Visibility.Collapsed : Visibility.Visible;
        ModelPanel.Visibility = models ? Visibility.Visible : Visibility.Collapsed;
        if (models) await ModelViewModel.RefreshCommand.ExecuteAsync();
    }

    /// <summary>最初の表示時に一度だけチャットのモデル一覧を取得する。</summary>
    /// <param name="sender">通知元のオブジェクト。</param>
    /// <param name="e">イベントまたは起動の引数。</param>
    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)sender).Loaded -= Root_Loaded;
        await ViewModel.InitializeAsync();
    }

}
