using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Lucia.Homelab.Server.Assistant;

/// <summary>
/// Reads public web pages for read_web_page. Every connection is checked after DNS, so a name that resolves into the lab or
/// another special network is refused. A redirect to another site isn't followed, so the broker decides on every site read.
/// </summary>
internal static partial class AssistantWeb
{
    public const int MaxUrl = 2048, MaxBytes = 2 * 1024 * 1024, MaxChars = 20_000, MaxRedirects = 5;
    private const RegexOptions Html = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;
    private const int RegexTimeout = 2000;

    private static readonly IPNetwork GlobalUnicast6 = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] Special = [.. new[]
    {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24",
        "192.0.2.0/24", "192.88.99.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3",
        "2001::/23", "2001:db8::/32", "2002::/16", "3fff::/20",
    }.Select(IPNetwork.Parse)];

    // The checks swap this to reach their loopback server.
    internal static Func<IPAddress, bool> Reachable = Public;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false, UseCookies = false, PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10),
        ActivityHeadersPropagator = null, ConnectCallback = Connect,
    });

    /// <summary>Reads <paramref name="url"/> as text, up to <see cref="MaxChars"/> characters from <paramref name="start"/>.</summary>
    public static async Task<string> Read(string url, int start, CancellationToken ct)
    {
        if (url is not { Length: <= MaxUrl } || !Uri.TryCreate(url, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https"))
            throw new AssistantException(400, "invalid_url", $"Give an absolute http or https URL of up to {MaxUrl} characters.");
        if (start < 0) throw new AssistantException(400, "invalid_start", "Start at character 0 or later.");
        var site = Site(page);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            for (var hops = 0; ; hops++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, page);
                request.Headers.TryAddWithoutValidation("User-Agent", "Lucia-Assistant/1.0");
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.5");
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = new Uri(page, location);
                    if (next.Scheme is not ("http" or "https") || Site(next) != site)
                        return $"{page} moved to {next}, which is another site. Read it with read_web_page if you still need it.";
                    if (hops == MaxRedirects)
                        throw new AssistantException(502, "too_many_redirects", $"{page} redirected more than {MaxRedirects} times.");
                    page = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new AssistantException(502, "page_error", $"{page} answered HTTP {(int)response.StatusCode}.");
                var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "text/plain";
                var html = type is "text/html" or "application/xhtml+xml";
                if (!type.StartsWith("text/", StringComparison.Ordinal) && !html && !type.EndsWith("json", StringComparison.Ordinal)
                    && !type.EndsWith("xml", StringComparison.Ordinal) && !type.EndsWith("yaml", StringComparison.Ordinal) && type != "application/javascript")
                    throw new AssistantException(415, "not_text", $"{page} is {type}, not a page or a text file.");

                var buffer = new byte[MaxBytes];
                var length = 0;
                await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
                int read;
                while (length < MaxBytes && (read = await body.ReadAsync(buffer.AsMemory(length), deadline.Token)) > 0) length += read;
                var cut = length == MaxBytes && await body.ReadAsync(new byte[1], deadline.Token) > 0;
                Encoding encoding;
                try { encoding = Encoding.GetEncoding(response.Content.Headers.ContentType?.CharSet?.Trim('"', ' ') is { Length: > 0 } charset ? charset : "utf-8"); }
                catch (ArgumentException) { encoding = Encoding.UTF8; }
                using var reader = new StreamReader(new MemoryStream(buffer, 0, length), encoding, detectEncodingFromByteOrderMarks: true);
                var text = await reader.ReadToEndAsync(deadline.Token);
                if (html) text = Text(text, page);
                if (string.IsNullOrWhiteSpace(text))
                    return $"URL: {page}\n\nThe page has no readable text." + (html ? " It may be built by JavaScript, which read_web_page doesn't run." : "");
                if (start >= text.Length)
                    throw new AssistantException(400, "past_end", $"The page has {text.Length} characters; start before that.");
                // Never split an emoji's two halves: the result has to stay valid text.
                if (start > 0 && char.IsLowSurrogate(text[start])) start--;
                var end = Math.Min(text.Length, start + MaxChars);
                if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
                var note = end < text.Length ? $"\n\n[Characters {start} to {end} of {text.Length}. Read on with start {end}.]"
                    : cut ? $"\n\n[The page was cut at {MaxBytes / 1024 / 1024} MB.]" : "";
                return $"URL: {page}\n\n{text[start..end]}{note}";
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AssistantException(504, "page_timeout", $"{page.Host} didn't answer in time.");
        }
        catch (RegexMatchTimeoutException)
        {
            throw new AssistantException(502, "page_unreadable", $"{page} is too complex to turn into text.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            if (error.GetBaseException() is AssistantException refused) throw refused;
            throw new AssistantException(502, "page_unreachable", error.GetBaseException() switch
            {
                SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => $"{page.Host} doesn't resolve.",
                _ when error is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }
                    => $"{page.Host}'s HTTPS certificate isn't valid.",
                _ => $"Lucia couldn't connect to {page.Host}.",
            });
        }
    }

    /// <summary>Whether an address is on the public internet: not private, loopback, link-local, shared, multicast or reserved.</summary>
    internal static bool Public(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return (address.AddressFamily == AddressFamily.InterNetwork || GlobalUnicast6.Contains(address))
            && !Special.Any(network => network.Contains(address));
    }

    /// <summary>
    /// A page's readable text, Markdown-like: the title, headings, list items, links (made absolute) and code blocks survive;
    /// scripts, styles, menus and footers don't.
    /// </summary>
    internal static string Text(string html, Uri page)
    {
        html = html.ReplaceLineEndings("\n");
        var title = Title().Match(html) is { Success: true } found
            ? Whitespace().Replace(WebUtility.HtmlDecode(Tag().Replace(found.Groups[1].Value, "")), " ").Trim() : "";
        html = Noise().Replace(html, "");
        if (Content().Match(html) is { Success: true } main) html = main.Groups[1].Value;
        List<string> code = [];
        html = Whitespace().Replace(Pre().Replace(html, match =>
        {
            code.Add(WebUtility.HtmlDecode(Tag().Replace(match.Groups[1].Value, "")).Trim('\n'));
            return $"\uE000{code.Count - 1}\uE001";
        }), " ");
        var links = new Stack<string?>();
        html = Anchor().Replace(html, match =>
        {
            if (match.Groups["close"].Success) return links.TryPop(out var link) && link is not null ? $"]({link})" : "";
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            var target = href.Length > 0 && !href.StartsWith('#') && Uri.TryCreate(page, href, out var uri) && uri.Scheme is "http" or "https"
                ? uri.AbsoluteUri : null;
            links.Push(target);
            return target is null ? "" : "[";
        });
        html = Heading().Replace(html, match => match.Groups["level"].Success ? "\n\n" + new string('#', match.Groups["level"].Value[0] - '0') + " " : "\n\n");
        html = Item().Replace(html, "\n- ");
        html = Cell().Replace(html, " | ");
        html = Code().Replace(html, "`");
        html = Block().Replace(html, "\n");
        var text = WebUtility.HtmlDecode(Tag().Replace(html, ""));
        text = Blank().Replace(LineEnds().Replace(Spaces().Replace(text, " "), "\n"), "\n\n");
        // A page can spell a marker with &#xE000; entities, so a marker that names no block is dropped.
        text = Placeholder().Replace(EmptyLink().Replace(text, ""), match => int.TryParse(match.Groups[1].ValueSpan, out var block) && block < code.Count
            ? "\n\n```\n" + code[block] + "\n```\n\n" : "").Trim();
        return title.Length == 0 ? text : $"# {title}\n\n{text}";
    }

    private static string Site(Uri uri) => uri.IdnHost.TrimEnd('.').ToLowerInvariant();

    private static async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host.Trim('[', ']');
        var addresses = (await Dns.GetHostAddressesAsync(host, ct)).Where(Reachable).ToArray();
        if (addresses.Length == 0)
            throw new AssistantException(403, "private_address",
                $"{host} is on a private or reserved network. read_web_page reads public sites only; use the lab tools for the owner's network.");
        SocketException? failure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException error) { socket.Dispose(); failure = error; }
            catch { socket.Dispose(); throw; }
        }
        throw failure!;
    }

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>", Html, RegexTimeout)]
    private static partial Regex Title();

    [GeneratedRegex(@"<!--.*?-->|<(?<tag>script|style|noscript|svg|template|nav|footer|iframe|head)(?=[\s/>])[^>]*>.*?</\k<tag>\s*>", Html, RegexTimeout)]
    private static partial Regex Noise();

    [GeneratedRegex(@"<main(?=[\s/>])[^>]*>(.*)</main\s*>", Html, RegexTimeout)]
    private static partial Regex Content();

    [GeneratedRegex(@"<pre\b[^>]*>(.*?)</pre\s*>", Html, RegexTimeout)]
    private static partial Regex Pre();

    [GeneratedRegex(@"<a\s[^>]*?\bhref\s*=\s*(?:""(?<href>[^""]*)""|'(?<href>[^']*)'|(?<href>[^\s""'>]+))[^>]*>|<a(?=[\s>])[^>]*>|(?<close></a\s*>)", Html, RegexTimeout)]
    private static partial Regex Anchor();

    [GeneratedRegex(@"<h(?<level>[1-6])\b[^>]*>|</h[1-6]\s*>", Html, RegexTimeout)]
    private static partial Regex Heading();

    [GeneratedRegex(@"<li\b[^>]*>", Html, RegexTimeout)]
    private static partial Regex Item();

    [GeneratedRegex(@"<t[dh]\b[^>]*>", Html, RegexTimeout)]
    private static partial Regex Cell();

    [GeneratedRegex(@"</?code\b[^>]*>", Html, RegexTimeout)]
    private static partial Regex Code();

    [GeneratedRegex(@"<br\b[^>]*>|</?(?:p|div|section|article|header|aside|ul|ol|dl|dt|dd|table|tr|blockquote|figure|figcaption|details|summary|hr|form|fieldset|address)\b[^>]*>", Html, RegexTimeout)]
    private static partial Regex Block();

    [GeneratedRegex(@"<[a-z/!?][^>]*>", Html, RegexTimeout)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+", Html, RegexTimeout)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[^\S\n]+", Html, RegexTimeout)]
    private static partial Regex Spaces();

    [GeneratedRegex(@" ?\n ?", Html, RegexTimeout)]
    private static partial Regex LineEnds();

    [GeneratedRegex(@"\n{3,}", Html, RegexTimeout)]
    private static partial Regex Blank();

    [GeneratedRegex(@"\[\s*\]\([^)\s]*\)", Html, RegexTimeout)]
    private static partial Regex EmptyLink();

    [GeneratedRegex("\n*\uE000(\\d+)\uE001\n*", Html, RegexTimeout)]
    private static partial Regex Placeholder();
}
