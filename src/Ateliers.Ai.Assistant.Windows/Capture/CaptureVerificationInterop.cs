#if DEBUG
using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace Ateliers.Ai.Assistant.Platform.Windows.Capture;

public sealed partial class WindowsScreenCaptureService
{
    /// <summary>明示起動した診断だけで既知のテスト対象を作る。製品版では標準 Picker だけを使用する。</summary>
    public void SelectForVerification(nint window, bool monitor)
    {
        nint text = 0, factory = 0, result = 0;
        try
        {
            const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out text));
            var interop = new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, in interop, out factory));
            var create = Marshal.GetDelegateForFunctionPointer<CreateItem>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), (monitor ? 4 : 3) * IntPtr.Size));
            var iid = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            Marshal.ThrowExceptionForHR(create(factory, monitor ? MonitorFromWindow(window, 2) : window, in iid, out result));
            SetTarget(WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(result));
        }
        finally { if (result != 0) Marshal.Release(result); if (factory != 0) Marshal.Release(factory); if (text != 0) WindowsDeleteString(text); }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateItem(nint self, nint handle, in Guid iid, out nint item);
    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string source, int length, out nint value);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);
}
#endif
