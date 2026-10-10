using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using Lucia.Homelab.Server.Host;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.LabMap;

/// <summary>The network that holds an internet address: its AS number, country and owner.</summary>
public sealed record AsnOwner(int Asn, string Country, string Org);

/// <summary>Sorted, merged address ranges with their owners, searched by binary search.</summary>
internal sealed class AsnRanges<T>(T[] starts, T[] ends, AsnOwner[] owners) where T : struct, IBinaryInteger<T>
{
    public int Count => starts.Length;

    public AsnOwner? Find(T address)
    {
        var index = Array.BinarySearch(starts, address);
        if (index < 0) index = ~index - 1;
        return index >= 0 && address <= ends[index] ? owners[index] : null;
    }

    /// <summary>
    /// iptoasn.com's TSV: <c>start, end, AS number, country, description</c> per line, ranges without an AS (0) skipped and
    /// neighbours with the same owner merged.
    /// </summary>
    public static AsnRanges<T> Parse(TextReader reader, Func<IPAddress, T?> key)
    {
        var rows = new List<(T Start, T End, AsnOwner Owner)>();
        var owners = new Dictionary<(int, string, string), AsnOwner>();
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split('\t');
            if (fields.Length < 5 || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var asn) || asn <= 0
                || !IPAddress.TryParse(fields[0], out var first) || !IPAddress.TryParse(fields[1], out var last)
                || key(first) is not { } start || key(last) is not { } end || end < start)
                continue;
            var org = Org(fields[4]);
            if (!owners.TryGetValue((asn, fields[3], org), out var owner)) owners[(asn, fields[3], org)] = owner = new(asn, fields[3], org);
            rows.Add((start, end, owner));
        }
        rows.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(T Start, T End, AsnOwner Owner)>(rows.Count);
        foreach (var row in rows)
        {
            if (merged.Count > 0 && merged[^1] is var previous && previous.Owner == row.Owner && previous.End < row.Start && previous.End + T.One == row.Start)
                merged[^1] = (previous.Start, row.End, row.Owner);
            else if (merged.Count == 0 || row.Start > merged[^1].End) merged.Add(row);
        }
        return new([.. merged.Select(row => row.Start)], [.. merged.Select(row => row.End)], [.. merged.Select(row => row.Owner)]);
    }

    // "GOOGLE - Google LLC" reads better as "Google LLC".
    private static string Org(string description)
    {
        var dash = description.IndexOf(" - ", StringComparison.Ordinal);
        var org = (dash > 0 && dash + 3 < description.Length ? description[(dash + 3)..] : description).Trim();
        return org.Length > 80 ? org[..80] : org;
    }
}

/// <summary>
/// Internet addresses' owners from iptoasn.com's public-domain IP-to-ASN tables, kept beside Lucia's other state and refreshed
/// weekly. Loaded in the background on first use; until then, or when the download fails, nothing is known.
/// </summary>
public sealed class LabAsn(IOptions<AdGuardManagementOptions> options, ILogger<LabAsn> logger)
{
    private const long MaxDownloadBytes = 30 * 1024 * 1024;
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(7), Retry = TimeSpan.FromHours(6), Recheck = TimeSpan.FromDays(1);
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true, MaxAutomaticRedirections = 3, PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = DecompressionMethods.None,
    }) { Timeout = TimeSpan.FromMinutes(3) };
    private readonly string _directory = Path.GetFullPath(options.Value.CredentialsDirectory);
    private AsnRanges<uint>? _v4;
    private AsnRanges<UInt128>? _v6;
    private int _loading;
    private long _nextTicks;

    /// <summary>The address's owner when the tables are loaded; starts loading them otherwise.</summary>
    public AsnOwner? Find(IPAddress ip)
    {
        Load();
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork ? Volatile.Read(ref _v4)?.Find(BinaryPrimitives.ReadUInt32BigEndian(bytes))
            : ip.AddressFamily == AddressFamily.InterNetworkV6 ? Volatile.Read(ref _v6)?.Find(BinaryPrimitives.ReadUInt128BigEndian(bytes)) : null;
    }

    internal static uint? V4(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetwork ? BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes()) : null;
    internal static UInt128? V6(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetworkV6 ? BinaryPrimitives.ReadUInt128BigEndian(ip.GetAddressBytes()) : null;

    private void Load()
    {
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _nextTicks) || Interlocked.Exchange(ref _loading, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            var next = Recheck;
            try
            {
                var v4 = await Table("v4", V4, _v4);
                var v6 = await Table("v6", V6, _v6);
                if (v4 is not null) Volatile.Write(ref _v4, v4);
                if (v6 is not null) Volatile.Write(ref _v6, v6);
                if (_v4 is null) next = Retry;
            }
            catch (Exception error)
            {
                next = Retry;
                logger.LogWarning("The lab map's IP-to-ASN tables could not be loaded ({ErrorType}); retrying later.", error.GetType().Name);
            }
            finally
            {
                Interlocked.Exchange(ref _nextTicks, DateTime.UtcNow.Add(next).Ticks);
                Volatile.Write(ref _loading, 0);
            }
        });
    }

    /// <summary>The table, downloaded again when its copy is a week old; null when the current one stands.</summary>
    private async Task<AsnRanges<T>?> Table<T>(string family, Func<IPAddress, T?> key, AsnRanges<T>? current) where T : struct, IBinaryInteger<T>
    {
        var path = Path.Combine(_directory, $"lab-map-ip2asn-{family}.tsv.gz");
        var file = new FileInfo(path);
        if (!file.Exists || DateTime.UtcNow - file.LastWriteTimeUtc > Fresh)
        {
            Directory.CreateDirectory(_directory);
            var partial = path + ".tmp";
            try
            {
                await Download($"https://iptoasn.com/data/ip2asn-{family}.tsv.gz", partial);
                var fresh = Read(partial, key);
                if (fresh.Count == 0) throw new InvalidDataException("The IP-to-ASN table was empty.");
                File.Move(partial, path, overwrite: true);
                return fresh;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
            {
                logger.LogWarning("The lab map's IP-to-ASN {Family} table could not be downloaded ({ErrorType}).", family, error.GetType().Name);
                File.Delete(partial);
                if (!file.Exists) return null;
            }
        }
        return current ?? Read(path, key);
    }

    private static AsnRanges<T> Read<T>(string path, Func<IPAddress, T?> key) where T : struct, IBinaryInteger<T>
    {
        using var stream = File.OpenRead(path);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return AsnRanges<T>.Parse(reader, key);
    }

    private static async Task Download(string url, string path)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownloadBytes) throw new InvalidDataException("The IP-to-ASN table is too large.");
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(path);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            if ((total += read) > MaxDownloadBytes) throw new InvalidDataException("The IP-to-ASN table is too large.");
            await target.WriteAsync(buffer.AsMemory(0, read));
        }
    }
}
