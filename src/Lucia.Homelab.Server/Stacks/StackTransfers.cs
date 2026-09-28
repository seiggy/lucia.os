using System.Collections.Concurrent;

namespace Lucia.Homelab.Server.Stacks;

/// <summary>
/// Pipes a stack's data from the node it's leaving straight to the node it's moving to, so nothing is staged on this
/// host's disk. The receiver connects first and waits; the sender's upload is copied into the receiver's response as it
/// arrives. If either side drops, the other's connection is aborted, and the receiver discards what it got.
/// </summary>
public sealed class StackTransfers(TimeProvider time)
{
    private static readonly TimeSpan ReceiverWait = TimeSpan.FromMinutes(10), SenderWait = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<Guid, Slot> _slots = new();
    private readonly ConcurrentDictionary<Guid, Progress> _progress = new();

    public sealed record Progress(long Bytes, long? Total, DateTimeOffset UpdatedAt);

    private sealed class Slot(Stream body)
    {
        public Stream Body { get; } = body;
        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Claimed;
    }

    public Progress? Status(Guid move) => _progress.TryGetValue(move, out var value) ? value : null;

    public void Forget(Guid move)
    {
        _progress.TryRemove(move, out _);
        if (_slots.TryRemove(move, out var slot)) slot.Done.TrySetResult(false);
    }

    /// <summary>Holds the receiver's response open until a sender has streamed into it. Returns false if it must be aborted.</summary>
    public async Task<bool> Receive(Guid move, Stream body, CancellationToken ct)
    {
        var slot = new Slot(body);
        if (_slots.TryGetValue(move, out var previous)) previous.Done.TrySetResult(false);
        _slots[move] = slot;
        try
        {
            var finished = await Task.WhenAny(slot.Done.Task, Task.Delay(ReceiverWait, time, ct));
            if (finished == slot.Done.Task) return await slot.Done.Task;
            if (Interlocked.Exchange(ref slot.Claimed, 1) == 0) return false;
            return await slot.Done.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) { return false; }
        finally { _slots.TryRemove(new KeyValuePair<Guid, Slot>(move, slot)); }
    }

    /// <summary>Streams the sender's upload into the waiting receiver.</summary>
    public async Task<long> Send(Guid move, Stream upload, long? total, CancellationToken ct)
    {
        var until = time.GetUtcNow() + SenderWait;
        Slot? slot = null;
        while (slot is null)
        {
            if (_slots.TryGetValue(move, out var waiting) && Interlocked.Exchange(ref waiting.Claimed, 1) == 0) slot = waiting;
            else if (time.GetUtcNow() > until) throw new TimeoutException("The receiving node isn't connected.");
            else await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
        }
        long bytes = 0;
        var buffer = new byte[256 * 1024];
        try
        {
            _progress[move] = new(0, total, time.GetUtcNow());
            int read;
            while ((read = await upload.ReadAsync(buffer, ct)) > 0)
            {
                await slot.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                bytes += read;
                _progress[move] = new(bytes, total, time.GetUtcNow());
            }
            await slot.Body.FlushAsync(ct);
            slot.Done.TrySetResult(true);
            return bytes;
        }
        catch
        {
            slot.Done.TrySetResult(false);
            throw;
        }
    }
}
