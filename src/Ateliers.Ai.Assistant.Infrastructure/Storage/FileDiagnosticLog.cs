using Ateliers.Ai.Assistant.Application.Storage;
using System.Text;

namespace Ateliers.Ai.Assistant.Infrastructure.Storage;

/// <summary>各2 MiB・最大3ファイルの診断ログ。例外メッセージ・SQL・本文・画像を記録しない。</summary>
public sealed class FileDiagnosticLog(IApplicationPaths paths) : IAppDiagnostics
{
    private readonly object _sync = new();
    /// <summary>1ファイルの上限。次の記録前にローテーションする。</summary>
    public const long MaximumFileBytes = 2 * 1024 * 1024;
    /// <inheritdoc/>
    public void Error(string operation, Exception exception)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(paths.LogsDirectory);
                var file = Path.Combine(paths.LogsDirectory, "assistant.log");
                var line = $"{DateTimeOffset.UtcNow:O}\t{operation}\t{exception.GetType().FullName}\t0x{exception.HResult:X8}{Environment.NewLine}";
                if (File.Exists(file) && new FileInfo(file).Length + Encoding.UTF8.GetByteCount(line) > MaximumFileBytes)
                {
                    var previous = Path.Combine(paths.LogsDirectory, "assistant.1.log");
                    if (File.Exists(previous)) File.Move(previous, Path.Combine(paths.LogsDirectory, "assistant.2.log"), true);
                    File.Move(file, previous, true);
                }
                // operation はアプリ内の固定識別子だけを渡す。例外内容にはモデル出力が含まれ得るため出さない。
                File.AppendAllText(file, line);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("Diagnostic log unavailable: {0}", failure.GetType().Name);
            }
        }
    }
}
