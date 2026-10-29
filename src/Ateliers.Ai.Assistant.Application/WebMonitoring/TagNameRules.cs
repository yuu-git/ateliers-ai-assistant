using System.Text;

namespace Ateliers.Ai.Assistant.Application.WebMonitoring;

/// <summary>手動タグの共通規則。全角半角と内部空白を保持し、英字の大小は同一視する。</summary>
public static class TagNameRules
{
    /// <summary>表示名と一意制約用キーを返す。前後空白除去・Unicode NFC・最大64 UTF-16文字。</summary>
    public static (string Name, string Key) Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new WebMonitoringException("タグ名を入力してください。");
        var trimmed = name.Trim().Normalize(NormalizationForm.FormC);
        if (trimmed.Length > 64 || trimmed.Any(char.IsControl)) throw new WebMonitoringException("タグ名は制御文字を含めず、64文字以内で入力してください。");
        return (trimmed, trimmed.ToUpperInvariant().Normalize(NormalizationForm.FormC));
    }
}
