using System.Security.Cryptography;
using System.Text;
using Lucia.Homelab.Server.Nodes;
using Microsoft.AspNetCore.DataProtection;

namespace Lucia.Homelab.Server.Stacks;

public sealed partial class StackStore
{
    /// <summary>Variables Lucia adds to a bucket-using app's environment while Object storage runs.</summary>
    internal const string S3Prefix = "LUCIA_S3_";
    /// <summary>The buckets and keys Object storage provisions, one <c>bucket:key:secret</c> per app.</summary>
    internal const string StoragePrefix = "LUCIA_STORAGE_";

    /// <summary>
    /// Gives every installed catalog app that keeps a bucket its own bucket and key in the running Object storage app, or
    /// takes them away. Keys derive from the storage app's grant secret, so they're stable without being stored.
    /// </summary>
    internal async Task SyncStorage(CancellationToken ct)
    {
        var stacks = await Read(ct);
        var storage = stacks.FirstOrDefault(item => item.Manifest.Template?.Id == ObjectStorageApp.AppId && item is { Desired: "Running", Move: null });
        var env = storage is null ? null : StackCatalog.ReadEnv(_protector.Unprotect(storage.ProtectedEnv));
        var grant = env?.GetValueOrDefault(ObjectStorageApp.GrantSecret);
        var s3 = storage?.Manifest.Template is { } template
            ? StackCatalog.Settings(StackCatalog.Find(template.Id), template.Settings)["s3-host"] : null;
        var route = storage?.Manifest.Routes?.FirstOrDefault(item => item.Host == s3);
        var address = storage is null ? null
            : storage.Manifest.Address ?? (await nodes.Addresses(ct)).FirstOrDefault(item => item.Hostname == storage.Assigned)?.Address;
        // The server's own address, refreshed as the app moves: apps reach it over the LAN without trusting the domain's certificates.
        var endpoint = route is not null && address is not null ? $"{address}:{route.Port}" : null;
        var grants = new List<string>();
        foreach (var stack in stacks)
        {
            if (stack.Manifest.Template is not { } app || StackCatalog.Apps.FirstOrDefault(item => item.Id == app.Id) is not { Bucket: true }) continue;
            var lines = "";
            if (grant is { Length: > 0 } && endpoint is not null)
            {
                var (key, secret) = (ObjectStorageApp.Key(grant, stack.Name), ObjectStorageApp.KeySecret(grant, stack.Name));
                grants.Add($"{stack.Name}:{key}:{secret}");
                lines = $"{S3Prefix}ENDPOINT={endpoint}\n{S3Prefix}BUCKET={stack.Name}\n{S3Prefix}ACCESS_KEY={key}\n{S3Prefix}SECRET_KEY={secret}\n"
                    + $"{S3Prefix}OBJSTORE={{type: S3, config: {{bucket: {stack.Name}, endpoint: \"{endpoint}\", region: garage, access_key: {key}, "
                    + $"secret_key: {secret}, insecure: true, bucket_lookup_type: path}}}}\n";
            }
            await SetLucia(stack.Name, S3Prefix, lines, ct);
        }
        if (storage is not null)
            await SetLucia(storage.Name, StoragePrefix, grants.Count == 0 ? "" : $"{StoragePrefix}GRANTS={string.Join(' ', grants.Order(StringComparer.Ordinal))}\n", ct);
    }
}
