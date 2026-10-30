using System.Runtime.CompilerServices;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.ScreenAnalysis;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Chat;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

internal static class ScreenMonitorTests
{
    public static async Task SharedKeepAlive()
    {
        var handler = new LifetimeHandler(); using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var active = true;
        var service = new OllamaChatService(http, keepAliveSeconds: 300) { KeepAliveSecondsForModel = model => active && model == "vision" ? -1 : 300 };
        await service.LoadModelAsync("vision");
        await foreach (var _ in service.StreamReplyAsync("vision", [new(ChatRole.User, "要約")])) { }
        active = false;
        await foreach (var _ in service.StreamReplyAsync("vision", [new(ChatRole.User, "要約")])) { }
        await service.UnloadModelAsync("vision");
        Check(handler.Values.SequenceEqual(new[] { -1, -1, 300, 0 }), "summary never shortens active monitoring lifetime; idle summary and unload remain unchanged");
    }
    public static async Task ModelProtectionAndReconnect()
    {
        var port = new Port { Loaded = true }; var session = new ChatModelSession(port, port, port) { CanAutomaticallyUnloadAsync = (_, _) => Task.FromResult(false) };
        await session.LoadAsync("vision"); await session.ReleasePreviousAsync("vision"); await session.ShutdownAsync(TimeSpan.FromSeconds(2));
        Check(port.Unloads == 0, "automatic model changes and Desktop exit preserve monitoring model");
        var client = new Client(); using var vm = new ScreenMonitorViewModel(client, port);
        await vm.RefreshCommand.ExecuteAsync(); var item = vm.Comments.Single();
        vm.Prompt = "編集中"; client.Status = client.Status with { State = "Vision 解析中", Comments = [client.Status.Comments[0] with { Content = "# コメント\n追記", Status = "完了" }] };
        await vm.PollAsync(); Check(vm.Prompt == "編集中" && ReferenceEquals(item, vm.Comments.Single()) && item.Content.EndsWith("追記"), "reconnect polling preserves edits and comment identity");
        using var second = new ScreenMonitorViewModel(client, port); await second.PollAsync(); Check(second.Comments.Count == 1 && second.State == "Vision 解析中", "new UI restores runtime status and comments");
        var pending = new TaskCompletionSource<ScreenMonitorStatus>(TaskCreationOptions.RunContinuationsAsynchronously); client.Pending = pending.Task;
        var oldPoll = vm.PollAsync(); client.Pending = null;
        await vm.RefreshCommand.ExecuteAsync(); pending.SetResult(client.Status with { State = "古い状態" }); await oldPoll;
        Check(vm.State != "古い状態", "old asynchronous polling cannot overwrite explicit refresh");
    }
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
    private static async Task Until(Func<bool> predicate)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!predicate()) await Task.Delay(10, deadline.Token); }
    private sealed class Port : IChatService, IModelControlService, IModelUseGate
    {
        public bool Loaded; public int Unloads;
        public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["vision"]);
        public async IAsyncEnumerable<string> StreamReplyAsync(string model, IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.Yield(); yield return "ok"; }
        public Task<IReadOnlyList<LoadedModel>> GetLoadedModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LoadedModel>>(Loaded ? [new("vision", "hash", 1, 1, "Q4", null)] : []);
        public Task LoadModelAsync(string model, CancellationToken cancellationToken = default) { Loaded = true; return Task.CompletedTask; }
        public Task UnloadModelAsync(string model, CancellationToken cancellationToken = default) { Loaded = false; Unloads++; return Task.CompletedTask; }
        public Task<IDisposable> EnterAsync(CancellationToken token) => Task.FromResult<IDisposable>(new Lease());
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }
    private sealed record ScreenMonitorStatus(Guid SessionId, string? TargetName, bool Active, bool Picking, bool Processing, string State, string Error, DateTimeOffset? NextCaptureAt, long Captures, long Skipped, ScreenMonitorSettings Settings, IReadOnlyList<ScreenMonitorComment> Comments);
    private sealed class Client : IMultiScreenMonitorClient
    {
        public ScreenMonitorStatus Status = new(Guid.NewGuid(), "対象", true, false, false, "監視中", "", null, 1, 0, new("vision"), [new(Guid.NewGuid(), DateTimeOffset.UtcNow, "vision", "# コメント", "受信中…")]);
        public Task<ScreenMonitorStatus>? Pending;
        private readonly Guid _id = Guid.NewGuid(), _instance = Guid.NewGuid();
        private ScreenMonitorOverview Overview(ScreenMonitorStatus value) => new(new(_id, "監視", value.Settings), value.TargetName, value.Active, value.Picking, value.Processing, false, value.State, value.Error, value.NextCaptureAt, null, value.Captures, value.Skipped, "");
        public async Task<ScreenMonitorList> ListMonitorsAsync(CancellationToken token = default) => new(_instance, [Overview(Pending is null ? Status : await Pending)], 4);
        public Task<ScreenMonitorDetail> GetMonitorAsync(Guid id, CancellationToken token = default) => Task.FromResult(new ScreenMonitorDetail(_instance, id, Status.SessionId, Guid.Empty, Status.Comments));
        public Task<ScreenMonitorOverview> CreateMonitorAsync(string name, CancellationToken token = default) => Task.FromResult(Overview(Status));
        public Task<ScreenMonitorOverview> UpdateMonitorAsync(Guid id, UpdateScreenMonitorRequest request, CancellationToken token = default) => Task.FromResult(Overview(Status));
        public Task<ScreenMonitorOverview> PickMonitorAsync(Guid id, CancellationToken token = default) => Task.FromResult(Overview(Status));
        public Task<ScreenMonitorOverview> StartMonitorAsync(Guid id, CancellationToken token = default) => Task.FromResult(Overview(Status));
        public Task<ScreenMonitorOverview> StopMonitorAsync(Guid id, CancellationToken token = default) => Task.FromResult(Overview(Status));
        public Task<ScreenMonitorList> DeleteMonitorAsync(Guid id, CancellationToken token = default) => ListMonitorsAsync(token);
        public Task<ScreenMonitorList> StopAllMonitorsAsync(CancellationToken token = default) => ListMonitorsAsync(token);
    }
    private sealed class LifetimeHandler : HttpMessageHandler
    {
        public List<int> Values = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var json = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Values.Add(json.RootElement.GetProperty("keep_alive").GetInt32());
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"message\":{\"content\":\"ok\"},\"done\":true}\n", System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
