using System.Net;
using System.Text;
using System.Text.Json;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Chat;
using Ateliers.Ai.Assistant.Application.Models;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

internal static class ServiceTests
{
    private static readonly ChatMessage[] Question = { new(ChatRole.User, "質問") };

    public static async Task Models()
    {
        using var client = Client(new StubHandler((request, _) =>
        {
            Assert.That(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/tags", "GET /api/tags");
            return Task.FromResult(Json("""{"models":[{"name":"b"},{"name":"a"},{"name":"a"}]}"""));
        }));
        var models = await new OllamaChatService(client).GetModelsAsync();
        Assert.That(models.SequenceEqual(new[] { "a", "b" }), "モデル名を重複排除して返す");
        foreach (var invalid in new[] { "{}", "null", "{\"models\":[{}]}", "{\"models\":[null]}" })
        {
            using var bad = Client(new StubHandler((_, _) => Task.FromResult(Json(invalid))));
            await Assert.Throws<ChatServiceException>(async () => await new OllamaChatService(bad).GetModelsAsync());
        }
    }

    public static async Task Streaming()
    {
        JsonElement sent = default;
        var body = "\r\n{\"message\":{\"thinking\":\"推論\"},\"done\":false}\n" +
            "{\"message\":{\"content\":\"こん\"},\"done\":false}\n" +
            "{\"message\":{\"content\":\"にちは\"},\"done\":true}\n";
        var stream = new FragmentStream(body, fragmentSize: 1);
        using var client = Client(new StubHandler(async (request, token) =>
        {
            Assert.That(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/chat", "POST /api/chat");
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            sent = doc.RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        }));
        var messages = new[] { new ChatMessage(ChatRole.User, "前の質問"),
            new ChatMessage(ChatRole.Assistant, "前の回答"), new ChatMessage(ChatRole.User, "次の質問") };
        var chunks = new List<string>();
        await foreach (var chunk in new OllamaChatService(client).StreamReplyAsync("test-model", messages)) chunks.Add(chunk);
        Assert.That(chunks.SequenceEqual(new[] { "こん", "にちは" }), "UTF8 分割・最終チャンク本文・thinking の除外");
        Assert.That(sent.GetProperty("stream").GetBoolean() && sent.GetProperty("model").GetString() == "test-model", "stream=true とモデル名");
        Assert.That(sent.GetProperty("keep_alive").GetInt32() == -1, "チャットでも無期限の保持を維持");
        var history = sent.GetProperty("messages");
        Assert.That(history.GetArrayLength() == 3 && history[1].GetProperty("role").GetString() == "assistant" &&
            history[2].GetProperty("content").GetString() == "次の質問", "会話を role/content 形式で送る");
        Assert.That(!sent.TryGetProperty("think", out _) && !sent.TryGetProperty("options", out _), "モデル既定値を変更しない");
        Assert.That(stream.WasDisposed, "ストリームを解放");
    }

    public static async Task ProtocolErrors()
    {
        var invalidBodies = new[]
        {
            "{\"message\":{\"content\":\"途中\"},\"done\":false}\n", // done が来ない
            "{not-json}\n",
            "{\"error\":\"model failed\"}\n",
            "{\"message\":{\"thinking\":\"推論のみ\"},\"done\":true}\n",
            "{}\n",
            "null\n",
        };
        foreach (var body in invalidBodies)
        {
            using var client = Client(new StubHandler((_, _) => Task.FromResult(Json(body))));
            await Assert.Throws<ChatServiceException>(() => Consume(new OllamaChatService(client)));
        }
    }

    public static async Task ConnectionErrors()
    {
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.InternalServerError })
        {
            using var client = Client(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
            var error = await Assert.Throws<ChatServiceException>(() => Consume(new OllamaChatService(client)));
            Assert.That(error.Message.Contains(((int)status).ToString()), "HTTP ステータスを表示");
        }
        using var refused = Client(new StubHandler((_, _) => throw new HttpRequestException("connection refused")));
        var connection = await Assert.Throws<ChatServiceException>(() => Consume(new OllamaChatService(refused)));
        Assert.That(connection.Message.Contains("接続"), "接続拒否を UI 向けエラーに変換");
    }

    public static async Task CancellationAndTimeout()
    {
        var body = "{\"message\":{\"content\":\"部分本文\"},\"done\":false}\n";
        var stream = new FragmentStream(body, blockAtEnd: true);
        using var client = Client(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(stream) })));
        using var cancellation = new CancellationTokenSource();
        await using (var iterator = new OllamaChatService(client).StreamReplyAsync("test", Question, cancellation.Token).GetAsyncEnumerator())
        {
            Assert.That(await iterator.MoveNextAsync(), "ヘッダー受信後に本文が来る");
            var waiting = iterator.MoveNextAsync().AsTask();
            cancellation.Cancel();
            await Assert.Throws<OperationCanceledException>(async () => await waiting);
        }
        Assert.That(stream.WasDisposed, "中止時も HTTP ストリームを解放");
        var stalled = new FragmentStream(body, blockAtEnd: true);
        using var timedClient = Client(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(stalled) })));
        var timeout = await Assert.Throws<ChatServiceException>(() => Consume(new OllamaChatService(timedClient,
            replyTimeout: TimeSpan.FromMilliseconds(100))));
        Assert.That(timeout.Message.Contains("タイムアウト") && stalled.WasDisposed, "本文待機にも期限を適用");
        using var stalledModels = Client(new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Json("{}");
        }));
        var listTimeout = await Assert.Throws<ChatServiceException>(async () =>
            await new OllamaChatService(stalledModels, modelTimeout: TimeSpan.FromMilliseconds(100)).GetModelsAsync());
        Assert.That(listTimeout.Message.Contains("タイムアウト"), "一覧取得にも期限を適用");
    }

    public static async Task LiveConversation(string model)
    {
        using var client = new HttpClient { BaseAddress = new Uri("http://localhost:11434/"), Timeout = Timeout.InfiniteTimeSpan };
        var adapter = new OllamaChatService(client);
        var loaded = await adapter.GetLoadedModelsAsync();
        Assert.That(!loaded.Any(item => ChatModelSession.SameModel(item.Name, model)), "実機テストには未ロードのモデルを指定してください。");
        var session = new ChatModelSession(adapter, adapter);
        using var vm = new ChatViewModel(session, session);
        try
        {
        await vm.InitializeAsync();
        Assert.That(!vm.HasError && vm.Models.Contains(model), "実機のモデル一覧に指定モデルがある");
        vm.SelectedModel = model;
        await vm.ModelChangeTask;
        vm.InputText = "合言葉は青空です。了解、とだけ日本語で答えてください。";
        await vm.SendCommand.ExecuteAsync();
        Assert.That(!vm.HasError && vm.Messages[^1].StatusText == "完了", $"実機の初回回答: {vm.ErrorMessage}");
        vm.InputText = "さっきの合言葉だけを答えてください。";
        await vm.SendCommand.ExecuteAsync();
        Assert.That(!vm.HasError && vm.Messages.Count == 4 && vm.Messages[^1].Content.Contains("青空"),
            $"実機の継続回答: {vm.ErrorMessage} / {vm.Messages[^1].Content}");
        Console.WriteLine($"PASS: Live Ollama model list and two-turn streaming conversation ({model})");
        Console.WriteLine($"Reply: {vm.Messages[^1].Content}");
        }
        finally
        {
            vm.Dispose();
            await session.ShutdownAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
        { BaseAddress = new Uri("http://localhost:11434/"), Timeout = Timeout.InfiniteTimeSpan };
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private static async Task Consume(OllamaChatService service)
    {
        await foreach (var _ in service.StreamReplyAsync("test", Question)) { }
    }
}

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
    public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _send(request, cancellationToken);
}

internal sealed class FragmentStream : Stream
{
    private readonly MemoryStream _source;
    private readonly int _fragmentSize;
    private readonly bool _blockAtEnd;
    public bool WasDisposed { get; private set; }
    public FragmentStream(string content, int fragmentSize = 1024, bool blockAtEnd = false)
    {
        _source = new MemoryStream(Encoding.UTF8.GetBytes(content));
        _fragmentSize = fragmentSize;
        _blockAtEnd = blockAtEnd;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_source.Position == _source.Length && _blockAtEnd)
            await Task.Delay(Timeout.Infinite, cancellationToken);
        return await _source.ReadAsync(buffer[..Math.Min(buffer.Length, _fragmentSize)], cancellationToken);
    }
    public override int Read(byte[] buffer, int offset, int count) => _source.Read(buffer, offset, Math.Min(count, _fragmentSize));
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }
}
