using System;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Settings;
using Ateliers.Ai.Assistant.Application.Storage;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>固定保存先の表示とユーザー設定の読書き。Explorer・既知フォルダー API は直接扱わない。</summary>
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IUserSettingsStore _store;
    private readonly IAppDiagnostics _diagnostics;
    private bool _readable;
    private long _request;
    private string _error = "";
    private Task _pendingSave = Task.CompletedTask;
    private UserSettings _current = new();
    /// <summary>再読み込みした Web 設定を画面に適用する。モデルのロードは行わない。</summary>
    public Action<string?, string?>? ApplyWebSettings { get; set; }
    /// <summary>パス、設定ストア、固定フォルダーを開く処理をホストから受け取る。</summary>
    public SettingsViewModel(IApplicationPaths paths, IUserSettingsStore store, IAppDiagnostics diagnostics, Func<DataFolderKind, Task> openFolder)
    {
        Paths = paths; _store = store; _diagnostics = diagnostics;
        OpenSettingsCommand = new(() => OpenAsync(DataFolderKind.Settings, openFolder), () => true);
        OpenLocalCommand = new(() => OpenAsync(DataFolderKind.Local, openFolder), () => true);
        OpenDatabaseCommand = new(() => OpenAsync(DataFolderKind.Database, openFolder), () => true);
        OpenLogsCommand = new(() => OpenAsync(DataFolderKind.Logs, openFolder), () => true);
        ReloadCommand = new(InitializeAsync, () => true);
    }
    /// <summary>DB・ログと共通の固定パス。</summary>
    public IApplicationPaths Paths { get; }
    /// <summary>誤操作を防ぐため設定画面へ表示する保存環境。</summary>
    public string DataEnvironmentText => Paths.DataEnvironment == AppDataEnvironment.Debug
        ? "Debug（開発用データ）" : "Release（通常利用データ）";
    /// <summary>Roaming 設定フォルダーを開く。</summary>
    public AsyncCommand OpenSettingsCommand { get; }
    /// <summary>Local データフォルダーを開く。</summary>
    public AsyncCommand OpenLocalCommand { get; }
    /// <summary>DB 格納フォルダーを開く。ファイル自体は起動しない。</summary>
    public AsyncCommand OpenDatabaseCommand { get; }
    /// <summary>ログ格納フォルダーを開く。</summary>
    public AsyncCommand OpenLogsCommand { get; }
    /// <summary>修復後などに設定を再読み込みする。</summary>
    public AsyncCommand ReloadCommand { get; }
    /// <summary>保存されたモデルをロードせず選択するため、ホストへ依頼する処理。</summary>
    public Func<string?, Task>? ApplySelectedModelAsync { get; set; }
    /// <summary>設定適用中のモデル変更を再保存しないための状態。</summary>
    public bool IsApplyingSettings { get; private set; }
    /// <summary>設定・フォルダー操作の失敗表示。</summary>
    public string ErrorMessage { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    /// <summary>失敗が表示対象かどうか。</summary>
    public bool HasError => ErrorMessage.Length != 0;
    /// <summary>設定を読み、存在するモデルだけを選択する。不正なファイルは自動上書きしない。</summary>
    public async Task InitializeAsync()
    {
        IsApplyingSettings = true;
        try
        {
            await _pendingSave;
            var settings = await _store.LoadAsync();
            _current = settings;
            _readable = true; ErrorMessage = "";
            if (ApplySelectedModelAsync is not null) await ApplySelectedModelAsync(settings.LastSelectedModel);
            ApplyWebSettings?.Invoke(settings.WebFeedUrl, settings.WebSummaryModel);
        }
        catch (Exception exception) { _readable = false; Failure("settings.initialize", exception); }
        finally { IsApplyingSettings = false; }
    }
    /// <summary>最後の明示選択を保存する。読込失敗中は既存の不正ファイルを保護する。</summary>
    public Task RememberModelAsync(string? model)
    {
        if (IsApplyingSettings || !_readable) return Task.CompletedTask;
        var request = ++_request;
        _current = _current with { LastSelectedModel = model };
        return _pendingSave = SaveSettingsAsync(_current, request, _pendingSave);
    }
    /// <summary>他の設定値を維持して Web 設定を同じ保存キューへ追加する。不正ファイルの保護中は保存しない。</summary>
    public Task<bool> RememberWebAsync(string url, string? model)
    {
        if (!_readable) return Task.FromResult(false);
        _current = _current with { WebFeedUrl = url, WebSummaryModel = model };
        var save = SaveSettingsAsync(_current, ++_request, _pendingSave);
        _pendingSave = save;
        return save;
    }
    /// <summary>終了前に最後の設定保存の完了を待つ。</summary>
    public Task FlushAsync() => _pendingSave;
    /// <summary>設定を書き込み、古い保存の完了で新しいエラー表示を上書きしない。</summary>
    private async Task<bool> SaveSettingsAsync(UserSettings settings, long request, Task previous)
    {
        try { await previous; await _store.SaveAsync(settings); if (request == _request) ErrorMessage = ""; return true; }
        catch (Exception exception) { if (request == _request) Failure("settings.save", exception); return false; }
    }
    /// <summary>内部固定フォルダーの起動失敗を通知する。</summary>
    private async Task OpenAsync(DataFolderKind kind, Func<DataFolderKind, Task> openFolder)
    {
        try { await openFolder(kind); ErrorMessage = ""; }
        catch (Exception exception) { Failure("storage.folder.open", exception); }
    }
    /// <summary>機密内容を記録せず、利用者に失敗を伝える。</summary>
    private void Failure(string operation, Exception exception)
    {
        _diagnostics.Error(operation, exception);
        ErrorMessage = exception is StorageException ? exception.Message : "設定または保存先フォルダーを操作できませんでした。アクセス権を確認してください。";
    }
}
