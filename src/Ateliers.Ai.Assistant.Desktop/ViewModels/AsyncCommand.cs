using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>非同期処理の重複実行を防ぎ、実行可能状態を通知するパラメーターなしの UI コマンド。</summary>
public sealed partial class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool> _canExecute;
    private bool _isExecuting;

    /// <summary>実行処理と、画面状態に応じた実行可否判定を設定する。</summary>
    /// <param name="execute">コマンドの実行処理。非同期の場合は処理内で UI 向けの失敗を扱う。</param>
    /// <param name="canExecute">画面状態に応じて実行可能か判定する処理。</param>
    public AsyncCommand(Func<Task> execute, Func<bool> canExecute)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>実行可能状態の再評価が必要になったときに発生する。</summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>実行中でなく、指定された条件も満たす場合だけ実行を許可する。</summary>
    /// <param name="parameter">バインディングまたはコマンドのパラメーター。本実装では使用しない。</param>
    /// <returns>コマンドを実行できる場合は true。</returns>
    public bool CanExecute(object? parameter) => !_isExecuting && _canExecute();

    /// <summary>ICommand の呼び出しを、完了待ちできる実行処理へ接続する。</summary>
    /// <param name="parameter">バインディングまたはコマンドのパラメーター。本実装では使用しない。</param>
    public async void Execute(object? parameter) => await ExecuteAsync();

    /// <summary>実行可能な場合だけ一度実行し、成功・失敗にかかわらず実行中状態を解除する。</summary>
    /// <remarks>例外は抑止しない。UI で扱う失敗は注入された処理側で通知する。</remarks>
    /// <returns>操作の完了を表すタスク。</returns>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
            return;

        _isExecuting = true;
        NotifyCanExecuteChanged();
        try
        {
            await _execute();
        }
        finally
        {
            _isExecuting = false;
            NotifyCanExecuteChanged();
        }
    }

    /// <summary>バインド先に実行可否の再評価を要求する。</summary>
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
