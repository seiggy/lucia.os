namespace Lucia.Homelab.Server.LabMap;

/// <summary>
/// Sorts a UniFi client into a lab map group from what UniFi knows of it. UniFi's numeric fingerprint ids aren't published, so
/// only text counts: its name and hostname, any fingerprint names a controller sends as text, and its vendor (OUI).
/// </summary>
public static class ClientClassifier
{
    public const string Phones = "phones", Computers = "computers", Iot = "iot", Entertainment = "entertainment",
        Infrastructure = "infrastructure", Unknown = "unknown";
    public static readonly string[] Groups = [Phones, Computers, Iot, Entertainment, Infrastructure, Unknown];

    // Checked in order, so a more specific hint wins: "appletv" is entertainment before "apple" could say anything else.
    private static readonly (string Group, string[] Words)[] NameHints =
    [
        (Entertainment, ["appletv", "apple-tv", "apple tv", "roku", "chromecast", "firetv", "fire-tv", "fire tv", "shield", "sonos", "playstation",
            "ps4", "ps5", "xbox", "nintendo", "steam deck", "steamdeck", "steam-deck", "smarttv", "smart-tv", "bravia", "webos", "tizen", "vizio", "plex", "kodi", "television", "-tv", "tv-",
            "homepod", "denon", "yamaha", "bose", "speaker", "soundbar"]),
        (Phones, ["iphone", "android", "pixel", "galaxy", "oneplus", "phone", "ipad", "tablet", "watch"]),
        (Iot, ["esp32", "esp8266", "esp-", "espressif", "tasmota", "shelly", "tuya", "sonoff", "hue", "nest", "ecobee", "ring-", "wyze", "camera",
            "doorbell", "thermostat", "sensor", "plug", "bulb", "light", "roomba", "vacuum", "shark", "printer", "echo", "alexa", "google-home", "googlehome",
            "home-assistant", "homeassistant", "zigbee", "matter", "hub", "kasa", "meross", "switchbot", "reolink", "hikvision", "dahua", "wled"]),
        (Computers, ["macbook", "imac", "mac-mini", "macmini", "mac-pro", "laptop", "desktop", "notebook", "thinkpad", "surface", "workstation",
            "-pc", "pc-", "windows", "ubuntu", "debian", "fedora", "linux", "raspberry", "nuc", "framework"]),
    ];

    private static readonly (string Group, string[] Words)[] VendorHints =
    [
        (Infrastructure, ["ubiquiti"]),
        (Entertainment, ["roku", "sonos", "sony interactive", "nintendo", "vizio", "hisense", "tcl", "lg electronics", "nvidia", "denon", "bose"]),
        (Iot, ["espressif", "tuya", "shelly", "allterco", "signify", "philips lighting", "nest", "ecobee", "ring", "wyze", "irobot", "itead",
            "tp-link", "amazon", "brother", "canon", "seiko epson", "hp inc", "reolink", "hikvision", "dahua", "meross", "lumi", "silicon labs"]),
        (Computers, ["dell", "lenovo", "hewlett", "intel corporate", "micro-star", "asustek", "gigabyte", "framework", "raspberry", "microsoft",
            "liteon", "azurewave", "realtek"]),
        (Phones, ["oneplus", "motorola mobility", "huawei", "xiaomi", "oppo", "vivo mobile", "zte", "fairphone"]),
    ];

    /// <param name="fingerprint">Any fingerprint names UniFi sends as text (category, family, OS), joined.</param>
    /// <param name="infrastructure">Whether the MAC is a UniFi device's or a Lucia server's.</param>
    public static string Classify(string? name, string? hostname, string? fingerprint, string? vendor, bool infrastructure)
    {
        if (infrastructure) return Infrastructure;
        var text = $"{name} {hostname} {fingerprint}".ToLowerInvariant();
        foreach (var (group, words) in NameHints)
            if (words.Any(text.Contains)) return group;
        var os = fingerprint?.ToLowerInvariant() ?? "";
        if (os.Contains("ios") || os.Contains("android")) return Phones;
        if (os.Contains("macos") || os.Contains("mac os") || os.Contains("windows")) return Computers;
        var maker = vendor?.ToLowerInvariant() ?? "";
        foreach (var (group, words) in VendorHints)
            if (words.Any(word => maker.StartsWith(word, StringComparison.Ordinal) || maker.Contains(" " + word, StringComparison.Ordinal))) return group;
        return Unknown;
    }
}
