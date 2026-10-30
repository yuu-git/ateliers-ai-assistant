using System.Diagnostics;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Ateliers.Ai.Assistant.Infrastructure.Storage.Temp;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

internal static class TempManagementTests
{
    public static Task MemoryTimeAndBatch()
    {
        var clock = new Clock(); var store = new CapturedImageStore(new(3, 3 * 520, TimeSpan.FromMinutes(5)), clock);
        var session = Guid.NewGuid(); var target = Guid.NewGuid(); var image = new ChatImage([1], 1, 1);
        var current = store.Register(session, target, clock.Now, image);
        var old = store.Register(session, target, clock.Now.AddSeconds(-61), image);
        var middle = store.Register(session, target, clock.Now.AddSeconds(-29), image);
        Check(store.List(session, target).Select(e => e.Id).SequenceEqual(new[] { old.Id, middle.Id, current.Id }), "actual acquisition ordering");
        Throws(() => store.AcquireNearest(session, target, [clock.Now, clock.Now.AddMilliseconds(100)], TimeSpan.FromSeconds(1)));
        using (var batch = store.AcquireNearest(session, target, [clock.Now.AddSeconds(-60), clock.Now.AddSeconds(-30), clock.Now], TimeSpan.FromSeconds(2)))
        {
            Check(batch.Images.Select(e => e.Id).SequenceEqual(new[] { old.Id, middle.Id, current.Id }), "nearest real times and atomic batch");
            Check(!store.Remove(session, target, old.Id), "explicit deletion protects active images");
            Throws(() => store.Register(session, target, clock.Now, image));
            clock.Now += TimeSpan.FromMinutes(6); store.Cleanup();
            Check(batch.Images.Count == 3 && batch.Images[0].Image.Base64Data == image.Base64Data, "expired images remain protected for existing analysis");
            Throws(() => store.Acquire(session, target, [old.Id]));
            Throws(() => store.Register(session, target, clock.Now, image));
        }
        var next = store.Register(session, target, clock.Now, image);
        Throws(() => store.Acquire(session, Guid.NewGuid(), [next.Id]));
        Throws(() => store.Acquire(session, target, [next.Id, Guid.NewGuid()]));
        Check(store.List(session, target).Count == 1, "missing/cross-session batch does not leak references");
        try { using var batch = store.Acquire(session, target, [next.Id]); throw new OperationCanceledException(); } catch (OperationCanceledException) { }
        clock.Now += TimeSpan.FromMinutes(6); store.Cleanup();
        Check(store.List(session, target).Count == 0, "exception/cancellation using releases memory");
        var removable = store.Register(session, target, clock.Now, image);
        Check(!store.Remove(Guid.NewGuid(), target, removable.Id) && store.Remove(session, target, removable.Id), "scoped explicit memory removal");
        var generations = new CapturedImageStore(new(2, 4096), clock);
        var a = generations.Register(session, target, clock.Now.AddSeconds(-2), image);
        var b = generations.Register(session, target, clock.Now.AddSeconds(-1), image);
        var c = generations.Register(session, target, clock.Now, image);
        Check(generations.List(session, target).Select(e => e.Id).SequenceEqual(new[] { b.Id, c.Id }), "generation limit evicts oldest");
        return Task.CompletedTask;
    }
    public static async Task DiskQuotaAndLeases()
    {
        using var f = new Fixture(); var clock = new Clock(); await using var store = new TempFileStore(f.Paths, f.Log, new(2, 6, TimeSpan.FromMinutes(1)), clock);
        await store.CleanupAsync(); Check(!await store.DeleteAsync(Guid.NewGuid()) && !Directory.Exists(f.Paths.TempDirectory), "lazy startup and missing delete");
        await using var a = await store.CreateAsync(TempPurpose.Capture, new MemoryStream([1, 2, 3]), 3);
        clock.Now += TimeSpan.FromSeconds(1);
        await using var b = await store.CreateAsync(TempPurpose.Audio, new MemoryStream([4, 5, 6]), 3);
        await MustFail(() => store.CreateAsync(TempPurpose.Work, new MemoryStream([7]), 1));
        var ids = new[] { a.Files[0].Info.Id, b.Files[0].Info.Id };
        Check(!await store.DeleteAsync(ids[0]), "explicit deletion protects active file");
        await using var batch = await store.AcquireAsync(ids);
        await a.DisposeAsync(); await b.DisposeAsync();
        clock.Now += TimeSpan.FromMinutes(2); await store.CleanupAsync();
        Check(batch.Files[0].Content.ReadByte() == 1 && batch.Files[1].Content.ReadByte() == 4, "all used files protected across purposes and expiration");
        await MustFail(() => store.AcquireAsync([ids[0]]));
        await batch.DisposeAsync(); Check(Payloads(f.Paths).Length == 0, "release reclaims expired files");
        var oldest = await store.CreateAsync(TempPurpose.Work, new MemoryStream([1, 2, 3]), 3); var oldId = oldest.Files[0].Info.Id; await oldest.DisposeAsync();
        clock.Now += TimeSpan.FromSeconds(1);
        await using var newer = await store.CreateAsync(TempPurpose.Work, new MemoryStream([4, 5, 6]), 3);
        await using var third = await store.CreateAsync(TempPurpose.Work, new MemoryStream([7, 8, 9]), 3);
        await MustFail(() => store.AcquireAsync([oldId]));
        Check(Payloads(f.Paths).Length == 2, "quota reclaims oldest unused, never active");
        await newer.DisposeAsync(); await third.DisposeAsync();
        var disposal = store.DisposeAsync().AsTask(); await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Check(Payloads(f.Paths).Length == 0, "normal exit deletes only own idle payloads");
    }
    public static async Task AtomicityFailureAndSeparation()
    {
        using var f = new Fixture(); await using var ui = new TempFileStore(f.Paths, f.Log, new(2, 8));
        await using var worker = new TempFileStore(f.Paths, f.Log, new(2, 8));
        await MustFail(() => ui.CreateAsync(TempPurpose.Work, new MemoryStream([1, 2, 3]), 2));
        Check(Payloads(f.Paths).Length == 0 && !Directory.GetFiles(f.Paths.TempDirectory, "*.part", SearchOption.AllDirectories).Any(), "oversize rollback");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); try { await ui.CreateAsync(TempPurpose.Work, new MemoryStream([1]), 1, cancel.Token); throw new Exception("cancel ignored"); } catch (OperationCanceledException) { }
        }
        await using var first = await ui.CreateAsync(TempPurpose.Work, new MemoryStream([1, 2, 3, 4]), 4); var firstId = first.Files[0].Info.Id;
        await using var second = await worker.CreateAsync(TempPurpose.Work, new MemoryStream([5, 6, 7, 8]), 4);
        await MustFail(() => worker.CreateAsync(TempPurpose.Work, new MemoryStream([9]), 1));
        await MustFail(() => worker.AcquireAsync([firstId]));
        var uiClosing = ui.DisposeAsync().AsTask(); await worker.CleanupAsync();
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => worker.CleanupAsync()));
        Check(!uiClosing.IsCompleted && first.Files[0].Content.ReadByte() == 1, "closing owner retains lock until all leases release");
        await first.DisposeAsync(); await uiClosing.WaitAsync(TimeSpan.FromSeconds(5));
        Check(second.Files[0].Content.ReadByte() == 5, "UI exit preserves Worker owner");
        await second.DisposeAsync(); await worker.DisposeAsync();
        var releasePaths = new AppDataPaths(Path.Combine(f.Root, "env-r"), Path.Combine(f.Root, "env-l"), AppDataEnvironment.Release);
        var debugPaths = new AppDataPaths(Path.Combine(f.Root, "env-r"), Path.Combine(f.Root, "env-l"), AppDataEnvironment.Debug);
        await using var release = new TempFileStore(releasePaths, f.Log);
        await using var debug = new TempFileStore(debugPaths, f.Log);
        await using var releaseItem = await release.CreateAsync(TempPurpose.Work, new MemoryStream([1]), 1);
        await debug.CleanupAsync(); Check(releaseItem.Files[0].Content.ReadByte() == 1 && !Directory.Exists(debugPaths.TempDirectory), "environment isolation");
        await releaseItem.DisposeAsync();
    }
    public static async Task CorruptionAndCleanupErrors()
    {
        using var f = new Fixture(); var clock = new Clock();
        var fs = new FaultFileSystem(new SafeTempFileSystem(Path.Combine(f.Paths.TempDirectory, "v1")));
        await using var store = new TempFileStore(f.Paths, f.Log, new(3, 64, TimeSpan.FromSeconds(1)), clock, fs);
        var a = await store.CreateAsync(TempPurpose.Work, new MemoryStream([1]), 1); var aId = a.Files[0].Info.Id; await a.DisposeAsync();
        var b = await store.CreateAsync(TempPurpose.Work, new MemoryStream([2]), 1); var bId = b.Files[0].Info.Id; await b.DisposeAsync();
        var corrupt = Payloads(f.Paths).Single(path => path.Contains(bId.ToString("N")));
        File.WriteAllBytes(corrupt, [9]); await MustFail(() => store.AcquireAsync([aId, bId]));
        clock.Now += TimeSpan.FromSeconds(2); fs.FailDelete = true;
        await store.CleanupAsync(); Check(f.Log.Errors > 0 && Payloads(f.Paths).Length == 2, "cleanup failure logged without propagating");
        fs.FailDelete = false; await store.CleanupAsync();
        Check(Payloads(f.Paths).Length == 0, "failed batch releases all handles and bounded later cleanup succeeds");
        var c = await store.CreateAsync(TempPurpose.Work, new MemoryStream([3]), 1); var cId = c.Files[0].Info.Id; await c.DisposeAsync();
        File.Delete(Directory.GetFiles(f.Paths.TempDirectory, cId.ToString("N") + ".part", SearchOption.AllDirectories).Single());
        await MustFail(() => store.AcquireAsync([cId])); await store.CleanupAsync();
        Check(Payloads(f.Paths).Length == 0, "missing payload reclaims dangling marker");
        var d = await store.CreateAsync(TempPurpose.Work, new MemoryStream([4]), 1); var dId = d.Files[0].Info.Id; await d.DisposeAsync();
        Check(await store.DeleteAsync(dId) && !await store.DeleteAsync(dId), "explicit file removal and already absent");
        await using var cancelling = new TempFileStore(f.Paths, f.Log);
        var marker = Path.Combine(f.Root, "writing-ready");
        var writing = cancelling.CreateAsync(TempPurpose.Work, new BlockingInput(marker, 4), 8);
        for (var i = 0; i < 200 && !File.Exists(marker); i++) await Task.Delay(10);
        Check(File.Exists(marker), "in-progress write ready");
        var shutdown = cancelling.DisposeAsync().AsTask();
        try { await writing; throw new Exception("shutdown did not cancel write"); } catch (OperationCanceledException) { }
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
    }
    public static async Task ReparseAndRangeSafety()
    {
        using var f = new Fixture(); var root = Path.Combine(f.Paths.TempDirectory, "v1"); var fs = new SafeTempFileSystem(root);
        await using var store = new TempFileStore(f.Paths, f.Log);
        var lease = await store.CreateAsync(TempPurpose.Work, new MemoryStream([1]), 1); await lease.DisposeAsync();
        var outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside); var sentinel = Path.Combine(outside, "keep.txt"); File.WriteAllText(sentinel, "keep");
        Throws(() => fs.Delete(sentinel));
        var guarded = Path.Combine(root, "guarded");
        using (fs.GuardDirectory(guarded, true)) { Throws(() => Directory.Move(guarded, guarded + "-renamed")); }
        var unrelated = Path.Combine(f.Root, "unrelated"); Directory.CreateDirectory(unrelated);
        Directory.Move(unrelated, unrelated + "-renamed");
        Check(Directory.Exists(unrelated + "-renamed"), "long-lived Temp leases do not pin unrelated ancestor contents");
        var badOwner = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(badOwner);
        var junction = Path.Combine(badOwner, "work");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Arguments = $"/d /c mklink /J \"{junction}\" \"{outside}\"";
        using (var process = Process.Start(start)!) { await process.WaitForExitAsync(); Check(process.ExitCode == 0, "junction creation"); }
        try
        {
            await store.CleanupAsync(); await MustFail(() => store.CreateAsync(TempPurpose.Work, new MemoryStream([2]), 1));
            Check(File.ReadAllText(sentinel) == "keep", "junction outside never traversed/deleted/written");
        }
        finally { Directory.Delete(junction, false); }
        Check(Directory.Exists(guarded), "unknown directories retained");
    }
    public static async Task ProcessOwnershipAndCrash()
    {
        foreach (var mode in new[] { "lease", "writing" })
        {
            using var f = new Fixture(); var ready = Path.Combine(f.Root, "ready");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--temp-owner"); start.ArgumentList.Add(f.Root); start.ArgumentList.Add(mode);
            using var child = Process.Start(start)!;
            try
            {
                for (var i = 0; i < 200 && !File.Exists(ready) && !child.HasExited; i++) await Task.Delay(25);
                Check(File.Exists(ready), "child ready");
                await using var janitor = new TempFileStore(f.Paths, f.Log, new(1, 32));
                await janitor.CleanupAsync();
                Check(Directory.GetFiles(f.Paths.TempDirectory, mode == "lease" ? "*.bin" : "*.part", SearchOption.AllDirectories).Length == 1, "live process is protected independent of PID metadata");
                await MustFail(() => janitor.CreateAsync(TempPurpose.Work, new MemoryStream([1]), 1));
                child.Kill(); await child.WaitForExitAsync();
                await janitor.CleanupAsync();
                Check(Payloads(f.Paths).Length == 0 && Directory.GetFiles(f.Paths.TempDirectory, "*.part", SearchOption.AllDirectories).Length == 0, "crash reclaims payload and incomplete reservation");
                await using var fresh = await janitor.CreateAsync(TempPurpose.Work, new MemoryStream([1]), 1);
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
        }
    }
    internal static async Task RunOwner(string root, string mode)
    {
        var paths = new AppDataPaths(Path.Combine(root, "r"), Path.Combine(root, "l"));
        await using var store = new TempFileStore(paths, new Log(), new(1, 32));
        if (mode == "writing") await store.CreateAsync(TempPurpose.Work, new BlockingInput(Path.Combine(root, "ready")), 32);
        else
        {
            await using var file = await store.CreateAsync(TempPurpose.Work, new MemoryStream(new byte[32]), 32);
            File.WriteAllText(Path.Combine(root, "ready"), "ready"); await Task.Delay(Timeout.Infinite);
        }
    }
    private sealed class BlockingInput(string marker, int prefixLength = 16) : MemoryStream(new byte[prefixLength])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { if (Position < Length) return await base.ReadAsync(buffer, cancellationToken); File.WriteAllText(marker, "ready"); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
    private static string[] Payloads(AppDataPaths paths) => Directory.Exists(paths.TempDirectory) ? Directory.GetFiles(paths.TempDirectory, "*.bin", SearchOption.AllDirectories) : [];
    private static async Task MustFail(Func<Task> work) { try { await work(); } catch (TempStorageException) { return; } throw new Exception("Expected Temp failure"); }
    private static void Throws(Action work) { try { work(); } catch (Exception exception) when (exception is TempStorageException or IOException) { return; } throw new Exception("Expected failure"); }
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Log : IAppDiagnostics { public int Errors; public void Error(string operation, Exception exception) => Interlocked.Increment(ref Errors); }
    private sealed class Fixture : IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "ateliers-temp-tests-" + Guid.NewGuid().ToString("N"));
        public AppDataPaths Paths; public Log Log = new();
        public Fixture() { Directory.CreateDirectory(Root); Paths = new(Path.Combine(Root, "r"), Path.Combine(Root, "l")); }
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("ateliers-temp-tests-")) throw new Exception("Unexpected test root");
            Directory.Delete(full, true);
        }
    }
    private sealed class FaultFileSystem(ITempFileSystem inner) : ITempFileSystem
    {
        public bool FailDelete;
        public IDisposable GuardDirectory(string path, bool create) => inner.GuardDirectory(path, create);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public string[] Files(string path) => inner.Files(path);
        public string[] Directories(string path) => inner.Directories(path);
        public FileStream Open(string path, FileMode mode, FileAccess access, FileShare share) => inner.Open(path, mode, access, share);
        public void Delete(string path) { if (FailDelete) throw new IOException("injected"); inner.Delete(path); }
        public void DeleteEmptyDirectory(string path) => inner.DeleteEmptyDirectory(path);
    }
}
