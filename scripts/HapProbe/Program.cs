using System.Diagnostics;

namespace HapProbe;

/// <summary>
/// HAP を GPU 直接経路で扱えるかの試作（2026-09-20）。docs/HAP-GSTREAMER-INVESTIGATION-2026-09-11.md の
/// 「次にやるなら 1」。**製品コードには触れない。**
///
/// 測るのは 1 コマあたり:
///   1. Snappy 展開（単体 / 並列）
///   2. GPU へのアップロード（直接 / 中継バッファ経由）
///   3. 経路 1（GPU で BGRA に展開してから合成）と経路 2（圧縮のまま合成でサンプリング）
/// 表示は 60Hz を自前で刻む（画面のリフレッシュレートに左右されないため）。締め切りに間に合わなかった
/// コマを「落ちコマ」として数える。
/// </summary>
internal static class Program
{
    private const double TargetFps = 60.0;
    private const int DefaultSeconds = 60;

    private static int Main(string[] args)
    {
        string root = ArgValue(args, "--frames") ?? Path.Combine(Path.GetTempPath(), "hap");
        int seconds = int.TryParse(ArgValue(args, "--seconds"), out int parsed) ? parsed : DefaultSeconds;
        string? only = ArgValue(args, "--set");
        string? dump = ArgValue(args, "--dump");   // 絵の確認用に 1 枚書き出して終わる
        if (args.Contains("--diskread"))
        {
            foreach (string directory in Directory.GetDirectories(root))
                DiskRead.Measure(directory);
            return 0;
        }

        (string Name, int Width, int Height)[] sets =
        [
            ("4k_hapq", 3840, 2160),
            ("4k_hap1", 3840, 2160),
            ("1080_hapq", 1920, 1080),
            ("1080_hap1", 1920, 1080),
            // 実写（Snappy が効くので、展開時間の最悪ケースはこちら）。ノイズ素材は帯域の最悪ケース。
            ("4k_hapq_real", 3840, 2160),
            ("1080_hapq_real", 1920, 1080),
        ];

        using var gpu = new Gpu();
        Console.WriteLine($"GPU: {gpu.AdapterName}");
        Console.WriteLine($"素材: {root} / 1 条件 {seconds} 秒 / 目標 {TargetFps} fps");
        Console.WriteLine();
        Console.WriteLine("素材        経路        アップロード 展開    展開ms(中央/最大) 上げms(中央/最大) 1コマms(中央/最大) 落ちコマ 実fps");

        var results = new List<Result>();
        foreach ((string name, int width, int height) in sets)
        {
            if (only is not null && only != name) continue;
            string directory = Path.Combine(root, name);
            if (!Directory.Exists(directory)) { Console.WriteLine($"{name}: コマが無いので飛ばす"); continue; }
            HapFrameSet set = HapFrameSet.Load(directory, name, width, height);
            bool yCoCg = set.Format == HapTextureFormat.YCoCgDxt5;
            HapFrame first = set.Frames[0];
            string compressors = string.Join(",", first.Chunks.Select(c => c.Compressor switch
            {
                0xA => "無圧縮", 0xB => "Snappy", _ => $"0x{c.Compressor:X}",
            }).Distinct());
            Console.WriteLine($"  [{name}] 形式 {set.Format} / かたまり {first.Chunks.Count} 個 / 二段目 {compressors} / " +
                $"圧縮後 {first.Data.Length / 1024.0 / 1024.0:F2} MB → 展開後 {set.DecompressedLength / 1024.0 / 1024.0:F2} MB");
            if (dump is not null)
            {
                var buffer = new byte[set.DecompressedLength];
                foreach (SourcePath path in new[] { SourcePath.DecodeToBgra, SourcePath.CompressedPassthrough })
                {
                    gpu.PrepareFor(set, path);
                    set.Frames[0].Decompress(buffer, parallel: false);
                    gpu.Upload(buffer, set.DecompressedLength, UploadMethod.Direct, set.Format);
                    if (path == SourcePath.DecodeToBgra) gpu.DecodePass(yCoCg);
                    gpu.Compose(path, yCoCg);
                    string file = Path.Combine(dump, $"{name}_{path}.png");
                    gpu.SaveBackBuffer(file);   // 表示の前に読み戻す（FlipDiscard は Present 後の中身を保証しない）
                    gpu.Present();
                    Console.WriteLine($"  書き出し: {file}");
                }
                continue;
            }
            foreach (SourcePath path in new[] { SourcePath.DecodeToBgra, SourcePath.CompressedPassthrough })
            {
                gpu.PrepareFor(set, path);
                foreach (UploadMethod upload in new[] { UploadMethod.Direct, UploadMethod.Staging })
                {
                    foreach (bool parallel in new[] { false, true })
                    {
                        Result result = Measure(gpu, set, path, upload, parallel, yCoCg, seconds);
                        results.Add(result);
                        Console.WriteLine(result);
                    }
                }
            }
        }

        Console.WriteLine();
        Report(results);
        return 0;
    }

    private static Result Measure(Gpu gpu, HapFrameSet set, SourcePath path, UploadMethod upload, bool parallel, bool yCoCg, int seconds)
    {
        var buffer = new byte[set.DecompressedLength];
        int frameCount = (int)(TargetFps * seconds);
        var decompressMs = new double[frameCount];
        var uploadMs = new double[frameCount];
        var frameMs = new double[frameCount];
        long frequency = Stopwatch.Frequency;
        double periodTicks = frequency / TargetFps;

        // 1 回空回しして、シェーダ・テクスチャの初回コストを測定から外す。
        RunFrame(gpu, set, 0, buffer, path, upload, parallel, yCoCg);

        long start = Stopwatch.GetTimestamp();
        int late = 0;
        for (int i = 0; i < frameCount; i++)
        {
            long deadline = start + (long)(periodTicks * (i + 1));
            long frameStart = Stopwatch.GetTimestamp();
            (double decompress, double up) = RunFrame(gpu, set, i % set.Frames.Count, buffer, path, upload, parallel, yCoCg);
            long frameEnd = Stopwatch.GetTimestamp();
            decompressMs[i] = decompress;
            uploadMs[i] = up;
            frameMs[i] = (frameEnd - frameStart) * 1000.0 / frequency;
            if (frameEnd > deadline) late++;
            while (Stopwatch.GetTimestamp() < deadline) Thread.SpinWait(50);
            gpu.PumpMessages();
        }
        long total = Stopwatch.GetTimestamp() - start;
        double achievedFps = frameCount / ((double)total / frequency);
        return new Result(set.Name, path, upload, parallel, Median(decompressMs), Max(decompressMs),
            Median(uploadMs), Max(uploadMs), Median(frameMs), Max(frameMs), late, achievedFps);
    }

    private static (double DecompressMs, double UploadMs) RunFrame(Gpu gpu, HapFrameSet set, int index, byte[] buffer,
        SourcePath path, UploadMethod upload, bool parallel, bool yCoCg)
    {
        long frequency = Stopwatch.Frequency;
        long t0 = Stopwatch.GetTimestamp();
        set.Frames[index].Decompress(buffer, parallel);
        long t1 = Stopwatch.GetTimestamp();
        gpu.Upload(buffer, set.DecompressedLength, upload, set.Format);
        long t2 = Stopwatch.GetTimestamp();
        if (path == SourcePath.DecodeToBgra) gpu.DecodePass(yCoCg);
        gpu.ComposeAndPresent(path, yCoCg);
        return ((t1 - t0) * 1000.0 / frequency, (t2 - t1) * 1000.0 / frequency);
    }

    private static void Report(IReadOnlyList<Result> results)
    {
        if (results.Count == 0) return;
        Console.WriteLine("合格条件（1 コマの展開＋上げが中央 8ms 以下・最大 12ms 以下、落ちコマ 0）に対する判定:");
        foreach (Result r in results)
        {
            double medianWork = r.DecompressMedian + r.UploadMedian;
            double maxWork = r.DecompressMax + r.UploadMax;
            bool ok = medianWork <= 8.0 && maxWork <= 12.0 && r.LateFrames == 0;
            Console.WriteLine($"  {(ok ? "合格" : "不足")}  {r.Set,-10} {r.Path,-22} {r.Upload,-8} {(r.Parallel ? "並列" : "単体")}  " +
                $"中央 {medianWork:F2}ms / 最大 {maxWork:F2}ms / 落ちコマ {r.LateFrames}");
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static double Median(double[] values)
    {
        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private static double Max(double[] values) => values.Length == 0 ? 0 : values.Max();

    private sealed record Result(string Set, SourcePath Path, UploadMethod Upload, bool Parallel,
        double DecompressMedian, double DecompressMax, double UploadMedian, double UploadMax,
        double FrameMedian, double FrameMax, int LateFrames, double AchievedFps)
    {
        public override string ToString() =>
            $"{Set,-11} {Path,-22} {Upload,-9} {(Parallel ? "並列" : "単体"),-6} " +
            $"{DecompressMedian,6:F2}/{DecompressMax,6:F2} {UploadMedian,7:F2}/{UploadMax,6:F2} " +
            $"{FrameMedian,7:F2}/{FrameMax,6:F2} {LateFrames,7} {AchievedFps,6:F1}";
    }
}
