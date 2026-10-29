namespace Ateliers.Ai.Assistant.Application.Models;

/// <summary>ロード中モデルの API 非依存スナップショット。サイズはバイト単位で保持する。</summary>
/// <param name="Name">サーバーが返すモデル名。</param>
/// <param name="Digest">モデル内容を識別するダイジェスト。</param>
/// <param name="Size">モデルサイズ。単位はバイト。</param>
/// <param name="SizeVram">VRAM 使用量。単位はバイト。</param>
/// <param name="Quantization">量子化形式。取得できない場合は不明を表す文字列。</param>
/// <param name="ExpiresAt">ロード期限。サーバーから提供されない場合は null。</param>
public sealed record LoadedModel(string Name, string Digest, long Size, long SizeVram,
    string Quantization, DateTimeOffset? ExpiresAt);

/// <summary>Ollama 等のモデル操作ポート。プロセスの起動・停止は扱わない。</summary>
public interface IModelControlService
{
    /// <summary>サーバー全体のロード中モデルを取得する。</summary>
    /// <remarks>返された一覧は、本アプリが所有・管理するモデルの一覧ではない。</remarks>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>サーバー全体のロード中モデル情報の一覧。</returns>
    Task<IReadOnlyList<LoadedModel>> GetLoadedModelsAsync(CancellationToken cancellationToken = default);
    /// <summary>サーバープロセスを操作せず、指定モデルをロード保持する。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    Task LoadModelAsync(string model, CancellationToken cancellationToken = default);
    /// <summary>サーバープロセスを操作せず、指定モデルをアンロードする。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    Task UnloadModelAsync(string model, CancellationToken cancellationToken = default);
}

/// <summary>Desktop 内の生成・モデル操作を直列化する管理。並行利用の参照数・機能別所有権は扱わない。</summary>
public interface IModelLifecycleService
{
    /// <summary>ロード状態・実行中状態・終了状態の変更を通知する。</summary>
    /// <remarks>購読側で UI スレッドへの移送を行う。</remarks>
    event EventHandler? Changed;
    /// <summary>チャット・Web 収集・モデル操作のいずれかを実行中かどうかを取得する。</summary>
    bool IsBusy { get; }
    /// <summary>終了処理が開始され、新しい操作を受け付けないかどうかを取得する。</summary>
    bool IsClosing { get; }
    /// <summary>最後のロード状態取得が成功したかどうかを取得する。</summary>
    bool IsStateKnown { get; }
    /// <summary>サーバー全体で最後に取得したロード中モデルを取得する。</summary>
    IReadOnlyList<LoadedModel> LoadedModels { get; }
    /// <summary>本アプリがロード・使用を要求して追跡しているモデル名を取得する。</summary>
    /// <remarks>モデルの排他的な所有権を示さず、他アプリとの共有利用も完全には判定できない。</remarks>
    IReadOnlyList<string> ManagedModels { get; }
    /// <summary>ロード中モデルの実状態を再取得する。</summary>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    Task RefreshAsync(CancellationToken cancellationToken = default);
    /// <summary>指定モデルを管理対象としてロードし、実状態を確認する。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    Task LoadAsync(string model, CancellationToken cancellationToken = default);
    /// <summary>利用者の明示操作として、管理対象かどうかに関係なく指定モデルを停止する。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    Task UnloadAsync(string model, CancellationToken cancellationToken = default);
    /// <summary>選択変更前のモデルを、管理対象の場合だけ停止する。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    Task ReleasePreviousAsync(string model, CancellationToken cancellationToken = default);
    /// <summary>操作を終了状態にし、期限内に管理対象モデルの解放を試みる。</summary>
    /// <remarks>停止成功は保証しない。通信失敗や期限超過は記録して終了を継続し、サーバー全体の停止は行わない。</remarks>
    /// <param name="timeout">終了時の待機・解放処理全体に許容する時間。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    Task ShutdownAsync(TimeSpan timeout);
}
