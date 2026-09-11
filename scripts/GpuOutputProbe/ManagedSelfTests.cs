using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GpuOutputProbe;

// No WPF Application, DXGI enumeration, D3D device, or Spout call is made by these tests.
internal static class ManagedSelfTests
{
    public static int Run()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("options accept explicit valid configuration", ValidOptions),
            ("options reject invalid rate/duration/dimensions/sender", InvalidOptions),
            ("options reject unknown, missing, and duplicate switches", InvalidSwitches),
            ("schedule skips late ticks without catch-up", ScheduleSkips),
            ("schedule preserves fractional-rate future deadlines", FractionalSchedule),
            ("latest replacement skips stale images and retains borrowed pixels", LatestReplacement),
            ("GPU completion guards publication, abort, and lease return", GpuCompletionGuards),
            ("pool refuses reuse while all slots are occupied", PoolExhaustion),
            ("multiple readers retain a replaced slot until final release", MultipleReaders),
            ("concurrent latest readers preserve stamp and pool lifetime", ConcurrentReaders),
            ("missing required output invalidates performance result", MissingOutputInvalidation),
            ("failed or interrupted run is not a valid performance result", FailedRunInvalidation),
            ("log files cannot overwrite existing evidence", LogOverwriteRejected),
            ("mutex budget parses and clamps to remaining whole milliseconds", MutexWaitSelfTests.OptionsAndClamping),
            ("eight millisecond request obeys fractional, high-rate, and exact-deadline limits", MutexWaitSelfTests.EightMillisecondBudget),
            ("mutex uses true fractional scheduler deadline", MutexWaitSelfTests.FractionalDeadline),
            ("stale and cancelled work never acquires mutex", MutexWaitSelfTests.StaleAndCancelledBeforeWait),
            ("late and cancelled acquisitions release without sending", MutexWaitSelfTests.LateAndCancelledAcquisition),
            ("OS oversleep before deadline is allowed and busy acquisition does not retry", MutexWaitSelfTests.OversleepBeforeDeadlineAndBusy),
            ("abandoned and exceptional acquisitions release exactly once", MutexWaitSelfTests.AbandonedAndExceptionalOwnership),
            ("send phase validates scope, finite values, and strict period bound", SendPhaseSelfTests.Validation),
            ("zero send phase preserves original schedule and late skips", SendPhaseSelfTests.ZeroCompatibility),
            ("send phase shifts only sender origin and preserves fractional next deadline", SendPhaseSelfTests.FractionalOriginAndDeadline),
            ("shifted schedule preserves late skip, cancellation, and common end", SendPhaseSelfTests.LateCancellationAndSharedEnd),
            ("presentation request validates scope and remaining budget", PresentReadySelfTests.ValidationAndBudget),
            ("presentation waits once and records native wait errors", PresentReadySelfTests.SingleWaitAndErrorEvidence),
            ("late ready signal survives until newest image is presented", PresentReadySelfTests.LateSignalRetainedForNewestImage),
            ("cancelled and expired drawing preserves readiness permission", PresentReadySelfTests.CancellationAndDrawDeadlineRetention),
            ("expired readiness never waits and OS oversleep before deadline is allowed", PresentReadySelfTests.StaleBeforeWaitAndOversleep),
            ("presentation wait plans validate options and preserve fixed mode", PresentWaitPlanSelfTests.ValidationAndFixedCompatibility),
            ("presentation wait plans notify half-open and equal-valued segment boundaries", PresentWaitPlanSelfTests.BoundariesAndEqualNeighbors),
            ("presentation wait plan selection follows late scheduled ticks and fractional boundaries", PresentWaitPlanSelfTests.LateSkipAndFractionalSchedule),
            ("ready permission survives a presentation wait segment switch", PresentWaitPlanSelfTests.ReadyPermissionSurvivesSwitch),
            ("ready pacing validates its bounded scope and wait budget", DisplayWaitSelfTests.OptionsAndBudget),
            ("immediate and mid-tick notifications permit one display attempt", DisplayWaitSelfTests.ImmediateAndMidTickNotification),
            ("early display timeout cannot retry the same output slot", DisplayWaitSelfTests.EarlyTimeoutDoesNotRetry),
            ("late display notification preserves permission without holding source image", DisplayWaitSelfTests.LateGrantAndNewestLease),
            ("display notification stop and error paths preserve ownership evidence", DisplayWaitSelfTests.StopAndError),
            ("process CPU summary identifies whole-run measurement scope", DisplayWaitSelfTests.CpuSummaryScope),
            ("copy retry budget floors remaining whole milliseconds and caps at four", CopyRetrySelfTests.BudgetClamping),
            ("copy retry never budgets expired work or invalid frequency", CopyRetrySelfTests.ExpiredAndInvalid),
            ("fence source sync requires split+Spout and excludes signal copy retry", FenceVsyncSelfTests.SourceSyncOptions),
            ("vsync display pacing requires display output with fixed zero present wait", FenceVsyncSelfTests.VsyncOptions),
            ("fence values must strictly increase", FenceVsyncSelfTests.FenceValueMonotonic),
            ("vsync never waits on readiness without a newer image", FenceVsyncSelfTests.VsyncNoNewerImageNeverWaits),
            ("vsync presents at most once per readiness notification", FenceVsyncSelfTests.VsyncOnePresentPerNotification),
            ("vsync compose deadline wins and a late grant carries to the next slot", FenceVsyncSelfTests.VsyncComposePriority),
            ("vsync waits at most once per compose slot", FenceVsyncSelfTests.VsyncOneWaitPerSlotAndTimeout),
            ("vsync stop wins and wait errors are recorded before faulting", FenceVsyncSelfTests.VsyncStopPriorityAndError),
            ("scanout statistics advance once per PresentCount and suppress duplicates", ScanoutSelfTests.AdvanceAndDuplicateSuppression),
            ("scanout PresentCount jump reports the latest and leaves skipped presents pending", ScanoutSelfTests.JumpLeavesSkippedPending),
            ("scanout unknown or evicted PresentCount is unmapped", ScanoutSelfTests.UnmappedAndCapacity),
            ("scanout disjoint statistics are recorded once", ScanoutSelfTests.DisjointRecordedOnce),
            ("vblank display pacing validates scope and present margin", VblankSelfTests.VblankOptions),
            ("vblank gate validates margin and falls back to 60 Hz before statistics", VblankSelfTests.MarginAndPeriodDefaults),
            ("vblank prediction selects k and re-predicts on late arrival", VblankSelfTests.PredictionAndLateArrival),
            ("vblank period is the median per-refresh difference after a jump", VblankSelfTests.MedianPeriodAfterJump),
            ("vblank presents at most once per predicted vblank and compose slot", VblankSelfTests.OnePresentPerPredictedVblank),
            ("vblank de-duplicates by vblank time and guards presents by the predicted vblank", VblankSelfTests.TimeDedupeAndPresentDeadline),
            ("vblank compose deadline wins and waits record request and lateness", VblankSelfTests.ComposePriorityAndWaitRecording),
            ("vblank stop wins and wait errors are recorded before faulting", VblankSelfTests.StopPriorityAndError),
            ("vblank bootstraps until the first scanout then predicts", VblankSelfTests.BootstrapToNormalTransition),
            ("vblank idles without a newer image and defers an unsignaled attempt", VblankSelfTests.IdleWithoutNewerImageAndDefer),
            ("vblank passed target with a reachable vblank presents before a due compose", VblankSelfTests.PassedTargetPresentsBeforeDueCompose),
            ("vblank waitable timer reaches its deadline and stop wins", VblankSelfTests.WaitableTimerReachesDeadlineAndStopWins),
            ("loop idle wait yields at or below 50 us and uses the timer above it", VblankSelfTests.LoopIdleWaitThreshold),
            ("compose align validates scope and lead", ComposeAlignSelfTests.AlignOptions),
            ("compose align wraps the phase error and clamps the correction to slew", ComposeAlignSelfTests.WrapAndClamp),
            ("compose align never corrects before the first scanout", ComposeAlignSelfTests.NoCorrectionBeforeScanout),
            ("compose align converges from a half-period error within slew steps", ComposeAlignSelfTests.ConvergesFromHalfPeriodError),
            ("offset schedule never hands out an earlier or not-due tick", ComposeAlignSelfTests.OffsetScheduleNeverRegresses),
            ("Spout schedule keeps its phase under the shared offset", ComposeAlignSelfTests.SpoutKeepsPhaseUnderSharedOffset),
            ("source option accepts pattern and contract-fake only", VideoSourceSelfTests.SourceOptions),
            ("source generation switch retires older images and refuses mismatched requests", VideoSourceSelfTests.GenerationRejection),
            ("source returns the latest image at or before the position, else the next one", VideoSourceSelfTests.LatestPreferredSelection),
            ("source reports Ended only after the end position with no later image", VideoSourceSelfTests.EndedSemantics),
            ("full source ring replaces the oldest un-leased image and drops when all are leased", VideoSourceSelfTests.FullRingReplacementAndDrop),
            ("source lease disposes once and guards GPU use", VideoSourceSelfTests.LeaseGuards),
            ("source dispose waits for outstanding leases without forcing", VideoSourceSelfTests.DisposeWaitsForLeases),
            ("source offers from another thread interleave safely with acquires", VideoSourceSelfTests.OfferFromAnotherThread),
            ("fake source produces one image per source period from the tick clock", VideoSourceSelfTests.FakeSourceCadence),
            ("placement of a 16:9 source on a 16:9 canvas is the identity", CanvasPlacementSelfTests.Identity),
            ("placement of a 4:3 source bars left/right under fit-height and crops top/bottom under fit-width", CanvasPlacementSelfTests.FourByThree),
            ("placement of a 21:9 source crops left/right under fit-height and bars top/bottom under fit-width", CanvasPlacementSelfTests.TwentyOneByNine),
            ("placement of a portrait source and a portrait canvas", CanvasPlacementSelfTests.Portrait),
            ("placement of a 1x1 source", CanvasPlacementSelfTests.OnePixel),
            ("placement of sources larger than the canvas", CanvasPlacementSelfTests.LargerThanCanvas),
            ("fit registry inherits the default for null and falls back with a warning for unknown ids", CanvasPlacementSelfTests.RegistryFallback),
            ("placement rejects non-positive sizes", CanvasPlacementSelfTests.InvalidSizes),
            ("source size option defaults to the canvas and requires contract-fake otherwise", CanvasPlacementSelfTests.SourceSizeOption)
        };
        int failed = 0;
        foreach (var (name, test) in tests)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }
        Console.WriteLine($"Managed self-tests: {tests.Length - failed} passed, {failed} failed. No GPU/native graphics initialized.");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static Options Parse(params string[] args) => Options.Parse(args);

    private static void ValidOptions()
    {
        var o = Parse("--mode", "split", "--output", "spout", "--width", "3840", "--height", "2160", "--fps", "59.94", "--seconds", "32", "--warmup", "5", "--sender", "Managed-Test", "--monitor-index", "2", "--windowed");
        Check(o.Mode == "split" && o.HasSpout && !o.HasDisplay && o.Width == 3840 && o.Height == 2160 && o.Fps == 59.94 && o.MonitorIndex == 2 && o.Windowed, "Parsed configuration differs.");
        Check(Parse("--output", "fullscreen").HasDisplay && !Parse("--output", "fullscreen").HasSpout, "Output routing differs.");
    }

    private static void InvalidOptions()
    {
        foreach (var pair in new[] { ("--mode", "other"), ("--output", "other"), ("--width", "0"), ("--height", "8193"), ("--fps", "NaN"), ("--fps", "Infinity"), ("--fps", "0"), ("--seconds", "0"), ("--seconds", "NaN"), ("--warmup", "-1"), ("--warmup", "16"), ("--monitor-index", "-1"), ("--sender", "bad/name"), ("--sender", "日本語"), ("--sender", "") })
            Throws<ArgumentException>(() => Parse(pair.Item1, pair.Item2));
    }

    private static void InvalidSwitches()
    {
        Throws<ArgumentException>(() => Parse("--unknown", "1"));
        Throws<ArgumentException>(() => Parse("--fps"));
        Throws<ArgumentException>(() => Parse("--fps", "60", "--fps", "30"));
    }

    private static void ScheduleSkips()
    {
        var s = new TickSchedule(1000, 60, 6000);
        Throws<InvalidOperationException>(() => s.Take(999));
        Check(s.Take(1000) == (1000L, 0L), "First tick differs.");
        Check(s.Take(1450) == (1400L, 3L), "Late tick must skip 1100, 1200, 1300.");
        Check(s.DueQpc == 1500, "Next deadline must be in the future.");
        Throws<InvalidOperationException>(() => s.Take(1450));
        Check(s.Take(1500) == (1500L, 0L), "Next tick accumulated catch-up work.");
    }

    private static void FractionalSchedule()
    {
        var s = new TickSchedule(500, 59.94, 10_000_000);
        for (int i = 0; i < 1000; i++)
        {
            long due = s.DueQpc;
            var result = s.Take(due);
            Check(result.Scheduled == due && result.Skipped == 0 && s.DueQpc > due, "Fractional-rate schedule drifted or caught up.");
        }
        long late = s.DueQpc + 10_000_000;
        Check(s.Take(late).Skipped >= 59 && s.DueQpc > late, "Late fractional schedule did not advance past now.");
    }

    private static int Publish(LatestPool pool, long id)
    {
        int slot = pool.TryBeginWrite();
        Check(slot >= 0, "No slot for test publication.");
        pool.Publish(slot, new ImageStamp(id, id * 17), true);
        return slot;
    }

    private static void LatestReplacement()
    {
        var pool = new LatestPool(3);
        Check(pool.AcquireLatest() == null, "Uninitialized pool exposed an image.");
        int first = Publish(pool, 1);
        using var held = pool.AcquireLatest()!;
        Publish(pool, 2); Publish(pool, 3);
        using var latest = pool.AcquireLatest()!;
        Check(held.Slot == first && held.Stamp.Id == 1 && latest.Stamp.Id == 3, "Replacement changed an existing lease or queued an old image.");
        int spare = pool.TryBeginWrite();
        Check(spare != first && spare != latest.Slot, "Writer reused a held/latest image.");
        pool.AbortWrite(spare, true);
    }

    private static void GpuCompletionGuards()
    {
        var pool = new LatestPool(3);
        int slot = pool.TryBeginWrite();
        Throws<InvalidOperationException>(() => pool.Publish(slot, new ImageStamp(1, 17), false));
        Check(pool.AcquireLatest() == null, "Incomplete publication became visible.");
        Throws<InvalidOperationException>(() => pool.AbortWrite(slot, false));
        pool.Publish(slot, new ImageStamp(1, 17), true);
        var lease = pool.AcquireLatest()!;
        Throws<InvalidOperationException>(() => lease.CompleteGpuUse());
        lease.BeginGpuUse();
        Throws<InvalidOperationException>(() => lease.BeginGpuUse());
        Throws<InvalidOperationException>(() => lease.Dispose());
        lease.CompleteGpuUse(); lease.Dispose(); lease.Dispose();
        Throws<InvalidOperationException>(() => lease.BeginGpuUse());
    }

    private static void PoolExhaustion()
    {
        var pool = new LatestPool(3);
        int first = Publish(pool, 1);
        var one = pool.AcquireLatest()!;
        Publish(pool, 2);
        using var two = pool.AcquireLatest()!;
        Publish(pool, 3);
        Check(pool.TryBeginWrite() == -1, "Pool should skip production with three occupied slots.");
        one.Dispose();
        int free = pool.TryBeginWrite();
        Check(free == first, "Released obsolete slot was not reusable.");
        pool.AbortWrite(free, true);
    }

    private static void MultipleReaders()
    {
        var pool = new LatestPool(2);
        int first = Publish(pool, 1);
        var a = pool.AcquireLatest()!; var b = pool.AcquireLatest()!;
        Publish(pool, 2); a.Dispose();
        Check(pool.TryBeginWrite() == -1, "First lease release prematurely freed a shared image.");
        b.Dispose();
        int free = pool.TryBeginWrite();
        Check(free == first, "Last lease did not free replaced image.");
        pool.AbortWrite(free, true);
    }

    private static void ConcurrentReaders()
    {
        var pool = new LatestPool(3);
        Publish(pool, 1);
        var errors = new ConcurrentQueue<Exception>();
        using var start = new ManualResetEventSlim(false);
        int finished = 0, reads = 0;
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < 5000 && (i == 0 || Volatile.Read(ref finished) == 0); i++)
                {
                    using var lease = pool.AcquireLatest()!;
                    var stamp = lease.Stamp;
                    lease.BeginGpuUse(); Thread.Yield();
                    Check(lease.Stamp == stamp && stamp.GeneratedQpc == stamp.Id * 17, "Torn or mutated image metadata.");
                    lease.CompleteGpuUse();
                    Interlocked.Increment(ref reads);
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();
        var writer = Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (long id = 2; id < 5000; id++)
                {
                    int slot = pool.TryBeginWrite();
                    if (slot >= 0) pool.Publish(slot, new ImageStamp(id, id * 17), true);
                    Thread.Yield();
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
            finally { Volatile.Write(ref finished, 1); }
        });
        start.Set();
        Check(Task.WaitAll(readers.Append(writer).ToArray(), TimeSpan.FromSeconds(10)), "Concurrent ownership test timed out.");
        if (!errors.IsEmpty) throw new AggregateException(errors);
        Check(reads > 0 && pool.PeakOccupied <= 3, "No reader progress or pool exceeded capacity.");
    }

    private static string NewTestDirectory()
    {
        string path = Path.GetFullPath(Path.Combine("TestResults", "gpu-output-managed-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool SaveAndReadValidity(string output, string outcome, params string[] stages)
    {
        var o = Parse("--output", output, "--seconds", "2", "--warmup", "0") with { LogDir = NewTestDirectory() };
        const long origin = 1_000_000;
        var log = new ProbeLog { OriginQpc = origin };
        foreach (string stage in stages)
            log.Record(new ProbeEvent(stage, "managed-test", origin + Stopwatch.Frequency / 2, ImageId: 1, GeneratedQpc: origin + Stopwatch.Frequency / 3));
        log.Record(new ProbeEvent("run.complete", "managed-test", origin + Stopwatch.Frequency * 2));
        log.Save(o, outcome, new LatestPool(3));
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(o.LogDir, "summary.json")));
        return summary.RootElement.GetProperty("validPerformanceResult").GetBoolean();
    }

    private static void MissingOutputInvalidation()
    {
        Check(!SaveAndReadValidity("both", "completed", "compose.publish", "present.return"), "Missing Spout output was accepted.");
        Check(!SaveAndReadValidity("both", "completed", "compose.publish", "send.publish"), "Missing fullscreen output was accepted.");
        Check(!SaveAndReadValidity("both", "completed", "present.return", "send.publish"), "Missing composition was accepted.");
        Check(SaveAndReadValidity("fullscreen", "completed", "compose.publish", "present.return"), "Fullscreen-only result required Spout.");
        Check(SaveAndReadValidity("spout", "completed", "compose.publish", "send.publish"), "Spout-only result required fullscreen.");
    }

    private static void FailedRunInvalidation()
    {
        string[] stages = ["compose.publish", "present.return", "send.publish"];
        Check(!SaveAndReadValidity("both", "failed", stages), "Failed run was accepted.");
        Check(!SaveAndReadValidity("both", "cancelled", stages), "Cancelled run was accepted.");
        Check(!SaveAndReadValidity("both", "completed", stages.Append("error").ToArray()), "Run with error was accepted.");
    }

    private static void LogOverwriteRejected()
    {
        string path = Path.Combine(NewTestDirectory(), "sentinel.json");
        ProbeLog.WriteNew(path, new { sentinel = true });
        string original = File.ReadAllText(path);
        Throws<IOException>(() => ProbeLog.WriteNew(path, new { sentinel = false }));
        Check(File.ReadAllText(path) == original, "An existing evidence file changed.");
    }
}
