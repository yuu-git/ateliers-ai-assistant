#if DEBUG
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32.SafeHandles;

namespace Ateliers.Ai.Assistant.Desktop.Diagnostics;

/// <summary>通常の固定保存先と実ファイルハンドルの保存先を比較する Debug 診断。</summary>
internal static class DataPathVerification
{
    /// <summary>パス表示と実体の一致を検証する。検証ファイルはハンドル解放時に削除する。</summary>
    public static Window Create(string reportPath)
    {
        var window = new Window { Content = new TextBlock { Text = "保存先を確認しています" } };
        var started = false;
        window.Activated += (_, _) =>
        {
            if (started) return; started = true;
            var report = new List<string>();
            try
            {
                try { report.Add("Package: " + Windows.ApplicationModel.Package.Current.Id.FullName); }
                catch (InvalidOperationException) { report.Add("Package: unpackaged"); }
                var paths = new AppDataPaths();
                report.Add("Data environment: " + paths.DataEnvironment);
                foreach (var folder in new[] { paths.SettingsDirectory, paths.LocalDirectory, paths.DatabaseDirectory, paths.LogsDirectory })
                {
                    Directory.CreateDirectory(folder);
                    var expected = Path.Combine(folder, "ateliers-path-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
                    using var handle = File.OpenHandle(expected, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.DeleteOnClose);
                    var buffer = new StringBuilder(32768);
                    if (GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var actual = buffer.ToString();
                    if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
                    if (!string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Virtualized path differs from display: " + folder);
                    report.Add("PASS: " + folder);
                }
                report.Add("PASS: ALL FIXED DATA PATH CHECKS");
            }
            catch (Exception exception) { report.Add("FAIL: " + exception); }
            File.WriteAllLines(reportPath, report);
            window.Close();
        };
        return window;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
#endif
