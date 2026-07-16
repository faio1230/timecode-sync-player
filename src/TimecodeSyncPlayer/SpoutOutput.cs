using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// SpoutDX のライフサイクル管理とフレーム送信。
/// TryInitialize() が false を返した場合、以後の SendFrame は無視される。
/// IsEnabled = false でも初期化は維持されるため ON/OFF 切替は即座に反映される。
/// </summary>
public sealed class SpoutOutput : ISpoutOutput
{
    public const string DefaultSenderName = "TimecodeSyncPlayer";

    private IntPtr _obj = IntPtr.Zero;
    private bool   _initialized;
    private bool   _disposed;
    private long   _sendCount;   // 送信フレームカウント（ログ用）
    private readonly ISpoutNativeApi _native;

    public SpoutOutput() : this(SpoutNativeApi.Instance) { }

    internal SpoutOutput(ISpoutNativeApi native)
    {
        _native = native;
    }
    public bool IsEnabled    { get; set; } = false;
    public bool IsAvailable  => _initialized;

    /// <summary>
    /// SpoutDX.dll をロードして初期化する。
    /// DLL がない場合や初期化失敗の場合は false を返す（クラッシュしない）。
    /// </summary>
    public bool TryInitialize()
    {
        _native.ValidateObjectSize();

        if (_initialized) return true;
        if (_disposed) return false;

        try
        {
            _obj = _native.Create();
            if (_obj == IntPtr.Zero)
            {
                Log.Warning("SpoutOutput: spoutDX の生成に失敗");
                return false;
            }

            if (!_native.OpenDirectX11(_obj, IntPtr.Zero))
            {
                Log.Warning("SpoutOutput: OpenDirectX11 失敗");
                _native.Destroy(_obj);
                _obj = IntPtr.Zero;
                return false;
            }

            if (!_native.SetSenderName(_obj, DefaultSenderName))
            {
                Log.Warning("SpoutOutput: SetSenderName 失敗");
                _native.Destroy(_obj);
                _obj = IntPtr.Zero;
                return false;
            }

            _initialized = true;
            Log.Information("SpoutOutput: 初期化完了 sender='{Name}'", DefaultSenderName);
            return true;
        }
        catch (DllNotFoundException ex)
        {
            Log.Warning(ex, "SpoutOutput: SpoutDX.dll が見つからないため Spout 出力は無効");
            CleanupObj();
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SpoutOutput: 初期化中に例外が発生");
            CleanupObj();
            return false;
        }
    }

    /// <summary>
    /// ピクセルバッファを Spout で送信する。
    /// IsEnabled=false または未初期化の場合は無視する。
    /// </summary>
    /// <param name="pixels">bgr0 形式データの先頭ポインタ（呼び出し元でピン留め済み）</param>
    /// <param name="width">フレーム幅（ピクセル）</param>
    /// <param name="height">フレーム高さ（ピクセル）</param>
    public void SendFrame(IntPtr pixels, int width, int height)
    {
        if (!IsEnabled || !_initialized || pixels == IntPtr.Zero) return;
        if (width <= 0 || height <= 0) return;

        try
        {
            uint pitchBytes = GetTightlyPackedBgraPitch(width);
            bool ok = _native.SendImage(_obj, pixels, (uint)width, (uint)height,
                pitchBytes);
            _sendCount++;

            if (_sendCount == 1)
                Log.Information("SpoutOutput: 送信開始 {W}x{H} pitch={Pitch} sender='{Name}'",
                    width, height, pitchBytes, DefaultSenderName);
            else if (_sendCount % 300 == 0)
                Log.Debug("SpoutOutput: 送信中 {Count} フレーム送信済み ({W}x{H} pitch={Pitch})",
                    _sendCount, width, height, pitchBytes);

            if (!ok)
                InvalidateAfterSendFailure();
        }
        catch (Exception ex)
        {
            InvalidateAfterSendFailure(ex);
        }
    }

    internal static uint GetTightlyPackedBgraPitch(int width)
        => checked((uint)(width * 4));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_initialized)
        {
            try { _native.ReleaseSender(_obj); }
            catch (Exception ex) { Log.Warning(ex, "SpoutNative.ReleaseSender failed during dispose"); }
        }

        CleanupObj();
    }

    private void InvalidateAfterSendFailure(Exception? exception = null)
    {
        if (!_initialized)
            return;

        _initialized = false;
        IsEnabled = false;

        if (exception == null)
            Log.Warning("SpoutOutput: SendImage が false を返した count={Count} (device lost?)", _sendCount);
        else
            Log.Warning(exception, "SpoutOutput: SendFrame 中に例外が発生 count={Count}", _sendCount);

        try { _native.ReleaseSender(_obj); }
        catch (Exception ex) { Log.Warning(ex, "SpoutNative.ReleaseSender failed after send failure"); }

        CleanupObj();
    }

    private void CleanupObj()
    {
        if (_obj != IntPtr.Zero)
        {
            try { _native.Destroy(_obj); }
            catch (Exception ex) { Log.Warning(ex, "SpoutNative.Destroy failed during dispose"); }
            _obj = IntPtr.Zero;
        }
    }
}

internal interface ISpoutNativeApi
{
    void ValidateObjectSize();
    IntPtr Create();
    bool OpenDirectX11(IntPtr self, IntPtr device);
    bool SetSenderName(IntPtr self, string name);
    bool SendImage(IntPtr self, IntPtr pixels, uint width, uint height, uint pitch);
    void ReleaseSender(IntPtr self);
    void Destroy(IntPtr self);
}

internal sealed class SpoutNativeApi : ISpoutNativeApi
{
    internal static SpoutNativeApi Instance { get; } = new();

    private SpoutNativeApi() { }

    public void ValidateObjectSize() => SpoutNative.ValidateObjectSize();
    public IntPtr Create() => SpoutNative.Create();
    public bool OpenDirectX11(IntPtr self, IntPtr device) => SpoutNative.OpenDirectX11(self, device);
    public bool SetSenderName(IntPtr self, string name) => SpoutNative.SetSenderName(self, name);
    public bool SendImage(IntPtr self, IntPtr pixels, uint width, uint height, uint pitch) =>
        SpoutNative.SendImage(self, pixels, width, height, pitch);
    public void ReleaseSender(IntPtr self) => SpoutNative.ReleaseSender(self);
    public void Destroy(IntPtr self) => SpoutNative.Destroy(self);
}
