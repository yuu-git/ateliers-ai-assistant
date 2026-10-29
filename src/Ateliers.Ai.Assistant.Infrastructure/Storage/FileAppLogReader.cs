using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ateliers.Ai.Assistant.Application.Logging;
using Ateliers.Ai.Assistant.Application.Storage;

namespace Ateliers.Ai.Assistant.Infrastructure.Storage;

/// <summary>UI / Worker / 旧版ログを読み、分類・重大度・期間・キーワードで上限付き抽出する。</summary>
public sealed class FileAppLogReader(IApplicationPaths paths) : IAppLogReader
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<AppLogEntry>> ReadAsync(LogQuery query, CancellationToken token = default) => Task.Run<IReadOnlyList<AppLogEntry>>(() =>
    {
        var result = new PriorityQueue<AppLogEntry, long>();
        var limit = Math.Clamp(query.Limit, 1, 1000);
        foreach (var stem in new[] { "ui", "worker", "assistant" })
        foreach (var suffix in new[] { ".log", ".1.log", ".2.log" })
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(Path.Combine(paths.LogsDirectory, stem + suffix), FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var length = stream.Length;
                var offset = Math.Max(0, length - FileDiagnosticLog.MaximumFileBytes);
                stream.Position = offset;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                if (offset != 0) reader.ReadLine();
                var consumed = 0L;
                string? line;
                while (consumed < FileDiagnosticLog.MaximumFileBytes && (line = reader.ReadLine()) is not null)
                {
                    token.ThrowIfCancellationRequested(); consumed += Encoding.UTF8.GetByteCount(line) + 1;
                    if (line.Length > 16384) continue;
                    AppLogEntry? entry = null;
                    try
                    {
                        if (stem == "assistant")
                        {
                            var fields = line.Split('\t');
                            if (fields.Length >= 4 && DateTimeOffset.TryParse(fields[0], out var at))
                                entry = new(at, AppLogCategory.System, AppLogLevel.Error, "旧 UI", fields[1], string.Join(" / ", fields.Skip(2)));
                        }
                        else entry = JsonSerializer.Deserialize(line, AppLogJsonContext.Default.AppLogEntry);
                    }
                    catch (JsonException) { /* 書込中の部分行・壊れた行は表示しない。 */ }
                    if (entry is null || entry.Category != query.Category || query.MinimumLevel is { } level && entry.Level < level ||
                        query.Since is { } since && entry.At < since || query.Keyword.Length != 0 &&
                        !(entry.Message + " " + entry.Operation + " " + entry.Origin).Contains(query.Keyword, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Enqueue(entry, entry.At.UtcTicks);
                    if (result.Count > limit) result.Dequeue();
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { /* ローテーション中は次のファイルを読む。 */ }
            catch (UnauthorizedAccessException exception) { throw new StorageException("ログを読み込めません。保存先のアクセス権を確認してください。", exception); }
        }
        return result.UnorderedItems.Select(item => item.Element).OrderByDescending(entry => entry.At).ToArray();
    }, token);
}
/// <summary>ログ行を共通形式で安全に直列化するメタデータ。</summary>
[JsonSerializable(typeof(AppLogEntry))]
internal partial class AppLogJsonContext : JsonSerializerContext { }
