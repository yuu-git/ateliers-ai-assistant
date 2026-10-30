using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.ScreenAnalysis;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Desktop.Images;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Ateliers.Ai.Assistant.Desktop.Capture;

/// <summary>標準 Picker と WGC の単一フレーム取得。選択だけでは取得を開始しない。</summary>
public sealed partial class WindowsScreenCaptureService(Func<nint> ownerHandle) : IScreenCaptureService
{
    private GraphicsCaptureItem? _item;
    private bool _disposed;
    /// <inheritdoc/>
    public string? TargetName => _item?.DisplayName;
    /// <inheritdoc/>
    public event EventHandler? TargetClosed;

    /// <inheritdoc/>
    public async Task<bool> PickAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!GraphicsCaptureSession.IsSupported()) throw new InvalidOperationException("この環境では画面キャプチャを利用できません。");
        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHandle());
        var item = await picker.PickSingleItemAsync().AsTask(token);
        if (item is null) return false;
        token.ThrowIfCancellationRequested();
        SetTarget(item);
        return true;
    }

    /// <summary>対象の購読を交換する。対象の種類は OS が公開しないため推測しない。</summary>
    private void SetTarget(GraphicsCaptureItem item)
    {
        if (_item is not null) _item.Closed -= ItemClosed;
        _item = item; _item.Closed += ItemClosed;
    }
    private void ItemClosed(GraphicsCaptureItem sender, object args)
    {
        if (!ReferenceEquals(_item, sender)) return;
        sender.Closed -= ItemClosed; _item = null; TargetClosed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public async Task<ChatImage> CaptureAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var item = _item ?? throw new InvalidOperationException("キャプチャ対象を選択してください。");
        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0 || (ulong)size.Width * (ulong)size.Height > ClipboardImageService.MaximumSourcePixels)
            throw new InvalidOperationException("キャプチャ対象のサイズが無効、または4000万画素を超えています。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var device = CreateDevice();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
        using var session = pool.CreateCaptureSession(item);
        var ready = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Arrived(Direct3D11CaptureFramePool sender, object args)
        {
            try { var frame = sender.TryGetNextFrame(); if (frame is not null && !ready.TrySetResult(frame)) frame.Dispose(); }
            catch (Exception exception) { ready.TrySetException(exception); }
        }
        void Closed(GraphicsCaptureItem sender, object args) => ready.TrySetException(new InvalidOperationException("対象が閉じられました。選択し直してください。"));
        pool.FrameArrived += Arrived; item.Closed += Closed;
        using var cancel = deadline.Token.Register(() => ready.TrySetCanceled(deadline.Token));
        try
        {
            session.StartCapture();
            var frame = await ready.Task;
            // GPU コピーを FrameArrived の内側で待たない。フレームはコピー完了まで保持する。
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask(deadline.Token);
            var content = frame.ContentSize;
            if (content.Width <= 0 || content.Height <= 0 || content.Width > size.Width || content.Height > size.Height)
                throw new InvalidOperationException("取得中に対象サイズが変わりました。もう一度キャプチャしてください。");
            using var encoded = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded).AsTask(deadline.Token);
            encoder.SetSoftwareBitmap(bitmap);
            // ウィンドウ縮小時のフレーム余白を送らない。
            encoder.BitmapTransform.Bounds = new BitmapBounds { Width = (uint)content.Width, Height = (uint)content.Height };
            await encoder.FlushAsync().AsTask(deadline.Token);
            encoded.Seek(0);
            return await ClipboardImageService.NormalizeAsync(encoded, deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("キャプチャがタイムアウトしました。最小化・画面ロック・保護コンテンツ等を確認してください。"); }
        finally
        {
            pool.FrameArrived -= Arrived; item.Closed -= Closed;
            // キャンセルとフレーム到着が競合しても、受信済みフレームを残さない。
            if (ready.Task.IsCompletedSuccessfully) ready.Task.Result.Dispose();
        }
    }

    /// <summary>BGRA 対応 D3D11 デバイスを WinRT へ渡す。生 COM 参照は必ず解放する。</summary>
    private static IDirect3DDevice CreateDevice()
    {
        nint device = 0, context = 0, dxgi = 0, inspectable = 0;
        try
        {
            var result = D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out device, out _, out context);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            var iid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally { if (inspectable != 0) Marshal.Release(inspectable); if (dxgi != 0) Marshal.Release(dxgi); if (context != 0) Marshal.Release(context); if (device != 0) Marshal.Release(device); }
    }
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, int driver, nint software, uint flags, nint levels, uint count, uint version, out nint device, out int level, out nint context);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint result);
    /// <inheritdoc/>
    public void Dispose() { _disposed = true; if (_item is not null) _item.Closed -= ItemClosed; _item = null; }
}
