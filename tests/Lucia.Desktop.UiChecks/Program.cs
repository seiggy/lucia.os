using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lucia.Desktop;
using Lucia.Desktop.Core;

internal static class Program
{
    private static int _checks;

    [STAThread]
    public static void Main(string[] args)
    {
        Check(SetupForm.Host(" spark.local ", "host") == "spark.local", "Host whitespace is normalized.");
        Check(SetupForm.Host("2001:db8::1", "host") == "2001:db8::1", "IPv6 addresses are accepted.");
        foreach (var host in new[] { "", "https://spark", "host:9443", "x; touch /tmp/test", "host\nother", "-host", "bad..name", "fe80::1%eth0" })
            Throws<FormException>(() => SetupForm.Host(host, "host"), "Unsafe host rejected.");
        Throws<FormException>(() => SetupForm.Connection("spark", "0", "owner", false, "secret", "", ""), "Invalid SSH port rejected.");
        Throws<FormException>(() => SetupForm.Connection("spark", "22", "owner", false, "", "", ""), "Missing SSH password rejected.");
        Throws<FormException>(() => SetupForm.Connection("spark", "22", "owner;bad", false, "secret", "", ""), "Invalid SSH user rejected.");
        var fresh = Inspection(ownerReady: false);
        var options = SetupForm.Setup(fresh, "spark.local", "my-owner", "a-long-test-password!", "a-long-test-password!", "");
        Check(!options.VerifyOnly && options.OwnerUsername == "my-owner", "Fresh setup enrolls the supplied owner.");
        Check(options.ConfigureHost, "The GUI always requests managed host/app reconciliation.");
        Check(SetupForm.Setup(fresh, "spark.local", "my-owner", "a-long-test-password!", "a-long-test-password!", "", "/home/user/models").ModelDirectory
            == "/home/user/models", "Explicit reviewed Linux model directory is passed unchanged.");
        Throws<FormException>(() => SetupForm.Setup(fresh, "spark.local", "my-owner", "a-long-test-password!", "a-long-test-password!", "", "/home/user/../models"),
            "Model path traversal rejected.");
        Check(!options.ToString().Contains("a-long-test-password!"), "Options never print passwords.");
        Throws<FormException>(() => SetupForm.Setup(fresh, "spark.local", "root", "a-long-test-password!", "a-long-test-password!", ""), "Reserved owner rejected.");
        Throws<FormException>(() => SetupForm.Setup(fresh, "spark.local", "owner", "a-long-test-password!", "different", ""), "Password mismatch rejected.");
        Throws<FormException>(() => SetupForm.Setup(fresh, "spark.local", "owner", "long-password\nnew-line", "long-password\nnew-line", ""), "Multiline password rejected.");
        var existing = Inspection(ownerReady: true);
        Check(SetupForm.Setup(existing, "spark.local", "existing-owner", "a-current-test-password!", "", "").VerifyOnly,
            "Existing owner only verifies, without a password reset.");
        Check(SetupForm.Setup(existing, "spark.local", "existing-owner", "legacy-pass", "", "").OwnerPassword == "legacy-pass",
            "Existing-owner verification preserves a password shorter than the new-creation policy.");
        Check(SetupForm.Setup(existing with { ModelDirectory = "/home/user/models" }, "spark.local", "existing-owner", "old", "", "").ModelDirectory
            == "/home/user/models", "Reruns retain the persisted model directory.");
        Throws<FormException>(() => SetupForm.Setup(existing with { ModelDirectory = "/home/user/models" }, "spark.local", "existing-owner", "old", "", "", "/home/user/other"),
            "Implicit model directory migration rejected.");
        Throws<FormException>(() => SetupForm.Setup(existing, "spark.local", "existing-owner", "", "", ""), "Existing verification still requires a password.");
        Throws<FormException>(() => SetupForm.Setup(existing, "different.local", "existing-owner", "a-current-test-password!", "", ""), "Implicit endpoint migration rejected.");
        Throws<FormException>(() => SetupForm.Setup(existing, "spark.local", "replacement", "a-current-test-password!", "", ""), "Implicit owner replacement rejected.");

        var screenshots = args.Length == 2 && args[0] == "--screenshots" ? Path.GetFullPath(args[1]) : null;
        if (screenshots is not null)
            Directory.CreateDirectory(screenshots);
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var window = new MainWindow();
        window.Show();
        if (screenshots is not null)
            window.SetRenderScaling(2);
        Dispatcher.UIThread.RunJobs();
        Check(window.CurrentStep == 0 && Get<StackPanel>(window, "ConnectPage").IsVisible, "The app starts at connection, with no remote activity.");
        Check(Get<TextBox>(window, "SshPasswordInput").PasswordChar != default, "SSH password is masked.");
        Check(Get<TextBox>(window, "OwnerPasswordInput").PasswordChar != default, "Owner password is masked.");
        Check(!Get<Border>(window, "HostKeyPanel").IsVisible, "Host approval is not fabricated before a handshake.");
        var placeholder = Get<TextBox>(window, "HostInput").GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Name == "PART_Placeholder");
        Check(placeholder.Opacity == 1, "Placeholders do not halve the accessible foreground contrast.");
        CheckPlaceholderContrast(window);
        Capture(window, screenshots, "connect-light");
        if (screenshots is not null)
            CaptureHostKey(window, screenshots);

        window.ShowInspection(fresh);
        Check(window.CurrentStep == 1 && !Get<Button>(window, "ContinueButton").IsEnabled, "Inspection requires review approval.");
        Get<CheckBox>(window, "ReviewConsent").IsChecked = true;
        Check(Get<Button>(window, "ContinueButton").IsEnabled, "A reviewed, ready Spark can continue.");
        Get<TextBox>(window, "ModelDirectoryInput").Text = "/home/user/models";
        Dispatcher.UIThread.RunJobs();
        Check(Get<CheckBox>(window, "ReviewConsent").IsChecked == false, "Changing the model path requires renewed review approval.");
        Get<CheckBox>(window, "ReviewConsent").IsChecked = true;
        Capture(window, screenshots, "review-light");
        Get<Button>(window, "ContinueButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Check(window.CurrentStep == 2, "Review leads to creating the owner as part of setup.");
        Capture(window, screenshots, "owner-light");
        if (screenshots is not null)
            CaptureSetup(window, screenshots);
        window.ShowInspection(fresh with { CanInstall = false, Checks = [new("Docker", "blocked", "Resolve Docker access before proceeding.")] });
        Get<CheckBox>(window, "ReviewConsent").IsChecked = true;
        Check(!Get<Button>(window, "ContinueButton").IsEnabled, "Approval cannot bypass a prerequisite blocker.");
        window.ShowInspection(existing);
        Check(Get<TextBlock>(window, "ReviewHeading").Text == "Finish connecting Lucia", "An existing owner does not hide missing host or app repairs.");
        Check(Get<TextBox>(window, "OwnerUsernameInput").IsReadOnly, "Existing owner is locked against replacement.");
        Check(Get<TextBox>(window, "PublicHostInput").IsReadOnly, "Existing certificate address is locked.");
        Check(!Get<StackPanel>(window, "ConfirmPasswordPanel").IsVisible, "Existing sign-in doesn't ask for a replacement password.");
        window.ShowInspection(fresh with { ActiveJobId = "abc123" });
        Check(Get<Button>(window, "ContinueButton").IsEnabled && !Get<CheckBox>(window, "ReviewConsent").IsVisible,
            "An active setup resumes monitoring without new mutation approval or duplicate setup.");
        for (var index = 0; index < 150; index++)
            window.ShowEvent(new BootstrapEvent("check", $"Bounded progress event {index}"));
        Check(Get<TextBox>(window, "EventLog").Text!.Split(Environment.NewLine).Length == 120, "Progress logs stay bounded.");

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Lucia UI check CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        var result = new InstallationResult("https://spark.local:9443", "ldaps://spark.local:636", "existing-owner",
            certificate.ExportCertificatePem(), certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant(), true,
            "https://spark.local", true, true);
        Throws<InvalidOperationException>(() => window.ShowInstallation(result with { OwnerLoginVerified = false }), "Healthy containers alone cannot finish setup.");
        Throws<InvalidOperationException>(() => window.ShowInstallation(result with { HostReady = false }), "Identity-only success cannot finish setup.");
        Throws<InvalidOperationException>(() => window.ShowInstallation(result with { ApplicationReady = false }), "Missing app registration prevents finish.");
        window.ShowInstallation(result);
        Check(window.CurrentStep == 4 && Get<StackPanel>(window, "FinishPage").IsVisible, "Verified identity reaches the finish screen.");
        Check(Get<CheckBox>(window, "TrustConsent").IsChecked == false && !Get<Button>(window, "TrustButton").IsEnabled,
            "Local CA trust is never implicitly approved.");
        Check(Get<TextBox>(window, "AuthentikAddress").Text == result.AuthentikUrl, "Finish shows the actual endpoint.");
        Check(Get<TextBox>(window, "HostAddress").Text == result.HostUrl && Equals(Get<Button>(window, "ContinueButton").Content, "Open Lucia"),
            "Finish opens the managed Lucia host, not the Authentik administration page.");
        if (screenshots is not null)
            window.ShowInstallation(result with { OwnerUsername = "alex" });
        Capture(window, screenshots, "finish-light");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        window.Width = 780;
        Dispatcher.UIThread.RunJobs();
        Check(!Get<Grid>(window, "StepRail").IsVisible, "Compact desktop layout removes the sidebar.");
        Check(Get<TextBlock>(window, "CompactStep").IsVisible, "Compact layout retains step context.");
        CheckPlaceholderContrast(window);
        Capture(window, screenshots, "finish-compact-dark");
        window.Close();
        Console.WriteLine($"Desktop UI checks passed: {_checks} assertions; no remote calls, passwords, or OS trust changes.");
    }

    private static InspectionResult Inspection(bool ownerReady) => new(
        "spark", "aarch64", "Ubuntu 24.04", "/home/user/.local/share/lucia/identity",
        ownerReady, ownerReady, ownerReady ? "existing-owner" : null, ownerReady ? "spark.local" : null,
        true, false, null, [new("Docker", "ready", "Available."), new("Identity", "ready", "No conflicts.")],
        ["Prepare the identity services and owner sign-in."], ownerReady ? "https://spark.local:9443" : null);

    private static T Get<T>(MainWindow window, string name) where T : Control =>
        window.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing control {name}.");

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
        _checks++;
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { _checks++; return; }
        throw new InvalidOperationException(message);
    }

    private static void Capture(MainWindow window, string? directory, string name)
    {
        if (directory is null)
            return;
        Get<TextBlock>(window, "ScopeCaption").Text = "Synthetic UI fixture - no network or certificate-trust changes";
        Dispatcher.UIThread.RunJobs();
        for (var tick = 0; tick < 30; tick++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
        using var bitmap = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("UI check frame did not render.");
        bitmap.Save(Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        var text = Get<TextBlock>(window, "FinishDescription");
        Console.WriteLine($"{name}: body font {new Avalonia.Media.Typeface(text.FontFamily, text.FontStyle, text.FontWeight).GlyphTypeface.FamilyName}");
    }

    // Sample screenshots of states the real flow reaches only over SSH: an unverified host key, a filled-in owner, and setup progress.
    private static void CaptureHostKey(MainWindow window, string directory)
    {
        var pending = typeof(MainWindow).GetField("_pendingKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData("Lucia sample host key"u8)).TrimEnd('=');
        Get<TextBox>(window, "HostInput").Text = "spark.local";
        Get<TextBox>(window, "SshUsernameInput").Text = "alex";
        Get<TextBox>(window, "SshPasswordInput").Text = "sample-password";
        pending.SetValue(window, new HostKeyInfo("spark.local", 22, "ssh-ed25519", fingerprint));
        Get<TextBlock>(window, "HostKeyTarget").Text = "spark.local:22 · ssh-ed25519";
        Get<TextBox>(window, "HostKeyFingerprint").Text = fingerprint;
        Get<Border>(window, "HostKeyPanel").IsVisible = true;
        Get<CheckBox>(window, "HostKeyConsent").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Get<Border>(window, "HostKeyPanel").BringIntoView();
        Capture(window, directory, "hostkey-light");
        pending.SetValue(window, null);
        Get<Border>(window, "HostKeyPanel").IsVisible = false;
        Get<CheckBox>(window, "HostKeyConsent").IsChecked = false;
        foreach (var name in new[] { "HostInput", "SshUsernameInput", "SshPasswordInput" })
            Get<TextBox>(window, name).Text = "";
        Dispatcher.UIThread.RunJobs();
    }

    private static void CaptureSetup(MainWindow window, string directory)
    {
        Get<TextBox>(window, "OwnerUsernameInput").Text = "alex";
        Get<TextBox>(window, "OwnerPasswordInput").Text = "a-long-sample-passphrase";
        Get<TextBox>(window, "ConfirmPasswordInput").Text = "a-long-sample-passphrase";
        Get<TextBox>(window, "PublicHostInput").Text = "spark.local";
        Capture(window, directory, "owner-filled-light");

        // The real client and bootstrap job messages, in the order a fresh setup reports them.
        string[] steps =
        [
            "Packaging the reviewed bootstrap source.",
            "Verifying the host runtime package before upload.",
            "Monitoring the durable Spark job. Cancelling stops monitoring, not remote work.",
            "Reviewed setup accepted; remote work continues if you disconnect.",
            "Rechecking reviewed host state before making changes.",
            "Preparing identity inputs while preserving existing credentials and trust.",
            "Publishing the Identity AppHost from its stable user-local path.",
            "Deploying CA, LDAP and Authentik containers with their persistent storage.",
            "Verifying the private CA and service certificate; client trust is unchanged.",
            "Verifying directory service access over trusted LDAPS.",
            "Reconciling the verified LDAP source and waiting for actual synchronization.",
            "Enrolling the directory owner without resetting any existing password.",
            "Verifying real LDAP-backed owner sign-in and revoking the verification session.",
            "Preparing stable local OIDC credentials; existing client secrets and identity passwords are preserved.",
            "Publishing the managed host and gateway on the existing identity network.",
            "Deploying the managed host with NVIDIA CDI and HTTPS port 443; identity storage stays in place.",
        ];
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetMethod("ShowStep", flags)!.Invoke(window, [3]);
        typeof(MainWindow).GetMethod("StartOperation", flags)!.Invoke(window, []);
        var log = Get<TextBox>(window, "EventLog");
        log.Text = string.Join(Environment.NewLine, steps.Select((step, index) => $"{new TimeSpan(14, 2, 10 + index * 23):hh\\:mm\\:ss}  {step}"));
        Get<TextBlock>(window, "CurrentOperation").Text = steps[^1];
        Get<TextBlock>(window, "ElapsedText").Text = "Elapsed 6:12";
        Get<ProgressBar>(window, "InstallProgress").IsVisible = true;
        Capture(window, directory, "progress-light");
        var details = window.GetVisualDescendants().OfType<Expander>().Single(expander => Equals(expander.Header, "Setup details"));
        details.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        log.CaretIndex = log.Text.Length;
        log.BringIntoView();
        Capture(window, directory, "progress-details-light");
        details.IsExpanded = false;
        Get<ProgressBar>(window, "InstallProgress").IsVisible = false;
        typeof(MainWindow).GetMethod("StopOperation", flags)!.Invoke(window, []);
        Dispatcher.UIThread.RunJobs();
    }

    private static void CheckPlaceholderContrast(MainWindow window)
    {
        foreach (var name in new[] { "HostInput", "SshUsernameInput" })
        {
            var field = Get<TextBox>(window, name);
            var text = field.GetVisualDescendants().OfType<TextBlock>().Single(item => item.Name == "PART_Placeholder");
            var foreground = ((Avalonia.Media.ISolidColorBrush)text.Foreground!).Color;
            var background = ((Avalonia.Media.ISolidColorBrush)field.Background!).Color;
            static double Linear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            static double Luminance(double r, double g, double b) => 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
            var alpha = foreground.A / 255.0 * text.Opacity;
            var ink = Luminance((foreground.R * alpha + background.R * (1 - alpha)) / 255,
                (foreground.G * alpha + background.G * (1 - alpha)) / 255,
                (foreground.B * alpha + background.B * (1 - alpha)) / 255);
            var paper = Luminance(background.R / 255.0, background.G / 255.0, background.B / 255.0);
            Check((Math.Max(ink, paper) + 0.05) / (Math.Min(ink, paper) + 0.05) >= 4.5, $"{name} placeholder contrast must be at least 4.5:1.");
        }
    }
}
