using System.Net.Http.Json;
using Ateliers.Ai.Assistant.Application.ScreenAnalysis;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.Worker;

namespace Ateliers.Ai.Assistant.Infrastructure.Worker;

public sealed partial class WorkerApiClient : IMultiScreenMonitorClient
{
    /// <summary>対象選択の明示要求を送る直前に、Desktop が当該 Worker だけへ前面表示を許可する。</summary>
    public Action<int>? AllowPickerForeground { get; init; }
    /// <inheritdoc/>
    public Task<ScreenMonitorList> ListMonitorsAsync(CancellationToken token = default) => SendAsync(HttpMethod.Get, "monitors", null, WorkerJsonContext.Default.ScreenMonitorList, token);
    /// <inheritdoc/>
    public Task<ScreenMonitorDetail> GetMonitorAsync(Guid id, CancellationToken token = default) => SendAsync(HttpMethod.Get, $"monitors/{id}", null, WorkerJsonContext.Default.ScreenMonitorDetail, token);
    /// <inheritdoc/>
    public Task<ScreenMonitorOverview> CreateMonitorAsync(string name, CancellationToken token = default) => SendAsync(HttpMethod.Post, "monitors", JsonContent.Create(new CreateScreenMonitorRequest(name), WorkerJsonContext.Default.CreateScreenMonitorRequest), WorkerJsonContext.Default.ScreenMonitorOverview, token);
    /// <inheritdoc/>
    public Task<ScreenMonitorOverview> UpdateMonitorAsync(Guid id, UpdateScreenMonitorRequest request, CancellationToken token = default) => SendAsync(HttpMethod.Put, $"monitors/{id}", JsonContent.Create(request, WorkerJsonContext.Default.UpdateScreenMonitorRequest), WorkerJsonContext.Default.ScreenMonitorOverview, token);
    /// <inheritdoc/>
    public Task<ScreenMonitorOverview> PickMonitorAsync(Guid id, CancellationToken token = default) => MonitorCommandAsync(id, "pick", token);
    /// <inheritdoc/>
    public Task<ScreenMonitorOverview> StartMonitorAsync(Guid id, CancellationToken token = default) => MonitorCommandAsync(id, "start", token);
    /// <inheritdoc/>
    public Task<ScreenMonitorOverview> StopMonitorAsync(Guid id, CancellationToken token = default) => MonitorCommandAsync(id, "stop", token);
    /// <inheritdoc/>
    public Task<ScreenMonitorList> DeleteMonitorAsync(Guid id, CancellationToken token = default) => SendAsync(HttpMethod.Post, $"monitors/{id}/delete", JsonContent.Create(new { }), WorkerJsonContext.Default.ScreenMonitorList, token);
    /// <inheritdoc/>
    public Task<ScreenMonitorList> StopAllMonitorsAsync(CancellationToken token = default) => SendAsync(HttpMethod.Post, "monitors/stop-all", JsonContent.Create(new { }), WorkerJsonContext.Default.ScreenMonitorList, token);
    private Task<ScreenMonitorOverview> MonitorCommandAsync(Guid id, string command, CancellationToken token) => SendAsync(HttpMethod.Post, $"monitors/{id}/{command}", JsonContent.Create(new { }), WorkerJsonContext.Default.ScreenMonitorOverview, token);
    /// <summary>モデル排他内で再確認する自動解放判定。Worker の状態が不明なら安全側で保持する。</summary>
    public async Task<bool> CanAutomaticallyUnloadAsync(string model, CancellationToken token)
    {
        if (connection.InspectPresence() == WorkerPresence.Stopped) return true;
        try { var status = await ListMonitorsAsync(token).ConfigureAwait(false); return !status.Monitors.Any(m => (m.Active || m.Processing) && m.Definition.Settings.Model is { } name && ChatModelSession.SameModel(model, name)); }
        catch (WorkerException) { return false; }
    }
}
