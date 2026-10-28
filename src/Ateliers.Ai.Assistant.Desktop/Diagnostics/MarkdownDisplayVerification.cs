#if DEBUG
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Desktop.Controls;
using Ateliers.Ai.Assistant.Desktop.Presentation;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Microsoft.UI.Xaml;

namespace Ateliers.Ai.Assistant.Desktop.Diagnostics;

/// <summary>明示指定時だけ起動する開発用の WebView2 実機検証。Ollama は操作しない。</summary>
internal static class MarkdownDisplayVerification
{
    /// <summary>Markdown の実ブラウザー検証を実行し、結果を出力するウィンドウを作る。</summary>
    /// <param name="reportPath">検証結果のテキストを書き出すファイルパス。</param>
    /// <returns>検証開始前のウィンドウ。呼び出し元で Activate する。</returns>
    public static Window Create(string reportPath)
    {
        var messages = new ObservableCollection<ChatMessageViewModel>();
        var initial = new ChatMessageViewModel(new ChatMessage(ChatRole.Assistant, "# 初期化前\n\n**太字**\n\n- 一つ\n- 二つ\n\n|項目|値|\n|---|---|\n|言語|C#|\n\n```csharp\npublic class Example {}\n```"), "test", "完了");
        messages.Add(initial);
        var view = new ChatTranscriptView { ItemsSource = messages };
        initial.Append("\n\n初期化待機中の更新");
        var window = new Window { Content = view, Title = "Markdown 表示検証" };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));
        var started = false;
        window.Activated += async (_, _) =>
        {
            if (started) return;
            started = true;
            var report = new List<string>();
            try
            {
                if (!await view.Ready.WaitAsync(TimeSpan.FromSeconds(20))) throw new InvalidOperationException("WebView2 initialization failed");
                await RunAsync(view, messages, report);
                report.Add("PASS: ALL MARKDOWN DISPLAY CHECKS");
            }
            catch (Exception exception) { report.Add("FAIL: " + exception); }
            finally
            {
                File.WriteAllLines(reportPath, report);
                view.Dispose();
                window.Close();
            }
        };
        window.Closed += (_, _) => view.Dispose();
        return window;
    }

    /// <summary>書式・ストリーミング・安全性・旧結果排除・スクロールを実ブラウザーで検証する。</summary>
    /// <param name="view">初期化済みの会話表示コントロール。</param>
    /// <param name="messages">確定済みの会話履歴と、最後の要素に置く今回の利用者入力。</param>
    /// <param name="report">検証結果を追記する一覧。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    private static async Task RunAsync(ChatTranscriptView view, ObservableCollection<ChatMessageViewModel> messages, List<string> report)
    {
        var core = view.CoreForVerification;
        async Task Wait(string expression, string name)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (await core.ExecuteScriptAsync(expression) == "true") return;
                await Task.Delay(50);
            }
            throw new InvalidOperationException("DOM check failed: " + name + " / " + await core.ExecuteScriptAsync("document.body.innerText"));
        }
        await Wait("document.querySelectorAll('.message').length === 1 && document.body.innerText.includes('初期化待機中の更新') && !!document.querySelector('h1') && !!document.querySelector('strong') && document.querySelectorAll('li').length === 2 && !!document.querySelector('table') && !!document.querySelector('code .hljs-keyword')", "initial formatting");
        report.Add("PASS: pre-initialization updates, headings, bold, lists, tables, and C# highlighting");
        await core.ExecuteScriptAsync("window.__stable = document.querySelector('.message .body');");
        var streaming = new ChatMessageViewModel(new ChatMessage(ChatRole.Assistant, "```csharp\n"), "test", "受信中…");
        messages.Add(streaming);
        streaming.Append("public string Text = \"日本語\";");
        await Wait("document.querySelectorAll('.message').length === 2 && document.body.innerText.includes('public string Text')", "streaming partial");
        streaming.Append("\n// 中止直前の最終文字");
        streaming.StatusText = "中止 · 次回の履歴には含めません";
        await Wait("document.body.innerText.includes('中止直前の最終文字') && document.body.innerText.includes('中止 ·') && !!document.querySelector('.message:last-child code .hljs-keyword')", "cancel flush");
        streaming.StatusText = "失敗 · 次回の履歴には含めません";
        await Wait("document.body.innerText.includes('失敗 ·') && window.__stable === document.querySelector('.message .body')", "failure metadata");
        if (!streaming.Content.EndsWith("中止直前の最終文字", StringComparison.Ordinal)) throw new InvalidOperationException("Raw Markdown changed");
        report.Add("PASS: partial streaming, unfinished fence, cancel/failure final flush, unchanged completed DOM and raw Markdown");

        messages.Add(new ChatMessageViewModel(new ChatMessage(ChatRole.Assistant,
            "<script>window.__injected = true</script><img src=x onerror=alert(1)>\n\n[安全なリンク](https://example.com/test) [危険](javascript:alert%281%29)\n\n![外部画像](https://example.com/image.png)"), "test", "完了"));
        await Wait("document.querySelectorAll('.message').length === 3 && !document.querySelector('.body script, .body img, .body iframe') && !window.__injected && document.querySelectorAll('a[href]').length === 1", "safe HTML");
        Uri? launched = null;
        view.VerificationLinkLauncher = uri => { launched = uri; return Task.FromResult(true); };
        await core.ExecuteScriptAsync("document.querySelector('a[href]').scrollIntoView({block:'center'});");
        using (var rect = JsonDocument.Parse(await core.ExecuteScriptAsync("(()=>{const r=document.querySelector('a[href]').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()")))
        {
            var x = rect.RootElement.GetProperty("x").GetDouble().ToString(CultureInfo.InvariantCulture);
            var y = rect.RootElement.GetProperty("y").GetDouble().ToString(CultureInfo.InvariantCulture);
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mousePressed\",\"button\":\"left\",\"clickCount\":1,\"x\":" + x + ",\"y\":" + y + "}");
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mouseReleased\",\"button\":\"left\",\"clickCount\":1,\"x\":" + x + ",\"y\":" + y + "}");
        }
        for (var count = 0; count < 30 && launched is null; count++) await Task.Delay(50);
        if (launched?.AbsoluteUri != "https://example.com/test") throw new InvalidOperationException("Trusted link click did not reach validated host launcher");
        core.Navigate("https://example.com/");
        await Task.Delay(200);
        if (core.Source != TranscriptProtocol.Document) throw new InvalidOperationException("External navigation was allowed");
        await core.ExecuteScriptAsync("window.__blocked = false; fetch('https://example.com/blocked').catch(()=>window.__blocked=true);");
        await Wait("window.__blocked === true", "external resource restriction");
        report.Add("PASS: HTML/script/image rejection, trusted external link dispatch, external navigation and fetch blocking");

        messages.Clear();
        messages.Add(new ChatMessageViewModel(new ChatMessage(ChatRole.Assistant, new string('旧', 50000)), "test", "完了"));
        messages.Clear();
        messages.Add(new ChatMessageViewModel(new ChatMessage(ChatRole.Assistant, "新しい会話だけ"), "test", "完了"));
        await Wait("document.querySelectorAll('.message').length === 1 && document.body.innerText.includes('新しい会話だけ') && !document.body.innerText.includes('旧旧')", "conversation generation");
        report.Add("PASS: conversation reset rejects previous asynchronous rendering");

        messages.Clear();
        for (var index = 0; index < 40; index++)
            messages.Add(new ChatMessageViewModel(new ChatMessage(ChatRole.Assistant, $"## メッセージ {index}\n\n" + new string('長', 1000)), "test", "完了"));
        await Wait("document.querySelectorAll('.message').length === 40 && document.documentElement.scrollHeight - innerHeight - scrollY < 5", "long transcript and following");
        await core.ExecuteScriptAsync("window.scrollTo(0,0);");
        await Task.Delay(150);
        var last = new ChatMessageViewModel(new ChatMessage(ChatRole.Assistant, "追加メッセージ"), "test", "受信中…");
        messages.Add(last);
        await Wait("document.querySelectorAll('.message').length === 41 && scrollY < 5", "read position preserved");
        await core.ExecuteScriptAsync("window.scrollTo(0,document.documentElement.scrollHeight);");
        await Task.Delay(150);
        last.Append("\n\n" + new string('追', 2000));
        last.StatusText = "完了";
        await Wait("document.querySelector('.message:last-child').innerText.includes('追追') && document.documentElement.scrollHeight - innerHeight - scrollY < 5", "follow after update");
        report.Add("PASS: long content, growing history, reading position preservation, and bottom auto-scroll");
    }
}
#endif
