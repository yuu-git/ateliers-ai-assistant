using System.Runtime.CompilerServices;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.ScreenAnalysis;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Desktop.ViewModels;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

internal static class ScreenAnalysisTests
{
    public static async Task WorkflowAndIsolation()
    {
        var capture = new Capture(); var chat = new Chat(); var gate = new Coordinator(chat);
        var queued = new Queue<Action>();
        using var vm = new ScreenAnalysisViewModel(capture, new(chat, gate, _ => Task.CompletedTask), queued.Enqueue);
        vm.UpdateAvailability(true);
        Check(!vm.CaptureCommand.CanExecute(null) && !vm.AnalyzeCommand.CanExecute(null), "requires target/image/model");
        await vm.PickCommand.ExecuteAsync(); Check(capture.Captures == 0, "pick never captures");
        await vm.CaptureCommand.ExecuteAsync(); var image = vm.Image;
        Check(!vm.AnalyzeCommand.CanExecute(null), "preview must be decoded first"); vm.CompletePreview(image!, true);
        capture.CancelPick = true; await vm.PickCommand.ExecuteAsync(); Check(ReferenceEquals(image, vm.Image) && !vm.HasError, "picker cancel preserves image");
        await vm.RefreshModelsCommand.ExecuteAsync(); vm.SelectedModel = "vision";
        await vm.AnalyzeCommand.ExecuteAsync();
        Check(chat.Requests.Count == 1 && chat.Requests[0].Count == 1 && ReferenceEquals(chat.Requests[0][0].Image, image), "same preview image, no chat history");
        Check(vm.Results.Count == 1 && vm.Results[0].Content == "# 説明\n完了" && vm.Status.Contains("完了"), "final flush without dispatcher");
        await vm.AnalyzeCommand.ExecuteAsync(); while (queued.TryDequeue(out var update)) update();
        Check(vm.Results.Count == 1 && vm.Results[0].Content == "# 説明\n完了" && chat.Requests[1].Count == 1, "late previous updates and accumulation excluded");
        await vm.ClearCommand.ExecuteAsync(); Check(vm.Image is null && vm.Results.Count == 0 && capture.TargetName is not null, "explicit memory clear");
    }
    public static async Task FailuresAndCancellation()
    {
        var capture = new Capture(); var chat = new Chat();
        using var vm = new ScreenAnalysisViewModel(capture, new(chat, new Coordinator(chat), _ => Task.CompletedTask), a => a());
        vm.UpdateAvailability(true); await vm.PickCommand.ExecuteAsync(); await vm.CaptureCommand.ExecuteAsync();
        var image = vm.Image; vm.CompletePreview(image!, true); capture.Fail = true; await vm.CaptureCommand.ExecuteAsync();
        Check(vm.HasError && ReferenceEquals(image, vm.Image), "failed capture preserves last preview");
        vm.SelectedModel = "unsupported"; chat.Fail = true; await vm.AnalyzeCommand.ExecuteAsync();
        Check(vm.HasError && vm.Error.Contains("画像非対応") && vm.Results[0].Content == "# 説明\n", "API failure preserves partial response");
        chat.Fail = false; chat.Wait = true; var running = vm.AnalyzeCommand.ExecuteAsync(); await chat.Entered.Task;
        Check(vm.IsBusy && vm.Results[0].StatusText == "受信中…" && !vm.CaptureCommand.CanExecute(null) && !vm.AnalyzeCommand.CanExecute(null), "no duplicate operations and Markdown remains streaming");
        vm.UpdateAvailability(false); await running;
        Check(vm.Status.Contains("中止") && !vm.IsBusy && !vm.AnalyzeCommand.CanExecute(null), "pause cancels generation");
        capture.Close(); Check(vm.TargetName == "対象未選択" && !vm.CaptureCommand.CanExecute(null) && ReferenceEquals(image, vm.Image), "closed target keeps snapshot");
        vm.UpdateAvailability(true); var stopping = vm.AnalyzeCommand.ExecuteAsync(); await vm.StopAsync(); await stopping;
        Check(!vm.IsBusy && vm.Status.Contains("中止"), "shutdown waits cancellation");
    }
    public static async Task GuardWithinLease()
    {
        var chat = new Chat(); var coordinator = new Coordinator(chat) { BeforeWork = new() };
        var allowed = true;
        var service = new ScreenAnalysisService(chat, coordinator, _ => allowed ? Task.CompletedTask : Task.FromException(new InvalidOperationException("AI 一時停止")));
        var running = service.AnalyzeAsync("vision", "説明", new([1], 1, 1), _ => { }, default);
        allowed = false; coordinator.BeforeWork.SetResult();
        try { await running; throw new Exception("guard ignored"); } catch (InvalidOperationException) { }
        Check(chat.Requests.Count == 0, "pause is checked after lease, before model load/request");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Capture : IScreenCaptureService
    {
        public string? TargetName { get; private set; }
        public event EventHandler? TargetClosed;
        public bool CancelPick, Fail;
        public int Captures;
        public Task<bool> PickAsync(CancellationToken token) { if (!CancelPick) TargetName = "検証用対象"; return Task.FromResult(!CancelPick); }
        public Task<ChatImage> CaptureAsync(CancellationToken token) { Captures++; return Fail ? Task.FromException<ChatImage>(new InvalidOperationException("取得失敗")) : Task.FromResult(new ChatImage([1, 2], 2, 1)); }
        public void Close() { TargetName = null; TargetClosed?.Invoke(this, EventArgs.Empty); }
        public void Dispose() { }
    }
    private sealed class Coordinator(Chat chat) : IModelWorkCoordinator
    {
        public TaskCompletionSource? BeforeWork;
        public async Task RunExclusiveAsync(Func<IChatService, CancellationToken, Task> work, CancellationToken token)
        { if (BeforeWork is not null) await BeforeWork.Task.WaitAsync(token); await work(chat, token); }
    }
    private sealed class Chat : IChatService
    {
        public List<IReadOnlyList<ChatMessage>> Requests = [];
        public bool Fail, Wait;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<string>>(["vision", "text"]);
        public async IAsyncEnumerable<string> StreamReplyAsync(string model, IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages); yield return "# 説明\n"; Entered.TrySetResult();
            if (Wait) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Fail) throw new ChatServiceException("画像非対応");
            yield return "完了";
        }
    }
}
