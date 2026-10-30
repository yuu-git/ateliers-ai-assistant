using Microsoft.UI.Xaml;
using System;
using System.Net.Http;
using System.Threading;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Chat;
using Ateliers.Ai.Assistant.Application.Models;
using Microsoft.UI.Dispatching;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Ateliers.Ai.Assistant.Infrastructure.Conversations;
using Ateliers.Ai.Assistant.Desktop.Storage;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;
using Ateliers.Ai.Assistant.Infrastructure.Worker;
using Ateliers.Ai.Assistant.Application.Logging;
using Windows.ApplicationModel.DataTransfer;
using System.Threading.Tasks;

namespace Ateliers.Ai.Assistant.Desktop;

/// <summary>HTTP クライアントとサービスを構成し、WinUI のメインウィンドウを起動するホスト。</summary>
public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;
    private readonly HttpClient _chatClient = new()
    {
        BaseAddress = new Uri("http://localhost:11434/"),
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>アプリケーション共通の XAML リソースを初期化する。</summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>通常のサービス構成、または Debug 専用の検証ウィンドウを起動する。</summary>
    /// <param name="args">イベントまたは起動の引数。</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
#if DEBUG
        var arguments = Environment.GetCommandLineArgs();
        var monitorVerifyIndex = Array.IndexOf(arguments, "--verify-screen-monitor");
        if (monitorVerifyIndex >= 0 && monitorVerifyIndex + 2 < arguments.Length)
        {
            _window = Diagnostics.ScreenWorkerVerification.CreateMonitor(arguments[monitorVerifyIndex + 1], arguments[monitorVerifyIndex + 2]);
            _window.Activate(); return;
        }
        var screenIndex = Array.IndexOf(arguments, "--verify-screen-analysis");
        if (screenIndex >= 0 && screenIndex + 1 < arguments.Length)
        {
            _window = Diagnostics.ScreenAnalysisVerification.Create(arguments[screenIndex + 1], screenIndex + 2 < arguments.Length ? arguments[screenIndex + 2] : null,
                screenIndex + 3 < arguments.Length ? arguments[screenIndex + 3] : null);
            _window.Activate(); return;
        }
        var layoutIndex = Array.IndexOf(arguments, "--verify-ui-layout");
        if (layoutIndex >= 0 && layoutIndex + 2 < arguments.Length)
        {
            _window = Diagnostics.UiLayoutVerification.Create(arguments[layoutIndex + 1], arguments[layoutIndex + 2]);
            _window.Activate(); return;
        }
        var workerUiIndex = Array.IndexOf(arguments, "--verify-worker-ui");
        if (workerUiIndex >= 0 && workerUiIndex + 3 < arguments.Length)
        {
            _window = Diagnostics.WorkerDisplayVerification.Create(arguments[workerUiIndex + 1], arguments[workerUiIndex + 2], arguments[workerUiIndex + 3] == "offline", arguments[workerUiIndex + 3] == "notifications", arguments[workerUiIndex + 3] == "github");
            _window.Activate(); return;
        }
        var deletionIndex = Array.IndexOf(arguments, "--verify-web-deletion-interactive");
        if (deletionIndex >= 0 && deletionIndex + 1 < arguments.Length)
        {
            _window = Diagnostics.WebMonitoringVerification.CreateDeletionInteractive(arguments[deletionIndex + 1]);
            _window.Activate(); return;
        }
        var webVerifyIndex = Array.IndexOf(arguments, "--verify-web");
        if (webVerifyIndex >= 0 && webVerifyIndex + 3 < arguments.Length)
        {
            _window = Diagnostics.WebMonitoringVerification.Create(arguments[webVerifyIndex + 1], arguments[webVerifyIndex + 2], arguments[webVerifyIndex + 3] == "resume");
            _window.Activate(); return;
        }
        var liveStorageIndex = Array.IndexOf(arguments, "--verify-storage-interactive-live");
        if (liveStorageIndex >= 0 && liveStorageIndex + 1 < arguments.Length)
        {
            _window = Diagnostics.StorageVerification.CreateInteractive(arguments[liveStorageIndex + 1], liveOllama: true);
            _window.Activate(); return;
        }
        var pathVerifyIndex = Array.IndexOf(arguments, "--verify-data-paths");
        if (pathVerifyIndex >= 0 && pathVerifyIndex + 1 < arguments.Length)
        {
            _window = Diagnostics.DataPathVerification.Create(arguments[pathVerifyIndex + 1]);
            _window.Activate(); return;
        }
        var interactiveStorageIndex = Array.IndexOf(arguments, "--verify-storage-interactive");
        if (interactiveStorageIndex >= 0 && interactiveStorageIndex + 1 < arguments.Length)
        {
            _window = Diagnostics.StorageVerification.CreateInteractive(arguments[interactiveStorageIndex + 1]);
            _window.Activate(); return;
        }
        var storageVerifyIndex = Array.IndexOf(arguments, "--verify-storage");
        if (storageVerifyIndex >= 0 && storageVerifyIndex + 3 < arguments.Length)
        {
            _window = Diagnostics.StorageVerification.Create(arguments[storageVerifyIndex + 1], arguments[storageVerifyIndex + 2],
                arguments[storageVerifyIndex + 3] == "resume");
            _window.Activate(); return;
        }
        var imageVerifyIndex = Array.IndexOf(arguments, "--verify-image-paste");
        if (imageVerifyIndex >= 0 && imageVerifyIndex + 1 < arguments.Length)
        {
            _window = Diagnostics.ImagePasteVerification.Create(arguments[imageVerifyIndex + 1],
                imageVerifyIndex + 2 < arguments.Length ? arguments[imageVerifyIndex + 2] : null);
            _window.Activate();
            return;
        }
        var verifyIndex = Array.IndexOf(arguments, "--verify-markdown");
        if (verifyIndex >= 0 && verifyIndex + 1 < arguments.Length)
        {
            _window = Diagnostics.MarkdownDisplayVerification.Create(arguments[verifyIndex + 1]);
            _window.Activate();
            return;
        }
#endif
        // 接続先・HTTP の所有権・具体サービスの選択は Host の責務。
        var chatService = new OllamaChatService(_chatClient);
        var paths = new AppDataPaths();
        var session = new ChatModelSession(chatService, chatService, FileOperationGate.ForModels(paths));
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        void Dispatch(Action action)
        {
            if (dispatcher.HasThreadAccess) action();
            else dispatcher.TryEnqueue(() => action());
        }
        var diagnostics = new FileDiagnosticLog(paths, "UI");
        var temporaryFiles = new Ateliers.Ai.Assistant.Infrastructure.Storage.Temp.TempFileStore(paths, diagnostics);
        _ = temporaryFiles.CleanupAsync();
        diagnostics.Write(AppLogCategory.System, AppLogLevel.Information, "ui.started", "Desktop を起動しました。");
        var store = new SqliteConversationStore(paths, diagnostics);
        var settings = new SettingsViewModel(paths, new JsonUserSettingsStore(paths, diagnostics), diagnostics,
            new FixedDataFolderOpener(paths).OpenAsync);
        var workerHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var api = new WorkerApiClient(workerHttp, new WorkerConnectionFiles(paths));
        var otherEnvironment = AppDataPaths.CurrentEnvironment == Ateliers.Ai.Assistant.Application.Storage.AppDataEnvironment.Debug
            ? Ateliers.Ai.Assistant.Application.Storage.AppDataEnvironment.Release : Ateliers.Ai.Assistant.Application.Storage.AppDataEnvironment.Debug;
        var otherApi = new WorkerApiClient(workerHttp, new WorkerConnectionFiles(new AppDataPaths(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), otherEnvironment)));
        session.CanAutomaticallyUnloadAsync = async (model, token) => await api.CanAutomaticallyUnloadAsync(model, token).ConfigureAwait(false)
            && await otherApi.CanAutomaticallyUnloadAsync(model, token).ConfigureAwait(false);
        var web = new WebMonitoringViewModel(api, api, session, session, settings, diagnostics, Dispatch, new ArticleLinkOpener(paths).OpenAsync);
        var worker = new WorkerViewModel(api, new WorkerLauncher(paths), session, diagnostics) { RefreshResultsAsync = web.RefreshFeedAsync };
        var logs = new LogsViewModel(new FileAppLogReader(paths), async text =>
        {
            await Task.Yield(); var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data);
        }, () => new FixedDataFolderOpener(paths).OpenAsync(Ateliers.Ai.Assistant.Application.Storage.DataFolderKind.Logs));
        var main = new MainWindow(new ChatViewModel(session, session, Dispatch, store, diagnostics),
            new ModelManagementViewModel(session, Dispatch), session, settings, web, worker, logs);
        var screen = new ScreenAnalysisViewModel(new Capture.WindowsScreenCaptureService(() => WinRT.Interop.WindowNative.GetWindowHandle(main)),
            new Ateliers.Ai.Assistant.Application.ScreenAnalysis.ScreenAnalysisService(session, session, async token =>
            {
                var status = await api.GetStatusAsync(token).ConfigureAwait(false);
                if (status.Configuration.AiPaused) throw new InvalidOperationException("AI は一時停止中です。再開してから解析してください。");
            }), Dispatch);
        main.InitializeScreenAnalysis(screen);
        main.InitializeScreenMonitor(new ScreenMonitorViewModel(api, session));
        main.ReleaseTemporaryDataAsync = () => temporaryFiles.DisposeAsync().AsTask();
        _window = main;
        _window.Closed += (_, _) => { temporaryFiles.Dispose(); diagnostics.Write(AppLogCategory.System, AppLogLevel.Information, "ui.stopped", "Desktop を終了しました。Worker は継続します。"); _chatClient.Dispose(); workerHttp.Dispose(); };
        _window.Activate();
    }
}
