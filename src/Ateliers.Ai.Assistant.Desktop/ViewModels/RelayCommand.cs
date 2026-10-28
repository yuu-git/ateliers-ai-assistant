using System;
using System.Windows.Input;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>実行処理と実行条件を受け取る、同期のパラメーターなし UI コマンド。</summary>
public sealed partial class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    /// <summary>同期の実行処理と実行可能条件を設定する。</summary>
    /// <param name="execute">コマンドの実行処理。非同期の場合は処理内で UI 向けの失敗を扱う。</param>
    /// <param name="canExecute">画面状態に応じて実行可能か判定する処理。</param>
    public RelayCommand(Action execute, Func<bool> canExecute)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>実行可能状態の再評価が必要になったときに発生する。</summary>
    public event EventHandler? CanExecuteChanged;
    /// <summary>指定された条件でコマンドを実行できるか判定する。</summary>
    /// <param name="parameter">バインディングまたはコマンドのパラメーター。本実装では使用しない。</param>
    /// <returns>コマンドを実行できる場合は true。</returns>
    public bool CanExecute(object? parameter) => _canExecute();
    /// <summary>実行可能な場合だけ同期処理を呼び出す。</summary>
    /// <param name="parameter">バインディングまたはコマンドのパラメーター。本実装では使用しない。</param>
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute();
    }
    /// <summary>バインド先へ実行可能状態の再評価を要求する。</summary>
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
