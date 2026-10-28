using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Ateliers.Ai.Assistant.Desktop.Converters;

/// <summary>メッセージが利用者のものかどうかを、左右の表示位置へ変換する。</summary>
public sealed class MessageAlignmentConverter : IValueConverter
{
    /// <summary>true を右寄せ、それ以外を左寄せへ変換する。</summary>
    /// <param name="value">利用者メッセージなら true、それ以外なら左寄せとなる値。</param>
    /// <param name="targetType">バインディングで要求された変換先の型。本変換では使用しない。</param>
    /// <param name="parameter">バインディングまたはコマンドのパラメーター。本実装では使用しない。</param>
    /// <param name="language">バインディングの言語情報。本変換では使用しない。</param>
    /// <returns>利用者なら右寄せ、それ以外なら左寄せの配置値。</returns>
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    /// <summary>逆変換を提供しない。</summary>
    /// <exception cref="NotSupportedException">呼び出した場合は常に発生する。</exception>
    /// <param name="value">逆変換の入力。逆変換は未対応。</param>
    /// <param name="targetType">バインディングで要求された変換先の型。本変換では使用しない。</param>
    /// <param name="parameter">バインディングまたはコマンドのパラメーター。本実装では使用しない。</param>
    /// <param name="language">バインディングの言語情報。本変換では使用しない。</param>
    /// <returns>常に例外を送出するため、値は返さない。</returns>
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
