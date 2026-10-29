using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.Worker;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Ateliers.Ai.Assistant.Desktop.Storage;

/// <summary>パッケージは StartupTask、非パッケージは現在ユーザーの Run キー。管理者権限は要求しない。</summary>
public sealed class WorkerLauncher(IApplicationPaths paths) : IWorkerLauncher
{
    private const string StartupId = "AteliersAiWorker";
    private string RunName => paths.DataEnvironment == AppDataEnvironment.Debug ? "AteliersAiWorker.Debug" : "AteliersAiWorker";
    private string Executable => Path.Combine(AppContext.BaseDirectory, "Worker", "Ateliers.Ai.Assistant.Worker.exe");
    private static bool Packaged { get { try { return Package.Current.Id.FullName.Length != 0; } catch { return false; } } }
    /// <inheritdoc/>
    public Task StartAsync()
    {
        EnsureExecutable();
        Process.Start(new ProcessStartInfo(Executable) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(Executable)! });
        return Task.CompletedTask;
    }
    /// <inheritdoc/>
    public async Task<WorkerStartupState> GetStartupAsync()
    {
        if (Packaged)
        {
            var task = await StartupTask.GetAsync(StartupId);
            return new(task.State == StartupTaskState.Enabled, "サインイン時の自動起動: " + task.State + "（Windows のスタートアップ設定でも変更できます）");
        }
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var registered = key?.GetValue(RunName) as string == "\"" + Executable + "\"";
        return new(registered, registered ? "サインイン時起動を登録済みです。Windows のスタートアップ設定でも有効状態を確認してください。" : "サインイン時起動は未登録です。Worker の手動開始は可能です。");
    }
    /// <inheritdoc/>
    public async Task<WorkerStartupState> SetStartupAsync(bool enabled)
    {
        if (enabled) EnsureExecutable();
        if (Packaged)
        {
            var task = await StartupTask.GetAsync(StartupId);
            if (enabled) await task.RequestEnableAsync(); else task.Disable();
        }
        else
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled) key.SetValue(RunName, "\"" + Executable + "\""); else key.DeleteValue(RunName, false);
        }
        return await GetStartupAsync();
    }
    private void EnsureExecutable()
    {
        if (!File.Exists(Executable)) throw new WorkerException("Worker の実行ファイルがありません。アプリと Worker を同じ構成でビルドまたは発行してください。");
    }
}
