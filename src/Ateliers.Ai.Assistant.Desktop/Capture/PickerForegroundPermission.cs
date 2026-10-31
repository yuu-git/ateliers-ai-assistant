using System.Runtime.InteropServices;
using Ateliers.Ai.Assistant.Application.Worker;

namespace Ateliers.Ai.Assistant.Desktop.Capture;

/// <summary>利用者が前面の Desktop で対象選択を要求した時だけ、Worker に前面表示を許可する。</summary>
internal static class PickerForegroundPermission
{
    /// <summary>接続中 Worker の PID だけを許可する。拒否時は選択要求を送らず再操作を促す。</summary>
    internal static void Allow(int processId)
    {
        if (processId <= 0 || !AllowSetForegroundWindow((uint)processId))
            throw new WorkerException("対象選択を前面に表示できません。アプリ画面を前面にして「対象を選択」を押し直してください。");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
