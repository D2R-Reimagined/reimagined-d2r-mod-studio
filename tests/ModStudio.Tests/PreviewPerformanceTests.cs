using System.Buffers.Binary;
using System.Diagnostics;
using ModStudio.Core;

internal static class PreviewPerformanceTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        const int side = 256;
        var bytes = new byte[32 + side * side * 4 * 4];
        void Put(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        Put(0, 18); Put(4, side - 1); Put(8, side - 1); Put(24, 1); Put(28, 1);
        var path = Path.Combine(root, "performance.ds1"); File.WriteAllBytes(path, bytes);
        var asset = SpecialistPreview.Load(path); asset.Decode(0);
        long allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) asset.Decode(0);
        watch.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine($"DS1 256x256, five combined previews: {watch.Elapsed.TotalMilliseconds:F1} ms; {allocated:N0} allocated bytes");
        check(asset.Decode(0).Rgba.Length == side * side * 4, "Large map preview remains bounded to one pixel per tile");
        check(allocated < 6 * 1024 * 1024, "Map overview does not allocate per-tile sorting collections");
        int decodes = 0;
        PreviewPixels Decode(int frame) { decodes++; return new(1, 1, [(byte)frame, 20, 30, 40]); }
        var cache = new PreviewFrameCache(8);
        var first = cache.GetOrDecode(0, Decode, default);
        cache.GetOrDecode(1, Decode, default); cache.GetOrDecode(0, Decode, default); cache.GetOrDecode(2, Decode, default);
        check(decodes == 3 && cache.RetainedBytes == 8, "Frame cache reuses decodes and respects its byte budget");
        cache.GetOrDecode(1, Decode, default);
        check(decodes == 4, "Frame cache evicts the least recently used frame");
        var alpha = PreviewFrameCache.WithChannel(first, 2, default);
        var rgb = PreviewFrameCache.WithChannel(first, 1, default);
        check(first.Rgba.SequenceEqual(new byte[] { 0, 20, 30, 40 }) && alpha.Rgba.SequenceEqual(new byte[] { 40, 40, 40, 255 }) && rgb.Rgba.SequenceEqual(new byte[] { 0, 20, 30, 255 }), "Channel views preserve cached colors and transparency");
        var tiny = new PreviewFrameCache(3); tiny.GetOrDecode(0, Decode, default);
        check(tiny.RetainedBytes == 0, "Oversized frames are displayed without exceeding the cache budget");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        int before = decodes;
        try { cache.GetOrDecode(3, Decode, canceled.Token); } catch (OperationCanceledException) { }
        check(decodes == before, "Canceled frame requests never decode");
        using var interrupted = new CancellationTokenSource();
        var interruptedCache = new PreviewFrameCache();
        try { interruptedCache.GetOrDecode(0, _ => { interrupted.Cancel(); return Decode(0); }, interrupted.Token); } catch (OperationCanceledException) { }
        check(interruptedCache.RetainedBytes == 0, "Canceled decode results are not retained in the frame cache");
        TestQueueAsync(check).GetAwaiter().GetResult();
    }

    private static async Task TestQueueAsync(Action<bool, string> check)
    {
        var queue = new PreviewWorkQueue();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runningCancellation = new CancellationTokenSource();
        var running = queue.RunAsync(_ => { started.SetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return 1; }, runningCancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var queuedCancellation = new CancellationTokenSource();
        bool queuedRan = false;
        var queued = queue.RunAsync(_ => { queuedRan = true; return 2; }, queuedCancellation.Token);
        queuedCancellation.Cancel();
        try { await queued; } catch (OperationCanceledException) { }
        check(!queuedRan, "Preview queue cancels waiting work before it starts");
        bool nextRan = false;
        var next = queue.RunAsync(_ => { nextRan = true; return 3; }, default);
        check(!nextRan, "Preview queue bounds concurrent decoding to one worker");
        runningCancellation.Cancel(); release.Set();
        bool discarded = false;
        try { await running; } catch (OperationCanceledException) { discarded = true; }
        check(discarded && await next.WaitAsync(TimeSpan.FromSeconds(5)) == 3, "Canceled running decode is discarded and releases the next request");
        try { await queue.RunAsync<int>(_ => throw new InvalidDataException(), default); } catch (InvalidDataException) { }
        check(await queue.RunAsync(_ => 4, default) == 4, "Failed decoder releases the preview worker");
    }
}
