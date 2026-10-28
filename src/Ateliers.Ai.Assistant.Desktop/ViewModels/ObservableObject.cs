using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>変更通知付きプロパティを実装する ViewModel の共通基底。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>プロパティまたは関連する派生表示の変更を通知する。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>値が変わった場合だけフィールドを更新し、プロパティ変更を通知する。</summary>
    /// <param name="field">現在値を保持するフィールドへの参照。</param>
    /// <param name="value">更新する新しいプロパティ値。</param>
    /// <param name="name">変更するプロパティ名。省略時は呼び出し元メンバー名。</param>
    /// <typeparam name="T">変更通知する値の型。</typeparam>
    /// <returns>値が変わり通知を行った場合は true。</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>指定プロパティの変更を購読側へ通知する。</summary>
    /// <param name="name">変更するプロパティ名。省略時は呼び出し元メンバー名。</param>
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
