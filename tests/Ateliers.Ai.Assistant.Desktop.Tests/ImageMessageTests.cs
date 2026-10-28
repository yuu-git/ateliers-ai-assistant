using System.Net;
using System.Text;
using System.Text.Json;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Desktop.Presentation;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Chat;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

internal static class ImageMessageTests
{
    private const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jB2kAAAAASUVORK5CYII=";
    public static ChatImage Image() => new(Convert.FromBase64String(Pixel), 1, 1);

    private static async Task<ChatViewModel> Ready(TestChatService service)
    {
        var vm = new ChatViewModel(service);
        await vm.InitializeAsync();
        vm.SelectedModel = "model-a";
        return vm;
    }
    private static void Attach(ChatViewModel vm, ChatImage image)
    {
        Assert.That(vm.TryBeginImagePaste(), "貼り付け開始");
        Assert.That(!vm.CanEdit && !vm.SendCommand.CanExecute(null), "変換中の排他");
        vm.CompleteImagePaste(image);
    }
    public static async Task SendingAndHistory()
    {
        var service = new TestChatService();
        using var vm = await Ready(service);
        var image = Image();
        Attach(vm, image);
        Assert.That(vm.SendCommand.CanExecute(null) && vm.RemoveImageCommand.CanExecute(null), "画像だけで送信可能");
        vm.RemoveImageCommand.Execute(null);
        Assert.That(vm.PendingImage is null && !vm.SendCommand.CanExecute(null), "送信前の画像削除");
        Attach(vm, image);
        var replacement = Image();
        Attach(vm, replacement);
        service.EnqueueComplete("画像の回答");
        await vm.SendCommand.ExecuteAsync();
        Assert.That(service.Requests[0].Messages[0].Content == "" && ReferenceEquals(service.Requests[0].Messages[0].Image, replacement), "画像のみ・貼り直しは置き換え");
        Assert.That(vm.PendingImage is null && ReferenceEquals(vm.Messages[0].Image, replacement), "入力から履歴へ画像を移す");
        service.EnqueueComplete("継続回答");
        vm.InputText = "前の画像について";
        await vm.SendCommand.ExecuteAsync();
        Assert.That(service.Requests[1].Messages.Count == 3 && ReferenceEquals(service.Requests[1].Messages[0].Image, replacement) &&
            service.Requests[1].Messages[^1].Image is null, "後続のリクエストに以前の画像を保持");
        Attach(vm, image);
        vm.NewConversationCommand.Execute(null);
        Assert.That(vm.PendingImage is null && vm.Messages.Count == 0, "会話リセットで添付も破棄");
        service.EnqueueComplete("新規回答");
        vm.InputText = "新規";
        await vm.SendCommand.ExecuteAsync();
        Assert.That(service.Requests[^1].Messages.Count == 1 && service.Requests[^1].Messages[0].Image is null, "旧画像を送信しない");
    }
    public static async Task CancelAndFailure()
    {
        var service = new TestChatService();
        using var vm = await Ready(service);
        var image = Image();
        Attach(vm, image);
        service.EnqueuePending("途中");
        vm.InputText = "画像を解析";
        var pending = vm.SendCommand.ExecuteAsync();
        Assert.That(!vm.TryBeginImagePaste(), "チャット中の貼り付け禁止");
        vm.CancelCommand.Execute(null);
        await pending;
        Assert.That(ReferenceEquals(vm.Messages[0].Image, image), "中止画像は表示履歴に残す");
        Attach(vm, image);
        var failed = service.EnqueuePending();
        failed.Writer.TryComplete(new ChatServiceException("このモデルは画像に非対応です。"));
        await vm.SendCommand.ExecuteAsync();
        Assert.That(vm.HasError && vm.ErrorMessage.Contains("非対応"), "画像非対応の失敗表示");
        service.EnqueueComplete("回復");
        vm.InputText = "テキスト";
        await vm.SendCommand.ExecuteAsync();
        Assert.That(service.Requests[^1].Messages.Count == 1 && service.Requests[^1].Messages[0].Image is null, "中止・失敗画像を次回へ送らない");
    }
    public static async Task HttpPayload()
    {
        JsonElement payload = default;
        using var client = new HttpClient(new StubHandler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            payload = document.RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"message\":{\"content\":\"画像回答\"},\"done\":true}\n", Encoding.UTF8, "application/x-ndjson") };
        })) { BaseAddress = new Uri("http://localhost:11434/") };
        var image = Image();
        var history = new[] { new ChatMessage(ChatRole.User, "", image), new ChatMessage(ChatRole.Assistant, "前の回答"),
            new ChatMessage(ChatRole.User, "次の質問", image) };
        await foreach (var _ in new OllamaChatService(client).StreamReplyAsync("vision", history)) { }
        var messages = payload.GetProperty("messages");
        Assert.That(messages[0].GetProperty("content").GetString() == "" && messages[0].GetProperty("images").GetArrayLength() == 1 &&
            messages[0].GetProperty("images")[0].GetString() == image.Base64Data, "PNG の Base64 だけを images に渡す");
        Assert.That(!messages[1].TryGetProperty("images", out _) && messages[2].GetProperty("images")[0].GetString() == image.Base64Data,
            "本文と画像の併用・画像なしのメッセージは images を省略");
    }
    public static async Task ServerError()
    {
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"model does not support images\"}", Encoding.UTF8, "application/json")
        }))) { BaseAddress = new Uri("http://localhost:11434/") };
        var error = await Assert.Throws<ChatServiceException>(async () =>
        {
            await foreach (var _ in new OllamaChatService(client).StreamReplyAsync("text-only", new[] { new ChatMessage(ChatRole.User, "", Image()) })) { }
        });
        Assert.That(error.Message.Contains("400") && error.Message.Contains("model does not support images"), "Ollama の理由を表示する");
    }
    public static Task MemoryAndDisplay()
    {
        var original = Convert.FromBase64String(Pixel);
        var image = new ChatImage(original, 1, 1);
        original[0] = 0;
        var copy = image.GetPngBytes();
        copy[0] = 0;
        Assert.That(image.Base64Data == Pixel, "入力配列・返却配列の変更から画像を保護");
        var cache = new TranscriptRenderCache();
        var snapshot = new MessageSnapshot("image", "原文 **維持**", "あなた", "完了", true, true, image);
        var rendered = cache.Render(new[] { snapshot })[0];
        Assert.That(rendered.ImageDataUrl == "data:image/png;base64," + Pixel && !rendered.Html.Contains("<img") && snapshot.Content == "原文 **維持**",
            "添付は安全化 HTML と別経路で表示");
        return Task.CompletedTask;
    }
}
