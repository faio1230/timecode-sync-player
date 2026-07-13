namespace TimecodeSyncPlayer;

/// <summary>デコード済みタイムコード</summary>
public record LtcTimecode(int Hours, int Minutes, int Seconds, int Frames, bool DropFrame)
{
    public override string ToString() =>
        $"{Hours:D2}:{Minutes:D2}:{Seconds:D2}{(DropFrame ? ';' : ':')}{Frames:D2}";

    /// <summary>
    /// タイムコードを実時間（秒）に変換する。
    /// 実時間 = H×3600 + M×60 + S + F÷fps
    /// DropFrame (29.97) の場合は fps = 30000/1001 を使用する。
    /// </summary>
    public double ToRealSeconds(double fps)
    {
        double actualFps = DropFrame ? 30000.0 / 1001.0 : fps;
        return Hours * 3600.0 + Minutes * 60.0 + Seconds + Frames / actualFps;
    }

    /// <summary>実時間を秒表記（例: 10376.480 s）にフォーマットする</summary>
    public static string FormatRealTime(double totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        return $"{totalSeconds:F3} s";
    }
}

/// <summary>
/// 純粋C# LTCデコーダ。ネイティブDLL不要。
///
/// アルゴリズム:
///   1. ゼロクロッシングで遷移を検出
///   2. 遷移間隔の長短（ショート=ハーフビット / ロング=フルビット）でBMCビットを復元
///   3. 80ビットのシフトレジスタで同期ワード(0xBFFC)を検出したらフレームを解析
/// </summary>
public sealed class LtcDecoder
{
    // LTC 同期ワード: ビット64〜79 = 0011111111111101
    // _syncReg に bit64=pos0, bit79=pos15 で格納したときの値
    private const ushort FwdSync = 0xBFFC;

    // ── BMC クロックリカバリ ────────────────────────────────────
    private double _halfPeriod;    // 推定ハーフビット周期（サンプル数）
    private int    _sinceTrans;    // 前回遷移からのサンプル数
    private bool   _expectMid;    // ミッドビット遷移待ち中
    private float  _prev;         // 前サンプル値

    // ── 80ビット シフトレジスタ ─────────────────────────────────
    // 新ビットは _syncReg の MSB(bit15) に入り、LSB(bit0) が _data の MSB(bit63) へ流れる
    private ulong  _data;         // ビット 0〜63（LTCデータ部）
    private ushort _syncReg;      // ビット 64〜79（同期ワード検出用）

    private readonly Queue<LtcTimecode> _queue = new();
    private const int MaxQueueSize = 60; // 最大60フレーム（約2秒@30fps）
    private readonly int _sampleRate;

    /// <param name="sampleRate">オーディオサンプルレート (例: 48000)</param>
    /// <param name="fps">初期フレームレート推定値（適応的に更新される）</param>
    public LtcDecoder(int sampleRate, double fps = 25.0)
    {
        _sampleRate = sampleRate;
        // 初期ハーフビット推定: sampleRate / (fps × 80bits × 2)
        _halfPeriod = sampleRate / (fps * 80.0 * 2.0);
    }

    /// <summary>
    /// ビットクロックから推定したフレームレート。
    /// 標準値 (24 / 25 / 29.97 / 30) に丸めて返す。
    /// 29.97 と 30 の区別は <see cref="LtcTimecode.DropFrame"/> で判定する。
    /// </summary>
    public double EstimatedFps
    {
        get
        {
            double raw = _sampleRate / (_halfPeriod * 160.0);
            // 標準フレームレートに丸める
            return raw switch
            {
                < 24.5 => 24.0,
                < 27.5 => 25.0,
                _      => 30.0   // 29.97 と 30 は DropFrame フラグで区別
            };
        }
    }

    /// <summary>float サンプル配列を書き込む（チャンネル0のモノラル、値域 -1〜+1）</summary>
    public void Write(float[] samples, int count)
    {
        for (int i = 0; i < count; i++)
            Feed(samples[i]);
    }

    /// <summary>デコード済みフレームを1件取得。なければ null</summary>
    public LtcTimecode? Read() =>
        _queue.Count > 0 ? _queue.Dequeue() : null;

    // ── 内部処理 ─────────────────────────────────────────────────

    private void Feed(float s)
    {
        _sinceTrans++;
        // ゼロクロッシング（正負が変わった）＝遷移
        if ((_prev < 0f) != (s < 0f))
            OnTransition();
        _prev = s;
    }

    private void OnTransition()
    {
        int d = _sinceTrans;
        _sinceTrans = 0;

        if (d < _halfPeriod * 1.6)
        {
            // ショートインターバル: ハーフビット周期相当 → "1" ビットの構成要素
            if (!_expectMid)
            {
                _expectMid = true;                    // 1本目: 次の短い遷移を待つ
            }
            else
            {
                _expectMid = false;
                EmitBit(1);                           // 2本連続 → ビット "1" 確定
            }
            // ハーフビット周期を指数移動平均で更新
            _halfPeriod = _halfPeriod * 0.9 + d * 0.1;
        }
        else
        {
            // ロングインターバル: フルビット周期相当 → ビット "0"
            _expectMid = false;
            EmitBit(0);
            // フルビット周期の半分でハーフ周期を更新
            _halfPeriod = _halfPeriod * 0.9 + d * 0.05;
        }
    }

    private void EmitBit(int b)
    {
        // _syncReg の LSB(bit0) → _data の MSB(bit63) へ
        bool overflow = (_syncReg & 1) != 0;
        _data    = (_data >> 1) | (overflow ? (1UL << 63) : 0UL);
        _syncReg = (ushort)((_syncReg >> 1) | (b << 15));

        if (_syncReg == FwdSync)
            TryParseFrame();
    }

    private void TryParseFrame()
    {
        ulong f = _data;

        // LTC 80ビットフレーム構造（SMPTE 12M）
        int frameU    = (int)( f        & 0x0F);  // bits 0-3
        int frameTens = (int)((f >>  8) & 0x03);  // bits 8-9
        bool df       = (f & (1UL << 10)) != 0;   // bit 10: ドロップフレームフラグ

        int secU      = (int)((f >> 16) & 0x0F);  // bits 16-19
        int secTens   = (int)((f >> 24) & 0x07);  // bits 24-26

        int minU      = (int)((f >> 32) & 0x0F);  // bits 32-35
        int minTens   = (int)((f >> 40) & 0x07);  // bits 40-42

        int hourU     = (int)((f >> 48) & 0x0F);  // bits 48-51
        int hourTens  = (int)((f >> 56) & 0x03);  // bits 56-57

        int fr = frameTens * 10 + frameU;
        int s  = secTens   * 10 + secU;
        int m  = minTens   * 10 + minU;
        int h  = hourTens  * 10 + hourU;

        // 範囲チェック（ノイズによる誤検出を排除）
        if (fr > 29 || s > 59 || m > 59 || h > 23) return;

        while (_queue.Count >= MaxQueueSize)
        {
            _queue.Dequeue();
        }

        _queue.Enqueue(new LtcTimecode(h, m, s, fr, df));
    }
}
