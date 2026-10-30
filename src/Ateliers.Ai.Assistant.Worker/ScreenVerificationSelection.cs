#if DEBUG
namespace Ateliers.Ai.Assistant.Worker;
/// <summary>隔離した明示検証だけで既知の対象を指定する。製品版にこの API は含めない。</summary>
internal sealed record ScreenVerificationSelection(long Handle, bool Monitor);
#endif
