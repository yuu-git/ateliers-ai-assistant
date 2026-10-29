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
        var session = new ChatModelSession(chatService, chatService);
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        void Dispatch(Action action)
        {
            if (dispatcher.HasThreadAccess) action();
            else dispatcher.TryEnqueue(() => action());
        }
        var paths = new AppDataPaths();
        var diagnostics = new FileDiagnosticLog(paths);
        var store = new SqliteConversationStore(paths, diagnostics);
        var settings = new SettingsViewModel(paths, new JsonUserSettingsStore(paths, diagnostics), diagnostics,
            new FixedDataFolderOpener(paths).OpenAsync);
        var feedClient = new HttpClient(RssFeedSource.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var webStore = new SqliteWebMonitoringStore(paths, diagnostics);
        var webService = new WebCollectionService(new RssFeedSource(feedClient), webStore, new ArticleMarkdownWriter(paths), session, diagnostics);
        var web = new WebMonitoringViewModel(webService, webStore, session, session, settings, diagnostics, Dispatch, new ArticleLinkOpener(paths).OpenAsync);
        _window = new MainWindow(new ChatViewModel(session, session, Dispatch, store, diagnostics),
            new ModelManagementViewModel(session, Dispatch), session, settings, web);
        _window.Closed += (_, _) => { _chatClient.Dispose(); feedClient.Dispose(); };
        _window.Activate();
    }
}
