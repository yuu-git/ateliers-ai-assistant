#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Desktop.Controls;
using Ateliers.Ai.Assistant.Desktop.Images;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Chat;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Ateliers.Ai.Assistant.Desktop.Diagnostics;

/// <summary>クリップボードを退避・復元し、画像貼り付けを実画面で確認する Debug 専用の診断。</summary>
internal static class ImagePasteVerification
{
    /// <summary>合成画像・貼り付け・履歴・競合を検証し、結果を出力するウィンドウを作る。</summary>
    /// <param name="reportPath">検証結果のテキストを書き出すファイルパス。</param>
    /// <param name="visionModel">実機確認する導入済みモデル名。null の場合は検証用サービスを使い Ollama を呼ばない。</param>
    /// <returns>検証開始前のウィンドウ。呼び出し元で Activate する。</returns>
    public static Window Create(string reportPath, string? visionModel)
    {
        var client = new HttpClient { BaseAddress = new Uri("http://localhost:11434/"), Timeout = Timeout.InfiniteTimeSpan };
        var adapter = new OllamaChatService(client);
        var session = new ChatModelSession(adapter, adapter);
        var recording = new RecordingService(visionModel is null ? new PreviewChatService() : session);
        var vm = new ChatViewModel(recording);
        var transcript = new ChatTranscriptView { ItemsSource = vm.Messages };
        var input = new TextBox { AcceptsReturn = true, PlaceholderText = "画像貼り付け検証" };
        input.SetBinding(Control.IsEnabledProperty, new Binding { Source = vm, Path = new PropertyPath(nameof(ChatViewModel.CanEdit)), Mode = BindingMode.OneWay });
        input.TextChanged += (_, _) => vm.InputText = input.Text;
        var preview = new Image { Width = 180, Height = 100 };
        var panel = new StackPanel { Visibility = Visibility.Collapsed };
        panel.Children.Add(preview);
        var error = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = true };
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(error); stack.Children.Add(panel); stack.Children.Add(input);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(transcript); root.Children.Add(stack); Grid.SetRow(stack, 1);
        var controller = new ClipboardImageController(input, preview, panel, error, vm);
        var insidePasteEvent = false;
        var readInsidePasteEvent = false;
        var clipboardReads = 0;
        controller.BeforeClipboardReadForVerification = () =>
        {
            clipboardReads++;
            readInsidePasteEvent |= insidePasteEvent;
        };
        void PasteInput()
        {
            insidePasteEvent = true;
            try { input.PasteFromClipboard(); }
            finally { insidePasteEvent = false; }
        }
        var window = new Window { Content = root, Title = "画像貼り付け検証" };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 800));
        var started = false;
        window.Activated += async (_, _) =>
        {
            if (started) return;
            started = true;
            var report = new List<string>();
            DataPackage? backup = null;
            var clipboardChanged = false;
            try
            {
                if (!await transcript.Ready.WaitAsync(TimeSpan.FromSeconds(20))) throw new InvalidOperationException("WebView2 initialization failed");
                var original = Clipboard.GetContent();
                backup = new DataPackage();
                foreach (var format in original.AvailableFormats) backup.SetData(format, await original.GetDataAsync(format));
                if (visionModel is not null && (await adapter.GetLoadedModelsAsync()).Count != 0)
                    throw new InvalidOperationException("Live vision test requires Ollama to have no loaded models");
                await vm.InitializeAsync();
                vm.SelectedModel = visionModel ?? "preview";
                if (!vm.Models.Contains(vm.SelectedModel!)) throw new InvalidOperationException("Requested model is not installed");
                input.Focus(FocusState.Programmatic);
                var text = new DataPackage(); text.SetText("通常のテキスト貼り付け");
                Clipboard.SetContent(text); clipboardChanged = true;
                PasteInput();
                await Wait(() => input.Text == "通常のテキスト貼り付け" && vm.PendingImage is null, "plain text paste");
                report.Add("PASS: native TextBox paste preserves normal text-only behavior");

                input.Text = "before SELECT after";
                input.Select(7, 6);
                var replacement = new DataPackage(); replacement.SetText("置換\r\n次の行");
                Clipboard.SetContent(replacement); PasteInput();
                await Wait(() => input.Text.Replace("\r\n", "\n").Replace('\r', '\n') == "before 置換\n次の行 after", "selected text replacement");
                if (input.SelectionStart != input.Text.IndexOf(" after", StringComparison.Ordinal) || input.SelectionLength != 0)
                    throw new InvalidOperationException("Paste caret position incorrect");
                input.Undo();
                await Wait(() => input.Text == "before SELECT after", "paste undo");
                input.Text = "ab"; input.Select(2, 0); input.MaxLength = 5;
                var limited = new DataPackage(); limited.SetText("cdefgh");
                Clipboard.SetContent(limited); PasteInput();
                await Wait(() => input.Text == "abcde", "text maximum length");
                input.MaxLength = 0;
                input.Text = "";
                var repeated = new DataPackage(); repeated.SetText("追"); Clipboard.SetContent(repeated);
                for (var i = 0; i < 50; i++) PasteInput();
                await Wait(() => input.Text == new string('追', 50), "50 queued text pastes");
                report.Add("PASS: selection replacement, multiline text, caret, undo, maximum length, and 50 queued text pastes");

                var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var delayed = new DataPackage();
                delayed.SetDataProvider(StandardDataFormats.Text, async request =>
                {
                    var deferral = request.GetDeferral();
                    try { providerStarted.TrySetResult(); await releaseProvider.Task; request.SetData("遅れて届くテキスト"); }
                    finally { deferral.Complete(); }
                });
                Clipboard.SetContent(delayed); PasteInput();
                try
                {
                    await providerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    input.Text = "待機中に編集した入力";
                }
                finally { releaseProvider.TrySetResult(); }
                await Wait(() => error.IsOpen, "late clipboard result rejected");
                if (input.Text != "待機中に編集した入力") throw new InvalidOperationException("Late clipboard content overwrote newer input");
                report.Add("PASS: delayed clipboard provider cannot overwrite newer user input");

                using var bitmap = await CreateRedImageAsync();
                var data = new DataPackage(); data.SetBitmap(RandomAccessStreamReference.CreateFromStream(bitmap));
                Clipboard.SetContent(data);
                PasteInput();
                await Wait(() => vm.PendingImage is not null && preview.Source is not null && vm.CanEdit, "bitmap paste and preview");
                var attached = vm.PendingImage!;
                if (attached.Width != 1536 || attached.Height > 1536 || panel.Visibility != Visibility.Visible)
                    throw new InvalidOperationException("Resize or preview failed");
                vm.RemoveImageCommand.Execute(null);
                if (vm.PendingImage is not null || preview.Source is not null || panel.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("Remove image failed");
                PasteInput();
                await Wait(() => vm.PendingImage is not null && preview.Source is not null && vm.CanEdit, "reattach");
                var previous = vm.PendingImage;
                PasteInput();
                await Wait(() => vm.PendingImage is not null && !ReferenceEquals(previous, vm.PendingImage) && vm.CanEdit, "replace attachment");
                report.Add("PASS: clipboard bitmap, resize 3000px to 1536px, preview, remove, and replacement");
                for (var i = 0; i < 20; i++)
                {
                    Clipboard.SetContent(text); input.Text = ""; PasteInput();
                    await Wait(() => input.Text == "通常のテキスト貼り付け", "alternating text paste " + i);
                    previous = vm.PendingImage;
                    Clipboard.SetContent(data); PasteInput();
                    await Wait(() => vm.PendingImage is not null && !ReferenceEquals(previous, vm.PendingImage) && preview.Source is not null && vm.CanEdit,
                        "alternating image paste " + i);
                }
                report.Add("PASS: 20 alternating text / 3000px bitmap paste cycles with preview");
                var imageReadsBefore = clipboardReads;
                for (var i = 0; i < 5; i++) PasteInput();
                await Wait(() => clipboardReads == imageReadsBefore + 5 && vm.CanEdit && preview.Source is not null, "5 queued bitmap pastes");
                report.Add("PASS: 5 queued bitmap pastes processed serially through asynchronous decoding and preview");
                input.Text = "";
                await Wait(() => vm.InputText == "", "image-only input synchronization");
                if (!vm.SendCommand.CanExecute(null)) throw new InvalidOperationException("Image-only send disabled");
                await vm.SendCommand.ExecuteAsync();
                if (vm.HasError || recording.Requests[0][^1].Content != "" || recording.Requests[0][^1].Image is null)
                    throw new InvalidOperationException("Image-only send failed: " + vm.ErrorMessage);
                await WaitDom(transcript, "document.querySelectorAll('.attachment:not([hidden])').length === 1 && document.querySelector('.attachment:not([hidden])').naturalWidth === 1536");
                if (vm.PendingImage is not null || panel.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Sent preview was not cleared");
                input.Text = "前の画像は何色ですか。日本語の短い一文で答えてください。";
                await Wait(() => vm.InputText == input.Text, "follow-up input synchronization");
                await vm.SendCommand.ExecuteAsync();
                if (vm.HasError || recording.Requests[1].Count != 3 || recording.Requests[1][0].Image is null || recording.Requests[1][^1].Image is not null)
                    throw new InvalidOperationException("Image conversation failed: " + vm.ErrorMessage);
                report.Add("PASS: image-only send, sent image in WebView2 history, and image retained for follow-up request");
                if (visionModel is not null) report.Add("PASS: live Ollama image-only and follow-up responses (" + visionModel + "): " + vm.Messages[^1].Content);

                PasteInput();
                await Wait(() => vm.PendingImage is not null && vm.CanEdit, "attach before reset");
                vm.NewConversationCommand.Execute(null);
                await WaitDom(transcript, "document.querySelectorAll('.message').length === 0");
                if (vm.PendingImage is not null) throw new InvalidOperationException("Pending image survived new conversation");
                using var oversized = new InMemoryRandomAccessStream { Size = ClipboardImageService.MaximumSourceBytes + 1 };
                try { await ClipboardImageService.NormalizeAsync(oversized, CancellationToken.None); throw new InvalidOperationException("Size guard not applied"); }
                catch (InvalidOperationException exception) when (exception.Message.Contains("32 MiB", StringComparison.Ordinal)) { }
                using var invalid = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(invalid)) { writer.WriteBytes(new byte[] { 1, 2, 3 }); await writer.StoreAsync(); writer.DetachStream(); }
                invalid.Seek(0);
                var bad = new DataPackage(); bad.SetBitmap(RandomAccessStreamReference.CreateFromStream(invalid));
                Clipboard.SetContent(bad); PasteInput();
                await Wait(() => error.IsOpen && vm.CanEdit, "invalid bitmap error recovery");
                report.Add("PASS: new conversation clears images, oversized source rejected, invalid bitmap recovers with error");
                if (readInsidePasteEvent) throw new InvalidOperationException("Clipboard was read inside the native Paste event");
                report.Add("PASS: all " + clipboardReads + " clipboard reads deferred until after the native Paste call returned");
                var readsBeforeDispose = clipboardReads;
                Clipboard.SetContent(text); PasteInput(); controller.Dispose();
                await Task.Delay(150);
                if (clipboardReads != readsBeforeDispose) throw new InvalidOperationException("Disposed controller accessed clipboard");
                report.Add("PASS: queued paste discarded when controller is disposed");
                report.Add("PASS: ALL IMAGE PASTE CHECKS");
            }
            catch (Exception exception) { report.Add("FAIL: " + exception); }
            finally
            {
                controller.Dispose(); vm.Dispose(); transcript.Dispose();
                await session.ShutdownAsync(TimeSpan.FromSeconds(5));
                client.Dispose();
                if (clipboardChanged && backup is not null)
                {
                    try { Clipboard.SetContent(backup); Clipboard.Flush(); report.Add("PASS: original clipboard restored"); }
                    catch (Exception exception) { report.Add("FAIL: clipboard restore: " + exception.Message); }
                }
                File.WriteAllLines(reportPath, report);
                window.Close();
            }
        };
        window.Closed += (_, _) => { controller.Dispose(); vm.Dispose(); transcript.Dispose(); };
        return window;
    }

    /// <summary>条件が成立するまで短時間ずつ待ち、15秒で失敗にする。</summary>
    /// <param name="condition">成立を待つ検証条件。</param>
    /// <param name="label">失敗時に対象を識別する検証項目名。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    private static async Task Wait(Func<bool> condition, string label)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15)) { if (condition()) return; await Task.Delay(50); }
        throw new InvalidOperationException("Timed out: " + label);
    }
    /// <summary>ブラウザー上の条件式が true になるまで、15秒を上限に確認する。</summary>
    /// <param name="transcript">初期化済みの会話表示コントロール。</param>
    /// <param name="expression">ブラウザーで評価する検証用の真偽式。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    private static async Task WaitDom(ChatTranscriptView transcript, string expression)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (await transcript.CoreForVerification.ExecuteScriptAsync(expression) == "true") return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("Image DOM check failed");
    }
    /// <summary>縮小検証用の3000×1600ピクセルの赤い PNG をメモリ内に生成する。</summary>
    /// <returns>呼び出し側で破棄する、合成 PNG のメモリ内ストリーム。</returns>
    private static async Task<InMemoryRandomAccessStream> CreateRedImageAsync()
    {
        var pixels = new byte[3000 * 1600 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 255; pixels[i + 3] = 255; }
        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 3000, 1600, 96, 96, pixels);
        await encoder.FlushAsync(); stream.Seek(0); return stream;
    }
    /// <summary>検証対象サービスへ委譲しながら、送信された会話履歴を記録するラッパー。</summary>
    /// <param name="inner">要求を委譲する検証対象サービス。</param>
    private sealed class RecordingService(IChatService inner) : IChatService
    {
        /// <summary>画像と履歴の引き継ぎを確認するための送信要求一覧を取得する。</summary>
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = new();
        /// <summary>検証対象サービスのモデル一覧を取得する。</summary>
        /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
        /// <returns>利用可能なモデル名の一覧。</returns>
        public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default) => inner.GetModelsAsync(cancellationToken);
        /// <summary>要求履歴を記録してから、検証対象サービスの回答を返す。</summary>
        /// <param name="model">操作または送信に使用するモデル名。</param>
        /// <param name="messages">確定済みの会話履歴と、最後の要素に置く今回の利用者入力。</param>
        /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
        /// <returns>受信順に列挙する回答本文の断片。</returns>
        public IAsyncEnumerable<string> StreamReplyAsync(string model, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
        { Requests.Add(messages); return inner.StreamReplyAsync(model, messages, cancellationToken); }
    }
    /// <summary>Ollama を呼ばず、画像送信経路の確認用 Markdown を返す検証用サービス。</summary>
    private sealed class PreviewChatService : IChatService
    {
        /// <summary>検証専用モデル preview の一覧を返す。</summary>
        /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
        /// <returns>利用可能なモデル名の一覧。</returns>
        public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(new[] { "preview" });
        /// <summary>赤い画像を説明する Markdown を二つの断片として返す。</summary>
        /// <param name="model">操作または送信に使用するモデル名。</param>
        /// <param name="messages">確定済みの会話履歴と、最後の要素に置く今回の利用者入力。</param>
        /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
        /// <returns>受信順に列挙する回答本文の断片。</returns>
        public async IAsyncEnumerable<string> StreamReplyAsync(string model, IReadOnlyList<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.Delay(20, cancellationToken); yield return "# 解析\n\n"; await Task.Delay(20, cancellationToken); yield return "**赤い画像**です。"; }
    }
}
#endif
