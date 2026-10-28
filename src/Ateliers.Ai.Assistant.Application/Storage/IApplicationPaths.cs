namespace Ateliers.Ai.Assistant.Application.Storage;

/// <summary>データ保存先を区別するビルド環境。利用者が保存先を切り替える設定ではない。</summary>
public enum AppDataEnvironment
{
    /// <summary>開発用の独立したデータ領域。</summary>
    Debug,
    /// <summary>通常利用の既存データ領域。</summary>
    Release
}

/// <summary>アプリ内部で解決する固定保存先。参照するだけではディレクトリを作成しない。</summary>
public interface IApplicationPaths
{
    /// <summary>この保存先が対象とするビルド環境。</summary>
    AppDataEnvironment DataEnvironment { get; }
    /// <summary>小容量ユーザー設定の Roaming ディレクトリ。</summary>
    string SettingsDirectory { get; }
    /// <summary>端末固有データの Local ディレクトリ。</summary>
    string LocalDirectory { get; }
    /// <summary>会話 DB を格納するディレクトリ。</summary>
    string DatabaseDirectory { get; }
    /// <summary>会話 DB のファイルパス。</summary>
    string DatabaseFile { get; }
    /// <summary>ユーザー設定のファイルパス。</summary>
    string SettingsFile { get; }
    /// <summary>ローテーション付きログのディレクトリ。</summary>
    string LogsDirectory { get; }
    /// <summary>必要になるまで作成しないキャッシュのディレクトリ。</summary>
    string CacheDirectory { get; }
    /// <summary>必要になるまで作成しない一時データのディレクトリ。</summary>
    string TempDirectory { get; }
}

/// <summary>設定画面から開ける内部固定フォルダーの種類。</summary>
public enum DataFolderKind
{
    /// <summary>Roaming の設定フォルダー。</summary>
    Settings,
    /// <summary>Local のアプリデータフォルダー。</summary>
    Local,
    /// <summary>会話 DB の格納フォルダー。</summary>
    Database,
    /// <summary>ログフォルダー。</summary>
    Logs
}

/// <summary>保存・読込・設定処理の失敗。本文や画像を含まない説明だけを公開する。</summary>
public sealed class StorageException : Exception
{
    /// <summary>利用者向けの説明と、診断用の原因を保持する。</summary>
    public StorageException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>本文・画像・資格情報を出力しない診断ログの境界。</summary>
public interface IAppDiagnostics
{
    /// <summary>固定の操作識別子と例外の型・コードを記録する。秘密情報や会話を渡さない。</summary>
    void Error(string operation, Exception exception);
}
