using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ateliers.Ai.Assistant.Infrastructure.Storage.Temp;

/// <summary>Windows のハンドルで親パスを固定し、再解析ポイントを辿らず Temp 内だけを操作する。</summary>
public sealed class SafeTempFileSystem(string root) : ITempFileSystem
{
    private readonly string _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
    private const uint ReadAttributes = 0x80, DeleteAccess = 0x10000, OpenReparse = 0x200000, Backup = 0x2000000;
    /// <inheritdoc/>
    public bool DirectoryExists(string path) { Check(path); return Directory.Exists(path); }
    /// <inheritdoc/>
    public IDisposable GuardDirectory(string path, bool create) => GuardDirectoryCore(path, create, false);
    /// <summary>実操作中は祖先全体を固定し、長寿命の使用権では Temp 内の固定だけを保持する。</summary>
    private IDisposable GuardDirectoryCore(string path, bool create, bool keepAncestors)
    {
        Check(path); var held = new Handles();
        try
        {
            var full = Path.GetFullPath(path); var current = Path.GetPathRoot(full)!;
            // 祖先を先に固定してから次の要素を開く。検査と利用の間のパス差し替えを防ぐ。
            held.Add(current, OpenDirectory(current, ReadAttributes));
            foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (create && !Directory.Exists(current)) Directory.CreateDirectory(current);
                held.Add(current, OpenDirectory(current, ReadAttributes));
            }
            if (!keepAncestors) held.ReleaseAncestors(_root);
            return held;
        }
        catch { held.Dispose(); throw; }
    }
    /// <inheritdoc/>
    public string[] Files(string path) { using var held = GuardDirectoryCore(path, false, true); return Directory.GetFiles(path); }
    /// <inheritdoc/>
    public string[] Directories(string path) { using var held = GuardDirectoryCore(path, false, true); return Directory.GetDirectories(path); }
    /// <inheritdoc/>
    public FileStream Open(string path, FileMode mode, FileAccess access, FileShare share)
    {
        Check(path);
        using var parent = GuardDirectoryCore(Path.GetDirectoryName(path)!, false, true);
        uint creation = mode switch { FileMode.CreateNew => 1, FileMode.Open => 3, FileMode.OpenOrCreate => 4, _ => throw new ArgumentException("上書き可能なモードは使用できません。") };
        var flags = access == FileAccess.Read ? 0x80000000u : access == FileAccess.Write ? 0x40000000u : 0xc0000000u;
        var handle = CreateFile(path, flags, (uint)share, 0, creation, OpenReparse, 0);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); ThrowIo(error); }
        try { Validate(handle, false); return new FileStream(handle, access); }
        catch { handle.Dispose(); throw; }
    }
    /// <inheritdoc/>
    public void Delete(string path)
    {
        Check(path); using var held = GuardDirectoryCore(Path.GetDirectoryName(path)!, false, true);
        using var handle = CreateFile(path, DeleteAccess | ReadAttributes, 1, 0, 3, OpenReparse, 0);
        if (handle.IsInvalid) { var code = Marshal.GetLastWin32Error(); if (code == 2) return; ThrowIo(code); }
        Validate(handle, false); MarkDeleted(handle);
    }
    /// <inheritdoc/>
    public void DeleteEmptyDirectory(string path)
    {
        Check(path); if (Path.GetFullPath(path).Equals(_root, StringComparison.OrdinalIgnoreCase)) throw new IOException("管理ルートは削除しません。");
        using var held = GuardDirectoryCore(Path.GetDirectoryName(path)!, false, true);
        using var handle = OpenDirectory(path, DeleteAccess | ReadAttributes);
        MarkDeleted(handle);
    }
    private void Check(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("管理範囲外のパスです。");
        if (full[Path.GetPathRoot(full)!.Length..].Contains(':')) throw new IOException("代替データストリームは使用できません。");
    }
    private static SafeFileHandle OpenDirectory(string path, uint access)
    {
        // 属性読み取りだけのハンドルは共有制限を固定できない。ディレクトリ列挙の読み取り権も保持する。
        var handle = CreateFile(path, access | 1, 1, 0, 3, OpenReparse | Backup, 0);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); ThrowIo(error); }
        try { Validate(handle, true); return handle; } catch { handle.Dispose(); throw; }
    }
    private static void Validate(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info)) ThrowIo();
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory || (!directory && info.Links != 1))
            throw new IOException("再解析ポイント・リンク・種別の異なるデータは操作しません。");
    }
    private static void MarkDeleted(SafeFileHandle handle)
    { var disposition = new Disposition { DeleteFile = true }; if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<Disposition>())) ThrowIo(); }
    private static void ThrowIo(int? error = null) { var code = error ?? Marshal.GetLastWin32Error(); throw new IOException("Temp のファイル操作に失敗しました。", new Win32Exception(code)); }
    private sealed class Handles : IDisposable
    {
        private readonly List<(string Path, SafeFileHandle Handle)> _handles = [];
        internal void Add(string path, SafeFileHandle handle) => _handles.Add((path, handle));
        internal void ReleaseAncestors(string root)
        {
            foreach (var entry in _handles.Where(entry => !entry.Path.Equals(root, StringComparison.OrdinalIgnoreCase) && !entry.Path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray())
            { entry.Handle.Dispose(); _handles.Remove(entry); }
        }
        public void Dispose() { for (var i = _handles.Count - 1; i >= 0; i--) _handles[i].Handle.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoNative { public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Disposition { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }
    /// <summary>長い AppData パスも Win32 の MAX_PATH に制限されない形式で開く。</summary>
    private static SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template)
    {
        var full = Path.GetFullPath(name);
        var native = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        return CreateFileNative(native, access, share, security, creation, flags, template);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileNative(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfoNative info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int type, ref Disposition info, uint size);
}
