using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Ateliers.Ai.Assistant.Application.ScreenAnalysis;
using Ateliers.Ai.Assistant.Core.Chat;

namespace Ateliers.Ai.Assistant.Platform.Windows.Capture;

/// <summary>Worker 内の STA・メッセージループ。標準 Picker と最小限の監視トレイを所有する。</summary>
public sealed class CaptureWindowHost : IDisposable
{
    private readonly ConcurrentQueue<Action> _jobs = new();
    private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Thread _thread;
    private readonly WindowProcedure _procedure;
    private readonly ConcurrentDictionary<Guid, CaptureScope> _scopes = new();
    private readonly SemaphoreSlim _pickerGate = new(1, 1);
    private nint _window;
    private volatile bool _disposed, _locked;
    private bool _icon;
    private string _status = "停止中";
    private uint _taskbarCreated;
    private readonly string _selectionTitle;
    /// <summary>トレイからの明示停止要求。</summary>
    public event EventHandler? StopRequested;
    /// <summary>セッションロック通知。解除だけでは自動再開しない。</summary>
    public event EventHandler? SessionLocked;
    /// <summary>Explorer 再起動後に通知領域へ復帰できなかった場合の停止要求。</summary>
    public event EventHandler? TrayUnavailable;
    /// <summary>現在の対話セッションがロック通知を受けたか。</summary>
    public bool IsSessionLocked => _locked;
    /// <summary>同じ STA を使い、対象と取消を他監視から独立して保持する。</summary>
    public IScreenCaptureService CreateScope()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Guid.NewGuid(); var scope = new CaptureScope(this, id); _scopes[id] = scope; return scope;
    }
    private async Task<bool> PickScopeAsync(WindowsScreenCaptureService capture, CancellationToken token)
    {
        if (!await _pickerGate.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("別の監視で対象選択中です。選択画面を閉じてください。");
        try
        {
            return await InvokeAsync(async () =>
            {
                ShowWindow(_window, 5); SetForegroundWindow(_window);
                try { return await capture.PickAsync(token, _lifetime.Token); }
                finally { ShowWindow(_window, 0); }
            }).ConfigureAwait(false);
        }
        finally { _pickerGate.Release(); }
    }
    private sealed class CaptureScope : IScreenCaptureService
    {
        private readonly CaptureWindowHost _host; private readonly Guid _id;
        internal WindowsScreenCaptureService Capture { get; }
        public CaptureScope(CaptureWindowHost host, Guid id) { _host = host; _id = id; Capture = new(() => host._window); }
        public string? TargetName => Capture.TargetName;
        public event EventHandler? TargetClosed { add => Capture.TargetClosed += value; remove => Capture.TargetClosed -= value; }
        public Task<bool> PickAsync(CancellationToken token) => _host.PickScopeAsync(Capture, token);
        public async Task<ChatImage> CaptureAsync(CancellationToken token)
        {
            if (_host._locked) throw new InvalidOperationException("画面ロック中です。");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _host._lifetime.Token);
            return await Capture.CaptureAsync(linked.Token).ConfigureAwait(false);
        }
        public void Dispose() { if (_host._scopes.TryRemove(_id, out _)) Capture.Dispose(); }
    }
    /// <summary>独立した UI スレッドを遅延起動し、既存 Worker のメインスレッドをブロックしない。</summary>
    public CaptureWindowHost(string? selectionWindowTitle = null)
    {
        _selectionTitle = selectionWindowTitle ?? "Ateliers AI Assistant — 監視対象を選択";
        _procedure = ProcessMessage;
        _thread = new Thread(Run) { IsBackground = true, Name = "Screen selection and tray" };
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
    }
    /// <summary>監視開始後の状態をトレイに表示する。対象名や画面内容はツールチップへ入れない。</summary>
    public Task SetStatusAsync(string status, bool visible) => InvokeAsync(() =>
    {
        _status = status;
        if (!visible && _icon) { var icon = IconData(); ShellNotifyIcon(2, ref icon); _icon = false; }
        else if (visible) { var icon = IconData(); if (!ShellNotifyIcon(_icon ? 1u : 0u, ref icon)) throw new InvalidOperationException("監視用のトレイアイコンを表示できません。"); _icon = true; }
        return Task.FromResult(true);
    });
    private async Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var window = await _ready.Task.ConfigureAwait(false);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Enqueue(async () => { try { completion.TrySetResult(await action()); } catch (Exception exception) { completion.TrySetException(exception); } });
        if (!PostMessage(window, 0x8001, 0, 0)) throw new InvalidOperationException("選択ウィンドウへ接続できません。");
        return await completion.Task.ConfigureAwait(false);
    }
    private void Run()
    {
        var initialized = RoInitialize(0) >= 0;
        try
        {
            var name = "AteliersScreenHost" + Guid.NewGuid().ToString("N");
            var definition = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = _procedure, Instance = GetModuleHandle(null), ClassName = name };
            if (RegisterClassEx(ref definition) == 0) throw new InvalidOperationException("画面監視のウィンドウを登録できません。");
            _window = CreateWindowEx(0x80, name, _selectionTitle, 0x00c80000, 100, 100, 540, 120, 0, 0, definition.Instance, 0);
            if (_window == 0) throw new InvalidOperationException("画面監視のウィンドウを作成できません。");
            // Hidden 起動の STARTUPINFO は最初の ShowWindow の指定を上書きする。
            // 所有者を明示的に隠して消費し、後の Picker 表示が影響を受けないようにする。
            ShowWindow(_window, 0);
            ShowWindow(_window, 0);
            SynchronizationContext.SetSynchronizationContext(new WindowContext(this));
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            WTSRegisterSessionNotification(_window, 0);
            SetWindowDisplayAffinity(_window, 0x11);
            _ready.TrySetResult(_window);
            while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        catch (Exception exception) { _ready.TrySetException(exception); }
        finally { _window = 0; foreach (var scope in _scopes.Values) scope.Dispose(); if (initialized) RoUninitialize(); }
    }
    private nint ProcessMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x8001) { while (_jobs.TryDequeue(out var action)) action(); return 0; }
        if (message == 0x8003) { DestroyWindow(window); return 0; }
        if (message == 0x0010) { ShowWindow(window, 0); return 0; }
        if (message == 0x0002)
        {
            if (_icon) { var icon = IconData(); ShellNotifyIcon(2, ref icon); }
            WTSUnRegisterSessionNotification(window); PostQuitMessage(0); return 0;
        }
        if (message == 0x02b1)
        {
            if (wParam == 7) { _locked = true; SessionLocked?.Invoke(this, EventArgs.Empty); }
            if (wParam == 8) _locked = false;
            return 0;
        }
        if (message == _taskbarCreated && _icon) { _icon = false; var icon = IconData(); _icon = ShellNotifyIcon(0, ref icon); if (!_icon) TrayUnavailable?.Invoke(this, EventArgs.Empty); return 0; }
        if (message == 0x8002 && ((uint)lParam & 0xffff) is 0x0205 or 0x007b)
        {
            var menu = CreatePopupMenu();
            try
            {
                AppendMenu(menu, 2, 0, "画面監視: " + _status); AppendMenu(menu, 0, 1, "全監視を停止");
                GetCursorPos(out var point); SetForegroundWindow(window);
                if (TrackPopupMenu(menu, 0x100, point.X, point.Y, 0, window, 0) == 1) StopRequested?.Invoke(this, EventArgs.Empty);
            }
            finally { DestroyMenu(menu); }
            return 0;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }
    private NotifyIcon IconData() => new() { Size = (uint)Marshal.SizeOf<NotifyIcon>(), Window = _window, Id = 1, Flags = 7, Callback = 0x8002, Icon = LoadIcon(0, (nint)32512), Tip = "Ateliers 画面監視: " + _status, Info = "", InfoTitle = "" };
    private sealed class WindowContext(CaptureWindowHost owner) : SynchronizationContext
    { public override void Post(SendOrPostCallback callback, object? state) { owner._jobs.Enqueue(() => callback(state)); PostMessage(owner._window, 0x8001, 0, 0); } }
    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _lifetime.Cancel();
        if (_window != 0) PostMessage(_window, 0x8003, 0, 0);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
    }
#if DEBUG
    /// <summary>隔離試験だけで独立対象へ既知 HWND を割り当てる。</summary>
    public Task SelectScopeForVerificationAsync(IScreenCaptureService scope, nint window, bool monitor) => InvokeAsync(() => { ((CaptureScope)scope).Capture.SelectForVerification(window, monitor); return Task.FromResult(true); });
    /// <summary>同一対象の独立保持と、片方の破棄後の取得を実証する。</summary>
    public async Task<bool> VerifyDuplicateTargetAsync()
    {
        var window = await CreateTargetForVerificationAsync();
        using var a = CreateScope(); using var b = CreateScope();
        await SelectScopeForVerificationAsync(a, window, false); await SelectScopeForVerificationAsync(b, window, false);
        var first = await a.CaptureAsync(default); var second = await b.CaptureAsync(default);
        a.Dispose(); var remaining = await b.CaptureAsync(default);
        return first.Width > 0 && second.Width > 0 && remaining.Width == second.Width;
    }
    /// <summary>標準 Picker の所有者 HWND。診断時の UI Automation の対象限定に使う。</summary>
    public Task<nint> OwnerForVerificationAsync() => _ready.Task;
    /// <summary>画像を保存せずに既知のテスト対象を用意する。</summary>
    public Task<nint> CreateTargetForVerificationAsync() => InvokeAsync(() =>
    {
        var target = CreateWindowEx(0, "STATIC", "Ateliers Phase 7 capture verification", 0x10cf0000, 720, 100, 600, 400, 0, 0, GetModuleHandle(null), 0);
        ShowWindow(target, 5);
        ShowWindow(target, 5); // Hidden 起動の最初の表示指定を検証対象自身でも消費する。
        return Task.FromResult(target);
    });
#endif
    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass { public uint Size, Style; public WindowProcedure Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? MenuName, ClassName; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIcon { public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip; public uint State, StateMask; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info; public uint Version; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle; public uint InfoFlags; public Guid Guid; public nint BalloonIcon; }
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extended, string type, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint action, ref NotifyIcon icon);
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(nint window, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
}
