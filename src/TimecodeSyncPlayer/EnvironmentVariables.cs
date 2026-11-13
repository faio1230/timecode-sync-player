namespace TimecodeSyncPlayer;

/// <summary>
/// v0.6.7: 環境変数の名前を ERYTHEIA_* に変えた。v0.6.x の間は旧の名前（TIMECODE_SYNC_PLAYER_*・TCS_*）も読む
/// （v0.7.0 で外す）。旧の名前を読んだら変数ごとに Warning を 1 行出す。新旧の両方があれば新を使い、その旨を 1 行出す。
/// Serilog を作る前に読んだ分（shim のログの出力先など）は、App が Serilog を作った後に
/// <see cref="FlushPendingWarnings"/> で出す。shim が読む変数は shim の側で同じことをする。
/// </summary>
internal static class EnvironmentVariables
{
    internal const string Prefix = "ERYTHEIA_";

    /// <summary>アプリが読む変数の旧の名前。新しい名前は旧の接頭辞を ERYTHEIA_ に替えたもの。</summary>
    private static readonly string[] LegacyNames =
    {
        "TIMECODE_SYNC_PLAYER_SETTINGS_PATH",
        "TIMECODE_SYNC_PLAYER_SPOUT_NAME",
        "TIMECODE_SYNC_PLAYER_FORCE_GPU_UNAVAILABLE",
        "TIMECODE_SYNC_PLAYER_TEST_CARD",
        "TIMECODE_SYNC_PLAYER_SIMULATE_DEVICE_LOSS",
        "TIMECODE_SYNC_PLAYER_OUTPUT_TRACE",
        "TIMECODE_SYNC_PLAYER_OUTPUT_TRACE_CAPACITY",
        "TCS_GAP_MODE",
        "TCS_LOG_FILE",
        "TCS_PUMP_BUDGET_MS",
        "TCS_LTC_SAMPLE_CLOCK",
        "TCS_SEEK_COST_HINT",
        "TCS_GOP_SCAN",
        "TCS_TEST_FORCE_GAP_BLACK_ON_SWITCH",
        "TCS_SEEK_LATENCY_COMPENSATION",
        "TCS_SYNC_POSITION_FEEDBACK",
    };

    /// <summary>新しい名前 → 旧の名前。</summary>
    internal static IReadOnlyDictionary<string, string> LegacyNameByName { get; } =
        LegacyNames.ToDictionary(ToNewName, legacy => legacy, StringComparer.Ordinal);

    private static readonly object _lock = new();
    private static readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private static readonly List<Action> _pending = new();
    private static bool _loggerReady;

    /// <summary>旧の名前（TIMECODE_SYNC_PLAYER_* か TCS_*）を新しい名前（ERYTHEIA_*）にする。</summary>
    internal static string ToNewName(string legacyName)
    {
        foreach (string prefix in new[] { "TIMECODE_SYNC_PLAYER_", "TCS_" })
        {
            if (legacyName.StartsWith(prefix, StringComparison.Ordinal))
                return Prefix + legacyName[prefix.Length..];
        }
        throw new ArgumentException($"旧の接頭辞の名前ではありません: {legacyName}", nameof(legacyName));
    }

    /// <summary>新しい名前で読む。無ければ旧の名前を読み、Warning を 1 行出す（変数ごとに 1 回）。</summary>
    public static string? Get(string name) => Get(name, Environment.GetEnvironmentVariable);

    internal static string? Get(string name, Func<string, string?> read)
    {
        string? value = read(name);
        if (!LegacyNameByName.TryGetValue(name, out string? legacyName))
            return value;

        string? legacyValue = read(legacyName);
        if (string.IsNullOrEmpty(legacyValue))
            return value;

        if (!string.IsNullOrEmpty(value))
        {
            Report(name, () => Serilog.Log.Warning(
                "環境変数 {Name} と旧の名前 {LegacyName} の両方があります。新しい名前の方を使います（旧の名前は v0.6.x の間だけ読みます）",
                name, legacyName));
            return value;
        }

        Report(name, () => Serilog.Log.Warning(
            "旧の名前の環境変数 {LegacyName} を読みました。新しい名前 {Name} に変えてください（旧の名前は v0.6.x の間だけ読みます）",
            legacyName, name));
        return legacyValue;
    }

    /// <summary>Serilog を作った後に 1 回呼ぶ。それまでに読んだ旧の名前の Warning を出す。</summary>
    public static void FlushPendingWarnings()
    {
        Action[] pending;
        lock (_lock)
        {
            _loggerReady = true;
            pending = _pending.ToArray();
            _pending.Clear();
        }
        foreach (Action warn in pending)
            warn();
    }

    internal static void ResetForTesting(bool loggerReady)
    {
        lock (_lock)
        {
            _reported.Clear();
            _pending.Clear();
            _loggerReady = loggerReady;
        }
    }

    private static void Report(string name, Action warn)
    {
        lock (_lock)
        {
            if (!_reported.Add(name))
                return;
            if (!_loggerReady)
            {
                _pending.Add(warn);
                return;
            }
        }
        warn();
    }
}
