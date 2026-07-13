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


    public bool IsEnabled    { get; set; } = false;
    public bool IsAvailable  => _initialized;

    /// <summary>
    /// SpoutDX.dll をロードして初期化する。
    /// DLL がない場合や初期化失敗の場合は false を返す（クラッシュしない）。
    /// </summary>
    public bool TryInitialize()
    {
        SpoutNative.ValidateObjectSize();

        if (_initialized) return true;
        if (_disposed) return false;

        try
        {
            _obj = SpoutNative.Create();
            if (_obj == IntPtr.Zero)
            {
                Log.Warning("SpoutOutput: spoutDX の生成に失敗");
                return false;
            }

            if (!SpoutNative.OpenDirectX11(_obj, IntPtr.Zero))
            {
                Log.Warning("SpoutOutput: OpenDirectX11 失敗");
                SpoutNative.Destroy(_obj);
                _obj = IntPtr.Zero;
                return false;
            }

            if (!SpoutNative.SetSenderName(_obj, DefaultSenderName))
            {
                Log.Warning("SpoutOutput: SetSenderName 失敗");
                SpoutNative.Destroy(_obj);
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
            bool ok = SpoutNative.SendImage(_obj, pixels, (uint)width, (uint)height,
                pitchBytes);
            _sendCount++;

            if (_sendCount == 1)
                Log.Information("SpoutOutput: 送信開始 {W}x{H} pitch={Pitch} sender='{Name}'",
                    width, height, pitchBytes, DefaultSenderName);
            else if (_sendCount % 300 == 0)
                Log.Debug("SpoutOutput: 送信中 {Count} フレーム送信済み ({W}x{H} pitch={Pitch})",
                    _sendCount, width, height, pitchBytes);

            if (!ok)
                Log.Warning("SpoutOutput: SendImage が false を返した count={Count} (device lost?)", _sendCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SpoutOutput: SendFrame 中に例外が発生 count={Count}", _sendCount);
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
            try { SpoutNative.ReleaseSender(_obj); }
            catch (Exception ex) { Log.Warning(ex, "SpoutNative.ReleaseSender failed during dispose"); }
        }

        CleanupObj();
    }

    private void CleanupObj()
    {
        if (_obj != IntPtr.Zero)
        {
            try { SpoutNative.Destroy(_obj); }
            catch (Exception ex) { Log.Warning(ex, "SpoutNative.Destroy failed during dispose"); }
            _obj = IntPtr.Zero;
        }
    }
}
