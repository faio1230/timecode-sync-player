using System;
using System.Collections.Generic;
using System.Linq;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests;

public class DirtyLtcSignalTests
{
    [Fact]
    public void NoiseAmplitudeFor_MatchesRequestedSnr()
    {
        const double amplitude = 0.1;
        double noise = DirtyLtcSignal.NoiseAmplitudeFor(12.0, amplitude);
        double noiseRms = noise / Math.Sqrt(3.0);
        Assert.Equal(amplitude * Math.Pow(10.0, -12.0 / 20.0), noiseRms, 6);
    }

    [Fact]
    public void ApplyHum_RmsMatchesRequestedSnr()
    {
        var timecodes = Enumerable.Range(0, 50).Select(i => new LtcTimecode(0, 0, 1, i % 30, false)).ToList();
        var options = new LtcTestSignalGenerator.Options { Amplitude = 1.0f };
        float[] baseSamples = LtcTestSignalGenerator.Generate(timecodes, 25.0, 48000, options);
        var level = new DirtyLevel { HumHz = 50, HumDb = 12 };
        float[] hummed = DirtyLtcSignal.ApplyHum((float[])baseSamples.Clone(), 48000, level, 1.0);
        double sum = 0;
        for (int i = 0; i < baseSamples.Length; i++)
        {
            double difference = hummed[i] - baseSamples[i];
            sum += difference * difference;
        }
        double rms = Math.Sqrt(sum / baseSamples.Length);
        Assert.Equal(Math.Pow(10.0, -12.0 / 20.0), rms, 3);
    }
}
