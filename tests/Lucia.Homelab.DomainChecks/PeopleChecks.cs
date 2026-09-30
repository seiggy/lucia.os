using System.Text.Json;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.Extensions.Options;

internal static class PeopleChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var root = Directory.CreateTempSubdirectory("lucia-people-check-").FullName;
        try
        {
            using var store = new DomainOnboardingStore(new DomainOnboardingOptions { StateDirectory = Path.Combine(root, "state") });
            var sshKeys = new OwnerSshKeys(Options.Create(new HardwareOnboardingOptions { StateDirectory = Path.Combine(root, "onboarding") }));
            var directory = new PeopleDirectory(store, sshKeys, TimeProvider.System);

            async Task Refused(DirectoryRequest request, int status, string why)
            {
                try { await directory.Queue("zack", request, default); check(false, why); }
                catch (HardwareOnboardingException error) when (error.StatusCode == status) { check(true, why); }
            }
            await Refused(new("dropTables"), 400, "An unknown directory action was queued.");
            await Refused(new("createUser", "Sam", "Sam", "", "Correct-Horse-9-battery", []), 400, "An invalid username was queued.");
            await Refused(new("createUser", "sam", "Sam", "", "short-9A", []), 400, "A weak password was queued.");
            await Refused(new("createUser", "sam", "Sam\n", "", "Correct-Horse-9-battery", []), 400, "A control character was queued.");
            await Refused(new("setUserGroups", "sam", Groups: ["family", "family"]), 400, "A duplicate group was queued.");
            await Refused(new("deleteUser", "zack"), 409, "An owner could delete themselves.");
            await Refused(new("setUserActive", "zack", Active: false), 409, "An owner could disable themselves.");
            await Refused(new("setUserGroups", "zack", Groups: ["family"]), 409, "An owner could leave lucia-owners.");
            await Refused(new("setGroupMembers", Group: "lucia-owners", Members: ["sam"]), 409, "An owner could remove themselves from owners.");

            var queued = await directory.Queue("zack", new("createUser", "sam", " Sam Lee ", "", "Correct-Horse-9-battery", ["lucia-users", "family"],
                Active: true, Group: "ignored"), default);
            var file = Path.Combine(store.Root, PeopleDirectory.Requests, queued.Id + ".json");
            using (var json = JsonDocument.Parse(File.ReadAllBytes(file)))
            {
                var names = json.RootElement.EnumerateObject().Select(item => item.Name).Order().ToArray();
                check(names.SequenceEqual(["action", "email", "groups", "id", "name", "password", "requestedAt", "requestedBy", "schemaVersion", "username"]),
                    "A queued change carried fields beyond its action's.");
                check(json.RootElement.GetProperty("name").GetString() == "Sam Lee"
                    && json.RootElement.GetProperty("groups").EnumerateArray().Select(item => item.GetString()).SequenceEqual(["family", "lucia-users"]),
                    "A queued change was not normalized.");
            }
            var view = await directory.View("zack", default);
            check(!view.Ready && view.Changes is [{ State: "pending", Action: "createUser", Target: "sam" }], "A queued change was not listed as pending.");
            check(!JsonSerializer.Serialize(view).Contains("Correct-Horse", StringComparison.Ordinal), "The directory view exposed a queued password.");

            // The identity service answers, deletes the request, and later republishes the directory.
            File.Delete(file);
            var answers = Path.Combine(store.Root, PeopleDirectory.Responses);
            Directory.CreateDirectory(answers);
            var answered = DateTimeOffset.UtcNow;
            File.WriteAllText(Path.Combine(answers, queued.Id + ".json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, id = queued.Id, success = true, message = (string?)null, checkedAt = answered, action = "deleteUser", target = "sam",
            }));
            await sshKeys.Add("sam", new("ssh-ed25519 " + Convert.ToBase64String([0, 0, 0, 11, .. "ssh-ed25519"u8, 0, 0, 0, 32, .. System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)]), null),
                DateTimeOffset.UtcNow, default);
            view = await directory.View("zack", default);
            check(view.Changes is [{ State: "pending" }], "A change showed done before the directory included it.");
            File.WriteAllText(Path.Combine(store.Root, "directory.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, checkedAt = answered.AddSeconds(1), passwordChange = true,
                users = new[] { new { username = "zack", name = "Zack", email = (string?)null, active = true, @protected = true, synced = true, groups = new[] { "lucia-owners" } } },
                groups = new[] { new { name = "lucia-owners", description = "", kind = "directory", @protected = true, members = new[] { "zack" } } },
                apps = new[] { new { slug = "lucia", name = "Lucia", launchUrl = (string?)null, @fixed = true, groups = new[] { "lucia-owners" } } },
            }));
            view = await directory.View("zack", default);
            check(view is { Ready: true, PasswordChange: true, Users: [{ Protected: true }], Changes: [{ State: "done" }] }, "A published change was not shown done.");
            check((await sshKeys.Authorized(default)).Count == 0, "A deleted person's SSH keys were still sent to servers.");

            var old = Path.Combine(answers, "20200101000000000-" + new string('a', 32) + ".json");
            File.WriteAllText(old, "{}");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
            await directory.View("zack", default);
            check(!File.Exists(old), "An old directory answer was kept.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
