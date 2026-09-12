// V2（音声）の計測ツール: 既定の再生デバイスを WASAPI ループバックで録音し、
// 窓ごとの RMS / ピーク（dBFS）を CSV に書く。
//
//   AudioLoopbackProbe <seconds> <out.csv> [windowMs=100]
//
// 出力: t_ms,rms_dbfs,peak_dbfs（t_ms は録音開始からの窓の先頭）
// 終了時に標準出力へ要約（無音床・最大・窓数）を出す。
// システム全体のミックスを拾うので、計測中は他の音を鳴らさないこと。
using System.Globalization;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: AudioLoopbackProbe <seconds> <out.csv> [windowMs=100]");
    return 2;
}

if (!double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds <= 0)
{
    Console.Error.WriteLine($"seconds が不正: {args[0]}");
    return 2;
}

string outPath = args[1];
int windowMs = args.Length > 2 && int.TryParse(args[2], out int w) && w > 0 ? w : 100;

using var enumerator = new MMDeviceEnumerator();
MMDevice device;
try
{
    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"既定の再生デバイスが取得できない: {ex.Message}");
    return 3;
}

using (device)
using (var capture = new WasapiLoopbackCapture(device))
{
    WaveFormat fmt = capture.WaveFormat;
    int channels = fmt.Channels;
    int sampleRate = fmt.SampleRate;
    Console.WriteLine($"device={device.FriendlyName} format={fmt.Encoding} {sampleRate}Hz {fmt.BitsPerSample}bit ch={channels} window={windowMs}ms");

    // 窓ごとの集計。1 窓 = windowMs 分のフレーム数。
    int framesPerWindow = Math.Max(1, sampleRate * windowMs / 1000);
    var rows = new List<(long TMs, double Rms, double Peak)>();
    double sumSq = 0;
    double peak = 0;
    int framesInWindow = 0;
    long windowIndex = 0;
    var gate = new object();

    capture.DataAvailable += (_, e) =>
    {
        // 32bit float / 16bit PCM のどちらでも読めるようにする。
        int bytesPerSample = fmt.BitsPerSample / 8;
        int frameBytes = bytesPerSample * channels;
        if (frameBytes == 0) return;
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
            || (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32);

        lock (gate)
        {
            for (int off = 0; off + frameBytes <= e.BytesRecorded; off += frameBytes)
            {
                // フレーム内のチャンネルを平均してモノラル化する。
                double acc = 0;
                for (int c = 0; c < channels; c++)
                {
                    int p = off + c * bytesPerSample;
                    double v = isFloat
                        ? BitConverter.ToSingle(e.Buffer, p)
                        : BitConverter.ToInt16(e.Buffer, p) / 32768.0;
                    acc += v;
                }

                double s = acc / channels;
                sumSq += s * s;
                double a = Math.Abs(s);
                if (a > peak) peak = a;
                framesInWindow++;

                if (framesInWindow >= framesPerWindow)
                {
                    double rms = Math.Sqrt(sumSq / framesInWindow);
                    rows.Add((windowIndex * windowMs, ToDbfs(rms), ToDbfs(peak)));
                    windowIndex++;
                    sumSq = 0;
                    peak = 0;
                    framesInWindow = 0;
                }
            }
        }
    };

    var stopped = new TaskCompletionSource();
    capture.RecordingStopped += (_, e) =>
    {
        if (e.Exception != null) Console.Error.WriteLine($"録音が異常終了: {e.Exception.Message}");
        stopped.TrySetResult();
    };

    capture.StartRecording();
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    capture.StopRecording();
    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

    (long TMs, double Rms, double Peak)[] snapshot;
    lock (gate) snapshot = rows.ToArray();

    var sb = new StringBuilder("t_ms,rms_dbfs,peak_dbfs\n");
    foreach ((long t, double rms, double pk) in snapshot)
        sb.Append(t).Append(',')
          .Append(rms.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
          .Append(pk.ToString("F2", CultureInfo.InvariantCulture)).Append('\n');

    string? dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));

    if (snapshot.Length == 0)
    {
        Console.Error.WriteLine("窓が 1 つも取れなかった（録音が始まっていない可能性）");
        return 4;
    }

    double[] sorted = snapshot.Select(r => r.Rms).OrderBy(v => v).ToArray();
    // 無音床は下位 5% の中央値、再生水準は上位 5% の中央値で見る。
    double floorDb = Median(sorted.Take(Math.Max(1, sorted.Length / 20)).ToArray());
    double loudDb = Median(sorted.Skip(sorted.Length - Math.Max(1, sorted.Length / 20)).ToArray());
    Console.WriteLine($"windows={snapshot.Length} floor_dbfs={floorDb:F2} loud_dbfs={loudDb:F2} span_db={loudDb - floorDb:F2}");
    Console.WriteLine($"csv={Path.GetFullPath(outPath)}");
}

return 0;

static double ToDbfs(double amplitude) =>
    amplitude <= 1e-10 ? -200.0 : 20.0 * Math.Log10(amplitude);

static double Median(double[] values)
{
    if (values.Length == 0) return -200.0;
    double[] s = (double[])values.Clone();
    Array.Sort(s);
    int mid = s.Length / 2;
    return s.Length % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2.0;
}
