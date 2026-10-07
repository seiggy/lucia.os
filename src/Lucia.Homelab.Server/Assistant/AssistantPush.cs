using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lucia.Homelab.Server.Domains;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Assistant;

/// <summary>A browser that receives the owner's notifications. <see cref="Subject"/> is the VAPID contact sent with each push.</summary>
public sealed record PushDevice(string Id, string Name, string Endpoint, string P256dh, string Auth, string Subject,
    DateTimeOffset Added, DateTimeOffset? LastUsed);

/// <summary>A browser's PushSubscription, as <c>toJSON()</c> gives it, plus a name for the profile page.</summary>
public sealed record PushSubscriptionRequest(string? Name, string? Endpoint, PushKeys? Keys);
public sealed record PushKeys(string? P256dh, string? Auth);
public sealed record PushRename(string? Name);

/// <summary>Web Push (RFC 8030/8291/8292) without a library: VAPID-signed, aes128gcm-encrypted messages to the owner's browsers.</summary>
public sealed class AssistantPush(IOptions<AssistantOptions> options, IDataProtectionProvider protection, ILogger<AssistantPush> logger,
    HttpClient http)
{
    private const int MaxDevices = 20;
    private static readonly string[] PushHosts = ["fcm.googleapis.com", ".push.services.mozilla.com", ".push.apple.com", ".notify.windows.com"];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _keyLock = new();
    private ECDsa? _key;

    public static HttpClient CreateClient() => new() { Timeout = TimeSpan.FromSeconds(15) };

    private string Root => Path.GetFullPath(options.Value.Directory);
    private string DevicesPath(string owner) => Path.Combine(Root, "users", owner, "devices.json");

    /// <summary>The VAPID public key a browser subscribes with.</summary>
    public string PublicKey() => WebEncoders.Base64UrlEncode(Raw(Key().ExportParameters(false)));

    public async Task<IReadOnlyList<PushDevice>> DevicesAsync(string owner, CancellationToken ct)
    {
        var path = DevicesPath(owner);
        try
        {
            if (!File.Exists(path)) return [];
            DomainOnboardingStore.RejectLinks(path);
            await using var file = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<PushDevice>>(file, AssistantStream.Json, ct) ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("The notification devices could not be read ({ErrorType}).", e.GetType().Name);
            return [];
        }
    }

    /// <summary>Adds a browser, or refreshes it when its endpoint is already known. Returns its id.</summary>
    public async Task<PushDevice> AddAsync(string owner, PushSubscriptionRequest request, string subject, CancellationToken ct)
    {
        var name = Name(request.Name);
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
            || request.Endpoint!.Length > 2048 || !PushHosts.Any(host => host[0] == '.' ? endpoint.Host.EndsWith(host, StringComparison.OrdinalIgnoreCase)
                : endpoint.Host.Equals(host, StringComparison.OrdinalIgnoreCase)))
            throw new AssistantException(400, "invalid_device", "This browser's push service isn't one Lucia knows.");
        if (Decode(request.Keys?.P256dh) is not { Length: 65 } key || key[0] != 4 || Decode(request.Keys?.Auth) is not { Length: 16 })
            throw new AssistantException(400, "invalid_device", "This browser's push keys are invalid.");
        try { using var check = Import(key); }
        catch (CryptographicException) { throw new AssistantException(400, "invalid_device", "This browser's push keys are invalid."); }
        return await UpdateAsync(owner, devices =>
        {
            var index = devices.FindIndex(device => device.Endpoint == request.Endpoint);
            if (index < 0 && devices.Count >= MaxDevices)
                throw new AssistantException(409, "too_many_devices", $"Remove a device first: Lucia notifies at most {MaxDevices}.");
            var device = new PushDevice(index < 0 ? Guid.NewGuid().ToString("N") : devices[index].Id, name, request.Endpoint!,
                request.Keys!.P256dh!, request.Keys.Auth!, subject, index < 0 ? DateTimeOffset.UtcNow : devices[index].Added,
                index < 0 ? null : devices[index].LastUsed);
            if (index < 0) devices.Add(device);
            else devices[index] = device;
            return device;
        }, ct);
    }

    public Task RenameAsync(string owner, string id, string? name, CancellationToken ct) => UpdateAsync(owner, devices =>
    {
        var index = devices.FindIndex(device => device.Id == id);
        if (index < 0) throw NotFound();
        devices[index] = devices[index] with { Name = Name(name) };
        return devices[index];
    }, ct);

    public Task RemoveAsync(string owner, string id, CancellationToken ct) => UpdateAsync(owner, devices =>
        devices.RemoveAll(device => device.Id == id) > 0 ? (PushDevice?)null : throw NotFound(), ct);

    /// <summary>
    /// Notifies the owner's devices, or one of them, and returns how many took the message. Devices their push service
    /// no longer knows are removed; failures are logged, never thrown.
    /// </summary>
    public async Task<int> SendAsync(string owner, string title, string body, string url, CancellationToken ct, string? deviceId = null)
    {
        var devices = (await DevicesAsync(owner, ct)).Where(device => deviceId is null || device.Id == deviceId).ToList();
        if (deviceId is not null && devices.Count == 0) throw NotFound();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { title = Clip(title, 120), body = Clip(body, 1500), url });
        List<string> gone = [], used = [];
        foreach (var device in devices)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, device.Endpoint)
                {
                    Content = new ByteArrayContent(Encrypt(payload, Decode(device.P256dh)!, Decode(device.Auth)!)),
                };
                request.Content.Headers.ContentType = new("application/octet-stream");
                request.Content.Headers.ContentEncoding.Add("aes128gcm");
                request.Headers.Add("TTL", "86400");
                request.Headers.Add("Urgency", "high");
                request.Headers.TryAddWithoutValidation("Authorization", Vapid(new Uri(device.Endpoint), device.Subject));
                using var response = await http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode) used.Add(device.Id);
                else if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) gone.Add(device.Id);
                else logger.LogWarning("A push service refused a notification ({StatusCode}).", (int)response.StatusCode);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or CryptographicException)
            {
                if (ct.IsCancellationRequested) throw;
                logger.LogWarning("A notification could not be sent ({ErrorType}).", e.GetType().Name);
            }
        }
        if (gone.Count + used.Count > 0)
        {
            try
            {
                await UpdateAsync(owner, list =>
                {
                    list.RemoveAll(device => gone.Contains(device.Id));
                    for (var i = 0; i < list.Count; i++)
                        if (used.Contains(list[i].Id)) list[i] = list[i] with { LastUsed = DateTimeOffset.UtcNow };
                    return (PushDevice?)null;
                }, ct);
            }
            catch (IOException e) { logger.LogWarning("The notification devices could not be saved ({ErrorType}).", e.GetType().Name); }
        }
        return used.Count;
    }

    /// <summary>RFC 8291 aes128gcm with one record. Internal so the RFC's test vector can check it.</summary>
    internal static byte[] Encrypt(byte[] payload, byte[] uaPublic, byte[] auth, ECDiffieHellman? server = null, byte[]? salt = null)
    {
        using var created = server is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : null;
        server ??= created!;
        salt ??= RandomNumberGenerator.GetBytes(16);
        var asPublic = Raw(server.ExportParameters(false));
        using var ua = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
        });
        var shared = server.DeriveRawSecretAgreement(ua.PublicKey);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, auth, [.. "WebPush: info\0"u8, .. uaPublic, .. asPublic]);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());
        byte[] plain = [.. payload, 2];
        var body = new byte[16 + 4 + 1 + 65 + plain.Length + 16];
        salt.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16), 4096);
        body[20] = 65;
        asPublic.CopyTo(body, 21);
        using var aes = new AesGcm(cek, 16);
        aes.Encrypt(nonce, plain, body.AsSpan(86, plain.Length), body.AsSpan(86 + plain.Length));
        return body;
    }

    /// <summary>RFC 8292: a short-lived ES256 token for the push service's origin.</summary>
    private string Vapid(Uri endpoint, string subject)
    {
        static string Part(object value) => WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(value));
        var unsigned = Part(new { typ = "JWT", alg = "ES256" }) + "." + Part(new
        {
            aud = endpoint.GetLeftPart(UriPartial.Authority), exp = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds(), sub = subject,
        });
        var signature = Key().SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256);
        return $"vapid t={unsigned}.{WebEncoders.Base64UrlEncode(signature)}, k={PublicKey()}";
    }

    /// <summary>The VAPID key, made once and kept with data protection so pushes survive restarts.</summary>
    private ECDsa Key()
    {
        lock (_keyLock)
        {
            if (_key is not null) return _key;
            var path = Path.Combine(Root, "push-key.json");
            var protector = protection.CreateProtector("Lucia.Homelab.AssistantPush.v1");
            var key = ECDsa.Create();
            if (File.Exists(path))
            {
                DomainOnboardingStore.RejectLinks(path);
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(protector.Unprotect(JsonSerializer.Deserialize<string>(File.ReadAllText(path))!)), out _);
            }
            else
            {
                key.GenerateKey(ECCurve.NamedCurves.nistP256);
                DomainOnboardingStore.WriteJson(path, protector.Protect(Convert.ToBase64String(key.ExportPkcs8PrivateKey())))
                    .GetAwaiter().GetResult();
            }
            return _key = key;
        }
    }

    private async Task<T> UpdateAsync<T>(string owner, Func<List<PushDevice>, T> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var devices = (await DevicesAsync(owner, ct)).ToList();
            var result = change(devices);
            await DomainOnboardingStore.WriteJson(DevicesPath(owner), devices, ct, AssistantStream.Json);
            return result;
        }
        finally { _gate.Release(); }
    }

    private static ECDiffieHellman Import(byte[] key) => ECDiffieHellman.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = key[1..33], Y = key[33..65] },
    });

    private static byte[] Raw(ECParameters key) => [4, .. key.Q.X!, .. key.Q.Y!];

    private static byte[]? Decode(string? value)
    {
        try { return value is { Length: > 0 and < 256 } ? WebEncoders.Base64UrlDecode(value.TrimEnd('=')) : null; }
        catch (FormatException) { return null; }
    }

    private static string Name(string? name) => name?.Trim() is { Length: > 0 and <= 60 } trimmed ? trimmed
        : throw new AssistantException(400, "invalid_name", "Name the device in 1 to 60 characters.");

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static AssistantException NotFound() => new(404, "device_not_found", "That device isn't registered.");
}
