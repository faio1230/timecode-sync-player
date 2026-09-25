using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// v0.5.2 段 2c: シークの軸の着地窓（D37-b2 / D37-d / D37-e / D37-f / D37-g）。
/// ギャップ明け・トラック切替・追従開始の着地エピソード。速度補正優先をやめてシークで
/// 着地させ、窓の中は 1 回目をシークで試し、着地後に不足が実際に減ったかを観測する
/// （前進ガード）。上限（5 秒 / 連続 3 シーク）はシークが縮まらない素材への最後の歯止め。
///
/// 「閉じている／開いている＋開いている間のデータ」を <see cref="OpenState"/> で表す。
/// 閉じた後も読まれる値（<see cref="Origin"/>・<see cref="AwaitingObservation"/>）は
/// 窓の外に置く（v0.5.2 段 2c の確認。Close は発生元を戻さず、EvaluateDecision のログが
/// 閉じた後も発生元を読む。前進ガードの観測待ちは ObserveProgress の先頭で閉じていても読む）。
/// </summary>
internal sealed class SeekLandingWindow
{
    private const double LandingProgressEpsilonSeconds = 0.001;
    private static readonly TimeSpan SeekLandingMaxWindow = TimeSpan.FromSeconds(5.0);
    private const int SeekLandingMaxSeeks = 3;

    private readonly TimeProvider _timeProvider;

    /// <summary>窓が開いている間だけのデータ（開いていなければ null）。</summary>
    private readonly record struct OpenState(DateTime OpenedAt, int Seeks, double PreDeficitSeconds);

    private OpenState? _open;

    /// <summary>D37-e: この着地エピソードの発生元（追従開始だけ先行量を有効にする）。</summary>
    public LandingOrigin Origin { get; private set; } = LandingOrigin.Other;

    /// <summary>D37-d 前進ガード: 直前に窓の中で発行したシークの着地観測待ち。</summary>
    public bool AwaitingObservation { get; private set; }

    public SeekLandingWindow(TimeProvider timeProvider) => _timeProvider = timeProvider;

    /// <summary>窓が開いているか（上限による閉鎖は判定しない。<see cref="TimecodeSyncService.LatchSnapshot"/> 用）。</summary>
    public bool IsOpen => _open is not null;

    /// <summary>
    /// D37-b2/D37-d: ギャップ明け・トラック切替・追従開始の着地を通知する。ここから
    /// 誤差が許容内に入る（または上限に達する）まで、速度補正優先を止める。
    /// D37-e: 追従開始だけ origin=FollowStart を渡し、シーク目標の先行量を有効にする。
    /// </summary>
    public void NotifyLanding(LandingOrigin origin) => Open(_timeProvider.GetUtcNow().UtcDateTime, origin);

    /// <summary>呼び出し側が取った時刻で開く（ロード成立は同じ時刻をほかの処理と共有する）。</summary>
    public void OpenAt(DateTime now, LandingOrigin origin) => Open(now, origin);

    private void Open(DateTime now, LandingOrigin origin)
    {
        // D37-f: 追従開始の窓が生きている間は、Other で発生元を上書きしない。
        //
        // 追従開始とロード成立は同じフレームで起きうる。NotifyLanding(FollowStart) の直後に
        // ReleaseFileLoad が Open(Other) を呼ぶと、発生元が Other に戻り、
        // D37-e の先行補償が効かなくなる（実測: windowActive=true・origin=Other・hint=1.990 で
        // lookahead=0。シーク先が LTC と同値になり、追従開始に 16 秒かかっていた）。
        //
        // 窓そのものは開き直してよい（シーク回数と観測待ちはリセットする）。守りたいのは
        // 「この着地は追従開始である」という事実だけ。
        bool keepFollowStart = _open is not null
            && Origin == LandingOrigin.FollowStart
            && origin != LandingOrigin.FollowStart;
        _open = new OpenState(now, Seeks: 0, PreDeficitSeconds: double.NaN);
        AwaitingObservation = false;
        if (!keepFollowStart)
            Origin = origin;
    }

    /// <summary>
    /// D37-g: 追従開始のエピソードを終わらせる（着地窓そのものは残す）。
    ///
    /// Single の境界ホールド中は位置がクリップ端に固定されるため、誤差が許容内に入ることは
    /// 設計上ありえず、<see cref="ObserveArrival"/> が呼ばれない。結果として
    /// 追従開始の窓が上限の 5 秒まで開いたままになり、その間に起きた<b>無関係な</b>シーク
    /// （範囲外 LTC が範囲内へ巻き戻ったときの復帰シーク）まで D37-e の先行量を足していた。
    ///
    /// 実測（E2E S-3、1280x720・キーフレーム間隔 1 秒のフィクスチャ）: LTC 10.022 への復帰で
    /// 行き先が 10.322 になり、判定許容 0.3 秒をちょうど超えて着地。さらに LTC が止まっている
    /// 場面のため以後の同期評価が走らず、そのまま前へ流れて 15.0 まで離れた。
    ///
    /// 先行量は「シークの間にタイムコードが進むぶん」の見積もりなので、追従開始という
    /// 文脈が終わったら外す。窓（シーク優先・速度補正の抑止）は残してよい。
    ///
    /// <b>代償</b>: 範囲外で同期を入れてから範囲内へ入る運用では、その「最初の実質的な追従」の
    /// シークが先行量を失う。キーフレーム間隔が長い素材では、その遷移だけシークが 1 回増える
    /// （着地窓は到達まで続くので収束はする）。それでも外すのは、S-3 の場面ではタイムコードが
    /// 止まっており、<b>行き過ぎたまま補正が走らない</b>ほうが害が大きいため。
    /// 本来は「タイムコードが進んでいるか」で先行量を決めるべきだが、その信号を同期側へ
    /// 渡す仕組みがまだない（0.4.6 以降の課題）。
    ///
    /// 0.4.6: <b>LTC の Jump を適用したときにも終わらせる</b>（<paramref name="reason"/> = "ltc jump"）。
    /// ホールド解除だけでは足りなかった。検証機の S-3 で、端へのシークの着地に 0.655 秒かかり、
    /// ホールドが成立しないまま LTC が範囲内へ戻った。復帰シークに学習値 0.655 が乗り、
    /// 10.060 に対して 10.714 へ着地した。LTC が不連続に動いた時点で「シークの間に LTC が
    /// 進むぶん」という前提が崩れるので、ホールドの成否に関係なくそこで外す。
    /// </summary>
    public void EndFollowStartLanding(string reason)
    {
        if (_open is null || Origin != LandingOrigin.FollowStart)
            return;
        Origin = LandingOrigin.Other;
        Log.Information("Seek landing: follow-start episode ended ({Reason})", reason);
    }

    /// <summary>窓が開いているか。上限（回数・時間）に達していたら閉じて false。</summary>
    public bool IsActive()
    {
        if (_open is not { } open)
            return false;
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        double ageMs = (now - open.OpenedAt).TotalMilliseconds;
        if (open.Seeks >= SeekLandingMaxSeeks)
        {
            Log.Information(
                "Timecode sync: landing window closed at the seek cap seeks={Seeks} ageMs={AgeMs:F0}",
                open.Seeks, ageMs);
            Close();
            return false;
        }
        if (now - open.OpenedAt >= SeekLandingMaxWindow)
        {
            Log.Information(
                "Timecode sync: landing window closed at the age cap ageMs={AgeMs:F0} seeks={Seeks}",
                ageMs, open.Seeks);
            Close();
            return false;
        }
        return true;
    }

    /// <summary>D37-d: 到達（誤差が許容内）で着地エピソードを閉じる。</summary>
    public void ObserveArrival()
    {
        if (_open is not { } open)
            return;
        Log.Information(
            "Timecode sync: landing window closed on arrival ageMs={AgeMs:F0} seeks={Seeks}",
            (_timeProvider.GetUtcNow().UtcDateTime - open.OpenedAt).TotalMilliseconds,
            open.Seeks);
        Close();
    }

    /// <summary>
    /// D37-d 前進ガード: 窓の中で発行したシークの着地で、不足が実際に減ったかを見る。
    /// 減っていなければ（前進なし）窓を閉じ、以降のフレームは通常の判断に戻す。
    /// 事前予測ではなく観測なので、シークが効かない帯でも 1 回で止まる。
    /// </summary>
    public void ObserveProgress(double ltcSeconds, double playbackSeconds)
    {
        if (!AwaitingObservation)
            return;
        AwaitingObservation = false;
        if (_open is not { } open || double.IsNaN(open.PreDeficitSeconds))
            return;
        double post = Math.Abs(ltcSeconds - playbackSeconds);
        if (post >= open.PreDeficitSeconds - LandingProgressEpsilonSeconds)
        {
            Log.Information(
                "Timecode sync: landing window closed without progress preMs={PreMs:F0} postMs={PostMs:F0} ageMs={AgeMs:F0} seeks={Seeks}",
                open.PreDeficitSeconds * 1000.0, post * 1000.0,
                (_timeProvider.GetUtcNow().UtcDateTime - open.OpenedAt).TotalMilliseconds,
                open.Seeks);
            Close();
        }
    }

    /// <summary>前進ガード用: このシークの不足を覚えておく（<see cref="OnSeekSent"/> で着地観測を arm する）。</summary>
    public void SetSeekDeficit(double seconds)
    {
        if (_open is not { } open)
            return;
        _open = open with { PreDeficitSeconds = seconds };
    }

    /// <summary>T9/D37-d: 粗いシークを発行した。窓の中なら回数を数え、着地観測を arm する。</summary>
    public void OnSeekSent()
    {
        if (!IsActive() || _open is not { } open)
            return;
        _open = open with { Seeks = open.Seeks + 1 };
        AwaitingObservation = !double.IsNaN(open.PreDeficitSeconds);
    }

    private void Close()
    {
        _open = null;
        AwaitingObservation = false;
    }
}
