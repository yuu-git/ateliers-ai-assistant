namespace Ateliers.Ai.Assistant.Application.Settings;

/// <summary>Roaming に保存する小容量のユーザー設定。</summary>
/// <param name="LastSelectedModel">最後に選択したモデル名。自動ロードは指示しない。</param>
public sealed record UserSettings(string? LastSelectedModel = null);

/// <summary>固定された Roaming 保存先でユーザー設定を読み書きする契約。</summary>
public interface IUserSettingsStore
{
    /// <summary>未作成なら既定値を返す。不正な既存ファイルはエラーとし、自動上書きしない。</summary>
    Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default);
    /// <summary>設定を一時ファイルから置き換え、途中書き込みによる破損を防ぐ。</summary>
    Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default);
}
