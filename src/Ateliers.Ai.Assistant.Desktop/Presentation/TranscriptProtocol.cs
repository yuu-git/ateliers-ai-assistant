using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Ateliers.Ai.Assistant.Core.Chat;

namespace Ateliers.Ai.Assistant.Desktop.Presentation;

/// <summary>UI スレッドで取得し、背景変換へ渡す会話の不変スナップショット。</summary>
/// <param name="Id">表示中のメッセージを安定して識別する ID。</param>
/// <param name="Content">表示用 HTML ではない Markdown 原文。</param>
/// <param name="Speaker">送信者の表示名。</param>
/// <param name="Detail">モデル名や受信状態などの補足表示。</param>
/// <param name="IsUser">利用者からの入力なら true。</param>
/// <param name="Final">完了・中止・失敗など、コードの着色を許可する最終状態なら true。</param>
/// <param name="Image">任意のメモリ内 PNG 添付。添付がない場合は null。</param>
public sealed record MessageSnapshot(string Id, string Content, string Speaker, string Detail, bool IsUser, bool Final, ChatImage? Image = null);
/// <summary>安全化済み HTML と、本文から分離した PNG 添付を持つ表示用メッセージ。</summary>
/// <param name="Id">表示中のメッセージを安定して識別する ID。</param>
/// <param name="Html">Markdig と HtmlSanitizer による安全化済みの本文 HTML。</param>
/// <param name="Speaker">送信者の表示名。</param>
/// <param name="Detail">モデル名や受信状態などの補足表示。</param>
/// <param name="IsUser">利用者からの入力なら true。</param>
/// <param name="Final">完了・中止・失敗など、コードの着色を許可する最終状態なら true。</param>
/// <param name="ImageDataUrl">本文とは別経路で生成した PNG Data URL。添付なしは null。</param>
public sealed record RenderedMessage(string Id, string Html, string Speaker, string Detail, bool IsUser, bool Final, string? ImageDataUrl = null);
/// <summary>会話世代・更新番号と変更メッセージをまとめた WebView2 向け差分。</summary>
/// <param name="Generation">会話をリセットするたびに進む世代番号。</param>
/// <param name="Revision">同じ会話内で逆順の結果を拒否する更新番号。</param>
/// <param name="Messages">今回の差分で追加または更新する変換済みメッセージ。</param>
/// <param name="Order">会話全体の表示順 ID。並び順に変更がない場合は null。</param>
/// <param name="Dark">ダークテーマで表示する場合は true。</param>
/// <param name="DocumentMode">単一文書のプレビュー。自動追従をせず、選択変更時は先頭から表示する。</param>
public sealed record TranscriptPatch(long Generation, long Revision, RenderedMessage[] Messages, string[]? Order, bool Dark, bool DocumentMode = false);
/// <summary>同梱ページから受け取る準備通知またはリンク操作。</summary>
/// <param name="Type">ページが送信したメッセージの種類。</param>
/// <param name="Href">クリックされた未検証 URL。準備通知では null。</param>
public sealed record TranscriptWebMessage(string Type, string? Href);

/// <summary>表示差分とページ通知の JSON メタデータをソース生成するコンテキスト。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TranscriptPatch))]
[JsonSerializable(typeof(TranscriptWebMessage))]
internal partial class TranscriptJsonContext : JsonSerializerContext { }

/// <summary>会話表示専用の仮想ホスト、文書 URL、読み込み可能な同梱資産を定義する。</summary>
public static class TranscriptProtocol
{
    /// <summary>表示資産だけに割り当てる仮想ホスト名。</summary>
    public const string Host = "chat-ui.invalid";
    /// <summary>WebView2 内での遷移を許可する会話表示ページの URL。</summary>
    public const string Document = "https://chat-ui.invalid/index.html";
    /// <summary>クエリや認証情報を含まない、既知の同梱資産の URL か判定する。</summary>
    /// <param name="value">読み込み要求の未検証 URL。</param>
    /// <returns>許可された同梱資産の URL なら true。</returns>
    public static bool IsAllowedResource(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == Host && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
        uri.AbsolutePath is "/index.html" or "/chat.css" or "/chat.js" or "/vendor/highlight.min.js";
}

/// <summary>背景処理で再利用する変換キャッシュ。UI オブジェクトには触れない。</summary>
/// <remarks>呼び出し元で変換の同時実行を防ぐ。キャッシュ自体はスレッドセーフではない。</remarks>
public sealed class TranscriptRenderCache
{
    private readonly MarkdownMessageRenderer _renderer = new();
    private readonly Dictionary<string, (string Content, string Html, ChatImage? Image, string? ImageUrl)> _cache = new();
    /// <summary>変更された本文・添付だけを再変換し、一覧から消えた項目をキャッシュから除去する。</summary>
    /// <param name="messages">UI 上で取得済みの表示用スナップショット一覧。</param>
    /// <returns>現在の一覧に対応する安全化済みの表示用メッセージ。</returns>
    public RenderedMessage[] Render(IReadOnlyList<MessageSnapshot> messages)
    {
        var ids = messages.Select(message => message.Id).ToHashSet();
        foreach (var id in _cache.Keys.Where(id => !ids.Contains(id)).ToArray()) _cache.Remove(id);
        return messages.Select(message =>
        {
            var found = _cache.TryGetValue(message.Id, out var cached);
            var html = found && cached.Content == message.Content ? cached.Html : _renderer.Render(message.Content);
            // 添付経路だけが PNG Data URL を作る。Markdown の画像構文は引き続き許可しない。
            var imageUrl = found && ReferenceEquals(cached.Image, message.Image) ? cached.ImageUrl
                : message.Image is null ? null : "data:image/png;base64," + message.Image.Base64Data;
            _cache[message.Id] = (message.Content, html, message.Image, imageUrl);
            return new RenderedMessage(message.Id, html, message.Speaker, message.Detail, message.IsUser, message.Final, imageUrl);
        }).ToArray();
    }
}

/// <summary>世代をまたぐ結果と逆順の結果を拒否し、変更されたメッセージだけを転送する。</summary>
public sealed class TranscriptProjection
{
    private readonly Dictionary<string, RenderedMessage> _sent = new();
    private string[]? _order;
    private long _lastRevision = -1;
    /// <summary>現在の会話を識別し、旧会話の結果を拒否する世代番号を取得する。</summary>
    public long Generation { get; private set; }
    /// <summary>世代を進め、送信済みメッセージと更新番号を初期化する。</summary>
    public void Reset()
    {
        Generation++;
        _lastRevision = -1;
        _sent.Clear();
        _order = null;
    }
    /// <summary>旧世代・逆順の結果を拒否し、有効な更新から差分と必要な並び順を作る。</summary>
    /// <param name="generation">変換開始時の会話世代番号。</param>
    /// <param name="revision">変換開始時の更新番号。</param>
    /// <param name="messages">現在の会話全体の変換済みメッセージ。</param>
    /// <param name="dark">ダークテーマを適用する場合は true。</param>
    /// <returns>送信すべき差分。古い会話または逆順の結果なら null。</returns>
    public TranscriptPatch? CreatePatch(long generation, long revision, RenderedMessage[] messages, bool dark)
    {
        if (generation != Generation || revision <= _lastRevision) return null;
        var order = messages.Select(message => message.Id).ToArray();
        var orderChanged = _order is null || !_order.SequenceEqual(order);
        var changed = messages.Where(message => !_sent.TryGetValue(message.Id, out var old) || old != message).ToArray();
        _sent.Clear();
        foreach (var message in messages) _sent.Add(message.Id, message);
        _order = order;
        _lastRevision = revision;
        return new TranscriptPatch(generation, revision, changed, orderChanged ? order : null, dark);
    }
}
