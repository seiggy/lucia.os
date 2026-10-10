namespace Lucia.Homelab.Server.LabMap;

/// <summary>
/// What kind of thing a lab map building is, which picks its colour: finer than its group, so a phone and a watch differ.
/// Guessed from the same text as <see cref="ClientClassifier"/> for clients and from the stack and its images for apps.
/// </summary>
public static class LabCategories
{
    public const string Other = "other", Server = "server", Storage = "storage", Network = "network";
    public static readonly string[] Devices = ["phone", "watch", "tablet", "computer", "tv", "speaker", "console", "camera", "printer", "light",
        "sensor", "hub", "appliance", Network, Server, Storage];
    public static readonly string[] Apps = ["media", "ai", "automation", "monitoring", "dev", "database"];
    public static readonly string[] All = [.. Devices, .. Apps, Other];

    // Checked in order, so "apple watch" is a watch before "apple" or "watch tv" could say anything else.
    private static readonly (string Category, string[] Words)[] DeviceHints =
    [
        ("watch", ["watch"]),
        ("tablet", ["ipad", "tablet", "kindle", "fire hd", "galaxy tab"]),
        ("console", ["playstation", "ps4", "ps5", "xbox", "nintendo", "steam deck", "steamdeck", "steam-deck"]),
        ("tv", ["appletv", "apple-tv", "apple tv", "roku", "chromecast", "firetv", "fire-tv", "fire tv", "shield", "smarttv", "smart-tv", "bravia",
            "webos", "tizen", "vizio", "television", "-tv", "tv-", "projector"]),
        ("speaker", ["sonos", "homepod", "echo", "alexa", "nest-audio", "nest audio", "nest mini", "google-home", "googlehome", "google home", "speaker",
            "soundbar", "denon", "yamaha", "bose"]),
        ("phone", ["iphone", "android", "pixel", "galaxy", "oneplus", "phone"]),
        ("camera", ["camera", "doorbell", "reolink", "hikvision", "dahua", "wyze", "ring-", "-cam", "cam-", "webcam", "nvr"]),
        ("printer", ["printer", "laserjet", "officejet", "deskjet", "pixma", "epson", "brother"]),
        ("appliance", ["roomba", "vacuum", "shark", "washer", "dryer", "fridge", "refrigerator", "oven", "dishwasher", "purifier", "tesla", "wallbox"]),
        ("hub", ["hub", "bridge", "home-assistant", "homeassistant", "smartthings", "hubitat", "zigbee", "matter", "thread"]),
        ("light", ["hue", "bulb", "light", "lamp", "wled", "plug", "outlet", "kasa", "shelly", "sonoff", "tasmota", "meross", "lifx", "nanoleaf"]),
        ("sensor", ["sensor", "thermostat", "ecobee", "nest", "esp32", "esp8266", "esp-", "espressif", "weather", "airgradient", "co2"]),
        ("computer", ["macbook", "imac", "mac-mini", "macmini", "mac mini", "mac-pro", "laptop", "desktop", "notebook", "thinkpad", "surface",
            "workstation", "-pc", "pc-", "windows", "ubuntu", "debian", "fedora", "linux", "raspberry", "nuc", "framework"]),
    ];

    private static readonly (string Category, string[] Words)[] VendorHints =
    [
        (Network, ["ubiquiti"]),
        ("printer", ["brother", "canon", "seiko epson", "hp inc", "lexmark", "xerox"]),
        ("speaker", ["sonos", "bose", "denon", "amazon"]),
        ("tv", ["roku", "vizio", "hisense", "tcl", "lg electronics", "nvidia"]),
        ("console", ["sony interactive", "nintendo", "valve"]),
        ("camera", ["ring", "wyze", "reolink", "hikvision", "dahua"]),
        ("appliance", ["irobot", "sharkninja", "tesla"]),
        ("light", ["signify", "philips lighting", "itead", "shelly", "allterco", "tuya", "meross", "tp-link", "lifx", "nanoleaf"]),
        ("sensor", ["espressif", "silicon labs", "lumi", "ecobee", "nest"]),
        ("computer", ["dell", "lenovo", "hewlett", "intel corporate", "micro-star", "asustek", "gigabyte", "framework", "raspberry", "microsoft",
            "liteon", "azurewave", "realtek"]),
        ("phone", ["oneplus", "motorola mobility", "huawei", "xiaomi", "oppo", "vivo mobile", "zte", "fairphone"]),
    ];

    private static readonly (string Category, string[] Words)[] AppHints =
    [
        ("ai", ["ollama", "vllm", "open-webui", "openwebui", "comfyui", "llama", "whisper", "piper",         "litellm", "sglang", "text-generation",
                    "localai", "triton", "lucia-assistant", "wyoming"]),
        ("automation", ["home-assistant", "homeassistant", "node-red", "nodered", "zigbee2mqtt", "mosquitto", "mqtt", "esphome", "n8n", "frigate",
            "scrypted", "homebridge"]),
        ("media", ["jellyfin", "plex", "emby", "sonarr", "radarr", "lidarr", "readarr", "prowlarr", "bazarr", "overseerr", "jellyseerr", "qbittorrent",
            "transmission", "sabnzbd", "nzbget", "tautulli", "navidrome", "audiobookshelf", "immich", "photoprism", "kavita", "komga", "calibre"]),
        ("monitoring", ["grafana", "prometheus", "loki", "tempo", "otel", "opentelemetry", "collector", "alertmanager", "uptime-kuma", "netdata",
            "observability", "scrutiny", "influx", "telegraf", "exporter", "beszel", "glances"]),
        ("database", ["postgres", "mysql", "mariadb", "redis", "mongo", "clickhouse", "valkey", "qdrant", "couchdb", "etcd"]),
        ("dev", ["gitea", "forgejo", "gitlab", "jenkins", "drone", "code-server", "registry", "woodpecker", "act-runner", "devcontainer", "sonarqube"]),
        (Storage, ["nextcloud", "syncthing", "minio", "seafile", "restic", "backup", "samba", "duplicati", "kopia", "filebrowser", "paperless"]),
        (Network, ["adguard", "pihole", "pi-hole", "unbound", "traefik", "nginx", "caddy", "wireguard", "tailscale", "cloudflared", "unifi", "haproxy",
            "certbot", "bind9", "coredns", "headscale", "netbird"]),
    ];

    /// <summary>A UniFi client's category from its name, hostname, fingerprint text and vendor, else from its group.</summary>
    public static string Device(string? name, string? hostname, string? fingerprint, string? vendor, string group)
    {
        if (group == ClientClassifier.Infrastructure) return Network;
        var text = $"{name} {hostname} {fingerprint}".ToLowerInvariant();
        foreach (var (category, words) in DeviceHints)
            if (words.Any(text.Contains)) return category;
        var os = fingerprint?.ToLowerInvariant() ?? "";
        if (os.Contains("ios") || os.Contains("android")) return "phone";
        if (os.Contains("macos") || os.Contains("mac os") || os.Contains("windows")) return "computer";
        var maker = vendor?.ToLowerInvariant() ?? "";
        foreach (var (category, words) in VendorHints)
            if (words.Any(word => maker.StartsWith(word, StringComparison.Ordinal) || maker.Contains(" " + word, StringComparison.Ordinal))) return category;
        return group switch
        {
            ClientClassifier.Phones => "phone",
            ClientClassifier.Computers => "computer",
            ClientClassifier.Entertainment => "tv",
            _ => Other,
        };
    }

    /// <summary>An app's category from its stack name and container images.</summary>
    public static string App(string name, IEnumerable<string> images)
    {
        var text = string.Join(' ', images.Prepend(name)).ToLowerInvariant();
        foreach (var (category, words) in AppHints)
            if (words.Any(text.Contains)) return category;
        return Other;
    }
}
