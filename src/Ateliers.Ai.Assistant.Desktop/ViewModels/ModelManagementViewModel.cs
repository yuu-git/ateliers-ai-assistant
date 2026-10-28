using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Models;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>ロード中モデルのサイズ・期限などを表示用文字列へ変換した項目。</summary>
public sealed class LoadedModelViewModel
{
    /// <summary>バイト数とロード期限を、画面表示用の単位とローカル時刻へ変換する。</summary>
    /// <param name="model">サーバーから取得したロード中モデルの情報。</param>
    public LoadedModelViewModel(LoadedModel model)
    {
        Name = model.Name;
        Size = FormatBytes(model.Size);
        Vram = FormatBytes(model.SizeVram);
        Quantization = model.Quantization;
        ExpiresAt = model.ExpiresAt is null ? "不明" : model.ExpiresAt.Value.Year >= 9000
            ? "無期限" : model.ExpiresAt.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss zzz", CultureInfo.CurrentCulture);
    }
    /// <summary>モデル名を取得する。</summary>
    public string Name { get; }
    /// <summary>モデルサイズを MiB または GiB の表示で取得する。</summary>
    public string Size { get; }
    /// <summary>VRAM 使用量を MiB または GiB の表示で取得する。</summary>
    public string Vram { get; }
    /// <summary>量子化形式の表示を取得する。</summary>
    public string Quantization { get; }
    /// <summary>ローカル時刻、無期限または不明を表すロード期限表示を取得する。</summary>
    public string ExpiresAt { get; }
    /// <summary>バイト数を MiB または GiB の表示へ変換する。</summary>
    /// <param name="size">表示単位へ変換するバイト数。</param>
    /// <returns>表示用に整形した文字列。</returns>
    private static string FormatBytes(long size) => size >= 1024L * 1024 * 1024
        ? $"{size / (1024d * 1024 * 1024):F2} GiB" : $"{size / (1024d * 1024):F1} MiB";
}

/// <summary>サーバー全体のロード一覧と、明示的な手動停止を扱う ViewModel。</summary>
/// <remarks>チャットと同じライフサイクルサービスを共有する。HTTP を直接扱わず、状態変更は UI スレッドへ移送する。</remarks>
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public sealed partial class ModelManagementViewModel : ObservableObject, IDisposable
{
    private readonly IModelLifecycleService _lifecycle;
    private readonly Action<Action> _dispatch;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<LoadedModelViewModel> _models = new();
    private LoadedModelViewModel? _selectedModel;
    private string _errorMessage = "";
    private bool _disposed;

    /// <summary>共有モデル管理サービスと通知の移送処理を受け取り、一覧操作を構成する。</summary>
    /// <param name="lifecycle">チャットと共有する、必須のモデル管理サービス。</param>
    /// <param name="dispatch">サービス通知を UI スレッドへ移送する処理。省略時はその場で実行する。</param>
    public ModelManagementViewModel(IModelLifecycleService lifecycle, Action<Action>? dispatch = null)
    {
        _lifecycle = lifecycle;
        _dispatch = dispatch ?? (action => action());
        Models = new ReadOnlyObservableCollection<LoadedModelViewModel>(_models);
        RefreshCommand = new AsyncCommand(() => ExecuteAsync(() => _lifecycle.RefreshAsync(_lifetime.Token)), () => CanOperate);
        UnloadCommand = new AsyncCommand(() => ExecuteAsync(() => _lifecycle.UnloadAsync(SelectedModel!.Name, _lifetime.Token)),
            () => CanOperate && _lifecycle.IsStateKnown && SelectedModel is not null);
        _lifecycle.Changed += LifecycleChanged;
        Synchronize();
    }

    /// <summary>表示用に変換したサーバー全体のロード中モデル一覧を取得する。</summary>
    public ReadOnlyObservableCollection<LoadedModelViewModel> Models { get; }
    /// <summary>ロード状態を手動更新するコマンドを取得する。</summary>
    public AsyncCommand RefreshCommand { get; }
    /// <summary>選択項目を、アプリの管理対象かどうかに関係なく停止するコマンドを取得する。</summary>
    public AsyncCommand UnloadCommand { get; }
    /// <summary>手動停止の対象として選択された一覧項目を取得または設定する。</summary>
    public LoadedModelViewModel? SelectedModel
    {
        get => _selectedModel;
        set { if (SetProperty(ref _selectedModel, value)) UnloadCommand.NotifyCanExecuteChanged(); }
    }
    /// <summary>チャット・他のモデル操作・終了処理と競合せず操作可能かどうかを取得する。</summary>
    public bool CanOperate => !_disposed && !_lifecycle.IsBusy && !_lifecycle.IsClosing;
    /// <summary>共有モデルサービスが処理中かどうかを取得する。</summary>
    public bool IsBusy => _lifecycle.IsBusy;
    /// <summary>ロード状態が確認済みで、ロード中モデルが存在しないかどうかを取得する。</summary>
    public bool IsEmpty => _lifecycle.IsStateKnown && Models.Count == 0;
    /// <summary>未確認・空状態・件数を表す一覧の状態表示を取得する。</summary>
    public string StateText => !_lifecycle.IsStateKnown ? "ロード状態は未確認です。一覧を更新してください。"
        : IsEmpty ? "ロード中のモデルはありません。" : $"ロード中: {Models.Count} 件";
    /// <summary>一覧取得・手動停止で発生したエラー表示を取得する。</summary>
    public string ErrorMessage { get => _errorMessage; private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); } }
    /// <summary>表示すべきモデル管理エラーがあるかどうかを取得する。</summary>
    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>モデル操作を実行し、中止とエラーを表示用の状態へ反映する。</summary>
    /// <param name="operation">共通の状態制御の中で実行するモデル操作。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    private async Task ExecuteAsync(Func<Task> operation)
    {
        ErrorMessage = "";
        try { await operation(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception) { if (!_disposed) ErrorMessage = exception.Message; }
    }
    /// <summary>モデル状態の変更を UI 側の一覧同期処理へ移送する。</summary>
    /// <param name="sender">通知元のオブジェクト。</param>
    /// <param name="e">イベントまたは起動の引数。</param>
    private void LifecycleChanged(object? sender, EventArgs e) => _dispatch(Synchronize);
    /// <summary>最後のロード状態から表示項目を再構築し、選択と操作可能状態を更新する。</summary>
    private void Synchronize()
    {
        if (_disposed) return;
        var selected = SelectedModel?.Name;
        _models.Clear();
        foreach (var model in _lifecycle.LoadedModels) _models.Add(new LoadedModelViewModel(model));
        SelectedModel = _models.FirstOrDefault(model => model.Name == selected);
        OnPropertyChanged(nameof(CanOperate));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(StateText));
        RefreshCommand.NotifyCanExecuteChanged();
        UnloadCommand.NotifyCanExecuteChanged();
    }
    /// <summary>状態変更の購読を解除し、この画面からの進行中操作を中止する。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifecycle.Changed -= LifecycleChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
