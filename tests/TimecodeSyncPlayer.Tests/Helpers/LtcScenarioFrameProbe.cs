using System.Drawing;
using System.IO;
using FlaUI.Core.Capturing;

namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// LTC シナリオ E2E の画素判定。プレビュー（VideoImage）を読み戻し、中央 60% 領域の
/// 平均色・黒画素率・画素差分を返す。判定基準は
/// docs/LTC-SYNC-VERIFICATION-MATRIX-2026-09-17.md 2 節:
///  - 黒: 平均輝度 &lt; 8/255 かつ黒画素 >= 99%
///  - 参照一致: 平均色距離 &lt; 60 かつ最近傍（他の参照より近い）かつ画素差分平均 &lt; 12/255
/// </summary>
internal readonly record struct FrameSignature(
    double MeanR,
    double MeanG,
    double MeanB,
    double BlackFraction,
    int SampleCount,
    byte[] Pixels)
{
    /// <summary>相対輝度（Rec.709）。</summary>
    public double MeanLuminance => 0.2126 * MeanR + 0.7152 * MeanG + 0.0722 * MeanB;

    public bool IsBlack =>
        MeanLuminance < LtcScenarioFrameProbe.BlackLuminanceThreshold &&
        BlackFraction >= LtcScenarioFrameProbe.BlackPixelFraction;

    public double MeanColorDistanceTo(FrameSignature other)
    {
        double dr = MeanR - other.MeanR;
        double dg = MeanG - other.MeanG;
        double db = MeanB - other.MeanB;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    /// <summary>サンプル画素のチャンネル平均絶対差（0〜255）。</summary>
    public double MeanPixelDifferenceTo(FrameSignature other)
    {
        if (Pixels.Length == 0 || Pixels.Length != other.Pixels.Length)
            return double.PositiveInfinity;

        long sum = 0;
        for (int index = 0; index < Pixels.Length; index++)
            sum += Math.Abs(Pixels[index] - other.Pixels[index]);
        return sum / (double)Pixels.Length;
    }
}

internal static class LtcScenarioFrameProbe
{
    public const double BlackLuminanceThreshold = 8.0;
    public const double BlackPixelFraction = 0.99;
    public const double ReferenceColorDistance = 60.0;
    public const double ReferencePixelDifference = 12.0;
    public const double CenterFraction = 0.6;
    private const int SampleStride = 8;

    /// <summary>目視・ジャーナル用の色名。判定には使わない（実素材では意味を持たない）。</summary>
    public static readonly (string Name, Color Color)[] KnownPalette =
    [
        ("red", Color.FromArgb(255, 0, 0)),
        ("green", Color.FromArgb(0, 255, 0)),
        ("blue", Color.FromArgb(0, 0, 255)),
        ("yellow", Color.FromArgb(255, 255, 0)),
        ("cyan", Color.FromArgb(0, 255, 255)),
        ("magenta", Color.FromArgb(255, 0, 255)),
        ("white", Color.FromArgb(255, 255, 255)),
        ("orange", Color.FromArgb(255, 165, 0)),
        ("black", Color.FromArgb(0, 0, 0)),
    ];

    /// <summary>VideoImage の読み戻し経路（RealProjectGap.CaptureImage と共用）。</summary>
    public static Bitmap CaptureVideoImage(E2EAppRunner app, string reportDir, string imageName)
    {
        var image = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("VideoImage"))
            ?? throw new InvalidOperationException("VideoImage が見つかりません。");
        app.MainWindow.Focus();
        using var capture = FlaUI.Core.Capturing.Capture.Element(image);
        capture.ToFile(Path.Combine(reportDir, imageName + ".png"));
        return new Bitmap(capture.Bitmap);
    }

    public static FrameSignature Capture(
        E2EAppRunner app, string reportDir, string imageName, MonkeyJournal journal)
    {
        using Bitmap bitmap = CaptureVideoImage(app, reportDir, imageName);
        FrameSignature signature = MeasureCenter(bitmap);
        journal.Write("image", details: new
        {
            name = imageName,
            meanR = Math.Round(signature.MeanR, 1),
            meanG = Math.Round(signature.MeanG, 1),
            meanB = Math.Round(signature.MeanB, 1),
            meanLuminance = Math.Round(signature.MeanLuminance, 1),
            blackFraction = Math.Round(signature.BlackFraction, 4),
            isBlack = signature.IsBlack,
            samples = signature.SampleCount,
            nearestKnownColor = DescribeNearestKnownColor(signature),
        });
        return signature;
    }

    /// <summary>中央 60% 領域（各辺 20% を除く）を 8px 間隔で測る。</summary>
    public static FrameSignature MeasureCenter(Bitmap bitmap)
    {
        int x0 = (int)Math.Round(bitmap.Width * (1.0 - CenterFraction) / 2.0);
        int x1 = bitmap.Width - x0;
        int y0 = (int)Math.Round(bitmap.Height * (1.0 - CenterFraction) / 2.0);
        int y1 = bitmap.Height - y0;

        long sumR = 0, sumG = 0, sumB = 0;
        long black = 0, count = 0;
        var pixels = new List<byte>();
        for (int y = y0; y < y1; y += SampleStride)
        {
            for (int x = x0; x < x1; x += SampleStride)
            {
                Color pixel = bitmap.GetPixel(x, y);
                sumR += pixel.R;
                sumG += pixel.G;
                sumB += pixel.B;
                double luminance = 0.2126 * pixel.R + 0.7152 * pixel.G + 0.0722 * pixel.B;
                if (luminance < BlackLuminanceThreshold)
                    black++;
                count++;
                pixels.Add(pixel.R);
                pixels.Add(pixel.G);
                pixels.Add(pixel.B);
            }
        }

        if (count == 0)
            return new FrameSignature(0, 0, 0, 0, 0, []);

        return new FrameSignature(
            sumR / (double)count,
            sumG / (double)count,
            sumB / (double)count,
            black / (double)count,
            (int)count,
            [.. pixels]);
    }

    /// <summary>最近傍の既知色（ジャーナル用。判定には使わない）。</summary>
    public static string DescribeNearestKnownColor(FrameSignature signature)
    {
        string name = "unknown";
        double best = double.PositiveInfinity;
        foreach ((string candidate, Color color) in KnownPalette)
        {
            double dr = signature.MeanR - color.R;
            double dg = signature.MeanG - color.G;
            double db = signature.MeanB - color.B;
            double distance = Math.Sqrt(dr * dr + dg * dg + db * db);
            if (distance < best)
            {
                best = distance;
                name = candidate;
            }
        }

        return $"{name} (d={best:F1})";
    }
}

/// <summary>1 枚の参照フレーム（トラック記号 + 冒頭/最終）。</summary>
internal sealed record ReferenceFrame(string TrackSymbol, string Kind, string ImageName, FrameSignature Signature);

/// <summary>参照セットへの照合結果。Match は「距離 &lt; 60 かつ最近傍かつ画素差分 &lt; 12」。</summary>
internal readonly record struct ReferenceMatch(
    ReferenceFrame? Reference,
    double ColorDistance,
    double PixelDifference,
    bool IsMatch)
{
    public bool MatchesTrack(string trackSymbol) =>
        IsMatch && Reference is not null &&
        string.Equals(Reference.TrackSymbol, trackSymbol, StringComparison.Ordinal);
}

/// <summary>テスト開始時に一時停止シークで採った参照フレームの集合。</summary>
internal sealed class ReferenceSet
{
    private readonly List<ReferenceFrame> _frames = [];

    public IReadOnlyList<ReferenceFrame> Frames => _frames;

    public void Add(string trackSymbol, string kind, string imageName, FrameSignature signature) =>
        _frames.Add(new ReferenceFrame(trackSymbol, kind, imageName, signature));

    /// <summary>平均色距離が最小の参照（同距離は先着）。</summary>
    public ReferenceFrame? Closest(FrameSignature signature)
    {
        ReferenceFrame? best = null;
        double bestDistance = double.PositiveInfinity;
        foreach (ReferenceFrame frame in _frames)
        {
            double distance = frame.Signature.MeanColorDistanceTo(signature);
            if (best is null || distance < bestDistance)
            {
                best = frame;
                bestDistance = distance;
            }
        }

        return best;
    }

    public ReferenceMatch Match(FrameSignature signature)
    {
        if (_frames.Count == 0)
            return new ReferenceMatch(null, double.PositiveInfinity, double.PositiveInfinity, false);

        ReferenceFrame best = _frames[0];
        double bestColor = double.PositiveInfinity;
        double secondColor = double.PositiveInfinity;
        foreach (ReferenceFrame frame in _frames)
        {
            double distance = frame.Signature.MeanColorDistanceTo(signature);
            if (distance < bestColor)
            {
                secondColor = bestColor;
                bestColor = distance;
                best = frame;
            }
            else if (distance < secondColor)
            {
                secondColor = distance;
            }
        }

        double pixelDifference = best.Signature.MeanPixelDifferenceTo(signature);
        bool uniqueNearest = _frames.Count == 1 || bestColor < secondColor;
        bool isMatch = bestColor < LtcScenarioFrameProbe.ReferenceColorDistance &&
                       pixelDifference < LtcScenarioFrameProbe.ReferencePixelDifference &&
                       uniqueNearest;
        return new ReferenceMatch(best, bestColor, pixelDifference, isMatch);
    }
}
