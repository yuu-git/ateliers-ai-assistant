using Ateliers.Ai.Assistant.Core.Chat;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>Markdown 原文・添付と表示用メタデータを持ち、受信追記を通知するメッセージ。</summary>
public sealed class ChatMessageViewModel : ObservableObject
{
    private string _content;
    private string _statusText;

    /// <summary>会話メッセージと送信時のモデル・状態から、表示用の通知対象を作る。</summary>
    /// <param name="message">表示元の原文と任意の添付を持つメッセージ。</param>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="statusText">受信中・完了などの初期状態表示。</param>
    public ChatMessageViewModel(ChatMessage message, string model = "", string statusText = "")
    {
        Role = message.Role;
        _content = message.Content;
        Model = model;
        _statusText = statusText;
        Image = message.Image;
    }

    /// <summary>メッセージ送信者の役割を取得する。</summary>
    public ChatRole Role { get; }
    /// <summary>現在の原文と添付を持つ UI 非依存のメッセージを生成して取得する。</summary>
    public ChatMessage Message => new(Role, Content, Image);
    /// <summary>メモリ内で保持する任意の画像添付を取得する。</summary>
    public ChatImage? Image { get; }
    /// <summary>このターンを送信した時点のモデル名を取得する。</summary>
    public string Model { get; }
    /// <summary>HTML に変換していない Markdown 原文を取得する。</summary>
    public string Content
    {
        get => _content;
        private set => SetProperty(ref _content, value);
    }
    /// <summary>受信中・完了・中止・失敗などの状態表示を取得または設定する。</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetProperty(ref _statusText, value)) OnPropertyChanged(nameof(DetailText));
        }
    }
    /// <summary>モデル名と状態を組み合わせた補足表示を取得する。</summary>
    public string DetailText => string.IsNullOrEmpty(Model) ? StatusText
        : string.IsNullOrEmpty(StatusText) ? Model : $"{Model} · {StatusText}";
    /// <summary>送信者の表示名を取得する。</summary>
    public string Speaker => IsUser ? "あなた" : "Assistant";
    /// <summary>利用者のメッセージかどうかを取得する。</summary>
    public bool IsUser => Role == ChatRole.User;
    /// <summary>受信した回答の断片を Markdown 原文へ追記し、変更を通知する。</summary>
    /// <param name="content">原文へ追記する回答本文の断片。</param>
    public void Append(string content) => Content += content;
}
