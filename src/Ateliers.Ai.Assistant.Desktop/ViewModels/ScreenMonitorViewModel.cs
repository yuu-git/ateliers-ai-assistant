using System;
using System.Linq;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.ScreenAnalysis;
using Ateliers.Ai.Assistant.Core.Chat;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>一件の一覧状態と編集途中の入力。コメントも ID ごとに保持する。</summary>
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public sealed partial class ScreenMonitorItemViewModel : ObservableObject
{
    internal ScreenMonitorOverview Snapshot;
    internal string DraftName, DraftPrompt;
    internal string? DraftModel;
    internal int DraftInterval;
    internal bool Dirty;
    /// <summary>一覧行を保存済みの定義から生成する。</summary>
    public ScreenMonitorItemViewModel(ScreenMonitorOverview value) { Snapshot = value; DraftName = value.Definition.Name; DraftPrompt = value.Definition.Settings.Prompt; DraftModel = value.Definition.Settings.Model; DraftInterval = value.Definition.Settings.IntervalSeconds; }
    /// <summary>定義の不変 ID。</summary>
    public Guid Id => Snapshot.Definition.MonitorId;
    /// <summary>保存済みの表示名。</summary>
    public string Name => Snapshot.Definition.Name;
    /// <summary>一覧に対象・進行状態・次回・最終解析・エラーを表示する。</summary>
    public string Summary => $"{Snapshot.TargetName ?? "対象未選択"}\n{Snapshot.State}\n次回: {Time(Snapshot.NextCaptureAt)} / 最終解析: {Time(Snapshot.LastAnalysisAt)}" + (Snapshot.Error.Length > 0 ? "\n" + Snapshot.Error : "");
    private static string Time(DateTimeOffset? value) => value?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
    /// <summary>選択変更・一覧更新で本文インスタンスを作り直さない。</summary>
    public ObservableCollection<ChatMessageViewModel> Comments { get; } = [];
    internal void Apply(ScreenMonitorOverview value)
    {
        Snapshot = value;
        if (!Dirty) { DraftName = value.Definition.Name; DraftPrompt = value.Definition.Settings.Prompt; DraftModel = value.Definition.Settings.Model; DraftInterval = value.Definition.Settings.IntervalSeconds; }
        OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Summary));
    }
}

/// <summary>Worker の複数監視を表示・操作する。Windows API と HTTP に依存しない。</summary>
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public sealed partial class ScreenMonitorViewModel : ObservableObject, IDisposable
{
    private readonly IMultiScreenMonitorClient _client;
    private readonly IChatService _chat;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy, _polling, _connected, _disposed;
    private long _revision, _detailRevision;
    private Guid _instance;
    private string _error = "";
    private ScreenMonitorItemViewModel? _selected;
    /// <summary>既存のモデル一覧と ID 操作ポートを接続する。</summary>
    public ScreenMonitorViewModel(IMultiScreenMonitorClient client, IChatService chat)
    {
        _client = client; _chat = chat;
        AddCommand = new(() => ExecuteAsync(async () => { var created = await _client.CreateMonitorAsync("画面監視" + (Monitors.Count + 1), _lifetime.Token); await RefreshCoreAsync(); Selected = Monitors.FirstOrDefault(m => m.Id == created.Definition.MonitorId); }), () => _connected && !_busy && Monitors.Count < ScreenMonitorLimits.MaximumMonitors);
        PickCommand = new(() => WithSelectedAsync(async m => { await _client.PickMonitorAsync(m.Id, _lifetime.Token); }), () => CanEdit && !Monitors.Any(m => m.Snapshot.Picking));
        SaveCommand = new(() => WithSelectedAsync(SaveAsync), () => CanEdit);
        StartCommand = new(() => WithSelectedAsync(async m => { await SaveAsync(m); await _client.StartMonitorAsync(m.Id, _lifetime.Token); }), () => CanEdit && !string.IsNullOrWhiteSpace(Model) && !string.IsNullOrWhiteSpace(Prompt) && _selected?.Snapshot.TargetName is not null);
        StopCommand = new(() => WithSelectedAsync(async m => { await _client.StopMonitorAsync(m.Id, _lifetime.Token); }), () => !_busy && _connected && _selected is { Snapshot.Active: true } or { Snapshot.Picking: true });
        DeleteCommand = new(() => WithSelectedAsync(async m => { if (ConfirmDeleteAsync is null || !await ConfirmDeleteAsync(m.Name)) return; await _client.DeleteMonitorAsync(m.Id, _lifetime.Token); }), () => !_busy && _connected && _selected is { Snapshot.Picking: false, Snapshot.Deleting: false });
        StopAllCommand = new(() => ExecuteAsync(async () => { await _client.StopAllMonitorsAsync(_lifetime.Token); await RefreshCoreAsync(); }), () => !_busy && _connected && Monitors.Any(m => m.Snapshot.Active || m.Snapshot.Processing || m.Snapshot.Picking));
        RefreshCommand = new(() => ExecuteAsync(async () => { await RefreshCoreAsync(); await RefreshModelsAsync(); }), () => !_busy);
    }
    /// <summary>削除操作前の表示層による確認。</summary>
    public Func<string, Task<bool>>? ConfirmDeleteAsync { get; set; }
    /// <summary>登録済み一覧。定義 ID でインスタンスを維持する。</summary>
    public ObservableCollection<ScreenMonitorItemViewModel> Monitors { get; } = [];
    /// <summary>導入済みモデル名。能力を自動判定しない。</summary>
    public ObservableCollection<string> Models { get; } = [];
    /// <summary>表示中の監視。他の監視の実行と取消を変更しない。</summary>
    public ScreenMonitorItemViewModel? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) { _detailRevision++; Notify(); if (value is not null) _ = LoadDetailAsync(value); } }
    }
    /// <summary>選択中監視の本文だけを既存 Markdown 表示へ渡す。</summary>
    public ObservableCollection<ChatMessageViewModel> Comments => _selected?.Comments ?? _empty;
    private readonly ObservableCollection<ChatMessageViewModel> _empty = [];
    /// <summary>周期の固定候補。</summary>
    public System.Collections.Generic.IReadOnlyList<int> Intervals => ScreenMonitorSettings.Intervals;
    /// <summary>登録上限と稼働数を明示する。</summary>
    public string CountText => $"登録 {Monitors.Count} / {ScreenMonitorLimits.MaximumMonitors} 件・稼働 {Monitors.Count(m => m.Snapshot.Active)} 件";
    /// <summary>選択中監視だけの編集可否。</summary>
    public bool CanEdit => !_disposed && !_busy && _connected && _selected is { Snapshot.Active: false, Snapshot.Processing: false, Snapshot.Picking: false, Snapshot.Deleting: false };
    /// <summary>接続状態を不明と停止で区別する。</summary>
    public string State => !_connected ? "Worker 未接続・監視状態不明" : _selected?.Snapshot.State ?? "監視を追加してください。";
    /// <summary>対象の OS 表示名。ログには送らない。</summary>
    public string Target => _selected?.Snapshot.TargetName ?? "対象未選択";
    /// <summary>実行と周期破棄の件数。</summary>
    public string Schedule => _selected is { } m ? $"取得 {m.Snapshot.Captures} 回 / スキップ {m.Snapshot.Skipped} 回 {m.Snapshot.SkipReason}" : "";
    /// <summary>編集中の識別用名前。</summary>
    public string Name { get => _selected?.DraftName ?? ""; set { if (_selected is { } m && m.DraftName != value) { m.DraftName = value; m.Dirty = true; OnPropertyChanged(); } } }
    /// <summary>監視ごとに保持する編集中プロンプト。</summary>
    public string Prompt { get => _selected?.DraftPrompt ?? ""; set { if (_selected is { } m && m.DraftPrompt != value) { m.DraftPrompt = value; m.Dirty = true; Notify(); } } }
    /// <summary>監視ごとの利用モデル。</summary>
    public string? Model { get => _selected?.DraftModel; set { if (_selected is { } m && m.DraftModel != value) { m.DraftModel = value; m.Dirty = true; Notify(); } } }
    /// <summary>監視ごとの周期。</summary>
    public int Interval { get => _selected?.DraftInterval ?? 60; set { if (_selected is { } m && m.DraftInterval != value) { m.DraftInterval = value; m.Dirty = true; OnPropertyChanged(); } } }
    /// <summary>操作または選択中監視のエラー。</summary>
    public string Error { get => _error; private set { SetProperty(ref _error, value); OnPropertyChanged(nameof(HasError)); } }
    /// <summary>警告の表示要否。</summary>
    public bool HasError => Error.Length > 0;
    /// <summary>停止定義を追加する。</summary>
    public AsyncCommand AddCommand { get; }
    /// <summary>選択中定義の削除を依頼する。</summary>
    public AsyncCommand DeleteCommand { get; }
    /// <summary>選択中監視の対象を OS で選ぶ。</summary>
    public AsyncCommand PickCommand { get; }
    /// <summary>選択中監視の定義だけを保存する。</summary>
    public AsyncCommand SaveCommand { get; }
    /// <summary>選択中監視を保存して開始する。</summary>
    public AsyncCommand StartCommand { get; }
    /// <summary>選択中監視だけを止める。</summary>
    public AsyncCommand StopCommand { get; }
    /// <summary>すべての監視に中止を通知する。</summary>
    public AsyncCommand StopAllCommand { get; }
    /// <summary>状態とモデル一覧を再取得する。</summary>
    public AsyncCommand RefreshCommand { get; }
    /// <summary>既存5秒タイマーから呼ぶ。編集中入力とコメントの選択を保持する。</summary>
    public async Task PollAsync()
    {
        if (_disposed || _busy || _polling) return; _polling = true;
        var revision = _revision;
        try { await RefreshCoreAsync(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception) { if (revision == _revision) { _connected = false; Notify(); } }
        finally { _polling = false; }
    }
    private Task WithSelectedAsync(Func<ScreenMonitorItemViewModel, Task> action)
    { var selected = _selected; return selected is null ? Task.CompletedTask : ExecuteAsync(async () => { await action(selected); await RefreshCoreAsync(); }); }
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (_disposed || _busy) return; _revision++; _detailRevision++; _busy = true; Error = ""; Notify();
        try { await action(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception) { Error = exception.Message; }
        finally { _busy = false; Notify(); }
    }
    private async Task SaveAsync(ScreenMonitorItemViewModel m)
    {
        var request = new UpdateScreenMonitorRequest(m.DraftName, new(m.DraftModel, m.DraftInterval, m.DraftPrompt), m.Snapshot.Definition.Revision);
        var value = await _client.UpdateMonitorAsync(m.Id, request, _lifetime.Token);
        // 入力を保存要求のスナップショットと比較し、遅い完了で新しい下書きを破棄しない。
        m.Dirty = m.DraftName != request.Name || m.DraftModel != request.Settings.Model || m.DraftPrompt != request.Settings.Prompt || m.DraftInterval != request.Settings.IntervalSeconds;
        m.Apply(value);
    }
    private async Task RefreshCoreAsync()
    {
        var revision = _revision; var value = await _client.ListMonitorsAsync(_lifetime.Token);
        if (_disposed || revision != _revision) return;
        _connected = true;
        if (_instance != value.InstanceId) { _instance = value.InstanceId; _detailRevision++; foreach (var m in Monitors) m.Comments.Clear(); }
        var ids = value.Monitors.Select(m => m.Definition.MonitorId).ToHashSet();
        foreach (var m in Monitors.Where(m => !ids.Contains(m.Id)).ToArray()) Monitors.Remove(m);
        foreach (var snapshot in value.Monitors)
        {
            var existing = Monitors.FirstOrDefault(m => m.Id == snapshot.Definition.MonitorId);
            if (existing is null) Monitors.Add(new(snapshot)); else existing.Apply(snapshot);
        }
        if (_selected is null || !Monitors.Contains(_selected)) Selected = Monitors.FirstOrDefault();
        Error = _selected?.Snapshot.Error ?? ""; Notify();
        if (_selected is { } selected) await LoadDetailAsync(selected);
    }
    private async Task LoadDetailAsync(ScreenMonitorItemViewModel selected)
    {
        var revision = ++_detailRevision; var instance = _instance;
        try
        {
            var detail = await _client.GetMonitorAsync(selected.Id, _lifetime.Token);
            if (_disposed || revision != _detailRevision || instance != _instance || detail.InstanceId != instance || detail.MonitorId != selected.Id || !Monitors.Contains(selected)) return;
            var ids = detail.Comments.Select(c => c.Id).ToHashSet();
            foreach (var item in selected.Comments.Where(c => !ids.Contains(c.Id)).ToArray()) selected.Comments.Remove(item);
            foreach (var entry in detail.Comments)
            {
                var item = selected.Comments.FirstOrDefault(c => c.Id == entry.Id);
                if (item is null) selected.Comments.Add(new(new(ChatRole.Assistant, entry.Content), entry.Model + " · " + entry.CapturedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"), entry.Status, entry.Id, entry.CapturedAt));
                else { if (entry.Content.StartsWith(item.Content, StringComparison.Ordinal)) item.Append(entry.Content[item.Content.Length..]); item.StatusText = entry.Status; }
            }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception) { if (revision == _detailRevision && ReferenceEquals(_selected, selected)) Error = exception.Message; }
    }
    private async Task RefreshModelsAsync()
    {
        var values = await _chat.GetModelsAsync(_lifetime.Token); if (Models.SequenceEqual(values)) return;
        var selected = _selected; var model = selected?.DraftModel; var dirty = selected?.Dirty ?? false;
        Models.Clear(); foreach (var name in values) Models.Add(name);
        if (selected is not null) { selected.DraftModel = model; selected.Dirty = dirty; } OnPropertyChanged(nameof(Model));
    }
    private void Notify()
    {
        foreach (var property in new[] { nameof(CanEdit), nameof(State), nameof(Target), nameof(Schedule), nameof(Comments), nameof(Name), nameof(Model), nameof(Prompt), nameof(Interval), nameof(CountText) }) OnPropertyChanged(property);
        foreach (var command in new[] { AddCommand, DeleteCommand, PickCommand, SaveCommand, StartCommand, StopCommand, StopAllCommand, RefreshCommand }) command.NotifyCanExecuteChanged();
    }
    /// <summary>UI のみ切断する。Worker の監視を停止しない。</summary>
    public void Dispose() { _disposed = true; _revision++; _detailRevision++; _lifetime.Cancel(); }
}
