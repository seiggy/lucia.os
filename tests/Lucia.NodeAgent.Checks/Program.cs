using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Lucia.NodeAgent;

var checks = 0;
var fixture = Path.Combine(Directory.GetCurrentDirectory(), "tests", "Lucia.NodeAgent.Checks", ".fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

void Rejects(Action action, string message)
{
    try { action(); }
    catch (NodeAgentException) { checks++; return; }
    throw new InvalidOperationException(message);
}

async Task RejectsAsync(Func<Task> action, string message)
{
    try { await action(); }
    catch (NodeAgentException) { checks++; return; }
    throw new InvalidOperationException(message);
}

void Write(string root, string relative, string value)
{
    var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, value);
}

var roots = new InventoryRoots(Path.Combine(fixture, "proc"), Path.Combine(fixture, "sys"), Path.Combine(fixture, "dev"));
void Disk(string name, string sectors = "2097152")
{
    Write(roots.Sys, $"class/block/{name}/size", sectors);
    Write(roots.Sys, $"class/block/{name}/dev", name == "sda" ? "8:0" : "8:16");
    Write(roots.Sys, $"class/block/{name}/removable", "0");
    Write(roots.Sys, $"class/block/{name}/ro", "0");
    Write(roots.Sys, $"class/block/{name}/device/model", "Fixture SSD");
    Write(roots.Sys, $"class/block/{name}/device/serial", "test-serial-" + name);
    Write(roots.Dev, name, "");
}

HardwareInspector Inspector(Action? mutation = null) => new(roots, Architecture.X64,
    () => new Dictionary<string, string[]> { ["eth0"] = ["fe80::1%7", "192.0.2.2", "192.0.2.2"] }, mutation);

HardwareInspector AddressInspector(string[] addresses) => new(roots, Architecture.X64,
    () => new Dictionary<string, string[]> { ["eth0"] = addresses });

try
{
    checks += await InstallationChecks.RunAsync(fixture);

    var plan = NodeUpdates.ParsePlan("Reading package lists...\nInst libc6 [2.41-12] (2.41-12+deb13u1 Debian:13.1/stable, Debian-Security:13/stable-security [amd64]) []\r\n"
        + "Inst zlib1g [1:1.3] (1:1.3.1 Debian:13.1/stable [amd64])\nInst linux-image-6.12.48 (6.12.48-1 Debian:13.1/stable [amd64])\nConf libc6 (2.41-12+deb13u1)\n");
    Check(plan.Length == 3 && plan[0] is { Name: "libc6", Current: "2.41-12", Candidate: "2.41-12+deb13u1", Security: true }
        && plan[1] is { Name: "linux-image-6.12.48", Current: null, Security: false } && plan[2].Name == "zlib1g", "apt plan should parse, security first.");
    Check(NodeUpdates.ParsePlan("0 upgraded, 0 newly installed.\n").Length == 0, "An empty apt plan should have no updates.");
    var release = Path.Combine(fixture, "release");
    Write(release, "a", "1");
    Write(release, "B", "2");
    Check(AgentRelease.Compute(release) == "72a3aae02a41db9bf5c8a41a53348020cdeba3d09ff4d7a89a87f27d86705f9e"
        && AgentRelease.IsId(AgentRelease.Compute(release)) && !AgentRelease.IsId("ABC"), "Agent release IDs should match Lucia's.");
    async Task<MemoryStream> Tar(params (string Name, System.Formats.Tar.TarEntryType Type)[] entries)
    {
        var stream = new MemoryStream();
        await using (var writer = new System.Formats.Tar.TarWriter(stream, leaveOpen: true))
            foreach (var (name, type) in entries)
            {
                var entry = new System.Formats.Tar.PaxTarEntry(type, name);
                if (type == System.Formats.Tar.TarEntryType.RegularFile) entry.DataStream = new MemoryStream("x"u8.ToArray());
                if (type == System.Formats.Tar.TarEntryType.SymbolicLink) entry.LinkName = "/etc/shadow";
                await writer.WriteEntryAsync(entry);
            }
        stream.Position = 0;
        return stream;
    }
    var agentFiles = new[] { "lucia-node-agent", "lucia-node-agent.dll", "lucia-node-agent.deps.json", "lucia-node-agent.runtimeconfig.json",
        "libcoreclr.so", "libhostfxr.so" }.Select(name => (name, System.Formats.Tar.TarEntryType.RegularFile)).ToArray();
    var extracted = Path.Combine(fixture, "agent-next");
    Directory.CreateDirectory(extracted);
    await AgentRelease.ExtractAsync(await Tar(agentFiles), extracted, CancellationToken.None);
    Check(Directory.GetFiles(extracted).Length == agentFiles.Length, "A complete agent download should extract.");
    foreach (var (bad, label) in new[]
    {
        (agentFiles.Append(("../escape", System.Formats.Tar.TarEntryType.RegularFile)).ToArray(), "path traversal"),
        (agentFiles.Append(("sub/file", System.Formats.Tar.TarEntryType.RegularFile)).ToArray(), "a nested path"),
        (agentFiles.Append(("link", System.Formats.Tar.TarEntryType.SymbolicLink)).ToArray(), "a symlink"),
        (agentFiles.Append(("lucia-node-agent", System.Formats.Tar.TarEntryType.RegularFile)).ToArray(), "a duplicate"),
        (agentFiles.Skip(1).ToArray(), "a missing executable"),
    })
    {
        var target = Path.Combine(fixture, "agent-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        await RejectsAsync(async () => await AgentRelease.ExtractAsync(await Tar(bad), target, CancellationToken.None),
            "An agent download with " + label + " should be refused.");
    }

    if (args is ["installation-only"]) return;
    Write(roots.Proc, "cpuinfo", "processor : 0\nmodel name : Fixture CPU\n\nprocessor : 1\nmodel name : Fixture CPU\n");
    Write(roots.Proc, "meminfo", "MemTotal:       8388608 kB\nMemFree: 12 kB\n");
    Write(roots.Sys, "class/dmi/id/sys_vendor", "Vendor\u0000\u001b\n");
    Write(roots.Sys, "class/dmi/id/product_name", "Fixture model");
    Write(roots.Sys, "class/dmi/id/product_uuid", "92f80d57-31d6-4ac6-b90d-5ae66e694bd5");
    Write(roots.Sys, "class/net/eth0/address", "AA:BB:CC:00:11:22\n");
    Write(roots.Sys, "class/net/lo/address", "00:00:00:00:00:00");
    Disk("sda");
    Disk("sdb");
    Disk("sda1", "1024");
    Write(roots.Sys, "class/block/sda1/partition", "1");
    Disk("loop0");
    Disk("ram0");
    Disk("sr0");
    Write(roots.Sys, "class/block/sr0/device/type", "5");
    Disk("dm-0");
    Directory.Delete(Path.Combine(roots.Sys, "class", "block", "dm-0", "device"), true);
    var aliases = Path.Combine(roots.Dev, "disk", "by-id");
    Directory.CreateDirectory(aliases);
    File.CreateSymbolicLink(Path.Combine(aliases, "wwn-test-sda"), Path.Combine("..", "..", "sda"));
    File.CreateSymbolicLink(Path.Combine(aliases, "ata-test-sda"), Path.Combine("..", "..", "sda"));
    File.CreateSymbolicLink(Path.Combine(aliases, "ata-test-sda-part1"), Path.Combine("..", "..", "sda1"));
    Write(roots.Dev, "outside-target", "");
    File.CreateSymbolicLink(Path.Combine(aliases, "escaped"), Path.Combine(fixture, "proc", "meminfo"));
    var report = Inspector().Inspect();
    Check(report.Architecture == "x86_64" && report.BootMode == "bios" && report.SecureBoot is null, "BIOS/unknown secure boot is wrong.");
    Check(report.CpuModel == "Fixture CPU" && report.LogicalCpuCount == 2 && report.MemoryBytes == 8589934592, "CPU/memory were not parsed.");
    Check(report.Manufacturer == "Vendor" && report.SerialNumber is null, "DMI controls/unknown fields were not handled honestly.");
    Check(report.Disks.Length == 2 && report.Disks[0].Path == "/dev/sda" && report.Disks[1].Path == "/dev/sdb", "Whole devices/filter/order are wrong.");
    Check(report.Disks[0].Id == "/dev/disk/by-id/ata-test-sda" && report.Disks[1].Id is null, "Stable ID or missing ID handling is wrong.");
    Check(report.Disks[0].SizeBytes == 1073741824 && !report.Disks[0].IsReadOnly && !report.Disks[0].IsRemovable, "Disk metadata is wrong.");
    Check(report.Interfaces.Length == 1 && report.Interfaces[0].MacAddress == "aa:bb:cc:00:11:22"
        && report.Interfaces[0].Addresses.SequenceEqual(new[] { "192.0.2.2", "fe80::1" }), "NIC metadata is wrong.");
    Check(IPAddress.Parse(report.Interfaces[0].Addresses[1]).GetAddressBytes()
        .SequenceEqual(IPAddress.Parse("fe80::1%7").GetAddressBytes()), "IPv6 scope normalization changed address bytes.");
    Check(AgentJson.SerializeReport(report) == AgentJson.SerializeReport(Inspector().Inspect()), "Inventory is not deterministic.");
    Check(new HardwareInspector(roots, Architecture.Arm64, () => new Dictionary<string, string[]>()).Inspect().Architecture == "aarch64", "Arm64 architecture mapping failed.");
    Rejects(() => new HardwareInspector(roots, Architecture.X86, () => new Dictionary<string, string[]>()), "Unsupported architecture passed.");

    File.Delete(Path.Combine(roots.Sys, "class", "net", "eth0", "address"));
    Check(Inspector().Inspect().Interfaces.Single().MacAddress is null, "A missing MAC was fabricated or its NIC omitted.");
    Write(roots.Sys, "class/net/eth0/address", "not-a-MAC");
    Check(Inspector().Inspect().Interfaces.Single().MacAddress is null, "An unavailable valid MAC was fabricated.");
    Write(roots.Sys, "class/net/eth0/address", "AA:BB:CC:00:11:22");
    var eightAddresses = Enumerable.Range(1, 8).Select(i => $"fe80::{i}%7").ToArray();
    Check(AddressInspector(eightAddresses).Inspect().Interfaces.Single().Addresses
        .SequenceEqual(Enumerable.Range(1, 8).Select(i => $"fe80::{i}")), "Eight scoped addresses were not preserved.");
    Rejects(() => AddressInspector([.. eightAddresses, "192.0.2.8"]).Inspect(), "Nine addresses were accepted or silently dropped.");
    Check(AddressInspector([]).Inspect().Interfaces.Single().Addresses.Length == 0, "An address was invented for an unconfigured NIC.");

    var nicDirectory = Path.Combine(roots.Sys, "class", "net", "eth0");
    var heldNic = Path.Combine(fixture, "held-nic");
    Directory.Move(nicDirectory, heldNic);
    Rejects(() => Inspector().Inspect(), "Zero non-loopback NICs passed.");
    Directory.Move(heldNic, nicDirectory);
    for (var i = 1; i <= 15; i++) Write(roots.Sys, $"class/net/eth{i}/address", "AA:BB:CC:00:11:22");
    Check(Inspector().Inspect().Interfaces.Length == 16, "Exactly sixteen NICs failed.");
    Write(roots.Sys, "class/net/eth16/address", "AA:BB:CC:00:11:22");
    Rejects(() => Inspector().Inspect(), "Seventeen NICs were accepted or silently dropped.");
    for (var i = 1; i <= 16; i++) Directory.Delete(Path.Combine(roots.Sys, "class", "net", $"eth{i}"), true);
    for (var i = 0; i < 20; i++) Write(roots.Sys, $"class/net/veth{i:x7}/address", "AA:BB:CC:00:11:22");
    Write(roots.Sys, "class/net/docker0/address", "AA:BB:CC:00:11:22");
    Write(roots.Sys, "class/net/br-6f34759cb496/address", "AA:BB:CC:00:11:22");
    Check(Inspector().Inspect().Interfaces.Single().Name == "eth0", "Docker's bridges and veths were counted as NICs.");
    for (var i = 0; i < 20; i++) Directory.Delete(Path.Combine(roots.Sys, "class", "net", $"veth{i:x7}"), true);
    Directory.Delete(Path.Combine(roots.Sys, "class", "net", "docker0"), true);
    Directory.Delete(Path.Combine(roots.Sys, "class", "net", "br-6f34759cb496"), true);
    var longNic = Path.Combine(roots.Sys, "class", "net", new string('n', 64));
    Directory.Move(nicDirectory, longNic);
    Check(Inspector().Inspect().Interfaces.Single().Name.Length == 64, "A 64-character NIC name failed.");
    var oversizedNic = longNic + "n";
    Directory.Move(longNic, oversizedNic);
    Rejects(() => Inspector().Inspect(), "A 65-character NIC name passed.");
    Directory.Move(oversizedNic, nicDirectory);

    foreach (var invalidAlias in new[] { ".hidden", "-prefix", "unsafe..alias", "unsafe alias", "unsafe$id",
        "partition-part1", "trailing-part99", new string('a', 201) })
    {
        var path = Path.Combine(aliases, invalidAlias);
        File.CreateSymbolicLink(path, Path.Combine("..", "..", "sdb"));
        var inspected = Inspector().Inspect();
        Check(inspected.Disks.Length == 2 && inspected.Disks.Single(disk => disk.Path == "/dev/sdb").Id is null,
            "An ineligible stable alias was used or its device omitted.");
        File.Delete(path);
    }
    foreach (var validAlias in new[] { "nvme+fixture", new string('a', 200) })
    {
        var path = Path.Combine(aliases, validAlias);
        File.CreateSymbolicLink(path, Path.Combine("..", "..", "sdb"));
        Check(Inspector().Inspect().Disks.Single(disk => disk.Path == "/dev/sdb").Id == "/dev/disk/by-id/" + validAlias,
            "An eligible stable alias failed.");
        File.Delete(path);
    }
    if (OperatingSystem.IsLinux())
    {
        var path = Path.Combine(aliases, "nvme:fixture+1");
        File.CreateSymbolicLink(path, Path.Combine("..", "..", "sdb"));
        Check(Inspector().Inspect().Disks.Single(disk => disk.Path == "/dev/sdb").Id == "/dev/disk/by-id/nvme:fixture+1",
            "A colon/plus stable alias failed.");
        File.Delete(path);
    }
    Write(roots.Sys, "class/block/sda/partition", "1");
    Write(roots.Sys, "class/block/sdb/partition", "1");
    Check(Inspector().Inspect().Disks.Length == 0, "A diskless inventory was rejected or given a synthetic disk.");
    File.Delete(Path.Combine(roots.Sys, "class", "block", "sda", "partition"));
    File.Delete(Path.Combine(roots.Sys, "class", "block", "sdb", "partition"));
    for (var i = 0; i < 30; i++) Disk($"vd{i}");
    Check(Inspector().Inspect().Disks.Length == 32, "Exactly 32 whole disks failed.");
    Disk("vd30");
    Rejects(() => Inspector().Inspect(), "33 whole disks were accepted or silently dropped.");
    for (var i = 0; i <= 30; i++)
    {
        Directory.Delete(Path.Combine(roots.Sys, "class", "block", $"vd{i}"), true);
        File.Delete(Path.Combine(roots.Dev, $"vd{i}"));
    }
    Write(roots.Sys, "class/block/sda/size", "2251799813685248");
    Check(Inspector().Inspect().Disks[0].SizeBytes == 1_152_921_504_606_846_976, "Maximum disk capacity failed.");
    Write(roots.Sys, "class/block/sda/size", "2251799813685249");
    Rejects(() => Inspector().Inspect(), "An oversized disk passed.");
    Write(roots.Sys, "class/block/sda/size", "2097152");
    Write(roots.Proc, "meminfo", "MemTotal: 1099511627776 kB\n");
    Check(Inspector().Inspect().MemoryBytes == 1_125_899_906_842_624, "Maximum memory capacity failed.");
    Write(roots.Proc, "meminfo", "MemTotal: 1099511627777 kB\n");
    Rejects(() => Inspector().Inspect(), "Oversized memory passed.");
    Write(roots.Proc, "meminfo", "MemTotal: 8388608 kB\n");
    var originalCpu = File.ReadAllText(Path.Combine(roots.Proc, "cpuinfo"));
    Write(roots.Proc, "cpuinfo", string.Join('\n', Enumerable.Range(0, 4096).Select(i => $"processor: {i}")));
    Check(Inspector().Inspect().LogicalCpuCount == 4096, "Exactly 4096 CPUs failed.");
    File.AppendAllText(Path.Combine(roots.Proc, "cpuinfo"), "\nprocessor: 4096");
    Rejects(() => Inspector().Inspect(), "4097 CPUs passed.");
    Write(roots.Proc, "cpuinfo", originalCpu);

    var efi = Path.Combine(roots.Sys, "firmware", "efi", "efivars");
    Directory.CreateDirectory(efi);
    Check(Inspector().Inspect() is { BootMode: "uefi", SecureBoot: null }, "Missing efivar was mistaken for disabled.");
    var secureBoot = Path.Combine(efi, "SecureBoot-8be4df61-93ca-11d2-aa0d-00e098032b8c");
    File.WriteAllBytes(secureBoot, [7, 0, 0, 0, 0]);
    Check(Inspector().Inspect().SecureBoot == false, "SecureBoot=0 was not read after attributes.");
    File.WriteAllBytes(secureBoot, [0, 0, 0, 0, 1]);
    Check(Inspector().Inspect().SecureBoot == true, "SecureBoot=1 was not read after attributes.");
    File.WriteAllBytes(secureBoot, [1, 0, 0, 0]);
    Check(Inspector().Inspect().SecureBoot is null, "Truncated efivar was not unknown.");
    File.WriteAllBytes(secureBoot, [1, 0, 0, 0, 2]);
    Check(Inspector().Inspect().SecureBoot is null, "Unknown EFI value was not unknown.");

    Rejects(() => Inspector(() => Write(roots.Sys, "class/block/sda/size", "4096")).Inspect(), "A capacity change during inspection passed.");
    Write(roots.Sys, "class/block/sda/size", "2097152");
    Rejects(() => Inspector(() =>
    {
        File.Delete(Path.Combine(aliases, "ata-test-sda"));
        File.CreateSymbolicLink(Path.Combine(aliases, "ata-test-sda"), Path.Combine("..", "..", "sdb"));
    }).Inspect(), "Repointed stable identity passed.");
    File.Delete(Path.Combine(aliases, "ata-test-sda"));
    File.CreateSymbolicLink(Path.Combine(aliases, "ata-test-sda"), Path.Combine("..", "..", "sda"));
    Rejects(() => Inspector(() => Write(roots.Sys, "class/block/sda/dev", "8:99")).Inspect(), "Replaced device number passed.");
    Write(roots.Sys, "class/block/sda/dev", "8:0");
    Rejects(() => Inspector(() => File.Delete(Path.Combine(roots.Dev, "sda"))).Inspect(), "Disappearing device passed.");
    Write(roots.Dev, "sda", "");
    foreach (var (file, invalid, valid) in new[]
    {
        (Path.Combine(roots.Proc, "cpuinfo"), "", "processor: 0\nprocessor: 1\nmodel name: Fixture CPU\n"),
        (Path.Combine(roots.Proc, "cpuinfo"), "processor: 1\nprocessor: 1\n", "processor: 0\nprocessor: 1\nmodel name: Fixture CPU\n"),
        (Path.Combine(roots.Proc, "meminfo"), "MemTotal: zero kB\n", "MemTotal: 8388608 kB\n"),
        (Path.Combine(roots.Sys, "class", "block", "sda", "size"), "-1", "2097152"),
        (Path.Combine(roots.Sys, "class", "block", "sda", "removable"), "2", "0")
    })
    {
        File.WriteAllText(file, invalid);
        Rejects(() => Inspector().Inspect(), "Malformed critical inventory data passed.");
        File.WriteAllText(file, valid);
    }
    File.Delete(Path.Combine(roots.Proc, "meminfo"));
    Rejects(() => Inspector().Inspect(), "Missing critical data passed.");
    Write(roots.Proc, "meminfo", "MemTotal: 8388608 kB\n");
    var json = AgentJson.SerializeReport(report);
    Check(AgentJson.MaxReportBytes == 32 * 1024, "The canonical report cap is not 32 KiB.");
    var maximumFields = report with
    {
        Interfaces = Enumerable.Range(0, 16).Select(i => new NetworkInterfaceReport($"eth{i}", null,
            Enumerable.Range(0, 8).Select(j => $"2001:db8:1234:5678:abcd:ef01:2345:{i * 8 + j:x4}").ToArray())).ToArray(),
        Disks = Enumerable.Range(0, 32).Select(i => new DiskReport("/dev/disk/by-id/" + new string('a', 198) + i.ToString("D2"),
            $"/dev/sd{i}", new string('m', 256), new string('s', 256), 1_152_921_504_606_846_976, false, false)).ToArray()
    };
    Check(JsonSerializer.SerializeToUtf8Bytes(maximumFields, AgentJson.Options).Length > AgentJson.MaxReportBytes,
        "The oversized report fixture does not exercise the byte limit.");
    Rejects(() => AgentJson.SerializeReport(maximumFields), "A report exceeding 32 KiB was serialized successfully.");
    AgentJson.ValidateReportSize(new string('a', 32 * 1024));
    Check(true, "Exactly 32 KiB failed.");
    AgentJson.ValidateReportSize(new string('\u00e9', 16 * 1024));
    Check(true, "Exactly 32 KiB of UTF-8 failed.");
    Rejects(() => AgentJson.ValidateReportSize(new string('\u00e9', 16 * 1024) + "a"), "UTF-8 byte overflow passed.");
    using (var document = JsonDocument.Parse(json))
    {
        Check(document.RootElement.EnumerateObject().Select(p => p.Name).SequenceEqual(new[]
        {
            "architecture", "bootMode", "secureBoot", "manufacturer", "model", "serialNumber", "hardwareUuid",
            "cpuModel", "logicalCpuCount", "memoryBytes", "interfaces", "disks"
        }), "Hardware JSON shape is wrong.");
        Check(document.RootElement.GetProperty("disks")[1].GetProperty("id").ValueKind == JsonValueKind.Null, "Missing ID omitted or synthesized.");
    }

    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var challenge = new DiscoveryChallenge("known-challenge", "known-nonce", DateTimeOffset.UtcNow.AddMinutes(2));
    var signed = DiscoveryClient.SignReport(challenge, json, key);
    using var publicKey = ECDsa.Create();
    publicKey.ImportFromPem(signed.PublicKeyPem);
    var signature = Convert.FromBase64String(signed.Signature);
    var message = "lucia-discovery-v1\nknown-challenge\nknown-nonce\n" + json;
    Check(signature.Length == 64 && publicKey.VerifyData(Encoding.UTF8.GetBytes(message), signature,
        HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), "P1363 signature verification failed.");
    Check(!publicKey.VerifyData(Encoding.UTF8.GetBytes(message + " "), signature,
        HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), "Tampered report verified.");
    Check(!publicKey.VerifyData(Encoding.UTF8.GetBytes(message.Replace("known-nonce", "other-nonce", StringComparison.Ordinal)),
        signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), "Tampered nonce verified.");
    Rejects(() => DiscoveryClient.SignReport(challenge with { Nonce = "nonce\ninjection" }, json, key), "Multiline challenge passed.");
    Rejects(() => DiscoveryClient.SignReport(challenge with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, json, key), "Expired challenge passed.");
    Rejects(() => DiscoveryClient.SignReport(challenge, new string('x', AgentJson.MaxReportBytes + 1), key), "Oversized report passed.");
    foreach (var origin in new[] { "http://host", "https://user@host", "https://host/path", "https://host?secret", "https://host#fragment", @"https://host\path" })
        Rejects(() => DiscoveryClient.ValidateServer(origin), "Unsafe HTTPS origin passed.");
    Check(DiscoveryClient.ValidateServer("https://node.test:8443").Host == "node.test", "Valid origin failed.");

    var registration = new DiscoveryRegistration(Guid.NewGuid(), "d1." + new string('A', 64) + "." + new string('b', 64),
        DateTimeOffset.UtcNow.AddMinutes(10), "AB12-CD34-EF56-7890");
    var credentials = new DiscoveryCredentials("https://node.test/", registration.DeviceId, registration.Token, registration.ExpiresAt, registration.VerificationCode);
    var requestCount = 0;
    using (var client = new DiscoveryClient(new Uri(credentials.Server), new FakeHandler(async request =>
    {
        requestCount++;
        if (request.RequestUri!.AbsolutePath == "/api/boot/challenge") return JsonResponse(challenge);
        Check(request.RequestUri.AbsolutePath == "/api/boot/discover" && request.Method == HttpMethod.Post, "Wrong registration endpoint.");
        var body = JsonSerializer.Deserialize<DiscoveryRequest>(await request.Content!.ReadAsStringAsync(), AgentJson.Options)!;
        Check(body.ReportJson == json && body.PublicKeyPem == key.ExportSubjectPublicKeyInfoPem(), "Wire report was changed or public key is not SPKI.");
        Check(body.Signature.Length == 88, "Wire signature length is wrong.");
        return JsonResponse(registration);
    })))
    {
        var result = await client.RegisterAsync(json, key, CancellationToken.None);
        Check(result == registration && requestCount == 2, "Registration failed.");
    }
    requestCount = 0;
    using (var client = new DiscoveryClient(new Uri(credentials.Server), new FakeHandler(_ =>
    {
        requestCount++;
        return Task.FromResult(JsonResponse(challenge));
    })))
    {
        try
        {
            await client.RegisterAsync(new string('x', 32 * 1024 + 1), key, CancellationToken.None);
            throw new InvalidOperationException("Oversized registration passed.");
        }
        catch (NodeAgentException ex)
        {
            Check(requestCount == 0 && ex.Message.Contains("32 KiB", StringComparison.Ordinal),
                "Oversized registration was not rejected clearly before any HTTP request.");
        }
    }
    var duplicatePosts = 0;
    using (var client = new DiscoveryClient(new Uri(credentials.Server), new FakeHandler(request =>
    {
        if (request.Method == HttpMethod.Get) return Task.FromResult(JsonResponse(challenge));
        duplicatePosts++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("private-duplicate-body") });
    })))
    {
        var originalPublicKey = key.ExportSubjectPublicKeyInfoPem();
        await RejectsAsync(async () => await client.RegisterAsync(json, key, CancellationToken.None), "Duplicate registration was accepted.");
        Check(duplicatePosts == 1 && key.ExportSubjectPublicKeyInfoPem() == originalPublicKey,
            "A duplicate registration retried or changed the identity.");
    }
    var attempts = 0;
    using (var client = new DiscoveryClient(new Uri(credentials.Server), new FakeHandler(_ => Task.FromResult(
        ++attempts < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : JsonResponse(challenge)))))
    {
        await client.GetChallengeAsync(CancellationToken.None);
        Check(attempts == 3, "Safe GET retry count is wrong.");
    }
    attempts = 0;
    using (var client = new DiscoveryClient(new Uri(credentials.Server), new FakeHandler(request =>
    {
        if (request.Method == HttpMethod.Get) return Task.FromResult(JsonResponse(challenge));
        attempts++;
        throw new HttpRequestException("synthetic-secret-in-transport-error");
    })))
    {
        await RejectsAsync(async () => await client.RegisterAsync(json, key, CancellationToken.None), "Failed POST was accepted.");
        Check(attempts == 1, "Unsafe POST was retried.");
    }
    foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Conflict, HttpStatusCode.Redirect })
    {
        using var client = new DiscoveryClient(new Uri(credentials.Server), new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(code) { Content = new StringContent("secret-do-not-relay") })));
        try { await client.GetChallengeAsync(CancellationToken.None); throw new InvalidOperationException("Failure response passed."); }
        catch (NodeAgentException ex) { Check(!ex.Message.Contains("secret-do-not-relay", StringComparison.Ordinal), "HTTP body leaked."); }
    }
    using (var client = new DiscoveryClient(new Uri(credentials.Server), new FakeHandler(_ => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 64 * 1024 + 1)) }))))
        await RejectsAsync(async () => await client.GetChallengeAsync(CancellationToken.None), "Oversized response passed.");
    using (var client = new DiscoveryClient(new Uri(credentials.Server), new FakeHandler(_ => Task.FromResult(JsonResponse(new { nonce = "test" })))))
        await RejectsAsync(async () => await client.GetChallengeAsync(CancellationToken.None), "Missing challenge fields passed.");
    using (var client = new DiscoveryClient(new Uri(credentials.Server), new FakeHandler(request =>
    {
        Check(request.Headers.Authorization?.Parameter == registration.Token && request.Method == HttpMethod.Get
            && request.RequestUri!.AbsolutePath == "/api/boot/devices/" + registration.DeviceId.ToString("D"), "Status authentication/path failed.");
        return Task.FromResult(JsonResponse(new { phase = "Discovered" }));
    })))
    {
        using var status = await client.GetStatusAsync(credentials, CancellationToken.None);
        Check(status.RootElement.GetProperty("phase").GetString() == "Discovered", "Status read failed.");
        await RejectsAsync(async () => await client.GetStatusAsync(credentials with { Server = "https://elsewhere.test/" }, CancellationToken.None), "Cross-origin token use passed.");
        await RejectsAsync(async () => await client.GetStatusAsync(credentials with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, CancellationToken.None), "Expired session passed.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await client.GetStatusAsync(credentials, cancelled.Token); throw new InvalidOperationException("Cancellation was ignored."); }
        catch (OperationCanceledException) { checks++; }
    }
    const string statusPrefix = "{\"phase\":\"Discovered\",\"padding\":\"";
    var limitStatus = statusPrefix + new string('x', 64 * 1024 - statusPrefix.Length - 2) + "\"}";
    using (var client = new DiscoveryClient(new Uri(credentials.Server), new FakeHandler(_ => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(limitStatus) }))))
    {
        using var status = await client.GetStatusAsync(credentials, CancellationToken.None);
        Check(status.RootElement.GetProperty("phase").GetString() == "Discovered" && Encoding.UTF8.GetByteCount(limitStatus) == 64 * 1024,
            "A status response at exactly 64 KiB failed.");
    }
    using (var client = new DiscoveryClient(new Uri(credentials.Server), new FakeHandler(_ => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(Encoding.UTF8.GetBytes(limitStatus + " ")) }))))
        await RejectsAsync(async () => await client.GetStatusAsync(credentials, CancellationToken.None), "A streamed status response exceeding 64 KiB passed.");

    using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var ca = Authority(caKey, "CN=Node agent fixture root");
    using var wrongCaKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var wrongCa = Authority(wrongCaKey, "CN=Wrong fixture root");
    using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var certificate = ServerCertificate(ca, serverKey, "node.test");
    using var wrongEku = ServerCertificate(ca, serverKey, "node.test", clientOnly: true);
    using var expired = ServerCertificate(ca, serverKey, "node.test", expired: true);
    using (var parsed = DiscoveryClient.ParseAuthority(ca.ExportCertificatePem()))
        Check(parsed.Thumbprint == ca.Thumbprint, "Public CA import failed.");
    Rejects(() => DiscoveryClient.ParseAuthority(certificate.ExportCertificatePem()), "Leaf accepted as CA.");
    Rejects(() => DiscoveryClient.ParseAuthority(ca.ExportCertificatePem() + caKey.ExportPkcs8PrivateKeyPem()), "Private CA key accepted.");
    Rejects(() => DiscoveryClient.ParseAuthority(ca.ExportCertificatePem() + wrongCa.ExportCertificatePem()), "Extra trust root accepted.");
    Check(await TlsRequest(certificate, ca, "node.test"), "Valid CA/hostname TLS failed.");
    Check(!await TlsRequest(certificate, wrongCa, "node.test"), "Wrong CA TLS passed.");
    Check(!await TlsRequest(certificate, ca, "wrong.test"), "Wrong hostname TLS passed.");
    Check(!await TlsRequest(wrongEku, ca, "node.test"), "Wrong server EKU passed.");
    Check(!await TlsRequest(expired, ca, "node.test"), "Expired server certificate passed.");

    if (OperatingSystem.IsLinux())
    {
        var securePath = Path.Combine(fixture, "state");
        var supported = true;
        try { using var state = new SecureStateDirectory(securePath); }
        catch (NodeAgentException ex) when (ex.Message.Contains("filesystem", StringComparison.Ordinal)
            || ex.Message.Contains("ancestors", StringComparison.Ordinal))
        {
            supported = false;
            Console.WriteLine("SKIP: positive Linux credential checks require a local ext/XFS/Btrfs/tmpfs/ramfs/overlay checkout with safe ancestor permissions.");
            checks++;
        }
        if (supported)
        {
            using (var state = new SecureStateDirectory(securePath))
            {
                using var first = state.LoadOrCreateKey();
                using var second = state.LoadOrCreateKey();
                Check(first.ExportSubjectPublicKeyInfoPem() == second.ExportSubjectPublicKeyInfoPem(), "Identity was not reused.");
                state.SaveCredentials(credentials);
                Check(state.LoadCredentials() == credentials, "Credentials did not round-trip.");
                Check(File.GetUnixFileMode(Path.Combine(securePath, "identity.pem")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Private key is not 0600.");
                Check(File.GetUnixFileMode(Path.Combine(securePath, "discovery.json")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Token is not 0600.");
                File.SetUnixFileMode(Path.Combine(securePath, "identity.pem"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                Rejects(() => state.LoadOrCreateKey(), "Broad key permissions passed.");
                File.SetUnixFileMode(Path.Combine(securePath, "identity.pem"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Delete(Path.Combine(securePath, "discovery.json"));
                File.CreateSymbolicLink(Path.Combine(securePath, "discovery.json"), Path.Combine(securePath, "identity.pem"));
                Rejects(() => state.LoadCredentials(), "Symlink token passed.");
                Rejects(() => state.SaveCredentials(credentials), "Symlink token write passed.");
                File.Delete(Path.Combine(securePath, "discovery.json"));
                Check(NativeChecks.Link(Path.Combine(securePath, "identity.pem"), Path.Combine(securePath, "discovery.json")) == 0, "Hardlink fixture failed.");
                Rejects(() => state.LoadOrCreateKey(), "Hard-linked key passed.");
                Rejects(() => state.LoadCredentials(), "Hard-linked token passed.");
                File.Delete(Path.Combine(securePath, "discovery.json"));
                File.WriteAllText(Path.Combine(securePath, "identity.pem"), "invalid-key-must-not-be-replaced");
                Rejects(() => state.LoadOrCreateKey(), "Malformed stored key passed.");
                Check(File.ReadAllText(Path.Combine(securePath, "identity.pem")) == "invalid-key-must-not-be-replaced", "Invalid identity was silently regenerated.");
                var publicCa = Path.Combine(securePath, "public-ca.pem");
                File.WriteAllText(publicCa, ca.ExportCertificatePem());
                File.SetUnixFileMode(publicCa, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                using (var imported = DiscoveryClient.LoadAuthority(publicCa))
                    Check(imported.Thumbprint == ca.Thumbprint, "Safe public CA file import failed.");
                File.SetUnixFileMode(publicCa, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
                Rejects(() => DiscoveryClient.LoadAuthority(publicCa), "World-writable public CA passed.");
                File.SetUnixFileMode(publicCa, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.CreateSymbolicLink(Path.Combine(securePath, "ca-link.pem"), publicCa);
                Rejects(() => DiscoveryClient.LoadAuthority(Path.Combine(securePath, "ca-link.pem")), "Symlink CA passed.");
            }
            var link = Path.Combine(fixture, "state-link");
            Directory.CreateSymbolicLink(link, securePath);
            Rejects(() => new SecureStateDirectory(link), "Symlink directory passed.");
            Rejects(() => new SecureStateDirectory(Path.Combine(link, "child")), "Symlink ancestor passed.");
            File.SetUnixFileMode(securePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
            Rejects(() => new SecureStateDirectory(securePath), "Non-private state directory passed.");
        }
        Rejects(() => new SecureStateDirectory("/run/../run/lucia"), "Dot-segment state path passed.");
        Rejects(() => new SecureStateDirectory("//server/path"), "Remote-style state path passed.");
    }
    else
    {
        Rejects(() => new HardwareInspector(), "Live inventory was allowed off Linux.");
        Rejects(() => new SecureStateDirectory(fixture), "Credential storage was allowed off Linux.");
    }
    Console.WriteLine($"PASS: {checks} node-agent checks.");
}
finally
{
    Directory.Delete(fixture, recursive: true);
}

static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
{
    Content = new StringContent(JsonSerializer.Serialize(value, AgentJson.Options), Encoding.UTF8, "application/json")
};

static X509Certificate2 Authority(ECDsa key, string subject)
{
    var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(10));
}

static X509Certificate2 ServerCertificate(X509Certificate2 ca, ECDsa key, string hostname, bool clientOnly = false, bool expired = false)
{
    var request = new CertificateRequest("CN=" + hostname, key, HashAlgorithmName.SHA256);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(clientOnly ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
    var names = new SubjectAlternativeNameBuilder();
    names.AddDnsName(hostname);
    request.CertificateExtensions.Add(names.Build());
    using var certificate = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-2),
        DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1), RandomNumberGenerator.GetBytes(16));
    return certificate.CopyWithPrivateKey(key);
}

static async Task<bool> TlsRequest(X509Certificate2 certificate, X509Certificate2 ca, string hostname)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var serving = Task.Run(async () =>
    {
        try
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            using var tls = new SslStream(connection.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
            var header = new List<byte>();
            var buffer = new byte[1];
            while (header.Count < 4096)
            {
                await tls.ReadExactlyAsync(buffer, timeout.Token);
                header.Add(buffer[0]);
                if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
            }
            await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"), timeout.Token);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException) { }
    });
    var success = false;
    try
    {
        using var client = new HttpClient(DiscoveryClient.CreateHandler(ca, IPAddress.Loopback));
        using var response = await client.GetAsync($"https://{hostname}:{port}/", timeout.Token);
        success = response.IsSuccessStatusCode;
    }
    catch (HttpRequestException) { }
    finally { await timeout.CancelAsync(); await serving; }
    return success;
}

sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return send(request);
    }
}

static class NativeChecks
{
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    internal static extern int Link(string target, string link);
}

sealed class UnknownLengthContent(byte[] bytes) : ByteArrayContent(bytes)
{
    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
