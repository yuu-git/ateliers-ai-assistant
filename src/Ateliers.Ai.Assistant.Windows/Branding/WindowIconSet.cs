using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ateliers.Ai.Assistant.Platform.Windows.Branding;

/// <summary>Owns the file icons used by a native window and its notification icon.</summary>
internal sealed class WindowIconSet : IDisposable
{
    private readonly nint _ownedLarge, _ownedSmall;
    private bool _disposed;
    public nint Large { get; }
    public nint Small { get; }

    public WindowIconSet()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Branding", "app.ico");
        _ownedLarge = LoadImage(0, path, 1, GetSystemMetrics(11), GetSystemMetrics(12), 0x10);
        _ownedSmall = LoadImage(0, path, 1, GetSystemMetrics(49), GetSystemMetrics(50), 0x10);
        Large = _ownedLarge != 0 ? _ownedLarge : LoadIcon(0, (nint)32512);
        Small = _ownedSmall != 0 ? _ownedSmall : Large;
        if (_ownedLarge == 0 || _ownedSmall == 0)
            Trace.TraceWarning("Ateliers AI Assistant: application icon could not be loaded: {0}", path);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // LoadIcon returns a borrowed system handle; only LoadImage handles are owned.
        if (_ownedSmall != 0) DestroyIcon(_ownedSmall);
        if (_ownedLarge != 0) DestroyIcon(_ownedLarge);
    }

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string path, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
