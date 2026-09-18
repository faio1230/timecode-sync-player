using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>M6: ダーティー LTC の 1 レベル。加工はテスト側だけで行う（製品コードは変更しない）。</summary>
internal sealed record DirtyLevel
{
    public string Name { get; init; } = "";
    /// <summary>計測に使う信号の長さ（settling を除く）。</summary>
    public double Seconds { get; init; } = 20.0;
    /// <summary>付加ノイズの SNR（dB）。矩形波 RMS を基準にする。</summary>
    public double? NoiseDb { get; init; }
    /// <summary>信号レベル（dBFS）。+3 はクリップ相当。</summary>
    public double? AmplitudeDb { get; init; }
    /// <summary>周期欠落の長さ（ms）。</summary>
    public double? DropoutMs { get; init; }
    public double DropoutPeriodSeconds { get; init; } = 5.0;
    /// <summary>生成サンプルレート（D 用）。未指定ならデバイスのミックスレート。</summary>
    public double? GenerateSampleRate { get; init; }
    /// <summary>速度差（ppm）。+ は信号が速い（波形を短くする）。</summary>
    public double Ppm { get; init; }
}

internal sealed record DirtyLtcPlan
{
    public string Condition { get; init; } = "";
    public double LtcFps { get; init; } = 25.0;
    public string LtcFpsMode { get; init; } = "fixed";
    public double SettlingSeconds { get; init; } = 2.0;
    public double GapSeconds { get; init; } = 0.5;
    public List<DirtyLevel> Levels { get; init; } = new();

    public static DirtyLtcPlan Load(string path)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        DirtyLtcPlan plan = JsonSerializer.Deserialize<DirtyLtcPlan>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException($"dirty plan is empty: {path}");
        if (plan.Levels.Count == 0)
            throw new InvalidDataException($"dirty plan has no levels: {path}");
        return plan;
    }
}

/// <summary>
/// M6: 波形の後加工。ノイズとレベルは <see cref="LtcTestSignalGenerator.Options"/> で付ける。
/// ここはドロップアウト・サンプルレート不一致・ppm を扱う。
/// </summary>
internal static class DirtyLtcSignal
{
    public static LtcTestSignalGenerator.Options BuildOptions(DirtyLevel level)
    {
        double amplitude = level.AmplitudeDb is double db ? Math.Pow(10.0, db / 20.0) : 1.0;
        double noise = level.NoiseDb is double snr ? NoiseAmplitudeFor(snr, amplitude) : 0.0;
        return new LtcTestSignalGenerator.Options
        {
            Amplitude = (float)amplitude,
            NoiseAmplitude = noise,
            NoiseSeed = 4242,
        };
    }

    /// <summary>矩形波 RMS=amplitude、一様ノイズ RMS=N/√3 として SNR から N を求める。</summary>
    internal static double NoiseAmplitudeFor(double snrDb, double amplitude) =>
        amplitude * Math.Sqrt(3.0) * Math.Pow(10.0, -snrDb / 20.0);

    /// <summary>先頭から period 秒ごとに dropoutMs の無音を挿入する（タイムコードは進めたまま）。</summary>
    internal static float[] ApplyDropouts(float[] samples, int sampleRate, double dropoutMs, double periodSeconds)
    {
        int period = Math.Max(1, (int)Math.Round(periodSeconds * sampleRate));
        int width = Math.Max(1, (int)Math.Round(dropoutMs / 1000.0 * sampleRate));
        for (int start = period; start < samples.Length; start += period)
        {
            int end = Math.Min(start + width, samples.Length);
            Array.Clear(samples, start, end - start);
        }
        return samples;
    }

    /// <summary>線形補間の簡易リサンプラ。D（生成レート不一致）と E（ppm）の両方に使う。</summary>
    internal static float[] ResampleLinear(float[] input, double sourceRate, double targetRate)
    {
        if (Math.Abs(sourceRate - targetRate) < 1e-9)
            return input;
        double ratio = targetRate / sourceRate;
        int length = Math.Max(1, (int)Math.Round(input.Length * ratio));
        var output = new float[length];
        for (int i = 0; i < length; i++)
        {
            double source = i / ratio;
            int index = (int)source;
            double fraction = source - index;
            float first = input[Math.Min(index, input.Length - 1)];
            float second = input[Math.Min(index + 1, input.Length - 1)];
            output[i] = (float)(first + ((second - first) * fraction));
        }
        return output;
    }

    /// <summary>
    /// 欠落 → リサンプルの順に適用する。generatedRate は波形を生成したレート、
    /// deviceRate は送出先のミックスレート。ppm は generatedRate=deviceRate のときだけ使う。
    /// </summary>
    internal static float[] Process(float[] samples, int generatedRate, int deviceRate, DirtyLevel level)
    {
        if (level.DropoutMs is double dropoutMs && dropoutMs > 0)
            ApplyDropouts(samples, generatedRate, dropoutMs, level.DropoutPeriodSeconds);

        double targetRate = deviceRate;
        if (Math.Abs(level.Ppm) > 1e-12)
            targetRate = deviceRate / (1.0 + (level.Ppm * 1e-6));
        return ResampleLinear(samples, generatedRate, targetRate);
    }
}
