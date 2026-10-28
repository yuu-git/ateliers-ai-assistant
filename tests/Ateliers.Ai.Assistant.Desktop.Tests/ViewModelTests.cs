using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Chat;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

internal static class Assert
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    public static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}

internal static class ViewModelTests
{
    public static async Task InitialState()
    {
        using var vm = new ChatViewModel(new DummyChatService());
        Assert.That(vm.Messages.Count == 0 && vm.IsEmpty, "ダミーの会話を本番履歴に含めない");
        vm.InputText = "テスト";
        Assert.That(!vm.SendCommand.CanExecute(null), "取得・選択前は送信不可");
        await vm.InitializeAsync();
        Assert.That(vm.Models.Count == 1 && vm.SelectedModel is null, "モデルを自動選択しない");
        vm.SelectedModel = "dummy";
        vm.InputText = " \r\n ";
        await vm.SendCommand.ExecuteAsync();
        Assert.That(vm.Messages.Count == 0, "空白入力を送信しない");
        vm.InputText = "  テスト\n改行  ";
        await vm.SendCommand.ExecuteAsync();
        Assert.That(vm.Messages.Count == 2 && vm.Messages[0].Content == "テスト\n改行", "改行を保持");
        Assert.That(vm.Messages[1].Content.Contains("テスト\n改行") && vm.Messages[1].StatusText == "完了", "応答完了");
        Assert.That(vm.InputText == "" && vm.CanEdit && !vm.HasError, "入力消去と操作復帰");
    }

    public static async Task Streaming()
    {
        var service = new TestChatService();
        using var vm = await Ready(service);
        var channel = service.EnqueuePending("前半");
        vm.InputText = "質問";
        var pending = vm.SendCommand.ExecuteAsync();
        var bubble = vm.Messages[1];
        Assert.That(vm.IsSending && !pending.IsCompleted && bubble.Content == "前半", "完了前に部分本文を表示");
        Assert.That(!vm.CanEdit && vm.CancelCommand.CanExecute(null) &&
            !vm.RefreshModelsCommand.CanExecute(null) && !vm.NewConversationCommand.CanExecute(null), "生成中の操作制御");
        vm.InputText = "二重送信";
        await vm.SendCommand.ExecuteAsync();
        Assert.That(service.Requests.Count == 1, "二重送信防止");
        var changes = new List<string?>();
        bubble.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        channel.Writer.TryWrite("後半");
        channel.Writer.TryComplete();
        await pending;
        Assert.That(ReferenceEquals(bubble, vm.Messages[1]) && bubble.Content == "前半後半", "同じ吹き出しを更新");
        Assert.That(changes.Contains("Content") && changes.Contains("DetailText"), "XAML 向け通知");
        Assert.That(vm.CanEdit && !vm.CancelCommand.CanExecute(null), "完了後の操作復帰");
    }

    public static async Task Conversation()
    {
        var service = new TestChatService();
        using var vm = await Ready(service);
        service.EnqueueComplete("回答1");
        vm.InputText = "質問1";
        await vm.SendCommand.ExecuteAsync();
        vm.SelectedModel = "model-b";
        service.EnqueueComplete("回答2");
        vm.InputText = "質問2";
        await vm.SendCommand.ExecuteAsync();
        var request = service.Requests[1];
        Assert.That(request.Model == "model-b" && request.Messages.Count == 3, "モデル変更でも会話を維持");
        Assert.That(request.Messages[1] == new ChatMessage(ChatRole.Assistant, "回答1"), "以前の完了応答を送る");
        Assert.That(vm.Messages[1].Model == "model-a", "各応答の使用モデルを保持");
        vm.NewConversationCommand.Execute(null);
        Assert.That(vm.IsEmpty && vm.SelectedModel == "model-b", "会話だけリセット");
        service.EnqueueComplete("新規回答");
        vm.InputText = "新規質問";
        await vm.SendCommand.ExecuteAsync();
        Assert.That(service.Requests[2].Messages.Count == 1, "新しい会話に前の履歴を含めない");
    }

    public static async Task Cancellation()
    {
        var service = new TestChatService();
        using var vm = await Ready(service);
        service.EnqueueComplete("完了回答");
        vm.InputText = "完了質問";
        await vm.SendCommand.ExecuteAsync();
        service.EnqueuePending("途中の回答");
        vm.InputText = "中止する質問";
        var pending = vm.SendCommand.ExecuteAsync();
        vm.CancelCommand.Execute(null);
        await pending;
        Assert.That(service.Token.IsCancellationRequested && vm.Messages[3].Content == "途中の回答", "中止伝播と部分本文保持");
        Assert.That(vm.Messages[2].StatusText.Contains("中止") && vm.Messages[3].StatusText.Contains("中止") &&
            !vm.HasError && vm.CanEdit, "中止を通信エラーと区別");
        service.EnqueueComplete("次の回答");
        vm.InputText = "次の質問";
        await vm.SendCommand.ExecuteAsync();
        var request = service.Requests[2].Messages;
        Assert.That(request.Count == 3 && request[0].Content == "完了質問" && request[^1].Content == "次の質問",
            "完了履歴を維持し、中止した送受信を両方除外");
    }

    public static async Task FailureRecovery()
    {
        var service = new TestChatService();
        using var vm = await Ready(service);
        var channel = service.EnqueuePending("一部");
        vm.InputText = "失敗質問";
        var pending = vm.SendCommand.ExecuteAsync();
        channel.Writer.TryComplete(new ChatServiceException("テスト用通信エラー"));
        await pending;
        Assert.That(vm.ErrorMessage == "テスト用通信エラー" && vm.Messages[1].Content == "一部" &&
            vm.Messages[1].StatusText.Contains("失敗") && vm.CanEdit, "部分応答の失敗状態と復帰");
        service.EnqueueComplete();
        vm.InputText = "空応答の質問";
        await vm.SendCommand.ExecuteAsync();
        Assert.That(vm.HasError, "空の応答を成功履歴にしない");
        service.EnqueueComplete("成功");
        vm.InputText = "回復確認";
        await vm.SendCommand.ExecuteAsync();
        Assert.That(service.Requests[^1].Messages.Count == 1 && !vm.HasError, "失敗の履歴除外・エラー消去");
    }

    public static async Task ModelFailures()
    {
        var service = new TestChatService
        {
            ListHandler = _ => Task.FromException<IReadOnlyList<string>>(new ChatServiceException("一覧取得失敗"))
        };
        using var vm = new ChatViewModel(service);
        await vm.InitializeAsync();
        Assert.That(vm.HasError && vm.CanEdit && !vm.SendCommand.CanExecute(null), "一覧失敗でも操作可能");
        service.ListHandler = _ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        await vm.RefreshModelsCommand.ExecuteAsync();
        Assert.That(!vm.HasError && vm.StatusText.Contains("モデルがありません"), "空のモデル一覧を区別");
        service.ListHandler = _ => Task.FromResult<IReadOnlyList<string>>(new[] { "model-a" });
        await vm.RefreshModelsCommand.ExecuteAsync();
        vm.SelectedModel = "model-a";
        await vm.RefreshModelsCommand.ExecuteAsync();
        Assert.That(vm.SelectedModel == "model-a", "再取得で選択を維持");
        service.ListHandler = _ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        await vm.RefreshModelsCommand.ExecuteAsync();
        Assert.That(vm.SelectedModel is null, "削除済みモデルを送信に使用しない");
    }

    public static async Task DisposeDuringWork()
    {
        var service = new TestChatService();
        var vm = await Ready(service);
        service.EnqueuePending();
        vm.InputText = "終了確認";
        var pending = vm.SendCommand.ExecuteAsync();
        vm.Dispose();
        await pending;
        vm.Dispose();
        Assert.That(service.Token.IsCancellationRequested && !vm.CanEdit && !vm.SendCommand.CanExecute(null), "終了時キャンセル");
        var listCompletion = new TaskCompletionSource<IReadOnlyList<string>>();
        service.ListHandler = _ => listCompletion.Task;
        var closing = new ChatViewModel(service);
        var loading = closing.InitializeAsync();
        Assert.That(closing.IsLoadingModels && !closing.CanEdit, "一覧取得も非同期");
        closing.Dispose();
        listCompletion.SetResult(new[] { "late-model" });
        await loading;
        Assert.That(closing.Models.Count == 0, "終了後に届いた一覧を反映しない");
    }

    private static async Task<ChatViewModel> Ready(TestChatService service)
    {
        var vm = new ChatViewModel(service);
        await vm.InitializeAsync();
        vm.SelectedModel = "model-a";
        return vm;
    }
}

internal sealed class TestChatService : IChatService
{
    public Func<CancellationToken, Task<IReadOnlyList<string>>> ListHandler { get; set; } =
        _ => Task.FromResult<IReadOnlyList<string>>(new[] { "model-a", "model-b" });
    private readonly Queue<Channel<string>> _replies = new();
    public List<(string Model, IReadOnlyList<ChatMessage> Messages)> Requests { get; } = new();
    public CancellationToken Token { get; private set; }

    public Channel<string> EnqueuePending(params string[] chunks)
    {
        var channel = Channel.CreateUnbounded<string>();
        foreach (var chunk in chunks) channel.Writer.TryWrite(chunk);
        _replies.Enqueue(channel);
        return channel;
    }
    public void EnqueueComplete(params string[] chunks) => EnqueuePending(chunks).Writer.TryComplete();
    public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default) => ListHandler(cancellationToken);
    public async IAsyncEnumerable<string> StreamReplyAsync(string model, IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add((model, messages));
        Token = cancellationToken;
        var channel = _replies.Dequeue();
        await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken)) yield return chunk;
    }
}
