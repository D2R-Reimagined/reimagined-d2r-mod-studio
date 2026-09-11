namespace ModStudio.Core;

/// <summary>Serializes expensive preview work without blocking the UI. Canceled queued requests never decode.</summary>
public sealed class PreviewWorkQueue
{
    private readonly SemaphoreSlim worker = new(1, 1);
    public async Task<T> RunAsync<T>(Func<CancellationToken, T> action, CancellationToken cancellation)
    {
        await worker.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => {
                cancellation.ThrowIfCancellationRequested();
                var result = action(cancellation);
                cancellation.ThrowIfCancellationRequested();
                return result;
            }, cancellation).ConfigureAwait(false);
        }
        finally { worker.Release(); }
    }
}

/// <summary>Worker-owned, byte-bounded LRU. Returned pixel arrays are shared and must not be mutated.</summary>
public sealed class PreviewFrameCache(long budget = 32L * 1024 * 1024)
{
    private readonly LinkedList<(int Frame, PreviewPixels Pixels)> entries = new();
    public long RetainedBytes { get; private set; }
    public PreviewPixels GetOrDecode(int frame, Func<int, PreviewPixels> decode, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        for (var node = entries.First; node != null; node = node.Next)
            if (node.Value.Frame == frame) { entries.Remove(node); entries.AddFirst(node); return node.Value.Pixels; }
        var pixels = decode(frame);
        cancellation.ThrowIfCancellationRequested();
        if (pixels.Rgba.LongLength > budget) return pixels;
        while (RetainedBytes + pixels.Rgba.LongLength > budget && entries.Last is { } last)
        { RetainedBytes -= last.Value.Pixels.Rgba.LongLength; entries.RemoveLast(); }
        entries.AddFirst((frame, pixels)); RetainedBytes += pixels.Rgba.LongLength;
        return pixels;
    }

    public static PreviewPixels WithChannel(PreviewPixels source, int channel, CancellationToken cancellation)
    {
        if (channel == 0) return source;
        var bytes = new byte[source.Rgba.Length];
        for (int i = 0; i < bytes.Length; i += 4)
        {
            if ((i & 65535) == 0) cancellation.ThrowIfCancellationRequested();
            bytes[i] = channel == 2 ? source.Rgba[i + 3] : source.Rgba[i];
            bytes[i + 1] = channel == 2 ? source.Rgba[i + 3] : source.Rgba[i + 1];
            bytes[i + 2] = channel == 2 ? source.Rgba[i + 3] : source.Rgba[i + 2];
            bytes[i + 3] = 255;
        }
        return new(source.Width, source.Height, bytes);
    }
}
