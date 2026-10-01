using System.Text;
using System.Text.Json;

namespace Lucia.NodeAgent;

/// <summary>A registry sign-in from Lucia, so apps can pull private images from <paramref name="Host"/>.</summary>
internal sealed record NodeRegistry(string Host, string Username, string Secret);

/// <summary>
/// Keeps Lucia's registry sign-ins in a root-only Docker config that Compose and the assistant's commands use, leaving
/// root's own <c>~/.docker</c> alone.
/// </summary>
internal static class RegistryLogins
{
    private const string ConfigDirectory = "/etc/lucia/docker", ConfigPath = ConfigDirectory + "/config.json";

    /// <summary>Points Docker at Lucia's config while it has sign-ins.</summary>
    internal static IReadOnlyDictionary<string, string> Environment =>
        File.Exists(ConfigPath) ? new Dictionary<string, string> { ["DOCKER_CONFIG"] = ConfigDirectory } : new Dictionary<string, string>();

    internal static void Write(NodeRegistry[] registries)
    {
        var auths = registries.Where(item => item is { Host.Length: > 0, Username.Length: > 0, Secret.Length: > 0 }).DistinctBy(item => item.Host)
            .Take(16).ToDictionary(item => item.Host == "docker.io" ? "https://index.docker.io/v1/" : item.Host,
                item => new { auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{item.Username}:{item.Secret}")) });
        if (auths.Count == 0)
        {
            if (File.Exists(ConfigPath)) SecureStateDirectory.DeleteSystemFile(ConfigPath);
            return;
        }
        SecureStateDirectory.MakePrivateDirectory(ConfigDirectory);
        NasMounts.WritePrivate(ConfigPath, JsonSerializer.Serialize(new { auths }));
    }
}
