using System.Text.Json.Serialization;

namespace Ateliers.Ai.Assistant.Infrastructure.Chat;

// 元プロジェクトの Release / trimming 設定でも JSON を処理できるよう、メタデータを生成する。
/// <summary>Ollama DTO の JSON メタデータを、trimming に対応してソース生成するコンテキスト。</summary>
[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(OllamaModelList))]
[JsonSerializable(typeof(OllamaChatChunk))]
[JsonSerializable(typeof(OllamaRunningModels))]
internal partial class OllamaJsonContext : JsonSerializerContext { }

/// <summary>チャット生成と空会話によるロード操作に共用する Ollama 送信 DTO。</summary>
/// <param name="Model">Ollama の要求先モデル名。</param>
/// <param name="Messages">送信する会話。空配列の場合はモデルのロード・停止要求に使用する。</param>
/// <param name="Stream">NDJSON による逐次応答を要求する場合は true。</param>
/// <param name="KeepAlive">ロード保持時間。-1 は無期限保持、0 は解放。</param>
internal sealed record OllamaChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] OllamaRequestMessage[] Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("keep_alive")] int KeepAlive = -1);

/// <summary>api/ps が返すサーバー全体のロード中モデル一覧。</summary>
internal sealed class OllamaRunningModels
{
    /// <summary>ロード中モデル一覧を取得する。欠落時の null は通信層で異常として検出する。</summary>
    [JsonPropertyName("models")] public List<OllamaRunningModel>? Models { get; init; }
}
/// <summary>api/ps のモデル1件を表す受信 DTO。表示形式への変換は行わない。</summary>
internal sealed class OllamaRunningModel
{
    /// <summary>Ollama が返したモデル名を取得する。</summary>
    [JsonPropertyName("name")] public string? Name { get; init; }
    /// <summary>モデル内容を識別するダイジェストを取得する。</summary>
    [JsonPropertyName("digest")] public string? Digest { get; init; }
    /// <summary>モデルサイズをバイト単位で取得する。</summary>
    [JsonPropertyName("size")] public long Size { get; init; }
    /// <summary>VRAM 使用量をバイト単位で取得する。</summary>
    [JsonPropertyName("size_vram")] public long SizeVram { get; init; }
    /// <summary>ロード期限を取得する。未提供の場合は null。</summary>
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>量子化形式などのモデル詳細を取得する。</summary>
    [JsonPropertyName("details")] public OllamaModelDetails? Details { get; init; }
}
/// <summary>Ollama モデルの詳細情報のうち、画面表示に必要な項目。</summary>
internal sealed class OllamaModelDetails
{
    /// <summary>Ollama の quantization_level を取得する。</summary>
    [JsonPropertyName("quantization_level")] public string? Quantization { get; init; }
}

/// <summary>Ollama へ送る役割・原文・任意の画像 Base64 配列。</summary>
/// <param name="Role">メッセージ送信者の役割。</param>
/// <param name="Content">表示用 HTML ではない Markdown 原文。</param>
/// <param name="Images">接頭辞なしの画像 Base64 配列。現在は1メッセージ1枚まで。画像なしは null。</param>
internal sealed record OllamaRequestMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("images"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Images = null);

/// <summary>api/tags が返す利用可能モデルの一覧。</summary>
internal sealed class OllamaModelList
{
    /// <summary>利用可能モデルの項目一覧を取得する。</summary>
    [JsonPropertyName("models")] public List<OllamaModelEntry>? Models { get; init; }
}
/// <summary>api/tags のモデル一覧の1項目。</summary>
internal sealed class OllamaModelEntry
{
    /// <summary>モデル選択に使う名前を取得する。</summary>
    [JsonPropertyName("name")] public string? Name { get; init; }
}
/// <summary>ストリーミングの1行、またはロード操作の完了・エラー応答。</summary>
internal sealed class OllamaChatChunk
{
    /// <summary>回答本文を含むメッセージを取得する。</summary>
    [JsonPropertyName("message")] public OllamaReplyMessage? Message { get; init; }
    /// <summary>生成またはモデル操作が完了したかどうかを取得する。</summary>
    [JsonPropertyName("done")] public bool? Done { get; init; }
    /// <summary>Ollama が返したエラー本文を取得する。</summary>
    [JsonPropertyName("error")] public string? Error { get; init; }
}
/// <summary>回答本文だけを取り出すための受信メッセージ DTO。</summary>
internal sealed class OllamaReplyMessage
{
    /// <summary>表示と送信履歴に使う回答原文を取得する。</summary>
    [JsonPropertyName("content")] public string? Content { get; init; }
}
