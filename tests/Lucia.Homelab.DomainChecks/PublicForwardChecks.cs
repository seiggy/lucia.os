using System.Text.Json;
using Lucia.Homelab.Server.Host;

/// <summary>The router forward public access keeps, including an internet port other than 443 for ISPs that block it.</summary>
internal static class PublicForwardChecks
{
    internal static Task Run(Action<bool, string> check)
    {
        const string spark = "192.168.0.222";
        static JsonElement Forward(string id, string name, string dstPort, string fwd, string fwdPort, string enabled = "true",
            string proto = "tcp", string? src = null) =>
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["_id"] = id, ["name"] = name, ["dst_port"] = dstPort, ["fwd"] = fwd, ["fwd_port"] = fwdPort,
                ["enabled"] = enabled == "true", ["proto"] = proto, ["src"] = src ?? "any",
            });
        var plan = UniFiConnectionService.PlanPublicForward;
        var name = UniFiConnectionService.PublicForwardName;

        var fresh = plan([], true, spark, 8445, 443);
        check(fresh is { State: "Forwarding", TargetId: null, Disable.Length: 0 } && (string)fresh.Body!["dst_port"] == "443"
            && (string)fresh.Body["fwd_port"] == "8445" && (string)fresh.Body["src"] == "any" && (string)fresh.Body["pfwd_interface"] == "wan",
            "With no forward, Lucia creates one from WAN 443 to the gateway's public entrypoint.");

        var steady = plan([Forward("a", name, "443", spark, "8445")], true, spark, 8445, 443);
        check(steady is { State: "Forwarding", Body: null, Disable.Length: 0 }, "A forward that is already right is left alone.");

        // The owner's ISP blocks 443: Lucia's own forward moves to the chosen port.
        var moved = plan([Forward("a", name, "443", spark, "8445")], true, spark, 8445, 10050);
        check(moved is { State: "Forwarding", TargetId: "a", Disable.Length: 0 } && (string)moved.Body!["dst_port"] == "10050"
            && !moved.Body.ContainsKey("src") && !moved.Body.ContainsKey("proto"),
            "Changing the internet port moves Lucia's forward without touching its source or protocol.");

        // The owner already forwards that port, limited to Cloudflare's addresses, to a stale address.
        JsonElement[] owner = [Forward("a", name, "443", spark, "8445"), Forward("b", "Cloudflare-MetalLB", "10050", "192.168.0.251", "443", src: "cloudflare-group")];
        var adopted = plan(owner, true, spark, 8445, 10050);
        check(adopted is { State: "Adopted", TargetId: "b" } && adopted.Disable.SequenceEqual(["a"])
            && (string)adopted.Body!["fwd"] == spark && (string)adopted.Body["fwd_port"] == "8445" && (string)adopted.Body["name"] == name
            && !adopted.Body.ContainsKey("src") && !adopted.Body.ContainsKey("proto") && !adopted.Body.ContainsKey("pfwd_interface"),
            "An existing forward on the port is adopted and pointed at the gateway, keeping its Cloudflare-only source, and Lucia's 443 forward is disabled.");

        // The next check after adoption: the adopted forward now carries Lucia's name and must not be reset to "any".
        JsonElement[] after = [Forward("a", name, "443", spark, "8445", enabled: "false"), Forward("b", name, "10050", spark, "8445", proto: "tcp_udp", src: "cloudflare-group")];
        check(plan(after, true, spark, 8445, 10050) is { State: "Forwarding", Body: null, Disable.Length: 0 },
            "An adopted forward stays as it is on later checks, source restriction included.");

        var off = plan(after, false, spark, 8445, 10050);
        check(off is { State: "Off", Body: null } && off.Disable.SequenceEqual(["b"]), "Turning public access off disables every live Lucia forward.");
        return Task.CompletedTask;
    }
}
