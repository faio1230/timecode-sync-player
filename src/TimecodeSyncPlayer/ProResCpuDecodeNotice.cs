namespace TimecodeSyncPlayer;

/// <summary>
/// v0.6.0: ProRes の素材を CPU（avdec_prores）で開いたことを、アプリのログに 1 行残す。
/// 理由（skip・失敗の reason）は shim が tcs-gst のログに出すので、ここでは設定の値だけを書く。
/// 同じ内容は起動の間に 1 回だけ（ロードのたびに出さない。守りすぎの記録を増やさない）。
/// GPU（proresd3d11dec）で開いたとき、ProRes 以外の素材では出さない。
/// </summary>
internal sealed class ProResCpuDecodeNotice
{
    internal const string CpuDecoderName = "avdec_prores";

    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);

    /// <summary>出すべき行。出さないとき（ProRes の CPU 復号でない、既に出した）は null。</summary>
    public string? Next(string? decoderName, ProResGpuMode mode)
    {
        string? message = Message(decoderName, mode);
        return message is not null && _emitted.Add(message) ? message : null;
    }

    internal static string? Message(string? decoderName, ProResGpuMode mode)
    {
        if (!IsProResCpuDecoder(decoderName)) return null;
        return $"ProRes: CPU で復号（proResGpu={ProResGpuPolicy.Describe(mode)}、理由は tcs-gst のログ）";
    }

    internal static bool IsProResCpuDecoder(string? decoderName) =>
        !string.IsNullOrWhiteSpace(decoderName) &&
        decoderName.Trim().Equals(CpuDecoderName, StringComparison.OrdinalIgnoreCase);
}
