using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Lucia.Homelab.Server.Host;

namespace Lucia.Homelab.Server.LabMap;

/// <summary>
/// Internet addresses' names from AdGuard's query log: each A/AAAA answer's address maps to the name the client asked for, CNAME
/// chains included. Read in the background at most every 30 seconds, newest first back to the previous read; names are kept for
/// two hours, at most 20,000 of them. Without AdGuard nothing is known.
/// </summary>
public sealed class LabDnsNames(AdGuardConnectionService adguard, ILogger<LabDnsNames> logger)
{
    private const int MaxNames = 20_000, PageSize = 300, MaxPages = 4;
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30), Keep = TimeSpan.FromHours(2);
    private readonly ConcurrentDictionary<IPAddress, (string Name, DateTimeOffset Seen)> _names = new();
    private DateTimeOffset _newest = DateTimeOffset.MinValue;
    private long _nextTicks;
    private int _busy;

    /// <summary>The name last resolved to this address within two hours.</summary>
    public string? Name(IPAddress ip) =>
        _names.TryGetValue(ip, out var entry) && DateTimeOffset.UtcNow - entry.Seen < Keep ? entry.Name : null;

    /// <summary>Starts reading the query log when the last read is 30 seconds old; never waits for it.</summary>
    public void Refresh()
    {
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _nextTicks) || Interlocked.Exchange(ref _busy, 1) == 1) return;
        Interlocked.Exchange(ref _nextTicks, DateTime.UtcNow.Add(Every).Ticks);
        _ = Task.Run(async () =>
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var since = _newest;
                var newest = since;
                await adguard.ReadQueryLogAsync(PageSize, MaxPages, page =>
                {
                    var (answers, oldest) = Parse(page);
                    foreach (var (address, name, at) in answers)
                    {
                        if (at > newest) newest = at;
                        _names.AddOrUpdate(address, (name, at), (_, known) => at >= known.Seen ? (name, at) : known);
                    }
                    return oldest is { } time && time.At > since ? time.Text : null;
                }, deadline.Token);
                _newest = newest;
                Trim();
            }
            catch (Exception error)
            {
                logger.LogDebug("AdGuard's query log could not be read for the lab map ({ErrorType}).", error.GetType().Name);
            }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    private void Trim()
    {
        var cutoff = DateTimeOffset.UtcNow - Keep;
        foreach (var (address, entry) in _names)
            if (entry.Seen < cutoff) _names.TryRemove(address, out _);
        if (_names.Count <= MaxNames) return;
        foreach (var (address, _) in _names.OrderBy(item => item.Value.Seen).Take(_names.Count - MaxNames * 9 / 10).ToArray())
            _names.TryRemove(address, out _);
    }

    /// <summary>A query log page's internet answers with the name asked for, and where the next page starts.</summary>
    internal static (List<(IPAddress Address, string Name, DateTimeOffset At)> Answers, (string Text, DateTimeOffset At)? Oldest) Parse(byte[] body)
    {
        var answers = new List<(IPAddress, string, DateTimeOffset)>();
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return (answers, null);
        (string, DateTimeOffset)? oldest = AdGuardValidation.String(root, "oldest") is { Length: > 0 and <= 64 } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? (text, time) : null;
        foreach (var entry in data.EnumerateArray().Take(1000))
        {
            if (entry.ValueKind != JsonValueKind.Object || AdGuardValidation.String(entry, "time") is not { } stamp
                || !DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                continue;
            if (!entry.TryGetProperty("question", out var question) || question.ValueKind != JsonValueKind.Object
                || AdGuardValidation.String(question, "name")?.Trim().TrimEnd('.').ToLowerInvariant() is not { Length: > 0 and <= 253 } name
                || !entry.TryGetProperty("answer", out var answer) || answer.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var record in answer.EnumerateArray().Take(32))
                if (record.ValueKind == JsonValueKind.Object && AdGuardValidation.String(record, "type") is "A" or "AAAA"
                    && TrafficKinds.Address(AdGuardValidation.String(record, "value")) is { } address && TrafficKinds.Public(address))
                    answers.Add((address, name, at));
        }
        return (answers, oldest);
    }
}
