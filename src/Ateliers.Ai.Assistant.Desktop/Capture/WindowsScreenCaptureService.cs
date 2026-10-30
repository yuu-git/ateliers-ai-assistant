using System;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.ScreenAnalysis;
using Ateliers.Ai.Assistant.Core.Chat;
namespace Ateliers.Ai.Assistant.Desktop.Capture;
/// <summary>Phase 6 の境界を維持し、共通の Windows キャプチャへ委譲する。</summary>
public sealed class WindowsScreenCaptureService(Func<nint> ownerHandle) : IScreenCaptureService
{
    private readonly Ateliers.Ai.Assistant.Platform.Windows.Capture.WindowsScreenCaptureService _capture = new(ownerHandle);
    /// <inheritdoc/>
    public string? TargetName => _capture.TargetName;
    /// <inheritdoc/>
    public event EventHandler? TargetClosed { add => _capture.TargetClosed += value; remove => _capture.TargetClosed -= value; }
    /// <inheritdoc/>
    public Task<bool> PickAsync(CancellationToken token) => _capture.PickAsync(token);
    /// <inheritdoc/>
    public Task<ChatImage> CaptureAsync(CancellationToken token) => _capture.CaptureAsync(token);
    /// <inheritdoc/>
    public void Dispose() => _capture.Dispose();
#if DEBUG
    /// <summary>既存の隔離検証から共通実装の実フレームを取得する。</summary>
    internal void SelectForVerification(nint window, bool monitor) => _capture.SelectForVerification(window, monitor);
#endif
}