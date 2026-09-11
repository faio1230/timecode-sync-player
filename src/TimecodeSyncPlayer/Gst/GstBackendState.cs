using System;
using System.Threading;
using Serilog;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Gst;

/// <summary>
/// GStreamer バックエンド単一インスタンスの所有者。
/// プレイヤーハンドル、再生ポーズ状態のミラー、フレーム更新コールバックの
/// 配線を here に集約し、IMpvApi / IMpvRenderApi / ISpoutOutput の各アダプタから共有する。
/// </summary>
internal sealed class GstBackendState : IDisposable
{
    /// <summary>検証実行時は環境変数で送信者名を分離できる（既定は運用名）。</summary>
    public const string SenderNameEnvVar = "TIMECODE_SYNC_PLAYER_SPOUT_NAME";
    public const string DefaultSenderName = "TimecodeSyncPlayer";

    private readonly IGstNativeApi _native;
    private readonly object _gate = new();
    private IntPtr _player;
    private IntPtr _externalDevice;
    private bool _playerDisposed;
    private GstNative.TcsFrameNotifyDelegate? _thunk;      // ネイティブへ渡すdelegateの寿命保持
    private MpvRenderNative.MpvRenderUpdateFn? _renderCallback;
    private IntPtr _renderCallbackCtx;

    public GstBackendState(IGstNativeApi native)
    {
        _native = native;
    }

    public IGstNativeApi Native => _native;

    public string SenderName { get; private set; } = ResolveSenderName();

    public IntPtr Player => Volatile.Read(ref _player);

    public string LastError { get; private set; } = string.Empty;

    /// <summary>pause プロパティの最新値ミラー（loadfile の初期状態に使う）。</summary>
    public volatile bool IsPaused = true;

    /// <summary>
    /// outputBackend=Gpu のとき、合成層の ID3D11Device を shim に渡す。
    /// ステージ 6b 以降 shim はこれを Adopt せず、アダプター LUID の読み取りにのみ使い、
    /// 同じ LUID 上に自前のデバイスと immediate context を作る（合成側 context は触らない）。
    /// プレイヤー生成前にだけ有効（生成後の変更は無視する）。
    /// </summary>
    public void SetExternalDevice(IntPtr device)
    {
        lock (_gate)
        {
            if (_player != IntPtr.Zero || _playerDisposed) return;
            _externalDevice = device;
        }
    }

    public bool EnsurePlayer()
    {
        lock (_gate)
        {
            if (_playerDisposed) return false;
            if (_player != IntPtr.Zero) return true;

            _player = _native.PlayerCreate(SenderName, _externalDevice, out string error);
            LastError = error;
            if (_player == IntPtr.Zero)
                Log.Error("GstBackendState: プレイヤー生成失敗 {Error}", error);
            else
            {
                Log.Information("GstBackendState: プレイヤー生成 sender='{Sender}'", SenderName);
                if (_renderCallback is not null)
                {
                    _thunk ??= OnNativeFrame;
                    _native.SetFrameCallback(_player, _thunk);
                }
            }
            return _player != IntPtr.Zero;
        }
    }

    public void DisposePlayer()
    {
        lock (_gate)
        {
            if (_playerDisposed || _player == IntPtr.Zero) return;
            DetachRenderCallbackLocked();
            _native.PlayerDestroy(_player);
            _player = IntPtr.Zero;
            _playerDisposed = true;
            Log.Information("GstBackendState: プレイヤー破棄");
        }
    }

    public void AttachRenderCallback(MpvRenderNative.MpvRenderUpdateFn callback, IntPtr callbackCtx)
    {
        lock (_gate)
        {
            _renderCallback = callback;
            _renderCallbackCtx = callbackCtx;
            _thunk ??= OnNativeFrame;
            if (_player != IntPtr.Zero)
                _native.SetFrameCallback(_player, _thunk);
        }
    }

    public void DetachRenderCallback()
    {
        lock (_gate)
        {
            DetachRenderCallbackLocked();
        }
    }

    private void DetachRenderCallbackLocked()
    {
        if (_player != IntPtr.Zero && !(_thunk is null))
            _native.SetFrameCallback(_player, null);
        _thunk = null;
        _renderCallback = null;
        _renderCallbackCtx = IntPtr.Zero;
    }

    /// <summary>
    /// mpv 互換 render の実体。前回リースを返却してから現世代の最新フレームを
    /// リースし、bgr0 バッファへ CPU コピーする。
    /// 戻り値は mpv と同様 0 成功 / 負値失敗。
    /// </summary>
    public int RenderInto(IntPtr dst, int dstStride, int width, int height)
    {
        IntPtr player = Player;
        if (player == IntPtr.Zero) return -1;
        try
        {
            _native.Release(player);
            if (_native.Acquire(player, _native.GetGeneration(player), out GstNative.TcsFrameInfo info) != 1)
                return -3; // none / ended
            if (info.Width != width || info.Height != height)
                return -5; // app renders at decoded size
            return _native.LeasedCpuCopy(player, dst, dstStride);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstBackendState.RenderInto 失敗");
            return -1;
        }
    }

    public void Dispose() => DisposePlayer();

    internal static string ResolveSenderName()
    {
        string? fromEnv = Environment.GetEnvironmentVariable(SenderNameEnvVar);
        return string.IsNullOrWhiteSpace(fromEnv) ? DefaultSenderName : fromEnv.Trim();
    }

    private void OnNativeFrame(IntPtr userData, ulong generation, ulong seq)
    {
        MpvRenderNative.MpvRenderUpdateFn? cb;
        IntPtr ctx;
        lock (_gate)
        {
            cb = _renderCallback;
            ctx = _renderCallbackCtx;
        }
        cb?.Invoke(ctx);
    }
}
