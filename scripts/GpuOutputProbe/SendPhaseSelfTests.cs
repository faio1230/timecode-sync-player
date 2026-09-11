using System.Globalization;

namespace GpuOutputProbe;

internal static class SendPhaseSelfTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Expected invalid phase to be rejected."); }
    private static Options Split(string phase, string fps = "60") => Options.Parse(["--mode", "split", "--output", "both", "--send-phase-ms", phase, "--fps", fps]);

    public static void Validation()
    {
        Check(Options.Parse([]).SendPhaseMs == 0, "Default phase must be zero.");
        foreach (string phase in new[] { "0", "1", "2", "4", "0.125" })
            Check(Split(phase).SendPhaseMs == double.Parse(phase, CultureInfo.InvariantCulture), "Valid phase not preserved.");
        foreach (string invalid in new[] { "NaN", "Infinity", "-Infinity", "-0.1", "17" }) Reject(() => Split(invalid));
        Reject(() => Split((1000.0 / 60).ToString("R", CultureInfo.InvariantCulture)));
        Check(Split("16.68", "59.94").SendPhaseMs == 16.68, "Fractional frame period must use configured fps.");
        Reject(() => Split("16.69", "59.94"));
        Reject(() => Options.Parse(["--send-phase-ms", "1"]));
        Reject(() => Options.Parse(["--mode", "split", "--output", "fullscreen", "--send-phase-ms", "1"]));
        Check(Options.Parse(["--mode", "common", "--output", "fullscreen", "--send-phase-ms", "0"]).SendPhaseMs == 0, "Explicit zero must remain valid for all outputs.");
        Check(Options.Parse(["--mode", "split", "--output", "spout", "--send-phase-ms", "4"]).SendPhaseMs == 4, "Spout-only split should allow phase.");
        Reject(() => Options.Parse(["--send-phase-ms", "0", "--send-phase-ms", "0"]));
    }

    public static void ZeroCompatibility()
    {
        var options = Split("0", "59.94");
        const long origin = 1234567, frequency = 10_000_000;
        var gpu = WorkerTiming.Create(options, origin, frequency, false);
        var send = WorkerTiming.Create(options, origin, frequency, true);
        Check(gpu == send && send.OriginQpc == origin, "Phase zero changed worker epoch/end.");
        var original = new TickSchedule(origin, options.Fps, frequency);
        var candidate = new TickSchedule(send.OriginQpc, options.Fps, frequency);
        for (int i = 0; i < 250; i++)
        {
            long now = original.DueQpc + (i % 7 == 0 ? frequency / 10 : 0);
            Check(original.Take(now) == candidate.Take(now) && original.DueQpc == candidate.DueQpc, "Phase zero changed late-skip/deadline behavior.");
        }
    }

    public static void FractionalOriginAndDeadline()
    {
        var options = Split("2.25", "59.94");
        const long origin = 1234567, frequency = 1_000_000;
        var gpu = WorkerTiming.Create(options, origin, frequency, false);
        var send = WorkerTiming.Create(options, origin, frequency, true);
        Check(gpu.OriginQpc == origin && send.OriginQpc == origin + 2250 && gpu.EndQpc == send.EndQpc, "Phase shifted composition or run duration.");
        var schedule = new TickSchedule(send.OriginQpc, options.Fps, frequency);
        bool roundingExercised = false;
        for (int i = 0; i < 250; i++)
        {
            long expected = send.OriginQpc + (long)Math.Round(i * frequency / options.Fps);
            Check(schedule.DueQpc == expected, "Shifted fractional origin drifted.");
            var tick = schedule.Take(expected);
            long next = send.OriginQpc + (long)Math.Round((i + 1) * frequency / options.Fps);
            Check(schedule.DueQpc == next && tick.Skipped == 0, "True next fractional deadline lost.");
            if (next != tick.Scheduled + (long)Math.Round(frequency / options.Fps)) roundingExercised = true;
            Check(MutexWaitPolicy.TimeoutMs(4, next - 1999, next, frequency) == 1, "Existing wait clamp failed on shifted deadline.");
        }
        Check(roundingExercised, "Test did not cover rounded-period mismatch.");
        var rounded = WorkerTiming.Create(Split("0.1234567"), origin, frequency, true);
        Check(rounded.OriginQpc == origin + 123, "Phase tick conversion does not round to QPC units.");
    }

    public static void LateCancellationAndSharedEnd()
    {
        var options = Split("4") with { Seconds = 1, Warmup = 0 };
        const long origin = 500, frequency = 1_000_000;
        var send = WorkerTiming.Create(options, origin, frequency, true);
        var gpu = WorkerTiming.Create(options, origin, frequency, false);
        var schedule = new TickSchedule(send.OriginQpc, options.Fps, frequency);
        _ = schedule.Take(schedule.DueQpc);
        long late = send.OriginQpc + 71_000;
        var tick = schedule.Take(late);
        Check(tick.Skipped == 3 && schedule.DueQpc > late, "Phase caused catch-up bursts after a late tick.");
        long nextBeforeCancel = schedule.DueQpc;
        Check(!send.AllowsWork(late, true) && schedule.DueQpc == nextBeforeCancel, "Cancellation should reject work without advancing the schedule.");
        Check(send.EndQpc == origin + frequency && send.EndQpc == gpu.EndQpc, "Sender phase extended the trial duration.");
        Check(send.AllowsWork(send.EndQpc - 1, false) && !send.AllowsWork(send.EndQpc, false) && !send.AllowsWork(send.EndQpc + 1, false), "Shared end boundary is not exclusive.");
        Check(send.WakeQpc(send.EndQpc + 4000) == send.EndQpc, "Shifted next tick delayed stopping past the common end.");
        Check(send.WakeQpc(send.EndQpc - 4000) == send.EndQpc - 4000, "Earlier scheduled tick was delayed to the end.");
    }
}
