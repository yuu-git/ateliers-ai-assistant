namespace Ateliers.Ai.Assistant.Infrastructure.Storage.Temp;

/// <summary>一時領域だけを操作する境界。テストで I/O 障害を注入できる。</summary>
public interface ITempFileSystem
{
    /// <summary>祖先から検査してディレクトリを固定する。使用中は削除・再解析ポイントへの差し替えを許可しない。</summary>
    IDisposable GuardDirectory(string path, bool create);
    /// <summary>管理領域が既にあるか。存在確認自体では作成しない。</summary>
    bool DirectoryExists(string path);
    /// <summary>直下だけを列挙する。再帰走査はしない。</summary>
    string[] Files(string path);
    /// <summary>直下のディレクトリだけを列挙する。</summary>
    string[] Directories(string path);
    /// <summary>再解析ポイント・ハードリンクを拒否して開く。親の固定は呼び出し元が保持する。</summary>
    FileStream Open(string path, FileMode mode, FileAccess access, FileShare share);
    /// <summary>検証したファイルハンドルで個別に削除する。</summary>
    void Delete(string path);
    /// <summary>空ディレクトリだけを削除する。</summary>
    void DeleteEmptyDirectory(string path);
}
