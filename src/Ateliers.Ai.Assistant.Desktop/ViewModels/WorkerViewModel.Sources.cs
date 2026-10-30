using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Application.Worker;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>情報源種別に応じた入力だけを担当する。外部 API は Worker が呼ぶ。</summary>
public sealed partial class WorkerViewModel
{
    private int _kindIndex;
    private bool _includePrereleases;
    /// <summary>0は既存 RSS / Atom、1は GitHub Releases。</summary>
    public int KindIndex
    {
        get => _kindIndex;
        set
        {
            if (value is < 0 or > 1 || !SetProperty(ref _kindIndex, value)) return;
            if (value == 0) IncludePrereleases = false;
            OnPropertyChanged(nameof(IsGitHubSource)); OnPropertyChanged(nameof(SourceInputHeader)); OnPropertyChanged(nameof(SourceInputHelp)); EditStateChanged();
        }
    }
    /// <summary>GitHub 固有項目を有効化する。</summary>
    public bool IsGitHubSource => KindIndex == (int)ContentSourceKind.GitHubReleases;
    /// <summary>リポジトリ単位のプレリリース取得。既定は無効。</summary>
    public bool IncludePrereleases { get => _includePrereleases; set { if (SetProperty(ref _includePrereleases, value)) EditStateChanged(); } }
    /// <summary>URL と owner/repository を区別する入力見出し。</summary>
    public string SourceInputHeader => IsGitHubSource ? "GitHub リポジトリ（owner/repository）" : "公開 RSS / Atom URL";
    /// <summary>取得範囲・認証と履歴の制約を入力に添える。</summary>
    public string SourceInputHelp => IsGitHubSource ? "例: microsoft/vscode。公開リポジトリ・認証なし。先頭100件から対象の最新10件を取得します。取得済みリリースの編集は反映しません。リポジトリ変更時は過去の履歴を統合しません。" : "例: https://code.visualstudio.com/feed.xml。公開 RSS / Atom の最新10件を取得します。";
    /// <summary>保存 URL と画面入力の表記を変換する。</summary>
    private static string SourceInput(SourceConfiguration source) => source.Kind == ContentSourceKind.GitHubReleases ? ContentSourceIdentity.Repository(source.Url) : source.Url;
    /// <summary>保存済み情報源を明示選択・再読込時だけ編集欄へ適用する。</summary>
    private void ApplySource(SourceConfiguration source)
    { KindIndex = (int)source.Kind; Name = source.Name; Url = SourceInput(source); IntervalMinutes = source.IntervalMinutes; Automatic = source.Automatic; IncludePrereleases = source.IncludePrereleases; }
}
