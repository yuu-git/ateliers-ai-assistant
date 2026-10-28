using Microsoft.UI.Xaml;
using System;
using System.Net.Http;
using System.Threading;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Chat;
using Ateliers.Ai.Assistant.Application.Models;
using Microsoft.UI.Dispatching;

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
        _window = new MainWindow(new ChatViewModel(session, session, Dispatch),
            new ModelManagementViewModel(session, Dispatch), session);
        _window.Closed += (_, _) => _chatClient.Dispose();
        _window.Activate();
    }
}
