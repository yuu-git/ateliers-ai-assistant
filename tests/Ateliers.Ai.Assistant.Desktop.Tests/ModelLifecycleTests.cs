using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Chat;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

internal static class ModelLifecycleTests
{
    public static async Task HttpContract()
    {
        var requests = new List<JsonElement>();
        using var client = new HttpClient(new StubHandler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.That(request.RequestUri!.AbsolutePath == "/api/ps", "ロード状態は GET /api/ps");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    {"models":[{"name":"test:latest","digest":"abc","size":2048,"size_vram":1024,
                    "details":{"quantization_level":"Q4_K_M"},"expires_at":"9999-12-31T23:59:59Z"}]}
                    """, Encoding.UTF8, "application/json") };
            }
            Assert.That(request.RequestUri!.AbsolutePath == "/api/chat", "操作も POST /api/chat");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            requests.Add(document.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"done\":true}", Encoding.UTF8, "application/json") };
        })) { BaseAddress = new Uri("http://localhost:11434/") };
        var service = new OllamaChatService(client);
        var models = await service.GetLoadedModelsAsync();
        Assert.That(models[0].Name == "test:latest" && models[0].Size == 2048 && models[0].SizeVram == 1024 &&
            models[0].Quantization == "Q4_K_M" && models[0].ExpiresAt!.Value.Year == 9999, "ps フィールドの変換");
        var display = new LoadedModelViewModel(models[0]);
        Assert.That(display.ExpiresAt == "無期限" && display.Quantization == "Q4_K_M", "画面表示への変換");
        await service.LoadModelAsync("test");
        await service.UnloadModelAsync("test");
        Assert.That(requests.Count == 2 && requests[0].GetProperty("keep_alive").GetInt32() == -1 &&
            requests[1].GetProperty("keep_alive").GetInt32() == 0, "起動 -1 / 停止 0");
        Assert.That(requests.All(request => request.GetProperty("messages").GetArrayLength() == 0 &&
            !request.GetProperty("stream").GetBoolean()), "空 messages の非ストリーム操作");
    }

    public static async Task OwnershipAndManualStop()
    {
        var control = new TestModelControl();
        control.Running.Add(TestModelControl.Model("outside"));
        var session = new ChatModelSession(new TestChatService(), control);
        await session.RefreshAsync();
        Assert.That(session.ManagedModels.Count == 0, "ps のモデルを管理対象に取り込まない");
        await session.ReleasePreviousAsync("outside");
        Assert.That(control.Stopped.Count == 0, "未管理の旧モデルを自動停止しない");
        await session.LoadAsync("owned");
        Assert.That(session.ManagedModels.Contains("owned") && session.LoadedModels.Count == 2 && control.Refreshes >= 3, "ロード後に ps 再取得・追跡");
        await session.ShutdownAsync(TimeSpan.FromSeconds(1));
        Assert.That(control.Stopped.SequenceEqual(new[] { "owned" }) && control.Running.Single().Name == "outside", "終了は管理モデルだけ停止");
        Assert.That(ReferenceEquals(session.ShutdownAsync(TimeSpan.FromSeconds(1)), session.ShutdownAsync(TimeSpan.FromSeconds(1))), "終了は一度だけ");
        var manual = new ChatModelSession(new TestChatService(), control);
        await manual.UnloadAsync("outside");
        Assert.That(control.Running.Count == 0 && manual.ManagedModels.Count == 0, "明示操作では一覧の任意モデルを停止できる");
        Assert.That(ChatModelSession.SameModel("org/name", "org/name:latest") &&
            !ChatModelSession.SameModel("name:3b", "name:8b"), "latest 補完だけを正規化");
    }

    public static async Task SelectionAndSharedState()
    {
        var control = new TestModelControl();
        var chat = new TestChatService();
        var session = new ChatModelSession(chat, control);
        using var vm = new ChatViewModel(session, session);
        using var management = new ModelManagementViewModel(session);
        await vm.InitializeAsync();
        vm.SelectedModel = "model-a";
        await vm.ModelChangeTask;
        Assert.That(vm.LoadModelCommand.CanExecute(null) && !vm.UnloadModelCommand.CanExecute(null), "未ロード時のボタン");
        await vm.LoadModelCommand.ExecuteAsync();
        Assert.That(!vm.LoadModelCommand.CanExecute(null) && vm.UnloadModelCommand.CanExecute(null) && management.Models.Count == 1, "両画面へロード状態反映");
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        control.BeforeUnload = (_, _) => { entered.TrySetResult(); return stopping.Task; };
        vm.InputText = "競合確認";
        vm.SelectedModel = "model-b";
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(!vm.CanEdit && !vm.SendCommand.CanExecute(null) && !management.CanOperate, "切替中は両画面の操作を排他");
        stopping.SetResult();
        await vm.ModelChangeTask;
        Assert.That(control.Stopped.SequenceEqual(new[] { "model-a" }) && control.Started.SequenceEqual(new[] { "model-a" }), "旧モデル停止、新モデルはロードしない");
        Assert.That(vm.SelectedModel == "model-b" && vm.CanEdit && vm.LoadModelCommand.CanExecute(null), "切替完了");
        control.BeforeUnload = (_, _) => Task.CompletedTask;
        await vm.LoadModelCommand.ExecuteAsync();
        management.SelectedModel = management.Models.Single();
        await management.UnloadCommand.ExecuteAsync();
        Assert.That(management.IsEmpty && vm.LoadModelCommand.CanExecute(null), "管理画面から停止するとチャットへ反映");
    }

    public static async Task ChatExclusionAndCleanup()
    {
        var chat = new TestChatService();
        var control = new TestModelControl();
        control.Running.Add(TestModelControl.Model("model-a"));
        control.Running.Add(TestModelControl.Model("outside"));
        var session = new ChatModelSession(chat, control);
        using var vm = new ChatViewModel(session, session);
        using var management = new ModelManagementViewModel(session);
        await vm.InitializeAsync();
        vm.SelectedModel = "model-a";
        await vm.ModelChangeTask;
        management.SelectedModel = management.Models.First();
        chat.EnqueuePending("途中");
        vm.InputText = "生成中";
        var sending = vm.SendCommand.ExecuteAsync();
        Assert.That(vm.IsSending && !vm.UnloadModelCommand.CanExecute(null) && !management.UnloadCommand.CanExecute(null), "チャット中は両画面から停止できない");
        await Assert.Throws<ChatServiceException>(() => session.UnloadAsync("model-a"));
        Assert.That(control.Stopped.Count == 0, "サービス層でも競合停止を拒否");
        vm.Dispose(); // Window の終了順序と同じ: 生成中止 → shutdown
        await session.ShutdownAsync(TimeSpan.FromSeconds(1));
        await sending;
        Assert.That(chat.Token.IsCancellationRequested && control.Stopped.SequenceEqual(new[] { "model-a" }), "使用したモデルを終了時に停止");
        Assert.That(control.Running.Single().Name == "outside", "他のロード済みモデルは維持");
    }

    public static async Task FailuresAndDeadline()
    {
        var control = new TestModelControl();
        var session = new ChatModelSession(new TestChatService(), control);
        using var vm = new ChatViewModel(session, session);
        await vm.InitializeAsync();
        vm.SelectedModel = "model-a";
        await vm.ModelChangeTask;
        await vm.LoadModelCommand.ExecuteAsync();
        control.BeforeUnload = (_, _) => Task.FromException(new ChatServiceException("停止失敗"));
        vm.SelectedModel = "model-b";
        await vm.ModelChangeTask;
        Assert.That(vm.HasError && vm.ErrorMessage == "停止失敗" && session.ManagedModels.Contains("model-a") &&
            session.LoadedModels.Count == 1 && vm.CanEdit, "停止失敗の通知・管理対象維持・操作復帰");
        control.ListError = new ChatServiceException("ps 通信失敗");
        await vm.RefreshModelsCommand.ExecuteAsync();
        Assert.That(vm.HasError && !session.IsStateKnown && !vm.LoadModelCommand.CanExecute(null) && !vm.UnloadModelCommand.CanExecute(null), "状態取得失敗時は未知として操作禁止");
        control.ListError = null;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        control.BeforeUnload = (_, _) => blocked.Task; // キャンセル非協調の通信も上限内で終了する
        var watch = Stopwatch.StartNew();
        await session.ShutdownAsync(TimeSpan.FromMilliseconds(100));
        Assert.That(watch.Elapsed < TimeSpan.FromSeconds(2) && session.IsClosing, "終了を通信の停止待ちで妨げない");
        blocked.TrySetResult();
    }
}

internal sealed class TestModelControl : IModelControlService
{
    public List<LoadedModel> Running { get; } = new();
    public List<string> Started { get; } = new();
    public List<string> Stopped { get; } = new();
    public int Refreshes { get; private set; }
    public Exception? ListError { get; set; }
    public Func<string, CancellationToken, Task> BeforeUnload { get; set; } = (_, _) => Task.CompletedTask;
    public static LoadedModel Model(string name) => new(name, name + "-digest", 100, 50, "Q4_K_M", DateTimeOffset.UtcNow.AddMinutes(5));
    public Task<IReadOnlyList<LoadedModel>> GetLoadedModelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Refreshes++;
        if (ListError is not null) return Task.FromException<IReadOnlyList<LoadedModel>>(ListError);
        return Task.FromResult<IReadOnlyList<LoadedModel>>(Running.ToArray());
    }
    public Task LoadModelAsync(string model, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Started.Add(model);
        Running.Add(Model(model));
        return Task.CompletedTask;
    }
    public async Task UnloadModelAsync(string model, CancellationToken cancellationToken = default)
    {
        await BeforeUnload(model, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Stopped.Add(model);
        Running.RemoveAll(item => ChatModelSession.SameModel(item.Name, model));
    }
}
