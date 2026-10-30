#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.ScreenAnalysis;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Desktop.Capture;
using Ateliers.Ai.Assistant.Desktop.Controls;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Chat;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Ateliers.Ai.Assistant.Infrastructure.Worker;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ateliers.Ai.Assistant.Desktop.Diagnostics;

/// <summary>既知の検証用ウィンドウとディスプレイをメモリ内で取得する、明示起動専用の診断。</summary>
internal static class ScreenAnalysisVerification
{
    internal static Window Create(string reportPath, string? model, string? verificationRoot)
    {
        var report = new List<string>();
        var target = new Window { Title = "Ateliers Phase 6 capture test" };
        target.Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.DarkBlue), Children =
        { new TextBlock { Text = "PHASE 6\nSCREEN CAPTURE TEST\n12345", FontSize = 32, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), Margin = new Thickness(30) } } };
        target.AppWindow.Resize(new(640, 400)); target.Activate();
        var window = new Window { Title = "Phase 6 verification" };
        var capture = new WindowsScreenCaptureService(() => WinRT.Interop.WindowNative.GetWindowHandle(window));
        var fake = new PreviewChat(); var coordinator = new Coordinator(fake);
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var vm = new ScreenAnalysisViewModel(capture, new(fake, coordinator, _ => Task.CompletedTask), action => { if (dispatcher.HasThreadAccess) action(); else dispatcher.TryEnqueue(() => action()); });
        var view = new ScreenAnalysisView(); view.Initialize(vm); window.Content = view; window.AppWindow.Resize(new(1280, 1000));
        var started = false;
        window.Activated += async (_, _) =>
        {
            if (started) return; started = true;
            try
            {
                await Task.Delay(500);
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(target);
                capture.SelectForVerification(handle, false);
                vm.UpdateAvailability(true); vm.SelectedModel = "verification";
                await vm.CaptureCommand.ExecuteAsync(); await Until(() => view.HasPreviewForVerification && vm.AnalyzeCommand.CanExecute(null));
                if (vm.Image is not { Width: > 100, Height: > 100 }) throw new Exception("window capture missing: " + vm.Error);
                await vm.AnalyzeCommand.ExecuteAsync();
                await view.VerifyMarkdownForVerificationAsync();
                report.Add("PASS: real WGC window frame, same PNG preview, streaming Markdown and local code highlighting");
                window.AppWindow.Resize(new(720, 900)); await Task.Delay(300);
                if (!view.IsStackedForVerification) throw new Exception("narrow layout is not stacked");
                report.Add("PASS: wide and narrow capture/preview/result layout");
                for (var i = 0; i < 3; i++) await capture.CaptureAsync(default);
                GC.Collect(); GC.WaitForPendingFinalizers(); var process = Process.GetCurrentProcess(); process.Refresh();
                var handles = process.HandleCount; var memory = process.PrivateMemorySize64;
                for (var i = 0; i < 15; i++) await capture.CaptureAsync(default);
                GC.Collect(); GC.WaitForPendingFinalizers(); process.Refresh();
                if (process.HandleCount - handles > 80 || process.PrivateMemorySize64 - memory > 256 * 1024 * 1024) throw new Exception("capture resource growth exceeds verification bound");
                report.Add("PASS: 18 repeated real captures; bounded handles and private memory after warm-up");
                if (model is not null)
                {
                    // 実 Ollama へ送るのは上記の検証用ウィンドウ画像だけ。ディスプレイ画像は送らない。
                    using var http = new HttpClient { BaseAddress = new Uri("http://localhost:11434/"), Timeout = Timeout.InfiniteTimeSpan };
                    var paths = new AppDataPaths();
                    var adapter = new OllamaChatService(http); var session = new ChatModelSession(adapter, adapter, FileOperationGate.ForModels(paths));
                    using var workerHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
                    var apiPaths = verificationRoot is null ? paths : new AppDataPaths(Path.Combine(verificationRoot, "r"), Path.Combine(verificationRoot, "l"));
                    var api = new WorkerApiClient(workerHttp, new WorkerConnectionFiles(apiPaths));
                    try
                    {
                        var text = "";
                        await new ScreenAnalysisService(session, session, async token =>
                        {
                            if ((await api.GetStatusAsync(token)).Configuration.AiPaused) throw new InvalidOperationException("AI は一時停止中です。");
                        }).AnalyzeAsync(model, "この検証用画面を日本語で短く説明してください。", vm.Image!, part => text += part, default);
                        if (string.IsNullOrWhiteSpace(text)) throw new Exception("live Vision response empty");
                        report.Add("PASS: real Ollama Vision reply from synthetic window image (reply content not logged)");
                    }
                    finally { await session.ShutdownAsync(TimeSpan.FromSeconds(5)); }
                }
                capture.SelectForVerification(handle, true);
                var display = await capture.CaptureAsync(default);
                if (display.Width <= 100 || display.Height <= 100) throw new Exception("display capture missing");
                report.Add("PASS: real WGC display frame normalized in memory; never sent to Ollama or persisted");
                capture.SelectForVerification(handle, false); target.Close(); await Task.Delay(400);
                if (capture.TargetName is not null || vm.CaptureCommand.CanExecute(null)) throw new Exception("closed target retained");
                report.Add("PASS: closed target disables capture and retains previous snapshot");
                report.Add("PASS: ALL SCREEN ANALYSIS CHECKS");
            }
            catch (Exception exception) { report.Add("FAIL: " + exception); }
            finally { await vm.StopAsync(); vm.Dispose(); view.Dispose(); target.Close(); File.WriteAllLines(reportPath, report); window.Close(); }
        };
        return window;
    }
    private static async Task Until(Func<bool> ready)
    { for (var i = 0; i < 200 && !ready(); i++) await Task.Delay(50); if (!ready()) throw new Exception("Preview did not become ready"); }
    private sealed class Coordinator(IChatService chat) : IModelWorkCoordinator
    { public Task RunExclusiveAsync(Func<IChatService, CancellationToken, Task> work, CancellationToken token) => work(chat, token); }
    private sealed class PreviewChat : IChatService
    {
        public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<string>>(["verification"]);
        public async IAsyncEnumerable<string> StreamReplyAsync(string model, IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken token = default)
        { yield return "# Vision\n\n**説明**\n\n```csharp\n"; await Task.Delay(150, token); yield return "var total = 1 + 2;\n```\n"; }
    }
}
#endif
