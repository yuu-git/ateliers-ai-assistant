using Ateliers.Ai.Assistant.Application.Storage;
using System.Text;
using System.Text.Json;
using Ateliers.Ai.Assistant.Application.Logging;

namespace Ateliers.Ai.Assistant.Infrastructure.Storage;

/// <summary>各2 MiB・最大3ファイルの診断ログ。例外メッセージ・SQL・本文・画像を記録しない。</summary>
public sealed class FileDiagnosticLog(IApplicationPaths paths, string? origin = null) : IAppDiagnostics, IAppLog
{
    private readonly object _sync = new();
    /// <summary>1ファイルの上限。次の記録前にローテーションする。</summary>
    public const long MaximumFileBytes = 2 * 1024 * 1024;
    /// <inheritdoc/>
    public void Error(string operation, Exception exception)
    {
        if (origin is not null)
        {
            Write(AppLogCategory.System, AppLogLevel.Error, operation, $"{exception.GetType().FullName} / 0x{exception.HResult:X8}");
            return;
        }
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
    /// <inheritdoc/>
    public void Write(AppLogCategory category, AppLogLevel level, string operation, string message, Guid? correlationId = null)
    {
        if (origin is not ("UI" or "Worker")) return;
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(paths.LogsDirectory);
                var stem = origin.ToLowerInvariant();
                using var gate = EnterWriter(Path.Combine(paths.LogsDirectory, stem + ".writer.lock"));
                var line = JsonSerializer.Serialize(new AppLogEntry(DateTimeOffset.UtcNow, category, level, origin,
                    operation, message.Length > 2048 ? message[..2048] : message, correlationId), AppLogJsonContext.Default.AppLogEntry) + "\n";
                var file = Path.Combine(paths.LogsDirectory, stem + ".log");
                if (File.Exists(file) && new FileInfo(file).Length + Encoding.UTF8.GetByteCount(line) > MaximumFileBytes)
                {
                    var previous = Path.Combine(paths.LogsDirectory, stem + ".1.log");
                    if (File.Exists(previous)) File.Move(previous, Path.Combine(paths.LogsDirectory, stem + ".2.log"), true);
                    File.Move(file, previous, true);
                }
                File.AppendAllText(file, line, new UTF8Encoding(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("Structured log unavailable: {0}", exception.GetType().Name); }
        }
    }
    /// <summary>同じ UI を複数起動しても、ファイル追記とローテーションを調整する。</summary>
    private static FileStream EnterWriter(string file)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(10); }
        }
    }
}
