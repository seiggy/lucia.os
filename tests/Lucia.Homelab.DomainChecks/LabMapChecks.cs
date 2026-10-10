using System.Text.Json;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.LabMap;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Stacks;
using Microsoft.AspNetCore.DataProtection;

internal static class LabMapChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        void Rejects(Action action, string code, string message)
        {
            try { action(); }
            catch (UniFiException error) when (error.Code == code) { check(true, message); return; }
            check(false, message);
        }

        // The client classifier, from what UniFi knows of a client.
        (string? Name, string? Hostname, string? Fingerprint, string? Vendor, bool Infrastructure, string Group)[] cases =
        [
            ("Living Room Apple TV", null, null, "Apple, Inc.", false, ClientClassifier.Entertainment),
            (null, "Zacks-iPhone", null, "Apple, Inc.", false, ClientClassifier.Phones),
            (null, "Galaxy-S24", null, "Samsung Electronics Co.,Ltd", false, ClientClassifier.Phones),
            (null, "MacBook-Pro", null, "Apple, Inc.", false, ClientClassifier.Computers),
            (null, "DESKTOP-4F2K", "Windows 11", null, false, ClientClassifier.Computers),
            (null, "shelly1pm-ABC123", null, null, false, ClientClassifier.Iot),
            (null, null, null, "Espressif Inc.", false, ClientClassifier.Iot),
            ("Office printer", null, null, null, false, ClientClassifier.Iot),
            (null, "SonosZP", null, null, false, ClientClassifier.Entertainment),
            (null, null, null, "Sonos, Inc.", false, ClientClassifier.Entertainment),
            (null, null, null, "Ubiquiti Inc", false, ClientClassifier.Infrastructure),
            ("anything", null, null, "Apple, Inc.", true, ClientClassifier.Infrastructure),
            (null, null, null, "Dell Inc.", false, ClientClassifier.Computers),
            (null, null, "android", null, false, ClientClassifier.Phones),
            (null, null, null, null, false, ClientClassifier.Unknown),
            (null, "abc123", null, "Apple, Inc.", false, ClientClassifier.Unknown),
        ];
        foreach (var item in cases)
            check(ClientClassifier.Classify(item.Name, item.Hostname, item.Fingerprint, item.Vendor, item.Infrastructure) == item.Group,
                $"The classifier must put {item.Name ?? item.Hostname ?? item.Vendor ?? "an empty client"} in {item.Group}.");
        check(ClientClassifier.Classify(null, "Steam Deck", null, null, false) == ClientClassifier.Entertainment
            && ClientClassifier.Classify("Shark RV2000", null, null, null, false) == ClientClassifier.Iot, "The classifier must know Steam Decks and Shark vacuums.");

        // Building categories, finer than groups.
        (string? Name, string? Hostname, string? Fingerprint, string? Vendor, string Group, string Category)[] kinds =
        [
            ("Zack's Apple Watch", null, null, "Apple, Inc.", ClientClassifier.Phones, "watch"),
            (null, "Zacks-iPhone", null, "Apple, Inc.", ClientClassifier.Phones, "phone"),
            (null, "iPad-Air", null, "Apple, Inc.", ClientClassifier.Phones, "tablet"),
            ("Living Room Apple TV", null, null, null, ClientClassifier.Entertainment, "tv"),
            (null, "SonosZP", null, null, ClientClassifier.Entertainment, "speaker"),
            (null, "Steam Deck", null, null, ClientClassifier.Entertainment, "console"),
            ("HP Printer", null, null, null, ClientClassifier.Iot, "printer"),
            ("Hue Bridge", null, null, null, ClientClassifier.Iot, "hub"),
            (null, "shelly1pm-ABC123", null, null, ClientClassifier.Iot, "light"),
            (null, null, null, "Espressif Inc.", ClientClassifier.Iot, "sensor"),
            ("Shark RV2000", null, null, null, ClientClassifier.Iot, "appliance"),
            (null, "MacBook-Pro", null, null, ClientClassifier.Computers, "computer"),
            (null, "abc123", null, null, ClientClassifier.Phones, "phone"),
            (null, null, null, null, ClientClassifier.Unknown, LabCategories.Other),
            (null, null, null, "Ubiquiti Inc", ClientClassifier.Infrastructure, LabCategories.Network),
        ];
        foreach (var item in kinds)
            check(LabCategories.Device(item.Name, item.Hostname, item.Fingerprint, item.Vendor, item.Group) == item.Category,
                $"The category guess must call {item.Name ?? item.Hostname ?? item.Vendor ?? "an empty client"} a {item.Category}.");
        check(LabCategories.App("plex", ["plexinc/pms-docker"]) == "media" && LabCategories.App("observability", ["grafana/grafana", "prom/prometheus"]) == "monitoring"
            && LabCategories.App("adguard", ["adguard/adguardhome"]) == LabCategories.Network && LabCategories.App("ollama", ["ollama/ollama"]) == "ai"
            && LabCategories.App("mystery", ["acme/thing"]) == LabCategories.Other,
            "Apps must be guessed a category from their name and images.");

        var root = Path.Combine(AppContext.BaseDirectory, "lab-map-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Client overrides: a valid MAC and group, stored lowercase; null clears.
            var overrides = new ClientOverrideStore(root);
            Rejects(() => ClientOverrideStore.Validate("not-a-mac", "iot"), "invalid_client_mac", "An invalid client MAC must be rejected.");
            Rejects(() => ClientOverrideStore.Validate("aa:bb:cc:dd:ee:ff", "toasters"), "invalid_client_group", "An unknown client group must be rejected.");
            check(await overrides.Set("AA:BB:CC:DD:EE:FF", "iot", CancellationToken.None) == "aa:bb:cc:dd:ee:ff"
                && (await overrides.Read(CancellationToken.None)).GetValueOrDefault("aa:bb:cc:dd:ee:ff") == "iot",
                "A client override must be stored by lowercase MAC.");
            await overrides.Set("aa:bb:cc:dd:ee:ff", null, CancellationToken.None);
            check((await overrides.Read(CancellationToken.None)).Count == 0, "Clearing a client override must remove it.");

            // Site districts: a destination id and a built-in category or the owner's own name, tidied; null clears.
            var districts = new SiteDistrictStore(root);
            Rejects(() => SiteDistrictStore.Validate("dest:Bad Key!", "cloud"), "invalid_site", "An invalid destination must be rejected.");
            Rejects(() => SiteDistrictStore.Validate("dest:github.com", "<script>"), "invalid_site_district", "An unsafe district name must be rejected.");
            check(await districts.Set("dest:github.com", "  Dev   tools ", CancellationToken.None) == ("github.com", "Dev tools")
                && await districts.Set("dest:as13335", "CLOUD", CancellationToken.None) == ("as13335", "cloud")
                && (await districts.Read(CancellationToken.None)) is { Count: 2 } stored && stored["github.com"] == "Dev tools",
                "A site district must be stored by destination key, built-in names as their id.");
            await districts.Set("dest:github.com", null, CancellationToken.None);
            check((await districts.Read(CancellationToken.None)).Keys.SequenceEqual(["as13335"]), "Clearing a site district must remove it.");

            // Categories: a client, app, server or NAS id and a built-in category or the owner's own name; null clears.
            var categories = new CategoryStore(root);
            Rejects(() => CategoryStore.Validate("client:nope", "phone"), "invalid_category_object", "A category for a bad client id must be rejected.");
            Rejects(() => CategoryStore.Validate("dest:github.com", "phone"), "invalid_category_object", "A category for an internet site must be rejected.");
            Rejects(() => CategoryStore.Validate("app:plex", "<b>"), "invalid_category", "An unsafe category name must be rejected.");
            check(await categories.Set("client:AA:BB:CC:DD:EE:FF", "WATCH", CancellationToken.None) == ("client:aa:bb:cc:dd:ee:ff", "watch")
                && await categories.Set("app:plex", " Movie  night ", CancellationToken.None) == ("app:plex", "Movie night")
                && (await categories.Read(CancellationToken.None)) is { Count: 2 } kept && kept["client:aa:bb:cc:dd:ee:ff"] == "watch",
                "A category must be stored by object id, built-in names as their id.");
            await categories.Set("app:plex", null, CancellationToken.None);
            check((await categories.Read(CancellationToken.None)).Keys.SequenceEqual(["client:aa:bb:cc:dd:ee:ff"]), "Clearing a category must remove it.");

            // SNMP settings: passphrases are encrypted on disk and never returned.
            var snmp = new SnmpSettingsStore(root, new EphemeralDataProtectionProvider());
            const string auth = "auth-Passphrase-1", priv = "priv-Passphrase-2";
            SnmpSettingsRequest Request(string user = "lucia", string authProtocol = "SHA256", string authPass = auth, string privProtocol = "AES",
                string privPass = priv) => new()
                {
                    Username = user, AuthProtocol = authProtocol, AuthPassphrase = authPass, PrivProtocol = privProtocol, PrivPassphrase = privPass,
                };
            Rejects(() => SnmpSettingsStore.Validate(Request(authPass: "short")), "invalid_snmp_passphrase", "A short SNMP passphrase must be rejected.");
            Rejects(() => SnmpSettingsStore.Validate(Request(privPass: "has'quote1")), "invalid_snmp_passphrase", "A quoted SNMP passphrase must be rejected.");
            Rejects(() => SnmpSettingsStore.Validate(Request(privPass: "has${env:X}1")), "invalid_snmp_passphrase", "An SNMP passphrase with $ must be rejected.");
            Rejects(() => SnmpSettingsStore.Validate(Request(authProtocol: "SHA1")), "invalid_snmp_auth", "An unknown SNMP auth protocol must be rejected.");
            Rejects(() => SnmpSettingsStore.Validate(Request(privProtocol: "3DES")), "invalid_snmp_privacy", "An unknown SNMP privacy protocol must be rejected.");
            Rejects(() => SnmpSettingsStore.Validate(Request(user: "a b")), "invalid_snmp_username", "An SNMP username with spaces must be rejected.");
            var saved = await snmp.Save(Request(), CancellationToken.None);
            var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var status = JsonSerializer.Serialize(await snmp.Status(CancellationToken.None), web);
            var file = File.ReadAllText(Path.Combine(root, "unifi-snmp.json"));
            var credentials = await snmp.Credentials(CancellationToken.None);
            check(saved.Configured && status.Contains("\"configured\":true", StringComparison.Ordinal) && status.Contains("\"authProtocol\":\"SHA256\"", StringComparison.Ordinal)
                && !status.Contains(auth, StringComparison.Ordinal) && !status.Contains(priv, StringComparison.Ordinal)
                && !file.Contains(auth, StringComparison.Ordinal) && !file.Contains(priv, StringComparison.Ordinal)
                && !Request().ToString().Contains(auth, StringComparison.Ordinal)
                && credentials is { Username: "lucia", AuthPassphrase: auth, PrivPassphrase: priv }
                && !credentials.ToString().Contains(auth, StringComparison.Ordinal),
                "SNMP passphrases must be encrypted at rest and never returned or printed.");

            // The collector's SNMP receivers, rendered into the Observability app's environment.
            SnmpTarget[] targets = [new("dev:aa:aa:aa:aa:aa:01", "Core ${env:OTLP_PASSWORD} switch", "192.168.0.2"),
                new("dev:aa:aa:aa:aa:aa:02", "Office AP", "192.168.0.3"), new("dev:aa:aa:aa:aa:aa:03", "Public", "8.8.8.8"), new("gw", "Gateway", "192.168.0.1")];
            var lines = LabCollector.Lines(credentials, targets);
            var collector = lines.Split('\n').Single(line => line.StartsWith("LUCIA_LAB_COLLECTOR='", StringComparison.Ordinal));
            check(LabCollector.Lines(null, targets) == "" && LabCollector.Lines(credentials, []) == ""
                && collector.EndsWith("'", StringComparison.Ordinal) && collector.Count(c => c == '\'') == 2
                && collector.Contains("snmp/0: &snmp {endpoint: \"udp://192.168.0.2:161\"", StringComparison.Ordinal)
                && collector.Contains("snmp/1: {<<: *snmp, endpoint: \"udp://192.168.0.3:161\"}", StringComparison.Ordinal)
                && !collector.Contains("8.8.8.8", StringComparison.Ordinal) && !collector.Contains("snmp/2", StringComparison.Ordinal)
                && collector.Contains("auth_type: SHA256", StringComparison.Ordinal) && collector.Contains("privacy_type: AES,", StringComparison.Ordinal)
                && collector.Contains("auth_password: \"${env:LUCIA_LAB_SNMP_AUTH}\"", StringComparison.Ordinal)
                && collector.Contains("{key: lucia.device, value: \"dev:aa:aa:aa:aa:aa:01\", action: upsert}", StringComparison.Ordinal)
                && collector.Contains("Core --env-OTLP_PASSWORD- switch", StringComparison.Ordinal) && !collector.Contains("${env:OTLP", StringComparison.Ordinal)
                && !collector.Contains(auth, StringComparison.Ordinal) && !collector.Contains(priv, StringComparison.Ordinal)
                && lines.Contains($"LUCIA_LAB_SNMP_AUTH='{auth}'\n", StringComparison.Ordinal) && lines.Contains($"LUCIA_LAB_SNMP_PRIV='{priv}'\n", StringComparison.Ordinal)
                && lines.Contains("LUCIA_LAB_SNMP_USER='lucia'\n", StringComparison.Ordinal)
                && LabCollector.Lines(credentials, Enumerable.Range(1, 40).Select(i => new SnmpTarget($"dev:aa:aa:aa:aa:ab:{i:x2}", "x", $"10.0.0.{i}")))
                    .Contains($"snmp/{LabCollector.MaxTargets - 1}:", StringComparison.Ordinal)
                && !LabCollector.Lines(credentials, Enumerable.Range(1, 40).Select(i => new SnmpTarget($"dev:aa:aa:aa:aa:ab:{i:x2}", "x", $"10.0.0.{i}")))
                    .Contains($"snmp/{LabCollector.MaxTargets}:", StringComparison.Ordinal),
                "The SNMP collector config must poll only private UniFi devices, at most 32, and keep credentials out of the config.");
            check(StackStore.LuciaLines("A=1\n" + lines + "B=2\n") == lines, "Re-rendering the Observability app must keep its lab map variables.");
            if (Environment.GetEnvironmentVariable("LUCIA_LAB_COLLECTOR_DUMP") is { Length: > 0 } dump)
                File.WriteAllText(dump, collector["LUCIA_LAB_COLLECTOR='".Length..^1]);
            await snmp.Delete(CancellationToken.None);
            check(!(await snmp.Status(CancellationToken.None)).Configured && await snmp.Credentials(CancellationToken.None) is null,
                "Removing the SNMP settings must forget them.");
        }
        finally { Directory.Delete(root, recursive: true); }

        // The Observability app takes NetFlow on 2055/udp and the lab map's collector config from its environment.
        var observability = StackCatalog.Find("observability");
        var obs = observability.Render(StackCatalog.Settings(observability, null), new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string>());
        check(obs.Compose.Contains("- \"2055:2055/udp\"", StringComparison.Ordinal)
            && obs.Compose.Contains("\"--config=env:LUCIA_LAB_COLLECTOR\"]", StringComparison.Ordinal)
            && obs.Compose.Contains("LUCIA_LAB_SNMP_AUTH: ${LUCIA_LAB_SNMP_AUTH:-}", StringComparison.Ordinal)
            && obs.Compose.Contains("netflow:", StringComparison.Ordinal) && obs.Compose.Contains("signal_to_metrics/flows:", StringComparison.Ordinal)
            && obs.Compose.Contains("--enable-feature=otlp-native-delta-ingestion", StringComparison.Ordinal)
            && !obs.Compose.Contains("delta_to_cumulative", StringComparison.Ordinal)
            && obs.Compose.Contains("lucia.device", StringComparison.Ordinal) && !obs.Env.Contains("LUCIA_LAB_", StringComparison.Ordinal),
            "The Observability app must receive NetFlow and read SNMP receivers and credentials only from its environment.");

        // The map itself, from UniFi's JSON, two Lucia servers and an app.
        JsonElement[] Rows(string json) => [.. JsonDocument.Parse(json).RootElement.EnumerateArray().Select(row => row.Clone())];
        var topology = new UniFiTopology("default",
            Rows("""
                [{"type":"udm","mac":"AA:00:00:00:00:01","name":"Gateway","model":"UDMPRO","ip":"192.168.0.1","state":1,"upgradable":true,
                  "wan1":{"ip":"203.0.113.9","up":true,"rx_bytes-r":1000,"tx_bytes-r":500},"port_table":[{"port_idx":1,"name":"Port 1","up":true,"speed":1000,"poe_enable":false}]},
                 {"type":"usw","mac":"aa:00:00:00:00:02","name":"Core switch","ip":"192.168.0.2","state":1,"uplink":{"uplink_mac":"aa:00:00:00:00:01","rx_bytes-r":10,"tx_bytes-r":20},
                  "port_table":[{"port_idx":"bad"},{"port_idx":2,"up":false,"speed":0}],"num_sta":3},
                 {"type":"uap","mac":"aa:00:00:00:00:03","name":"Office AP","state":5,"uplink":{"uplink_mac":"aa:00:00:00:00:02"}},
                 {"type":"uph","mac":"aa:00:00:00:00:04"},{"mac":"bad"},42]
                """),
            Rows("""
                [{"_id":"n1","name":"LAN","vlan_enabled":false,"ip_subnet":"192.168.0.1/24","purpose":"corporate","firewall_zone_id":"z1"},
                 {"_id":"n2","name":"IoT","vlan_enabled":true,"vlan":20,"ip_subnet":"192.168.20.1/24","purpose":"corporate"},
                 {"_id":"w1","name":"WAN","purpose":"wan"}]
                """),
            Rows("""[{"_id":"z1","name":"Internal","network_ids":["n1"]},{"_id":"z2","name":"IoT zone","network_ids":["n2"]}]"""),
            Rows("""
                [{"mac":"bb:00:00:00:00:01","ip":"192.168.0.241","network_id":"n1","sw_mac":"aa:00:00:00:00:02","rx_bytes-r":100,"tx_bytes-r":200},
                 {"mac":"bb:00:00:00:00:02","ip":"192.168.0.222","network_id":"n1","sw_mac":"aa:00:00:00:00:02"},
                 {"mac":"bb:00:00:00:00:03","hostname":"Zacks-iPhone","ip":"192.168.0.50","network_id":"n1","ap_mac":"aa:00:00:00:00:03","oui":"Apple, Inc.","rx_bytes-r":7,"tx_bytes-r":3,"last_seen":1760000000},
                 {"mac":"bb:00:00:00:00:04","hostname":"shelly1pm","ip":"192.168.20.9","ap_mac":"aa:00:00:00:00:03"},
                 {"mac":"bb:00:00:00:00:05","name":"Plex VIP","ip":"192.168.0.60"},
                 {"mac":"aa:00:00:00:00:02","ip":"192.168.0.2"},
                 {"mac":"bb:00:00:00:00:06","hostname":"mystery","ip":"10.9.9.9"}]
                """),
            [.. Rows("""[{"mac":"bb:00:00:00:00:03","name":"ignored, already online"},{"mac":"bb:00:00:00:00:07","name":"Old laptop","oui":"Dell Inc."}]"""),
                .. Rows($$"""[{"mac":"bb:00:00:00:00:08","name":"Gone a month","last_seen":{{DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeSeconds()}}},{"mac":"bb:00:00:00:00:09"}]""")],
            Rows("""[{"mac":"bb:00:00:00:00:09","display_name":"HP Printer f7:f1","model_name":"HP Printer"},{"mac":"bb:00:00:00:00:05","model_name":"Not shown"}]"""));
        var now = DateTimeOffset.UtcNow;
        ManagedNodeFacts[] facts = [new(Guid.NewGuid(), "lucialab01", true, null, null, now.AddSeconds(-30), "192.168.0.241"),
            new(Guid.NewGuid(), "lucialab02", false, null, null, now.AddMinutes(-5), "192.168.0.242")];
        NodeContainer plexContainer = new("c1", "lucia-plex-plex-1", "plexinc/pms-docker", "exited", "Exited (137) 2 minutes ago", "lucia-plex", "plex",
            "0.0.0.0:32400->32400/tcp, [::]:32400->32400/tcp, 0.0.0.0:1900->1900/udp, 8443/tcp",
            [new("lucia-plex_default", "172.18.0.2")], [new("bind", "/mnt/lucia/nas/unas/Media/movies", "/movies"), new("volume", "lucia-plex_config", "/config")]);
        var report = new NodeStackReport([], [plexContainer], [], [new("unas", "Media", "Mounted")]);
        var lab = new LabStacks([new LabStack("plex", "lucialab01", "Running", new("plex", "Running", 3, null, []), "192.168.0.60", 2, [plexContainer]),
            new LabStack("orphan", "gone", "Running", null, null, 0, [])],
            new Dictionary<string, NodeStackReport> { ["lucialab01"] = report }, [new LabNas("unas", "192.168.0.10", ["Media", "Backups"])]);
        var built = LabMapService.Compose(now, topology, "connected", null, facts,
            new Dictionary<string, HashSet<string>> { ["lucialab01"] = ["bb:00:00:00:00:01"] }, lab,
            new Dictionary<string, string> { ["bb:00:00:00:00:06"] = ClientClassifier.Computers }, new(true, false, true), false, "192.168.0.222", false, null,
            new Dictionary<string, string> { ["host:lucialab02"] = "ai", ["client:bb:00:00:00:00:04"] = "Garden" });
        var map = built.View;
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var device in map.Devices.Append(map.Gateway!)) parents[device.Id] = device.ParentId;
        foreach (var host in map.Hosts) parents[host.Id] = host.ParentId;
        foreach (var app in map.Apps) { parents[app.Id] = app.HostId; foreach (var container in app.Containers) parents[container.Id] = app.Id; }
        foreach (var group in map.ClientGroups) parents[group.Id] = group.ParentId;
        bool Reaches(string id)
        {
            for (var hops = 0; hops < 16; hops++)
            {
                if (id == "wan") return true;
                if (parents.GetValueOrDefault(id) is not { } parent) return false;
                id = parent;
            }
            return false;
        }
        var members = map.ClientGroups.SelectMany(group => group.Members).ToArray();
        check(map.Gateway is { Id: "dev:aa:00:00:00:00:01", Kind: "gateway", ParentId: "wan", UpdateAvailable: true, Ports: [{ Index: 1, SpeedMbps: 1000 }] }
            && map.Wan is { Address: "203.0.113.9", Health: "ok" }
            && map.Devices is [{ Id: "dev:aa:00:00:00:00:02", Kind: "switch", ParentId: "dev:aa:00:00:00:00:01", ClientCount: 3, Ports: [{ Index: 2, Up: false, SpeedMbps: null }] },
                { Id: "dev:aa:00:00:00:00:03", Kind: "ap", ParentId: "dev:aa:00:00:00:00:02", Health: "warn", Address: null }]
            && map.Networks.Select(network => network.Id).SequenceEqual(["net:n1", "net:n2", "net:other"])
            && map.Networks[1] is { Vlan: 20, ZoneId: "zone:z2" } && map.Networks[0] is { Vlan: null, ZoneId: "zone:z1" }
            && map.Zones.Select(zone => (zone.Id, string.Join(",", zone.NetworkIds))).SequenceEqual([("zone:z1", "net:n1"), ("zone:z2", "net:n2")]),
            "The lab map must read UniFi's gateway, switches, APs, networks and zones, skipping rows it doesn't understand.");
        check(map.Hosts is [{ Id: LabMapService.SparkId, Kind: "spark", Href: "#/ai", Address: "192.168.0.222", ParentId: "dev:aa:00:00:00:00:02", NetworkId: "net:n1" },
                { Id: "host:lucialab01", Health: "ok", ParentId: "dev:aa:00:00:00:00:02", NetworkId: "net:n1", Href: "#/devices" },
                { Id: "host:lucialab02", Health: "stale", ParentId: "dev:aa:00:00:00:00:01", NetworkId: "net:n1" }]
            && map.Apps is [{ Id: "app:plex", HostId: "host:lucialab01", Health: "ok", Href: "#/apps/plex", NetworkId: "net:n1", UpdateCount: 2, Containers:
                [{ Id: "ctr:host:lucialab01:lucia-plex-plex-1", Health: "failed", Networks: [{ Address: "172.18.0.2" }],
                    Mounts: [{ StorageId: "nas:unas" }, { StorageId: null }],
                    Ports: [{ Port: 1900, Protocol: "udp", Service: "iot" }, { Port: 32400, Protocol: "tcp", Service: "streaming" }] }] }]
            && map.Storage is [{ Id: "nas:unas", Health: "warn", Mounts: [{ HostId: "host:lucialab01", Share: "Media", Health: "ok" }, { Share: "Backups", Health: "warn" }] }]
            && built.HostClients.GetValueOrDefault("host:lucialab01") == "bb:00:00:00:00:01",
            "The lab map must place Lucia's servers under their UniFi uplinks and join their apps, containers and NAS mounts.");
        check(members.Select(client => client.Mac).Order(StringComparer.Ordinal).SequenceEqual(["bb:00:00:00:00:03", "bb:00:00:00:00:04", "bb:00:00:00:00:06", "bb:00:00:00:00:07", "bb:00:00:00:00:09"])
            && members.Single(client => client.Mac == "bb:00:00:00:00:09") is { Name: "HP Printer", Group: "iot" }
            && map.ClientGroups.Any(group => group is { Id: "grp:net:n1:phones", ParentId: "dev:aa:00:00:00:00:03", Online: 1, Total: 1 })
            && map.ClientGroups.Any(group => group is { Id: "grp:net:n2:iot", NetworkId: "net:n2" })
            && map.ClientGroups.Any(group => group is { Id: "grp:net:other:computers" } && group.Members.Any(client => client is { Mac: "bb:00:00:00:00:06", Overridden: true }))
            && members.Single(client => client.Mac == "bb:00:00:00:00:07") is { Online: false, Group: "computers", Vendor: "Dell Inc.", ParentId: null }
            && members.Single(client => client.Mac == "bb:00:00:00:00:03").LastSeenAt == DateTimeOffset.FromUnixTimeSeconds(1760000000)
            && built.Addresses.GetValueOrDefault("192.168.0.50") == "client:bb:00:00:00:00:03"
            && built.Addresses.GetValueOrDefault("192.168.0.10") == "nas:unas" && built.Addresses.GetValueOrDefault("192.168.0.1") == "dev:aa:00:00:00:00:01" && built.Addresses.GetValueOrDefault("192.168.0.60") == "app:plex"
            && built.ClientRates.GetValueOrDefault("bb:00:00:00:00:03") == (3, 7),
            "Client groups must leave out UniFi devices, Lucia's machines and apps, and clients gone over a month, name nameless clients by UniFi's fingerprint, and honour the owner's groups.");
        check(parents.Keys.All(Reaches), "Every object on the lab map must lead to the gateway and the WAN.");
        check(map.Apps[0] is { Category: "media", CategoryChosen: false } && map.Storage[0] is { Category: LabCategories.Storage }
            && map.Hosts[0] is { Category: LabCategories.Server, CategoryChosen: false } && map.Hosts[2] is { Category: "ai", CategoryChosen: true }
            && members.Single(client => client.Mac == "bb:00:00:00:00:03") is { Category: "phone", CategoryChosen: false }
            && members.Single(client => client.Mac == "bb:00:00:00:00:04") is { Category: "Garden", CategoryChosen: true }
            && members.Single(client => client.Mac == "bb:00:00:00:00:09") is { Category: "printer" },
            "Buildings must carry Lucia's category guess, or the owner's choice.");
        var unifiRates = new Dictionary<string, LabRate>(StringComparer.Ordinal);
        LabMapService.UnifiRates(built, unifiRates);
        check(unifiRates.GetValueOrDefault("client:bb:00:00:00:00:03") == new LabRate(24, 56) && unifiRates.GetValueOrDefault("grp:net:n1:phones") == new LabRate(24, 56)
            && built.ClientRates.Keys.All(mac => unifiRates.ContainsKey("client:" + mac)) && !unifiRates.ContainsKey("client:bb:00:00:00:00:07"),
            "Live rates must carry every online client by its own id beside its group's sum.");
        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        check(json.Contains("\"clientGroups\":[", StringComparison.Ordinal) && json.Contains("\"unifi\":{\"state\":\"connected\"", StringComparison.Ordinal)
            && json.Contains("\"sources\":{\"snmp\":true,\"netflow\":false,\"dockerStats\":true}", StringComparison.Ordinal)
            && json.Contains("\"speedMbps\":1000", StringComparison.Ordinal) && json.Contains("\"traffic\":\"reduced\"", StringComparison.Ordinal)
            && !json.Contains("\"Id\"", StringComparison.Ordinal),
            "The lab map must serialize as the contract's camelCase JSON.");

        // Without UniFi, Lucia's own machines hang off a stand-in gateway.
        var alone = LabMapService.Compose(now, null, "not-connected", "Connect UniFi.", facts, new Dictionary<string, HashSet<string>>(), lab,
            new Dictionary<string, string>(), new(false, false, false), false, null, false, null).View;
        check(alone.Gateway is { Id: LabMapService.StandInGateway, Health: "unknown", ParentId: "wan" } && alone.Traffic == "none" && alone.ClientGroups.Length == 0
            && alone.Hosts.All(host => host.ParentId == LabMapService.StandInGateway) && alone.Unifi.State == "not-connected" && alone.Networks.Length == 0,
            "Without UniFi the lab map must still draw Lucia's servers under a stand-in gateway.");

        // Prometheus' answers, mapped to the lab map's objects.
        check(PrometheusQuery.Parse("""{"status":"success","data":{"resultType":"vector","result":[{"metric":{"lucia_node":"a"},"value":[1,"2.5"]},{"metric":{},"value":[1,"NaN"]}]}}"""u8.ToArray())
                is [{ Value: 2.5 } sample] && sample.Labels["lucia_node"] == "a"
            && PrometheusQuery.Parse("""{"status":"error"}"""u8.ToArray()) is null, "Prometheus answers must be parsed strictly.");
        PromSample S(double value, params (string, string)[] labels) => new(labels.ToDictionary(label => label.Item1, label => label.Item2), value);
        var reading = LabTraffic.Reading([
            [S(100, ("lucia_node", "lucialab01"))], [S(50, ("lucia_node", "lucialab01"))],
            [S(10, ("lucia_node", "lucialab01"), ("container_name", "lucia-plex-plex-1"))], null,
            [S(1, ("lucia_device", "dev:x"), ("if_name", "eth0"), ("direction", "receive")), S(1000, ("lucia_device", "dev:x"), ("if_name", "eth1"), ("direction", "transmit")),
                S(5, ("lucia_device", "dev:x"), ("if_name", "eth1"), ("direction", "receive"))],
            [S(125, ("lucia_src", "192.168.0.50"), ("lucia_dst", "142.250.1.1"), ("lucia_port", "443"), ("lucia_proto", "tcp"), ("lucia_server", "dst")),
                S(1, ("lucia_src", "192.168.0.60"), ("lucia_dst", "192.168.0.50"), ("lucia_port", ""), ("lucia_proto", "udp"), ("lucia_server", ""))],
            [S(12.34, ("lucia_node", "lucialab01"), ("container_name", "lucia-plex-plex-1"))], [], [S(1760000000, [])]]);
        check(reading.Nodes["lucialab01"] == new LabRate(800, 400) && reading.Containers[("lucialab01", "lucia-plex-plex-1")] == new LabRate(80, 0)
            && reading.Devices["dev:x"] == new LabRate(40, 8000) && reading.Flows is [{ Source: "192.168.0.50", Destination: "142.250.1.1", Port: 443, Protocol: "tcp", Server: "dst", Bps: 1000 },
                { Port: null, Server: null, Bps: 8 }]
            && reading.ContainerLoad[("lucialab01", "lucia-plex-plex-1")] == new LabLoad(12.3, null)
            && reading.Sources == new LabSources(true, true, true) && reading.LastSnmpAt == DateTimeOffset.FromUnixTimeSeconds(1760000000)
            && LabTraffic.Queries.All(query => query.Contains("lucia_", StringComparison.Ordinal) || query.Contains("container_", StringComparison.Ordinal)),
            "Prometheus' rates must become bits per second keyed by server, container and UniFi device.");
        check(LabTraffic.QueriesFor("24h") is var day && day.Count(query => query.Contains("[24h]", StringComparison.Ordinal)) == 6
            && !day.Any(query => query.Contains("[1m]", StringComparison.Ordinal)) && LabTraffic.Windows.Keys.SequenceEqual(["1m", "15m", "1h", "24h"])
            && LabTraffic.QueriesFor("1h", 4000)[5].StartsWith("topk(4000,", StringComparison.Ordinal)
            && LabTraffic.QueriesFor("24h")[5].EndsWith("[24h])) / 86400)", StringComparison.Ordinal)
            && LabTraffic.Queries[5].Contains("sum_over_time(", StringComparison.Ordinal)
            && LabFlows.Sites(null) == 40 && LabFlows.Sites(1000) == 1000 && LabFlows.Sites(0) == PrometheusQuery.MaxSeries && LabFlows.Sites(7) == 40,
            "The map's traffic window must average every rate over the window chosen, for as many internet sites as asked.");
        check(LabMapService.ContainerHealth(plexContainer with { State = "running", Status = "Up 2 hours (unhealthy)" }, "Running") == "warn"
            && LabMapService.ContainerHealth(plexContainer with { State = "exited", Status = "Exited (0) 1 hour ago" }, "Stopped") == "ok"
            && LabMapService.ContainerHealth(plexContainer with { State = "exited", Status = "Exited (0) 1 hour ago" }, "Running") == "warn"
            && LabMapService.StackHealth(lab.Stacks[0] with { Status = new("plex", "Stopped", 3, null, []) }) == "warn"
            && LabMapService.StackHealth(lab.Stacks[0] with { Status = new("plex", "Failed", 3, "boom", []) }) == "failed",
            "Lab map health must follow the contract's mapping.");

        // Traffic types: services by server port, destination categories by name or owner, Docker's published ports.
        check(TrafficKinds.Service(443, "tcp") == "web" && TrafficKinds.Service(8081, "tcp") == "web" && TrafficKinds.Service(8081, "udp") == "other"
            && TrafficKinds.Service(8096, "tcp") == "streaming" && TrafficKinds.Service(8009, "tcp") == "streaming" && TrafficKinds.Service(53, "udp") == "dns"
            && TrafficKinds.Service(5353, "udp") == "iot" && TrafficKinds.Service(27015, "udp") == "gaming" && TrafficKinds.Service(3478, "udp") == "gaming"
            && TrafficKinds.Service(5222, "udp") == "gaming" && TrafficKinds.Service(5222, "tcp") == "other" && TrafficKinds.Service(22, "tcp") == "remote"
            && TrafficKinds.Service(51820, "udp") == "vpn" && TrafficKinds.Service(993, "tcp") == "mail" && TrafficKinds.Service(445, "tcp") == "files"
            && TrafficKinds.Service(123, "udp") == "time" && TrafficKinds.Service(null, "tcp") == "other" && TrafficKinds.Service(443, "icmp") == "other"
            && new[] { 80, 53, 554, 3074, 22, 500, 25, 21, 1883, 123, 9999 }.Select(port => TrafficKinds.Service(port, "tcp")).All(TrafficKinds.Services.Contains),
            "Server ports must map to the contract's services.");
        check(TrafficKinds.Category(["rr3---sn-abc.googlevideo.com"], "Google LLC") == "streaming" && TrafficKinds.Category(["steamcontent.com"], null) == "gaming"
            && TrafficKinds.Category(["scontent.fbcdn.net"], null) == "social" && TrafficKinds.Category(["netflix.com"], null) == "streaming"
            && TrafficKinds.Category(["x.com"], null) == "social" && TrafficKinds.Category(["discord.gg"], null) == "comms"
            && TrafficKinds.Category(["fe2.update.microsoft.com"], null) == "updates" && TrafficKinds.Category(["www.microsoft.com"], null) == "cloud"
            && TrafficKinds.Category(["example.org"], "Cloudflare, Inc.") == "cdn" && TrafficKinds.Category([], "Sony Interactive Entertainment") == "gaming"
            && TrafficKinds.Category([], "Amazon.com, Inc.") == "cloud" && TrafficKinds.Category(["example.org"], "Tiny ISP") == "other"
            && TrafficKinds.Category(["shopify.com"], null) == "other",
            "Destinations must be categorised by name first, then by their network's owner.");
        check(TrafficKinds.Registrable("rr3.sn-abc.googlevideo.com.") == "googlevideo.com" && TrafficKinds.Registrable("news.bbc.co.uk") == "bbc.co.uk"
            && TrafficKinds.Registrable("Plex.TV") == "plex.tv" && TrafficKinds.Registrable("localhost") is null && TrafficKinds.Registrable("a..b") is null,
            "Names must reduce to their registrable domain.");
        check(TrafficKinds.Listens("0.0.0.0:8080->80/tcp, [::]:8080->80/tcp, :::53->53/udp, 0.0.0.0:7000-7002->7000-7002/tcp, 9000/tcp, 0.0.0.0:1->1/sctp, junk")
                .Select(item => $"{item.Port}/{item.Protocol}/{item.Service}")
                .SequenceEqual(["53/udp/dns", "7000/tcp/other", "7001/tcp/other", "7002/tcp/other", "8080/tcp/web"])
            && TrafficKinds.Listens(null).Length == 0 && TrafficKinds.Listens("0.0.0.0:1-65535->1-65535/tcp").Length == 32,
            "Docker's published ports must become host ports, deduplicated and bounded.");

        // The IP-to-ASN table and AdGuard's query log.
        var asn = AsnRanges<uint>.Parse(new StringReader("1.0.0.0\t1.0.0.255\t13335\tUS\tCLOUDFLARENET - Cloudflare, Inc.\n1.0.1.0\t1.0.1.255\t0\tNone\tNot routed\n"
            + "8.8.4.0\t8.8.4.255\t15169\tUS\tGOOGLE - Google LLC\n8.8.8.0\t8.8.8.255\t15169\tUS\tGOOGLE - Google LLC\n8.8.9.0\t8.8.9.255\t15169\tUS\tGOOGLE - Google LLC\nbad\n"),
            LabAsn.V4);
        check(asn.Count == 3 && asn.Find(LabAsn.V4(System.Net.IPAddress.Parse("8.8.8.8"))!.Value) is { Asn: 15169, Org: "Google LLC", Country: "US" }
            && asn.Find(LabAsn.V4(System.Net.IPAddress.Parse("8.8.9.200"))!.Value)?.Asn == 15169
            && asn.Find(LabAsn.V4(System.Net.IPAddress.Parse("1.0.1.5"))!.Value) is null && asn.Find(LabAsn.V4(System.Net.IPAddress.Parse("9.9.9.9"))!.Value) is null
            && asn.Find(LabAsn.V4(System.Net.IPAddress.Parse("1.0.0.1"))!.Value) is { Asn: 13335, Org: "Cloudflare, Inc." },
            "The IP-to-ASN table must skip unrouted ranges, merge neighbours and find an address's owner.");
        var log = LabDnsNames.Parse("""
            {"oldest":"2026-10-10T10:00:00.5Z","data":[
             {"time":"2026-10-10T10:00:05Z","question":{"name":"www.netflix.com.","type":"A"},"answer":[{"type":"CNAME","value":"www.dradis.netflix.com"},
               {"type":"A","value":"52.1.2.3","ttl":60},{"type":"AAAA","value":"2a05:d018::1"},{"type":"A","value":"192.168.0.9"}]},
             {"time":"2026-10-10T10:00:00.5Z","question":{"name":"nas.lan","type":"A"},"answer":null},
             {"time":"bad"}]}
            """u8.ToArray());
        check(log.Answers.Select(item => $"{item.Address} {item.Name}").SequenceEqual(["52.1.2.3 www.netflix.com", "2a05:d018::1 www.netflix.com"])
            && log.Oldest is { Text: "2026-10-10T10:00:00.5Z" } && log.Answers[0].At == DateTimeOffset.Parse("2026-10-10T10:00:05Z"),
            "AdGuard's query log must map internet answers to the name asked for.");

        // Flow samples joined to the map: clients, apps, the internet city, server ports and NAT's second copy.
        var places = new Dictionary<string, string> { ["192.168.0.50"] = "client:bb:00:00:00:00:03", ["192.168.0.60"] = "app:plex", ["192.168.0.241"] = "host:lucialab01" };
        var dns = new Dictionary<string, string> { ["52.1.2.3"] = "occ-0-1.nflxvideo.net", ["52.1.2.4"] = "api.nflxvideo.net", ["140.82.1.1"] = "github.com" };
        var owners = new Dictionary<string, AsnOwner> { ["8.8.8.8"] = new(15169, "US", "Google LLC"), ["52.1.2.3"] = new(16509, "US", "Amazon.com, Inc.") };
        LabFlowSample F(string from, string to, int? port, string server, double bps, string proto = "tcp") => new(from, to, port, proto, server, bps);
        var joined = LabFlows.Aggregate([
                F("52.1.2.3", "192.168.0.50", 443, "src", 8000), F("192.168.0.50", "52.1.2.4", 443, "dst", 1000), F("192.168.0.50", "8.8.8.8", 53, "dst", 100, "udp"),
                F("192.168.0.50", "198.51.100.7", 3389, "dst", 50), F("198.51.100.20", "192.168.0.60", 32400, "dst", 400),
                F("192.168.0.50", "192.168.0.60", 32400, "dst", 2000), F("192.168.0.60", "192.168.0.50", 32400, "src", 6000),
                F("203.0.113.9", "52.1.2.3", 443, "dst", 9999), F("192.168.0.50", "224.0.0.251", 5353, "dst", 10, "udp"),
                F("2001:db8::5", "2a00:1450::1", 443, "dst", 300), F("2001:db8::5", "2a00:1450::2", null, "", 300)],
            places, "203.0.113.9", ip => dns.GetValueOrDefault(ip.ToString()), ip => owners.GetValueOrDefault(ip.ToString()));
        var netflix = joined.Destinations.FirstOrDefault(item => item.Id == "dest:nflxvideo.net");
        check(netflix is { Name: "nflxvideo.net", Category: "streaming", Org: "Amazon.com, Inc.", Asn: "AS16509", Bps: 9000 }
                && netflix.Domains.SequenceEqual(["occ-0-1.nflxvideo.net", "api.nflxvideo.net"]) && netflix.Mix.SequenceEqual([new KeyValuePair<string, double>("streaming", 9000)])
            && joined.Destinations.Any(item => item is { Id: "dest:as15169", Name: "Google LLC", Category: "cloud", Asn: "AS15169" } && item.Domains.Length == 0)
            && joined.Destinations.Any(item => item is { Id: "dest:198.51.100.0/24", Name: "198.51.100.0/24", Category: "other", Org: null, Bps: 450 }
                && item.Mix["streaming"] == 400 && item.Mix["remote"] == 50)
            && joined.Destinations.Any(item => item is { Id: "dest:2a00:1450::/48", Bps: 300 }) && joined.Destinations.Length == 4
            && joined.Destinations[0].Id == "dest:nflxvideo.net",
            "Internet destinations must be keyed by AdGuard's domain, else the owner's AS, else the /24, and categorised.");
        check(joined.Flows.Any(flow => flow is { From: "client:bb:00:00:00:00:03", To: "dest:nflxvideo.net", Bps: 9000, Service: "streaming", Label: "nflxvideo.net · streaming" })
            && joined.Flows.Any(flow => flow is { From: "client:bb:00:00:00:00:03", To: "app:plex", Bps: 2000, Service: "streaming" })
            && joined.Flows.Any(flow => flow is { From: "app:plex", To: "client:bb:00:00:00:00:03", Bps: 6000, Label: "192.168.0.60 → 192.168.0.50 · streaming" })
            && joined.Flows.Any(flow => flow is { From: "app:plex", To: "dest:198.51.100.0/24", Service: "streaming" })
            && joined.Flows.All(flow => !flow.To.Contains("203.0.113", StringComparison.Ordinal) && flow.From != flow.To)
            && joined.Mix["client:bb:00:00:00:00:03"] is { } phone && phone["streaming"] == 17000 && phone["dns"] == 100 && phone["remote"] == 50 && !phone.ContainsKey("iot")
            && joined.Mix["app:plex"]["streaming"] == 8400
            && joined.Listening["app:plex"] is [{ Port: 32400, Protocol: "tcp", Service: "streaming", Bps: 8400 }]
            && !joined.Listening.ContainsKey("client:bb:00:00:00:00:03"),
            "Flows must fold both directions, credit each object's mix and give server ports to the end that served them.");
        var crowd = LabFlows.Aggregate(Enumerable.Range(1, 60).Select(i => F("192.168.0.50", $"198.51.{i}.1", 443, "dst", i)), places, null, _ => null, _ => null);
        check(crowd.Destinations.Length == LabFlows.MaxDestinations && crowd.Flows.Any(flow => flow is { To: LabMapService.Wan, Label: "Internet · web", Bps: 210 })
            && crowd.Flows.Length <= LabFlows.MaxFlows,
            "Destinations beyond the busiest forty must fold into the Internet.");
        var moved = LabFlows.Aggregate([F("192.168.0.50", "198.51.7.1", 443, "dst", 5)], places, null, _ => null, _ => null, districts: new Dictionary<string, string> { ["198.51.7.0/24"] = "Dev tools" });
        check(moved.Destinations is [{ Category: "Dev tools", Chosen: true }] && crowd.Destinations.All(item => !item.Chosen),
            "The owner's district must replace Lucia's guess for that destination.");
        check(obs.Compose.Contains("lucia.server", StringComparison.Ordinal) && obs.Compose.Contains("- key: lucia.port", StringComparison.Ordinal)
            && !obs.Compose.Contains("\"wan\")", StringComparison.Ordinal)
            && LabTraffic.Queries[5].Contains("lucia_server", StringComparison.Ordinal),
            "The collector must label flows with raw addresses, transport and server port.");
        var live = JsonSerializer.Serialize(new LabMapLive(now, "full", [], [], [], joined.Flows, joined.Mix, joined.Listening, joined.Destinations),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        check(live.Contains("\"service\":\"streaming\"", StringComparison.Ordinal) && live.Contains("\"listening\":{\"app:plex\":[{\"port\":32400,\"protocol\":\"tcp\"", StringComparison.Ordinal)
            && live.Contains("\"destinations\":[{\"id\":\"dest:nflxvideo.net\",\"name\":\"nflxvideo.net\",\"category\":\"streaming\"", StringComparison.Ordinal),
            "The live traffic must serialize as the contract's camelCase JSON.");
    }
}
