using Microsoft.UI.Xaml.Controls;

namespace Ateliers.Ai.Assistant.Desktop.Controls;

/// <summary>ユーザーセッションの運転・モデル許可・起動登録を説明する設定表示。</summary>
public sealed partial class WorkerOptionsView : UserControl
{
    /// <summary>表示を初期化する。</summary>
    public WorkerOptionsView() => InitializeComponent();
#if DEBUG
    /// <summary>実編集コントロールの TwoWay バインドを確認する。</summary>
    internal void EditModelForVerification(string model) => SummaryModelInput.Text = model;
#endif
}
