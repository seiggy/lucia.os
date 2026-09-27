using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

internal static class DhcpChecks
{
    private const string Net = "600ee77e6510f70510300a21";

    internal static async Task Run(Action<bool, string> check)
    {
        static string Id(int n) => n.ToString("x24");
        UniFiClient Client(int id, string mac, string? ip, bool fixedIp = false, string? fixedAddress = null, string? network = Net) =>
            new(Id(id), mac, ip, network, fixedIp, fixedAddress);
        DhcpNode Node(string name, string address, params string[] macs) => new(name, address, macs);

        var nodes = new[]
        {
            Node("fresh", "192.168.0.241", "A0:36:BC:AD:D8:29"),
            Node("pinned", "192.168.0.50", "00:11:22:33:44:55"),
            Node("owner", "192.168.0.60", "00:11:22:33:44:66"),
            Node("taken", "192.168.0.70", "00:11:22:33:44:77"),
            Node("offline", "192.168.0.80", "00:11:22:33:44:88"),
            Node("moved", "192.168.0.90", "00:11:22:33:44:99"),
            Node("unknown", "192.168.0.100"),
        };
        var known = new[]
        {
            Client(2, "00:11:22:33:44:55", null, true, "192.168.0.50"),
            Client(3, "00:11:22:33:44:66", null, true, "192.168.0.61"),
            Client(9, "de:ad:be:ef:00:01", null, true, "192.168.0.70"),
        };
        var online = new[]
        {
            Client(1, "a0:36:bc:ad:d8:29", "192.168.0.241"),
            Client(2, "00:11:22:33:44:55", "192.168.0.50", true, "192.168.0.50"),
            Client(3, "00:11:22:33:44:66", "192.168.0.60", true, "192.168.0.61"),
            Client(4, "00:11:22:33:44:77", "192.168.0.70"),
            Client(6, "00:11:22:33:44:99", "192.168.0.91"),
        };
        var (states, actions) = ManagedNodeDhcp.Plan(nodes, known, online);
        string State(string name) => states.Single(state => state.Hostname == name).State;
        check(State("fresh") == "Reserving" && states[0].Mac == "a0:36:bc:ad:d8:29", "An unreserved node lease was not planned for reservation.");
        check(State("pinned") == "Reserved", "A matching reservation was not recognised.");
        check(State("owner") == "ReservedElsewhere", "An owner-chosen reservation was not left alone.");
        check(State("taken") == "AddressTaken", "An address reserved by another client was not protected.");
        check(State("offline") == "NotSeen" && State("moved") == "NotSeen" && State("unknown") == "NotSeen",
            "A node without a live lease at its address was acted on.");
        check(actions is [{ Index: 0, Address: "192.168.0.241", NetworkId: Net }] && actions[0].Client.Id == Id(1),
            "Only the unreserved live lease should produce a reservation.");
        var noNetwork = ManagedNodeDhcp.Plan([nodes[0]], [], [Client(1, "a0:36:bc:ad:d8:29", "192.168.0.241", network: null)]).Actions;
        check(noNetwork.Length == 0, "A lease without a network was reserved.");
        var spark = new DhcpNode("Lucia host", "192.168.0.222", null);
        var (sparkStates, sparkActions) = ManagedNodeDhcp.Plan([spark, Node("fresh", "192.168.0.241", "A0:36:BC:AD:D8:29")], known,
            [.. online, Client(7, "48:b0:2d:00:00:01", "192.168.0.222")]);
        check(sparkStates[0] is { State: "Reserving", Host: true, Mac: "48:b0:2d:00:00:01" } && !sparkStates[1].Host
            && sparkActions.Any(action => action.Index == 0 && action.Client.Id == Id(7)), "The Spark's own lease was not planned for reservation.");
        check(ManagedNodeDhcp.Plan([spark], [], online).States[0].State == "NotSeen", "The Spark was reserved without a live lease at its address.");

        var fake = new FakeDhcp(known, online);
        var applied = await ManagedNodeDhcp.Apply(fake, nodes, NullLogger.Instance, CancellationToken.None);
        check(fake.Reserved.SequenceEqual([(Id(1), "192.168.0.241", Net)]) && applied[0].State == "Reserved",
            "The reservation was not applied or reported.");
        fake.Fail = true;
        check((await ManagedNodeDhcp.Apply(fake, [nodes[0]], NullLogger.Instance, CancellationToken.None))[0].State == "Failed",
            "A refused reservation was not reported as failed.");
        check((await ManagedNodeDhcp.Apply(new FakeDhcp([], []) { Fail = true }, [], NullLogger.Instance, CancellationToken.None)).Length == 0,
            "UniFi was queried with no managed nodes.");

        UniFiConnectionRequest Request(string url = "https://192.168.0.1", string key = "abcdefghijklmnopqrstuvwx", string site = "default", string? pin = null) =>
            new() { BaseUrl = url, ApiKey = key, Site = site, CertificateSha256 = pin };
        check(UniFiValidation.Connection(Request("https://192.168.0.1/")) == ("https://192.168.0.1", null), "A trailing slash was not normalised.");
        check(UniFiValidation.Connection(Request(pin: new string('a', 64))).Pin == new string('a', 64), "A certificate pin was dropped.");
        foreach (var bad in new[]
        {
            Request("http://192.168.0.1"), Request("https://192.168.0.1/network"), Request("https://user@192.168.0.1"), Request("https://192.168.0.1?x"),
            Request(key: "short"), Request(key: "abcdefghijklmnop qrstuvwx"), Request(site: "de fault"), Request(site: "../x"),
            Request(pin: new string('A', 64)), Request(pin: "abc"),
        })
        {
            var rejected = false;
            try { UniFiValidation.Connection(bad); }
            catch (UniFiException error) { rejected = error.StatusCode == 400; }
            check(rejected, "Invalid UniFi connection input was accepted.");
        }
        check(UniFiValidation.Id(Net) && !UniFiValidation.Id("../../rest") && UniFiValidation.Mac("a0:36:bc:ad:d8:29") && !UniFiValidation.Mac("a0-36"),
            "UniFi identifier validation is wrong.");
    }

    private sealed class FakeDhcp(UniFiClient[] known, UniFiClient[] online) : IDhcpReservations
    {
        public event Action? Changed { add { } remove { } }
        public bool Fail { get; set; }
        public List<(string, string, string)> Reserved { get; } = [];
        public Task<UniFiConnectionStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(UniFiClient[] Known, UniFiClient[] Online)> ListClientsAsync(CancellationToken ct = default) =>
            Fail && known.Length == 0 ? throw new InvalidOperationException("Queried.") : Task.FromResult((known, online));
        public Task ReserveAsync(UniFiClient client, string address, string networkId, CancellationToken ct = default)
        {
            if (Fail) throw new UniFiException(409, "unifi_rejected", "Refused.");
            Reserved.Add((client.Id, address, networkId));
            return Task.CompletedTask;
        }
    }
}
