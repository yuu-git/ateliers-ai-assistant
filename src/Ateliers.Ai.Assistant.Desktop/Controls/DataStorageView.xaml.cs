using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Ateliers.Ai.Assistant.Desktop.Controls;

/// <summary>固定保存先の表示と既存の Explorer コマンドをまとめる。保存先の変更は提供しない。</summary>
public sealed partial class DataStorageView : UserControl
{
    /// <summary>ホストが所有する設定 ViewModel。</summary>
    public SettingsViewModel? ViewModel { get; private set; }
    /// <summary>表示を初期化する。</summary>
    public DataStorageView() => InitializeComponent();
    /// <summary>型付きバインドへ既存の設定を適用する。</summary>
    public void Initialize(SettingsViewModel settings) { ViewModel = settings; Bindings.Update(); }
}
