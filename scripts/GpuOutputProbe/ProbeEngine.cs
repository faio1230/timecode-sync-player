using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Globalization;

namespace GpuOutputProbe;

internal sealed class ProbeEngine(Options options, DisplayInfo display, IReadOnlyList<DisplayInfo> displays, IntPtr videoHwnd, Action<string> status)
{
    private readonly CancellationTokenSource stop = new();
    private readonly ProbeLog log = new();
    private readonly LatestPool pool = new(3);
    private ManualResetEventSlim? copyReleasedSignal;
    private readonly bool fenceSync = options.SourceSync == "fence";
    private readonly ScanoutTracker scanout = new(16); // GPU worker only.
    private readonly VblankDisplayGate? vblank = options.DisplayPacing == "vblank" ? new(options.PresentMarginMs, display.RefreshHz, Stopwatch.Frequency) : null; // GPU worker only.
    private readonly ComposeAlignGate? align = options.ComposeAlign == "vblank" ? new(options.ComposeLeadMs, Stopwatch.Frequency) : null; // GPU worker only.
    private readonly ScheduleOffset scheduleOffset = new(); // Shared by both loops' schedules while aligning; written by the GPU worker only.
    private TickSchedule? composeSchedule; // The GPU loop's schedule (its next due is the aligned quantity).
    private string? firstFault;
    private int failed, finished, requested, started;
    public bool Failed => Volatile.Read(ref failed) != 0;
    public bool Finished => Volatile.Read(ref finished) != 0;
    public void RequestStop() { Interlocked.Exchange(ref requested, 1); stop.Cancel(); status("Stopping acceptance; waiting for GPU and native calls to finish."); }
    private void Fault(string message)
    {
        Interlocked.CompareExchange(ref firstFault, message, null);
        Interlocked.Exchange(ref failed, 1); stop.Cancel(); log.Add("error", Thread.CurrentThread.Name ?? "worker", detail: message); status(message);
    }
    public Task RunAsync()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Already started.");
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { Run(); complete.SetResult(); } catch (Exception e) { complete.SetException(e); } }) { Name = "GPU", IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start(); return complete.Task;
    }
    private void Run()
    {
        using var process = Process.GetCurrentProcess();
        long cpuStartQpc = Stopwatch.GetTimestamp();
        TimeSpan cpuStart = process.TotalProcessorTime;
        GpuDevice? gpu = null; ShaderPipeline? shaders = null; DisplayTarget? target = null; SpoutSender? sender = null; SharedFence? sharedFence = null;
        var surfaces = new List<Surface>();
        var sourceSurfaces = new List<Surface>(); // --source contract-fake: private textures on the compose device, owned by the fake source.
        FakeVideoSource<Surface>? source = null;
        using var senderReady = new ManualResetEventSlim();
        using var copyReleased = new ManualResetEventSlim();
        copyReleasedSignal = copyReleased;
        using var startGate = new ManualResetEventSlim();
        Thread? senderThread = null;
        VblankWaitTimer? vblankTimer = null, gpuLoopTimer = null;
        bool? spoutLoopTimerHighResolution = null; // Written by the Spout worker before senderReady; read by the GPU worker after it.
        string? actualSenderName = null;
        long senderLuid = 0;
        bool timedEnd = false;
        try
        {
            status("Creating GPU resources.");
            gpu = new GpuDevice(display.AdapterLuid, Fault, fenceSync);
            shaders = new ShaderPipeline(gpu);
            if (options.HasDisplay) target = new DisplayTarget(gpu, videoHwnd);
            bool split = options.Mode == "split" && options.HasSpout;
            var sharing = fenceSync ? SourceSharing.FenceNt : split ? SourceSharing.KeyedMutex : SourceSharing.None;
            for (int i = 0; i < 3; i++) surfaces.Add(new Surface(gpu, gpu.Texture(options.Width, options.Height, sharing), true, sharing));
            Surface[] sources = surfaces.ToArray();
            if (options.Source == "contract-fake") for (int i = 0; i < 4; i++) sourceSurfaces.Add(new Surface(gpu, gpu.Texture(options.SourceWidth, options.SourceHeight, SourceSharing.None), true, SourceSharing.None));
            // Placement of the fake source on the canvas (fixed source size, so computed once): project default fit-height, no clip override.
            var canvas = new CanvasSettings(options.Width, options.Height, FitHeight.FitId);
            var placement = options.Source != "contract-fake" ? default : FitRegistry.CreateDefault().Compute(new ClipPlacement(null), canvas, options.SourceWidth, options.SourceHeight, out _, out _);
            if (fenceSync) sharedFence = new SharedFence(gpu);
            if (options.HasSpout)
            {
                if (split)
                {
                    senderThread = new Thread(() =>
                    {
                        GpuDevice? sendGpu = null; SpoutSender? splitSender = null; SharedFenceReader? fenceReader = null; VblankWaitTimer? loopTimer = null;
                        var opened = new List<Surface>();
                        try
                        {
                            sendGpu = new GpuDevice(display.AdapterLuid, Fault, fenceSync);
                            senderLuid = sendGpu.Luid;
                            if (senderLuid != gpu.Luid) throw new InvalidOperationException("Composition and sender adapter LUID mismatch.");
                            foreach (var surface in sources)
                                opened.Add(fenceSync
                                    ? new Surface(sendGpu, sendGpu.Device1.OpenSharedResource1<Vortice.Direct3D11.ID3D11Texture2D>(surface.Handle), false, SourceSharing.None)
                                    : new Surface(sendGpu, sendGpu.Device.OpenSharedResource<Vortice.Direct3D11.ID3D11Texture2D>(surface.Handle), false, SourceSharing.KeyedMutex));
                            if (sharedFence != null) fenceReader = new SharedFenceReader(sendGpu, sharedFence.Open(sendGpu));
                            splitSender = new SpoutSender(sendGpu, options, log, "Spout", copyReleased, fenceReader); actualSenderName = splitSender.ActualName;
                            loopTimer = new VblankWaitTimer(); spoutLoopTimerHighResolution = loopTimer.HighResolution; // Owned by the Spout worker; closed after its loop.
                            senderReady.Set();
                            startGate.Wait();
                            if (stop.IsCancellationRequested) return;
                            Surface[] reads = opened.ToArray();
                            Loop("Spout", loopTimer, (scheduled, nextScheduled) => { splitSender.Update(reads, pool, scheduled, nextScheduled, stop.Token); splitSender.Send(scheduled, nextScheduled, stop.Token); });
                        }
                        catch (Exception e) { Fault(e.ToString()); }
                        finally
                        {
                            senderReady.Set();
                            if (sendGpu != null) Drain(sendGpu, "Spout.shutdown");
                            DisposeOwned(splitSender, "Spout.sender");
                            DisposeOwned(loopTimer, "Spout.loopTimer");
                            foreach (var resource in opened) DisposeOwned(resource, "Spout.shared");
                            DisposeOwned(fenceReader, "Spout.fence");
                            DisposeOwned(sendGpu, "Spout.device");
                            log.Add("lifecycle", "Spout", detail: "worker.finished");
                        }
                    }) { Name = "Spout", IsBackground = true };
                    senderThread.SetApartmentState(ApartmentState.MTA); senderThread.Start();
                    senderReady.Wait();
                    // NT handles are owned: the creator closes them once the sender has opened (or failed to open) its references.
                    if (fenceSync) { foreach (var surface in sources) surface.CloseSharedHandle(); sharedFence!.CloseHandle(); }
                }
                else { sender = new SpoutSender(gpu, options, log, "GPU"); actualSenderName = sender.ActualName; senderLuid = gpu.Luid; }
            }
            if (stop.IsCancellationRequested) return;
            log.OriginQpc = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
            var presentWaitSegments = PresentWaitPlanPolicy.Create(options, log.OriginQpc, Stopwatch.Frequency);
            if (vblank != null) vblankTimer = new VblankWaitTimer(); // Owned by the GPU worker; closed after the loop, before device disposal.
            gpuLoopTimer = new VblankWaitTimer(); // The GPU loop's idle wait; same ownership as vblankTimer.
            string dllPath = Path.Combine(AppContext.BaseDirectory, "SpoutDX.dll");
            string? hash = File.Exists(dllPath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dllPath))) : null;
            ProbeLog.WriteNew(Path.Combine(options.LogDir, "manifest.json"), new
            {
                schemaVersion = 1, options, qpcFrequency = Stopwatch.Frequency, originQpc = log.OriginQpc, presentWaitSegments,
                adapter = new { description = display.AdapterDescription, luid = gpu.Luid, senderLuid, luidMatched = !options.HasSpout || senderLuid == gpu.Luid },
                displays, selectedDisplay = display,
                actualDisplay = target == null ? null : new { width = target.Width, height = target.Height, refreshHz = display.RefreshHz, vsyncInterval = 1, swapBufferCount = 2, maximumFrameLatency = 1 },
                highResolutionTimer = vblankTimer?.HighResolution, loopTimerHighResolution = new { gpu = gpuLoopTimer.HighResolution, spout = spoutLoopTimerHighResolution }, alignSlewMs = ComposeAlignGate.SlewMs,
                actualSenderName, dll = new { path = dllPath, sha256 = hash, allocationBytes = SpoutSender.AllocationBytes, verifiedSdkSize = SpoutSender.VerifiedSdkSize, verifiedSdkAlignment = 8, sdkVersion = "2.007.017" },
                operatorOverlayPossible = options.HasDisplay && !options.Windowed,
                imageFormat = "BGRA8_UNORM opaque SDR", gpuQuery = "D3D11_QUERY_EVENT; CPU-observed completion", poolCapacity = 3,
                source = options.Source != "contract-fake" ? null : new { fps = FakeVideoSource<Surface>.DefaultFps, slots = sourceSurfaces.Count, retainedImages = sourceSurfaces.Count - 1, generation = 1 },
                placement = options.Source != "contract-fake" ? null : new { source = new { width = options.SourceWidth, height = options.SourceHeight }, canvas = new { width = canvas.Width, height = canvas.Height },
                    fitId = canvas.DefaultFitId, destination = placement.Destination, sourceCrop = placement.SourceCrop },
                limitation = "GPU-generated source; no mpv, no CPU readback; API/GPU publications do not establish receiver arrivals or physical scanout."
            });
            log.Add("lifecycle", "GPU", detail: "ready"); status("Running " + options.Mode + " / " + options.Output + " at " + options.Fps + " Hz; mutex request " + options.MutexWaitMs + " ms; send phase " + options.SendPhaseMs + " ms.");
            startGate.Set();
            if (options.Source == "contract-fake")
            {
                var composeGpu = gpu; var composeShaders = shaders; long origin = log.OriginQpc;
                // Decode side of the fake: the pattern is rendered into a free source-owned surface (image id = source sequence, marker at
                // the source position) and offered only after its GPU completion, as a decoder would fence its output.
                source = new FakeVideoSource<Surface>(sourceSurfaces, origin, Stopwatch.Frequency, render: (target, stamp) =>
                {
                    composeShaders.Compose(target, options.SourceWidth, options.SourceHeight, new ImageStamp(stamp.Sequence, origin + (long)(stamp.PositionSeconds * Stopwatch.Frequency)), origin);
                    composeGpu.Fence.Wait("source.decode");
                }, describe: s => new SourceImageDescription(s.Texture, options.SourceWidth, options.SourceHeight, SourceImageFormat.Bgra8), gpu: display.AdapterDescription);
                source.SetGeneration(1);
            }
            long nextImageId = 0;
            var presentPlan = new PresentWaitPlanCursor(presentWaitSegments);
            var displayWait = target == null ? null : new DisplayWaitGate(target.Readiness);
            var vsync = target != null && options.DisplayPacing == "vsync" ? new VsyncDisplayGate(target.Readiness) : null;
            Func<long, long, long, bool>? idle = vsync == null ? null : (slot, now, due) =>
            {
                var (step, timeout) = vsync.Decide(slot, now, due, pool.LatestId, stop.IsCancellationRequested, Stopwatch.Frequency);
                if (step == VsyncStep.WaitReady)
                {
                    // Stop handle at index 0 wins; no lease, keyed mutex, or fence wait is held during this wait.
                    vsync.Wait(slot, due, timeout, stop.Token, Stopwatch.GetTimestamp, ms => target!.WaitReadyOrStop(stop.Token.WaitHandle, ms),
                        attempt =>
                        {
                            log.Record(new("display.vsync.wait.start", "GPU", attempt.StartQpc, slot, Detail: attempt.Kind, Value: attempt.TimeoutMs, DeadlineQpc: attempt.DeadlineQpc));
                            log.Record(new("display.vsync.wait.end", "GPU", attempt.EndQpc, slot, Detail: attempt.Outcome, Value: attempt.PermissionHeld ? 1 : 0, DeadlineQpc: attempt.DeadlineQpc));
                        }, reason => log.Add("skip", "GPU", slot, detail: reason, value: 1));
                    ObserveScanout(target!);
                    return true; // The loop re-checks stop and the compose deadline before presenting.
                }
                if (step == VsyncStep.Present) { Present(gpu, shaders, target!, sources, slot, due, 0, vsync); ObserveScanout(target!); return true; }
                return false;
            };
            if (vblank != null) idle = (slot, now, due) =>
            {
                long latestId = pool.LatestId;
                var (step, deadline, kind) = vblank.Decide(slot, now, due, latestId, stop.IsCancellationRequested);
                // Idle with a pending target still ahead of the compose deadline: wait for that target rather than the loop's
                // coarser stop-only wait (which would oversleep it). Once now >= target the same Idle falls through.
                if (step == VblankStep.Idle && vblank.Pending is { } p && now < p.TargetQpc && p.TargetQpc < due) (step, deadline, kind) = (VblankStep.WaitTarget, p.TargetQpc, "target");
                if (step == VblankStep.WaitTarget)
                {
                    // Stop handle at index 0 wins; no lease, keyed mutex, or fence wait is held during this wait.
                    vblank.Wait(deadline, kind, Stopwatch.GetTimestamp, (startQpc, dueQpc) => vblankTimer!.WaitUntilOrStop(stop.Token.WaitHandle, startQpc, dueQpc, Stopwatch.Frequency),
                        attempt =>
                        {
                            log.Record(new("display.vblank.wait.start", "GPU", attempt.StartQpc, slot, Detail: attempt.Kind, Value: attempt.RequestedMicroseconds, DeadlineQpc: attempt.DeadlineQpc));
                            log.Record(new("display.vblank.wait.end", "GPU", attempt.EndQpc, slot, Detail: attempt.Outcome, Value: attempt.LatenessMicroseconds, DeadlineQpc: attempt.DeadlineQpc));
                        });
                    return true; // The loop re-checks stop and the compose deadline; Decide then presents or re-predicts.
                }
                if (step == VblankStep.Present)
                {
                    var prediction = vblank.AttemptPrediction!.Value;
                    log.Record(new("display.vblank.predict", "GPU", Stopwatch.GetTimestamp(), slot, latestId, Detail: (prediction.PeriodTicks * 1_000_000 / Stopwatch.Frequency).ToString(CultureInfo.InvariantCulture),
                        Value: prediction.PredictedRefresh, DeadlineQpc: prediction.VblankQpc));
                    if (HoldVblankReadiness(target!, slot)) Present(gpu, shaders, target!, sources, slot, due, 0);
                    ObserveScanout(target!);
                    return true;
                }
                return false; // Stop, Compose, Idle; Bootstrap presents only from the compose tick.
            };
            // A passed vblank target is decided before a due compose tick (Present when its vblank is still reachable, else Compose).
            Func<long, bool>? beforeTick = vblank == null ? null : now => vblank.Pending is { } p && now >= p.TargetQpc;
            Loop("GPU", gpuLoopTimer, (scheduled, nextScheduled) =>
            {
                var presentSegment = presentPlan.Select(scheduled, out bool segmentChanged);
                if (segmentChanged)
                {
                    log.Record(new("present.wait.segment", "GPU", Stopwatch.GetTimestamp(), scheduled,
                        Detail: presentSegment.Index.ToString(CultureInfo.InvariantCulture), Value: presentSegment.WaitMs, DeadlineQpc: presentSegment.EndQpc));
                }
                source?.Tick(Stopwatch.GetTimestamp()); // Decode side of the fake: at most one new source image per source period.
                int slot = pool.TryBeginWrite();
                if (slot < 0) log.Add("skip", "GPU", scheduled, detail: "compose.noFreeSlot", value: 1);
                else
                {
                    var surface = sources[slot];
                    bool acquired = false, writing = true, leaseInFlight = false;
                    ISourceImageLease? lease = null;
                    try
                    {
                        bool sourceReady = source == null || AcquireSource(source, scheduled, out lease);
                        if (sourceReady) acquired = surface.Acquire();
                        if (!sourceReady) { } // compose.sourceNotReady is recorded; the pool keeps the previous latest image.
                        else if (!acquired) { log.Add("skip", "GPU", scheduled, detail: "compose.keyedMutexBusy", value: 1); }
                        else
                        {
                            var stamp = new ImageStamp(++nextImageId, Stopwatch.GetTimestamp());
                            log.Add("compose.start", "GPU", scheduled, stamp);
                            if (lease != null)
                            {
                                // Contract path: the leased source texture (same device) placed on the black-cleared canvas (fit-height; bars/crop per
                                // the placement computed at startup). The lease stays in GPU use until the compose fence.
                                lease.BeginGpuUse(); leaseInFlight = true;
                                shaders.Place(((SourceImageRing<Surface>.Lease)lease).Slot, lease.Width, lease.Height, surface.Target!, options.Width, options.Height, placement);
                            }
                            else shaders.Compose(surface, options.Width, options.Height, stamp, log.OriginQpc);
                            sharedFence?.Signal(gpu, stamp.Id); // Fence value == image id; readers wait for it on the GPU queue.
                            gpu.Fence.Wait("compose");
                            if (leaseInFlight) { lease!.CompleteGpuUse(); leaseInFlight = false; }
                            log.Add("compose.complete", "GPU", scheduled, stamp);
                            surface.Release(); acquired = false;
                            // Timestamp readiness before the CPU publication becomes visible to the sender.
                            log.Add("compose.publish", "GPU", scheduled, stamp);
                            pool.Publish(slot, stamp, true);
                            long visibleQpc = Stopwatch.GetTimestamp();
                            writing = false;
                            // This bounds CPU visibility from above; it is not the pool's exact linearization timestamp.
                            log.Record(new("compose.visible", "GPU", visibleQpc, scheduled, stamp.Id, stamp.GeneratedQpc));
                        }
                    }
                    finally
                    {
                        if (acquired) { try { gpu.Fence.Wait("compose.drain"); } finally { surface.Release(); } }
                        if (leaseInFlight) lease!.CompleteGpuUse(); // Only after the drain above: the GPU may still read the source texture.
                        lease?.Dispose(); // Returned after compose GPU completion; the pool holds the composed copy from here on.
                        if (writing) pool.AbortWrite(slot, true);
                    }
                }
                if (stop.IsCancellationRequested) return;
                if (target != null && vblank != null)
                {
                    // Before the first scanout observation there is no phase: present right after compose (as tick does) to start
                    // the statistics. Afterwards vblank presents only from the timer-driven idle path.
                    long commonEnd = log.OriginQpc + (long)(options.Seconds * Stopwatch.Frequency);
                    if (!vblank.HasScanout && vblank.Decide(scheduled, Stopwatch.GetTimestamp(), Math.Min(nextScheduled, commonEnd), pool.LatestId, stop.IsCancellationRequested).Step == VblankStep.Bootstrap)
                    {
                        log.Add("display.vblank.bootstrap", "GPU", scheduled, value: 1);
                        if (HoldVblankReadiness(target, scheduled)) Present(gpu, shaders, target, sources, scheduled, nextScheduled, 0);
                    }
                }
                if (target != null && vsync == null && vblank == null) // vsync/vblank present only from their idle paths.
                {
                    bool attemptDisplay = true;
                    if (options.DisplayPacing == "ready")
                    {
                        long deadline = Math.Min(nextScheduled, log.OriginQpc + (long)(options.Seconds * Stopwatch.Frequency));
                        // No source lease/key is held here. At most one wait and one display attempt belong to this composition tick.
                        attemptDisplay = displayWait!.TryForTick(scheduled, deadline, Stopwatch.Frequency, stop.Token,
                            Stopwatch.GetTimestamp, timeout => target.WaitReadyOrStop(stop.Token.WaitHandle, timeout),
                            attempt =>
                            {
                                log.Record(new("display.wait.start", "GPU", attempt.StartQpc, scheduled, Detail: attempt.Kind,
                                    Value: attempt.TimeoutMs, DeadlineQpc: attempt.DeadlineQpc));
                                log.Record(new("display.wait.end", "GPU", attempt.EndQpc, scheduled, Detail: attempt.Outcome,
                                    Value: attempt.PermissionHeld ? 1 : 0, DeadlineQpc: attempt.DeadlineQpc));
                            }, reason => log.Add("skip", "GPU", scheduled, detail: reason, value: 1));
                    }
                    if (attemptDisplay) Present(gpu, shaders, target, sources, scheduled, nextScheduled, presentSegment.WaitMs);
                }
                if (stop.IsCancellationRequested) return;
                if (sender != null) { sender.Update(sources, pool, scheduled, nextScheduled, stop.Token); sender.Send(scheduled, nextScheduled, stop.Token); }
                if (target != null) ObserveScanout(target); // No lease, keyed mutex, or fence wait is held here.
            }, idle, beforeTick);
            timedEnd = !stop.IsCancellationRequested;
        }
        catch (Exception e) { Fault(e.ToString()); }
        finally
        {
            stop.Cancel(); startGate.Set(); status("Draining native/GPU work and saving results.");
            // Shared source textures remain alive until the sender has stopped and released its opened references.
            senderThread?.Join();
            if (gpu != null) Drain(gpu, "GPU.shutdown");
            var sourceDiagnostics = source?.Diagnostics;
            // Every compose tick returned its lease before ending, so this succeeds at once; a false here is an engine bug and is faulted
            // (the surfaces are still destroyed below, as the shutdown path does for every resource after a fault).
            if (source != null && !source.TryDispose()) Fault("GPU.source: a source lease is still outstanding after the loop.");
            foreach (var surface in sourceSurfaces) DisposeOwned(surface, "GPU.sourceSurface");
            DisposeOwned(vblankTimer, "GPU.vblankTimer"); DisposeOwned(gpuLoopTimer, "GPU.loopTimer");
            DisposeOwned(sender, "GPU.sender"); DisposeOwned(target, "GPU.display"); DisposeOwned(shaders, "GPU.shaders");
            foreach (var surface in surfaces) DisposeOwned(surface, "GPU.surface");
            DisposeOwned(sharedFence, "GPU.fence");
            DisposeOwned(gpu, "GPU.device");
            var cpu = new ProcessCpuSample(cpuStartQpc, Stopwatch.GetTimestamp(), (process.TotalProcessorTime - cpuStart).TotalSeconds);
            log.Add("lifecycle", "GPU", detail: "finished");
            string outcome = Failed ? "faulted" : timedEnd && Volatile.Read(ref requested) == 0 ? "completed" : "cancelled";
            try
            {
                // Startup failures still get a manifest, but never claim an initialized GPU/measurement epoch.
                string manifest = Path.Combine(options.LogDir, "manifest.json");
                if (!File.Exists(manifest)) ProbeLog.WriteNew(manifest, new { schemaVersion = 1, options, qpcFrequency = Stopwatch.Frequency, originQpc = log.OriginQpc,
                    presentWaitSegments = PresentWaitPlanPolicy.Create(options, log.OriginQpc, Stopwatch.Frequency), displays, startupFailed = true, startupFailure = Volatile.Read(ref firstFault) });
                log.Save(options, outcome, pool, cpu, scanout, sourceDiagnostics);
            }
            catch (Exception e) { Interlocked.Exchange(ref failed, 1); status("Could not save complete logs: " + e.Message); }
            Volatile.Write(ref finished, 1); status(Failed ? "Stopped with error. See logs." : "Finished: " + outcome);
        }
    }
    // Contract path (GPU worker). Records source.acquire (imageId = source sequence, generatedQpc = DecodedQpc, detail = Ready|NotReady|Ended,
    // value = position in microseconds). A non-Ready result skips this compose tick with compose.sourceNotReady: holding the last image is
    // the compose layer's job, and the pool already keeps its previous latest. Generation is fixed at 1 (no seek in the probe).
    private bool AcquireSource(IVideoSource source, long scheduled, out ISourceImageLease? lease)
    {
        long now = Stopwatch.GetTimestamp();
        double position = (now - log.OriginQpc) / (double)Stopwatch.Frequency;
        var status = source.TryAcquire(1, position, out lease);
        var stamp = lease?.Stamp ?? default;
        log.Record(new("source.acquire", "GPU", now, scheduled, stamp.Sequence, stamp.DecodedQpc, status.ToString(), (long)Math.Round(position * 1_000_000)));
        if (status == SourceStatus.Ready) return true;
        log.Add("skip", "GPU", scheduled, detail: "compose.sourceNotReady", value: 1);
        return false;
    }
    // vblank (class field, GPU worker only): the latency permission is granted here by a timeout-0 check just before the attempt; the Present call consumes it.
    // A skipped attempt keeps the permission for the next attempt, so no present.ready.* attempt is ever made.
    private bool HoldVblankReadiness(DisplayTarget target, long slot)
    {
        if (target.Readiness.PermissionHeld) return true;
        if (target.WaitReady(0)) { target.Readiness.GrantFromNotification(); return true; }
        log.Add("skip", "GPU", slot, detail: "display.vblank.notReady", value: 1);
        vblank!.Defer(); // The prediction is dropped; the next Decide targets a later vblank.
        return false;
    }
    private void Present(GpuDevice gpu, ShaderPipeline shaders, DisplayTarget target, Surface[] sources, long scheduled, long nextScheduled, int presentWaitMs, VsyncDisplayGate? vsync = null)
    {
        long commonEnd = log.OriginQpc + (long)(options.Seconds * Stopwatch.Frequency);
        // vblank: a predicted attempt's guards run against the predicted vblank, so a target just before the compose tick is not
        // skipped as present.deadline; the compose may then start slightly late (its lateness is recorded).
        long deadline = vblank?.PresentDeadline(nextScheduled, commonEnd) ?? Math.Min(nextScheduled, commonEnd);
        long selectStarted = Stopwatch.GetTimestamp();
        if (options.DisplayPacing != "tick")
        {
            string? selectSkip = PresentReadyGate.SkipReason(selectStarted, deadline, stop.IsCancellationRequested);
            if (selectSkip != null) { log.Add("skip", "GPU", scheduled, detail: selectSkip, value: 1); return; }
        }
        vsync?.BeginAttempt(scheduled); // One selection per compose slot; the retained permission carries to the next slot on failure.
        vblank?.BeginAttempt(scheduled);
        using var lease = pool.AcquireLatest();
        long selectEnded = Stopwatch.GetTimestamp();
        ImageStamp selected = lease?.Stamp ?? default;
        log.Record(new("display.select.start", "GPU", selectStarted, scheduled, selected.Id, selected.GeneratedQpc, DeadlineQpc: deadline));
        log.Record(new("display.select.end", "GPU", selectEnded, scheduled, selected.Id, selected.GeneratedQpc,
            lease == null ? "none" : "latest", DeadlineQpc: deadline));
        if (lease == null) return;
        string? stale = vsync?.SkipReason(lease.Stamp.Id) ?? vblank?.SkipReason(lease.Stamp.Id);
        if (stale != null) { log.Add("skip", "GPU", scheduled, lease.Stamp, stale, 1); return; }
        Surface source = sources[lease.Slot];
        if (fenceSync)
        {
            // Same device as the compose signal: immediate-context order suffices, so only the fence value is recorded.
            log.Record(new("display.fence.wait", "GPU", Stopwatch.GetTimestamp(), scheduled, lease.Stamp.Id, lease.Stamp.GeneratedQpc, Detail: lease.Slot.ToString(), Value: lease.Stamp.Id));
        }
        else
        {
            if (!source.Acquire()) { log.Add("skip", "GPU", scheduled, lease.Stamp, "present.keyedMutexBusy", 1); return; }
            log.Record(new("display.mutex.acquire", "GPU", Stopwatch.GetTimestamp(), scheduled, lease.Stamp.Id, lease.Stamp.GeneratedQpc, Detail: lease.Slot.ToString()));
        }
        bool inFlight = false;
        try
        {
            // Select the newest image first. Latency permission survives skipped cycles, but this image lease does not.
            // vsync/vblank already hold the permission (notification wait / timeout-0 check), so no present.ready attempt is made.
            bool ready = vsync != null || vblank != null || target.Readiness.TryAcquire(presentWaitMs, deadline, Stopwatch.Frequency, stop.Token,
                Stopwatch.GetTimestamp, target.WaitReady,
                attempt =>
                {
                    log.Record(new("present.ready.start", "GPU", attempt.StartQpc, scheduled, lease.Stamp.Id, lease.Stamp.GeneratedQpc,
                        attempt.Kind, attempt.TimeoutMs, attempt.DeadlineQpc));
                    log.Record(new("present.ready.end", "GPU", attempt.EndQpc, scheduled, lease.Stamp.Id, lease.Stamp.GeneratedQpc,
                        attempt.Outcome, attempt.PermissionHeld ? 1 : 0, attempt.DeadlineQpc));
                }, reason => log.Add("skip", "GPU", scheduled, lease.Stamp, reason, 1));
            if (!ready) return;
            long drawStarted = Stopwatch.GetTimestamp();
            string? skip = PresentReadyGate.SkipReason(drawStarted, deadline, stop.IsCancellationRequested);
            if (skip != null) { log.Add("skip", "GPU", scheduled, lease.Stamp, skip, 1); return; }
            lease.BeginGpuUse(); inFlight = true;
            try { shaders.Display(source, target.Target, target.Width, target.Height, options.Width, options.Height); }
            finally { log.Record(new("display.draw.start", "GPU", drawStarted, scheduled, lease.Stamp.Id, lease.Stamp.GeneratedQpc)); }
            gpu.Fence.Wait("display.draw");
            lease.CompleteGpuUse(); inFlight = false;
            log.Add("display.draw.complete", "GPU", scheduled, lease.Stamp);
        }
        finally
        {
            try { if (inFlight) gpu.Fence.Wait("display.draw.drain"); }
            finally
            {
                if (inFlight) lease.CompleteGpuUse();
                if (!fenceSync)
                {
                    source.Release();
                    long releasedQpc = Stopwatch.GetTimestamp();
                    copyReleasedSignal?.Set();
                    log.Record(new("display.mutex.release", "GPU", releasedQpc, scheduled, lease.Stamp.Id, lease.Stamp.GeneratedQpc, Detail: lease.Slot.ToString()));
                }
            }
        }
        // No shared keyed mutex remains held across Present.
        var displayed = lease.Stamp;
        lease.Dispose();
        long presentStarted = Stopwatch.GetTimestamp();
        string? presentSkip = PresentReadyGate.SkipReason(presentStarted, deadline, stop.IsCancellationRequested);
        if (presentSkip != null) { log.Add("skip", "GPU", scheduled, displayed, presentSkip, 1); return; }
        int result;
        long presentReturned;
        try { result = target.Present(); presentReturned = Stopwatch.GetTimestamp(); }
        finally { log.Record(new("present.start", "GPU", presentStarted, scheduled, displayed.Id, displayed.GeneratedQpc)); }
        vsync?.Presented(displayed.Id); // The notification is consumed by the attempt whatever Present's status code.
        vblank?.Presented(displayed.Id);
        if (result == 0)
        {
            // value = this Present's count; frame statistics later report the same number when the image reached the display.
            uint presentCount = target.GetLastPresentCount();
            log.Record(new("present.return", "GPU", presentReturned, scheduled, displayed.Id, displayed.GeneratedQpc, Value: presentCount));
            scanout.Record(presentCount, displayed.Id, displayed.GeneratedQpc, presentStarted);
        }
        else log.Add("skip", "GPU", scheduled, displayed, $"present.status:0x{result:X8}", 1);
    }
    // GPU worker, no lease/keyed mutex/fence wait held. One statistics query; at most one present.scanout per advanced PresentCount.
    private void ObserveScanout(DisplayTarget target)
    {
        if (target.TryGetFrameStatistics(out var stats))
        {
            long observedQpc = Stopwatch.GetTimestamp();
            var seen = scanout.Observe(stats.PresentCount, stats.PresentRefreshCount, stats.SyncRefreshCount, stats.SyncQPCTime);
            if (seen is { } s)
            {
                log.Record(new("present.scanout", "GPU", observedQpc, 0, s.ImageId, s.GeneratedQpc, s.Detail, s.SyncRefreshCount, s.SyncQpcTime));
                vblank?.ObserveScanout(s.SyncQpcTime, s.SyncRefreshCount); // Every observation (mapped or not) carries the vblank phase.
                AlignCompose(); // At most one correction per scanout observation.
            }
        }
        else if (scanout.NoteDisjoint()) log.Add("present.stats.disjoint", "GPU", detail: "disjoint");
    }
    // GPU worker, no lease/keyed mutex/fence wait held. Moves the shared offset by at most slew towards the wanted compose
    // phase; both loops pick the change up at their next DueQpc read, so already taken ticks and their deadlines are unchanged.
    private void AlignCompose()
    {
        if (align == null || composeSchedule == null) return;
        long now = Stopwatch.GetTimestamp();
        if (align.Decide(vblank!, now, composeSchedule.DueQpc) is not { } d) return;
        scheduleOffset.Add(d.CorrectionTicks);
        log.Record(new("compose.align", "GPU", now, Detail: d.CorrectionMicroseconds.ToString(CultureInfo.InvariantCulture), Value: d.ErrorMicroseconds, DeadlineQpc: d.WantedQpc));
    }
    // idle(lastScheduled, now, due) may replace one idle wait before the next tick; returning false keeps the stop-only wait.
    // beforeTick(now) also consults idle when a tick is already due; returning false there takes the tick.
    // timer: this worker's own waitable timer for the stop-or-due idle wait (stop handle at index 0; no lease or mutex is held).
    private void Loop(string worker, VblankWaitTimer timer, Action<long, long> action, Func<long, long, long, bool>? idle = null, Func<long, bool>? beforeTick = null)
    {
        var timing = WorkerTiming.Create(options, log.OriginQpc, Stopwatch.Frequency, worker == "Spout");
        var schedule = new TickSchedule(timing.OriginQpc, options.Fps, Stopwatch.Frequency, align == null ? null : scheduleOffset);
        if (worker == "GPU") composeSchedule = schedule;
        long lastScheduled = long.MinValue;
        while (!stop.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            if (!timing.AllowsWork(now, stop.IsCancellationRequested)) break;
            long due = timing.WakeQpc(schedule.DueQpc);
            if (idle != null && (now < due || (beforeTick?.Invoke(now) ?? false)) && idle(lastScheduled, now, due)) continue;
            if (now < due)
            {
                // Wait until due itself (100 ns units, no whole-ms truncation); the loop re-checks stop and due after either outcome.
                if (LoopIdleWait.UseTimer(now, due, Stopwatch.Frequency)) timer.WaitUntilOrStop(stop.Token.WaitHandle, now, due, Stopwatch.Frequency);
                else Thread.Yield();
                continue;
            }
            var tick = schedule.Take(now);
            lastScheduled = tick.Scheduled;
            if (tick.Skipped > 0) log.Add("skip", worker, tick.Scheduled, detail: "schedule.late", value: tick.Skipped);
            action(tick.Scheduled, schedule.DueQpc);
        }
    }
    private void Drain(GpuDevice gpu, string stage)
    {
        try { gpu.Fence.Wait(stage); }
        catch (GpuDeviceLostException e) { Fault(e.Message); } // Confirmed loss permits destroying invalid resources.
    }
    private void DisposeOwned(IDisposable? resource, string stage)
    {
        try { resource?.Dispose(); }
        catch (Exception e) { Fault(stage + ": " + e); }
    }
}
