using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Logging;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>Worker の通信に依存せず、保存済みログの絞込・選択・コピーを扱う。</summary>
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public sealed partial class LogsViewModel : ObservableObject
{
    private readonly IAppLogReader _reader;
    private readonly Func<string, Task> _copy;
    private readonly Func<Task> _open;
    private readonly List<LogEntryViewModel> _selected = [];
    private int _category, _level, _period = 1;
    private string _keyword = "", _error = "";
    private LogEntryViewModel? _entry;
    /// <summary>読取サービスと、明示コピー・固定フォルダー起動を注入する。</summary>
    public LogsViewModel(IAppLogReader reader, Func<string, Task> copy, Func<Task> open)
    {
        _reader = reader; _copy = copy; _open = open;
        RefreshCommand = new(RefreshAsync, () => true);
        CopySelectedCommand = new(() => CopyAsync(_selected), () => _selected.Count != 0);
        CopyVisibleCommand = new(() => CopyAsync(Entries), () => Entries.Count != 0);
        CopyDetailsCommand = new(() => CopyAsync(SelectedEntry is null ? [] : [SelectedEntry]), () => SelectedEntry is not null);
        OpenFolderCommand = new(async () => { try { await _open(); ErrorMessage = ""; } catch { ErrorMessage = "ログフォルダーを開けませんでした。"; } }, () => true);
    }
    /// <summary>最大500件の表示スナップショット。</summary>
    public ObservableCollection<LogEntryViewModel> Entries { get; } = [];
    /// <summary>0: 運転記録、1: 技術診断。</summary>
    public int CategoryIndex { get => _category; set => SetProperty(ref _category, value); }
    /// <summary>0: 全レベル、1: 警告以上、2: エラー。</summary>
    public int LevelIndex { get => _level; set => SetProperty(ref _level, value); }
    /// <summary>0: 1時間、1: 24時間、2: 7日、3: 保存範囲すべて。</summary>
    public int PeriodIndex { get => _period; set => SetProperty(ref _period, value); }
    /// <summary>メッセージ・発生元・操作名の部分一致。</summary>
    public string Keyword { get => _keyword; set => SetProperty(ref _keyword, value); }
    /// <summary>最後に選択した行の詳細。</summary>
    public LogEntryViewModel? SelectedEntry { get => _entry; private set { if (SetProperty(ref _entry, value)) { OnPropertyChanged(nameof(DetailText)); CopyDetailsCommand.NotifyCanExecuteChanged(); } } }
    /// <summary>プレーンテキストの選択行詳細。</summary>
    public string DetailText => SelectedEntry?.CopyText ?? "ログ行を選択してください。";
    /// <summary>読取・コピー失敗を画面に示す。</summary>
    public string ErrorMessage { get => _error; private set => SetProperty(ref _error, value); }
    /// <summary>利用者の操作で更新する。</summary>
    public AsyncCommand RefreshCommand { get; }
    /// <summary>複数選択を改行区切りでコピーする。</summary>
    public AsyncCommand CopySelectedCommand { get; }
    /// <summary>表示中だけをコピーする。</summary>
    public AsyncCommand CopyVisibleCommand { get; }
    /// <summary>選択行の詳細をコピーする。</summary>
    public AsyncCommand CopyDetailsCommand { get; }
    /// <summary>固定ログフォルダーを開く。</summary>
    public AsyncCommand OpenFolderCommand { get; }
    /// <summary>UI の複数選択を反映する。ログ内容を自動コピーしない。</summary>
    public void Select(IReadOnlyList<LogEntryViewModel> entries)
    {
        _selected.Clear(); _selected.AddRange(entries); SelectedEntry = entries.LastOrDefault(); CopySelectedCommand.NotifyCanExecuteChanged();
    }
    /// <summary>上限付きでログファイルを背景読込する。</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var since = PeriodIndex switch { 0 => DateTimeOffset.UtcNow.AddHours(-1), 1 => DateTimeOffset.UtcNow.AddDays(-1), 2 => DateTimeOffset.UtcNow.AddDays(-7), _ => (DateTimeOffset?)null };
            var query = new LogQuery(CategoryIndex == 0 ? AppLogCategory.Application : AppLogCategory.System,
                LevelIndex switch { 1 => AppLogLevel.Warning, 2 => AppLogLevel.Error, _ => null }, since, Keyword, 500);
            var result = await _reader.ReadAsync(query);
            Select([]); Entries.Clear(); foreach (var entry in result) Entries.Add(new(entry));
            CopyVisibleCommand.NotifyCanExecuteChanged(); ErrorMessage = "";
        }
        catch { ErrorMessage = "ログを読み込めませんでした。保存先・アクセス権を確認してください。"; }
    }
    private async Task CopyAsync(IEnumerable<LogEntryViewModel> rows)
    {
        try { await _copy(string.Join(Environment.NewLine + Environment.NewLine, rows.Select(row => row.CopyText))); ErrorMessage = ""; }
        catch { ErrorMessage = "ログをクリップボードへコピーできませんでした。"; }
    }
}
/// <summary>ローカル時刻と UTC オフセットを併記したログ表示行。</summary>
public sealed record LogEntryViewModel(AppLogEntry Entry)
{
    /// <summary>時刻・重大度・発生元・メッセージ。</summary>
    public string Text => $"{Entry.At.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} [{Entry.Level}] [{Entry.Origin}] {Entry.Message}";
    /// <summary>コピー対象の詳細。秘密情報・本文はログ自体に記録しない。</summary>
    public string CopyText => Text + $"\n操作: {Entry.Operation}\n分類: {Entry.Category}" + (Entry.CorrelationId is { } id ? $"\n相関 ID: {id}" : "");
}
