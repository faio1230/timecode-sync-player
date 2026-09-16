using System;
using System.Threading;
using Serilog;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Gst;

/// <summary>
/// GStreamer バックエンド単一インスタンスの所有者。
/// プレイヤーハンドル、再生ポーズ状態のミラー、フレーム更新コールバックの
/// 配線を here に集約し、IPlaybackApi / IRenderUpdateSource / ISpoutOutput の各実装から共有する。
/// </summary>
internal sealed class GstBackendState : IDisposable
{
    /// <summary>検証実行時は環境変数で送信者名を分離できる（既定は運用名）。</summary>
    public const string SenderNameEnvVar = "TIMECODE_SYNC_PLAYER_SPOUT_NAME";

    private readonly IGstNativeApi _native;
    private readonly AppSettingsManager? _settingsManager;
    private readonly object _gate = new();
    private IntPtr _player;
    private IntPtr _externalDevice;
    private bool _playerDisposed;
    private GstNative.TcsFrameNotifyDelegate? _thunk;      // ネイティブへ渡すdelegateの寿命保持
    private RenderUpdateFn? _renderCallback;
    private IntPtr _renderCallbackCtx;

    public GstBackendState(IGstNativeApi native, AppSettingsManager? settingsManager = null)
    {
        _native = native;
        _settingsManager = settingsManager;
        Seeking = new GstSeekingTracker(this);
    }

    public IGstNativeApi Native => _native;

    /// <summary>シーク中判定（到着数ベース）。文字列経路と型付き経路で共有する。</summary>
    public GstSeekingTracker Seeking { get; }

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
                ApplyDecodeMode(_player);
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

    /// <summary>
    /// settings.json の decodeMode を shim に伝える。既定 hardware では shim の既定と同じなので触らない。
    /// software のときだけ、player 生成直後・最初の load 前に 1 回呼ぶ。設定の変更には再起動が必要。
    /// </summary>
    private void ApplyDecodeMode(IntPtr player)
    {
        DecodeMode mode = DecodeModePolicy.Resolve(
            _settingsManager?.Current.DecodeMode,
            value => Log.Warning("decodeMode の未知の値 '{Value}' は hardware として扱います", value));
        if (mode != DecodeMode.Software)
            return;

        int rc = _native.SetDecodeMode(player, GstNative.DecodeModeSoftware);
        if (rc == 0)
            Log.Information("GstBackendState: decodeMode=software を shim に設定しました");
        else
            Log.Error("GstBackendState: decodeMode=software を shim に設定できませんでした rc={Rc}", rc);
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

    /// <summary>
    /// 段階 5.2: 共有リングを開き直せなかった場合のみ使う。UI スレッドで player を破棄し、
    /// 新しい合成デバイス（ポインタ）で同じ名前の player を再生成する。呼び出し側が再ロード・シークする。
    /// </summary>
    public bool RecreatePlayer(IntPtr externalDevice)
    {
        lock (_gate)
        {
            if (_player != IntPtr.Zero)
            {
                DetachRenderCallbackLocked();
                _native.PlayerDestroy(_player);
                _player = IntPtr.Zero;
            }
            _playerDisposed = false;
            _externalDevice = externalDevice;
        }
        return EnsurePlayer();
    }

    public void AttachRenderCallback(RenderUpdateFn callback, IntPtr callbackCtx)
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

    public void Dispose() => DisposePlayer();

    internal static string ResolveSenderName()
    {
        string? fromEnv = Environment.GetEnvironmentVariable(SenderNameEnvVar);
        return string.IsNullOrWhiteSpace(fromEnv) ? SpoutDefaults.DefaultSenderName : fromEnv.Trim();
    }

    private void OnNativeFrame(IntPtr userData, ulong generation, ulong seq)
    {
        RenderUpdateFn? cb;
        IntPtr ctx;
        lock (_gate)
        {
            cb = _renderCallback;
            ctx = _renderCallbackCtx;
        }
        cb?.Invoke(ctx);
    }
}
