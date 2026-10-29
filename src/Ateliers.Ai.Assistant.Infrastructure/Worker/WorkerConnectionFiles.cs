using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.Worker;

namespace Ateliers.Ai.Assistant.Infrastructure.Worker;

/// <summary>環境別の秘密接続情報と単一 Worker のハンドルを管理する。秘密は API 応答・ログへ出さない。</summary>
public sealed class WorkerConnectionFiles(IApplicationPaths paths)
{
    /// <summary>Debug / Release で別の Local 保存先。</summary>
    public string DirectoryPath => Path.Combine(paths.LocalDirectory, "worker");
    private string EndpointPath => Path.Combine(DirectoryPath, "endpoint.json");
    /// <summary>接続情報や ACL を変更せず、単一起動ロックの保持だけを確認する。</summary>
    public WorkerPresence InspectPresence()
    {
        try
        {
            using var file = new FileStream(Path.Combine(DirectoryPath, "instance.lock"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return WorkerPresence.Stopped;
        }
        catch (FileNotFoundException) { return WorkerPresence.Stopped; }
        catch (DirectoryNotFoundException) { return WorkerPresence.Stopped; }
        catch (UnauthorizedAccessException) { return WorkerPresence.Unknown; }
        catch (IOException exception) { return (exception.HResult & 0xffff) is 32 or 33 ? WorkerPresence.Running : WorkerPresence.Unknown; }
    }
    /// <summary>現在ユーザーと SYSTEM だけが参照できる接続情報フォルダーを作成する。</summary>
    public void EnsurePrivateDirectory()
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateDirectory(DirectoryPath); return; }
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(DirectoryPath);
        directory.Create(security);
        directory.SetAccessControl(security);
    }
    /// <summary>重複起動は null を返して終了する。クラッシュ時は OS がハンドルを解放する。</summary>
    public IDisposable? TryEnterInstance()
    {
        EnsurePrivateDirectory();
        try { return new FileStream(Path.Combine(DirectoryPath, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }
    /// <summary>API 準備後だけ接続情報を原子的に公開する。</summary>
    public async Task PublishAsync(WorkerEndpoint endpoint, CancellationToken token)
    {
        EnsurePrivateDirectory();
        var temporary = EndpointPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(endpoint, WorkerJsonContext.Default.WorkerEndpoint), token).ConfigureAwait(false);
            File.Move(temporary, EndpointPath, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }
    /// <summary>固定の loopback 接続先だけを受け入れる。古い API や不正ファイルを盲信しない。</summary>
    public async Task<WorkerEndpoint> ReadAsync(CancellationToken token)
    {
        try
        {
            await using var stream = File.OpenRead(EndpointPath);
            if (stream.Length > 8192) throw new JsonException();
            var endpoint = await JsonSerializer.DeserializeAsync(stream, WorkerJsonContext.Default.WorkerEndpoint, token).ConfigureAwait(false) ?? throw new JsonException();
            if (endpoint.Protocol != WorkerCoordinator.Protocol || endpoint.Version != WorkerCoordinator.ImplementationVersion)
                throw new WorkerException("UI と Worker のバージョンが異なります。Worker を終了し、現在のアプリから起動し直してください。", connectionFailure: true);
            if (!Uri.TryCreate(endpoint.Address, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" ||
                uri.Port <= 0 || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                endpoint.Token?.Length != 64 || !endpoint.Token.All(Uri.IsHexDigit)) throw new JsonException();
            return endpoint;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { throw new WorkerException("Worker に接続できません。設定画面の「Worker を開始」を実行してください。", exception, true); }
    }
    /// <summary>自身の接続情報だけを削除する。</summary>
    public async Task RemoveAsync(Guid instance)
    {
        try { if ((await ReadAsync(CancellationToken.None).ConfigureAwait(false)).InstanceId == instance) File.Delete(EndpointPath); }
        catch (WorkerException) { }
        catch (IOException) { }
    }
}
