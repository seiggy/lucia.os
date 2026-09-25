using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Lucia.Desktop.Core;

namespace Lucia.Desktop;

public partial class MainWindow : Window
{
    private readonly BootstrapClient _client = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Queue<string> _events = new();
    private CancellationTokenSource? _operation;
    private ConnectionOptions? _connection;
    private InspectionResult? _inspection;
    private InstallationResult? _installation;
    private HostKeyInfo? _pendingKey;
    private IReadOnlyList<string> _secrets = [];
    private bool _initialized;
    private bool _busy;
    private bool _trustBusy;
    private long _started;

    internal int CurrentStep { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        _initialized = true;
        _clock.Tick += (_, _) => ElapsedText.Text = $"Elapsed {Stopwatch.GetElapsedTime(_started):m\\:ss}";
        SizeChanged += (_, _) => UpdateLayoutMode();
        Opened += (_, _) => HostInput.Focus();
        Closing += (_, _) =>
        {
            _operation?.Cancel();
            _clock.Stop();
            ClearCredentials();
        };
        ShowStep(0);
    }

    private static bool Expected(Exception error) => error is
        ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
        or TimeoutException or NotSupportedException or FormatException or CryptographicException
        or Win32Exception or SecurityException or JsonException or HttpRequestException;

    private async void Continue(object? sender, RoutedEventArgs args)
    {
        if (_busy)
        {
            if (CurrentStep == 3)
                _operation?.Cancel();
            return;
        }
        ErrorPanel.IsVisible = false;
        try
        {
            switch (CurrentStep)
            {
                case 0:
                    await ConnectAsync();
                    break;
                case 1:
                    if (_inspection is null || !CanProceedFromReview())
                        return;
                    if (_inspection.ActiveJobId is not null)
                        await RunSetupAsync(new SetupOptions
                        {
                            PublicHost = _inspection.PublicHost ?? _connection!.Host,
                            OwnerUsername = _inspection.OwnerUsername ?? "",
                            VerifyOnly = _inspection.OwnerReady, ModelDirectory = _inspection.ModelDirectory,
                            ConfigureHost = _inspection.ActiveJobConfigureHost
                        });
                    else
                        ShowStep(2);
                    break;
                case 2:
                    if (_inspection is null)
                        throw new InvalidOperationException("Reconnect to your Spark before starting setup.");
                    var options = SetupForm.Setup(_inspection, PublicHostInput.Text ?? "", OwnerUsernameInput.Text ?? "",
                        OwnerPasswordInput.Text ?? "", ConfirmPasswordInput.Text ?? "", SudoPasswordInput.Text ?? "",
                        ModelDirectoryInput.Text);
                    await RunSetupAsync(options);
                    break;
                case 3:
                    ResetConnection();
                    break;
                case 4:
                    if (_installation is null)
                        return;
                    CertificateTrust.Validate(_installation);
                    Process.Start(new ProcessStartInfo(_installation.HostUrl!) { UseShellExecute = true });
                    break;
            }
        }
        catch (FormException error)
        {
            ShowError(error.Message, "Check this field");
            this.FindControl<Control>(error.Field)?.Focus();
        }
        catch (OperationCanceledException)
        {
            ShowError(CurrentStep == 3
                ? "Watching stopped. Started jobs continue on your Spark; reconnect to resume monitoring. Uploads interrupted before a job starts must restart."
                : "The connection check was cancelled. Nothing was installed.", "You can continue later");
        }
        catch (Exception error) when (Expected(error))
        {
            ShowError(error.Message);
        }
        finally
        {
            UpdateButtons();
        }
    }

    private async Task ConnectAsync()
    {
        if (_pendingKey is not null)
        {
            if (HostKeyConsent.IsChecked != true || _connection is null)
                return;
            _client.TrustHost(_pendingKey);
            _pendingKey = null;
            HostKeyPanel.IsVisible = false;
        }
        else
        {
            _connection = SetupForm.Connection(HostInput.Text ?? "", PortInput.Text ?? "", SshUsernameInput.Text ?? "",
                AuthenticationChoice.SelectedIndex == 1, SshPasswordInput.Text ?? "", KeyPathInput.Text ?? "", KeyPassphraseInput.Text ?? "");
        }
        _secrets = new[] { _connection.Password, _connection.PrivateKeyPassphrase }.OfType<string>().Where(value => value.Length > 0).ToArray();
        StartOperation();
        try
        {
            var inspection = await _client.InspectAsync(_connection, _operation!.Token);
            ShowInspection(inspection);
        }
        catch (HostKeyConfirmationRequiredException confirmation)
        {
            _pendingKey = confirmation.Key;
            HostKeyTarget.Text = $"{confirmation.Key.Host}:{confirmation.Key.Port} · {confirmation.Key.Algorithm}";
            HostKeyFingerprint.Text = confirmation.Key.Fingerprint;
            HostKeyConsent.IsChecked = false;
            HostKeyPanel.IsVisible = true;
            HostKeyConsent.Focus();
            HostKeyPanel.BringIntoView();
        }
        finally
        {
            StopOperation();
        }
    }

    internal void ShowInspection(InspectionResult inspection)
    {
        _inspection = inspection;
        var active = inspection.ActiveJobId is not null;
        ReviewHeading.Text = active ? "Your setup is already running"
            : inspection.OwnerReady ? inspection.HostReady && inspection.ApplicationReady ? "Check your existing Lucia setup" : "Finish connecting Lucia"
            : inspection.CanInstall ? "A good place to begin" : "A few things need attention";
        ReviewDescription.Text = active ? "Reconnect to the existing job. Lucia will not start another installation."
            : inspection.OwnerReady ? "Your accounts, passwords, and certificate authority stay as they are. Review any host or Lucia sign-in repairs below, then verify with your current owner password."
            : "Lucia will prepare identity services, your owner account, and the managed host and dashboard, then verify application access.";
        SparkDetails.Text = $"{inspection.Hostname} · {inspection.OperatingSystem} · {inspection.Architecture}";
        ChecksPanel.Children.Clear();
        foreach (var check in inspection.Checks)
        {
            var heading = new TextBlock { Text = check.Name, FontWeight = Avalonia.Media.FontWeight.SemiBold };
            var message = new TextBlock { Text = check.Message, Margin = new Thickness(0, 5, 0, 0) };
            message.Classes.Add("caption");
            var status = new TextBlock
            {
                Text = check.Status switch { "ready" => "Ready", "action" => "Setup needed", _ => "Needs attention" },
                FontSize = 13, Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
            };
            status.Classes.Add("check-" + (check.Status is "ready" or "action" ? check.Status : "blocked"));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 18) };
            row.Children.Add(new StackPanel { Children = { heading, message } });
            Grid.SetColumn(status, 1);
            row.Children.Add(status);
            ChecksPanel.Children.Add(row);
        }
        PlannedChanges.ItemsSource = inspection.PlannedChanges;
        ChangesPanel.IsVisible = inspection.PlannedChanges.Count > 0 && !active;
        ModelDirectoryPanel.IsVisible = !active;
        ModelDirectoryInput.Text = inspection.ModelDirectory ?? "";
        ModelDirectoryInput.IsReadOnly = inspection.ModelDirectory is not null;
        ReviewConsentText.Text = "I approve the listed checks and repairs, including the selected model directory.";
        ReviewConsent.IsChecked = false;
        ReviewConsent.IsVisible = !active;
        OwnerHeading.Text = inspection.OwnerReady ? "Welcome back"
            : inspection.OwnerUsername is not null ? "Finish setting up your account" : "Make Lucia yours";
        OwnerDescription.Text = inspection.OwnerReady
            ? "Use your existing Lucia account. This verifies access without changing your password."
            : "This will be your everyday account for Lucia. It lives in LDAP and receives administrator access in Authentik.";
        OwnerUsernameInput.Text = inspection.OwnerUsername ?? "";
        OwnerUsernameInput.IsReadOnly = inspection.OwnerUsername is not null;
        OwnerPasswordInput.Text = null;
        ConfirmPasswordInput.Text = null;
        ConfirmPasswordPanel.IsVisible = !inspection.OwnerReady;
        OwnerPasswordHint.Text = inspection.OwnerReady ? "Your current Lucia password, not your Spark SSH password."
            : inspection.OwnerUsername is not null ? "Use the password from your earlier setup attempt. Existing credentials will not be reset."
            : "At least 14 characters. A long, unique passphrase works well.";
        PublicHostInput.Text = inspection.PublicHost ?? _connection?.Host ?? HostInput.Text ?? "";
        PublicHostInput.IsReadOnly = inspection.PublicHost is not null;
        SudoPanel.IsVisible = inspection.RequiresSudo;
        OwnerAuthorityNotice.Text = inspection.OwnerReady
            ? "Verification creates a temporary sign-in session and removes it afterward. No password or account reset is performed."
            : "This account will administer LDAP and Authentik. Your password is sent over verified SSH, never put in logs, and not saved on this computer.";
        ShowStep(1);
    }

    private bool CanProceedFromReview() => _inspection is not null &&
        (_inspection.ActiveJobId is not null ||
            (_inspection.CanInstall && _inspection.Checks.All(check => check.Status is "ready" or "action") && ReviewConsent.IsChecked == true));

    private async Task RunSetupAsync(SetupOptions options)
    {
        if (_connection is null || _inspection is null)
            throw new InvalidOperationException("Reconnect to your Spark before starting setup.");
        _secrets = new[] { _connection.Password, _connection.PrivateKeyPassphrase, options.OwnerPassword, options.SudoPassword }
            .OfType<string>().Where(value => value.Length > 0).OrderByDescending(value => value.Length).ToArray();
        _events.Clear();
        EventLog.Text = "";
        InstallHeading.Text = options.VerifyOnly ? "Checking and repairing Lucia" : "Setting up your Spark";
        InstallDescription.Text = options.VerifyOnly ? "Identity credentials stay unchanged. Approved host and application repairs still run."
            : "Your Spark is doing the work. Your accounts and trust stay there.";
        CurrentOperation.Text = _inspection.ActiveJobId is not null ? "Reconnecting to your setup job..." : "Preparing a secure setup job...";
        ShowStep(3);
        StartOperation();
        _started = Stopwatch.GetTimestamp();
        _clock.Start();
        InstallProgress.IsVisible = true;
        try
        {
            var result = await _client.RunAsync(_connection, _inspection, options,
                new Progress<BootstrapEvent>(ShowEvent), _operation!.Token);
            ShowInstallation(result);
        }
        catch (OperationCanceledException)
        {
            CurrentOperation.Text = "Monitoring paused";
            throw;
        }
        catch (Exception error) when (Expected(error))
        {
            CurrentOperation.Text = "Setup needs your attention";
            ShowError(error.Message, "Setup hasn't finished");
        }
        finally
        {
            _clock.Stop();
            InstallProgress.IsVisible = false;
            StopOperation();
            ClearCredentials();
        }
    }

    internal void ShowEvent(BootstrapEvent update)
    {
        var message = Redact(update.Message);
        CurrentOperation.Text = message;
        _events.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (_events.Count > 120)
            _events.Dequeue();
        EventLog.Text = string.Join(Environment.NewLine, _events);
        EventLog.CaretIndex = EventLog.Text.Length;
    }

    internal void ShowInstallation(InstallationResult installation)
    {
        if (!installation.OwnerLoginVerified)
            throw new InvalidOperationException("Owner sign-in has not been verified. Setup cannot be marked complete.");
        if (!installation.HostReady || !installation.ApplicationReady || installation.HostUrl is null)
            throw new InvalidOperationException("Identity is available, but managed host and application access are not both ready. Reconnect to review the remaining repairs.");
        if (!Uri.TryCreate(installation.HostUrl, UriKind.Absolute, out var launch) || launch.Scheme != "https" ||
            launch.Port != 443 || launch.AbsolutePath != "/" || launch.UserInfo.Length != 0 ||
            launch.Query.Length != 0 || launch.Fragment.Length != 0 ||
            launch.IdnHost != new Uri(installation.AuthentikUrl).IdnHost)
            throw new InvalidOperationException("The Lucia launch address does not match the verified HTTPS host.");
        CertificateTrust.Validate(installation);
        _installation = installation;
        FinishDescription.Text = $"The managed host is healthy and {installation.OwnerUsername}'s application access is verified. Open Lucia to complete your browser sign-in; browser SSO has not been tested by setup.";
        OwnerReadyText.Text = $"Username: {installation.OwnerUsername}";
        AuthentikAddress.Text = installation.AuthentikUrl;
        HostAddress.Text = installation.HostUrl;
        RootFingerprint.Text = installation.RootFingerprint;
        TrustScope.Text = CertificateTrust.ScopeDescription;
        TrustStatus.Text = "No local certificate trust has been changed by this setup flow.";
        TrustConsent.IsChecked = false;
        ShowStep(4);
    }

    private void ShowStep(int step)
    {
        CurrentStep = step;
        StackPanel[] pages = [ConnectPage, ReviewPage, OwnerPage, InstallPage, FinishPage];
        Border[] markers = [StepConnect, StepReview, StepOwner, StepInstall, StepFinish];
        for (var index = 0; index < pages.Length; index++)
        {
            pages[index].IsVisible = index == step;
            markers[index].Classes.Set("active", index == step);
        }
        CompactStep.Text = $"{step + 1} of 5 · {new[] { "Connect", "Review", "Your account", "Set up", "Ready" }[step]}";
        ErrorPanel.IsVisible = false;
        PageScroll.Offset = default;
        UpdateButtons();
        UpdateLayoutMode();
    }

    private void UpdateButtons()
    {
        if (!_initialized)
            return;
        ConnectionFields.IsEnabled = !_busy && _pendingKey is null;
        OwnerPage.IsEnabled = !_busy;
        HostKeyConsent.IsEnabled = !_busy;
        BackButton.IsVisible = CurrentStep is 1 or 2 or 4 || (_busy && CurrentStep == 0);
        BackButton.IsEnabled = !_busy || CurrentStep == 0;
        BackButton.Content = _busy ? "Cancel" : CurrentStep == 4 ? "Another Spark" : "Back";
        ContinueButton.Content = CurrentStep switch
        {
            0 => _busy ? "Connecting..." : _pendingKey is null ? "Connect to Spark" : "Trust and continue",
            1 => _inspection?.ActiveJobId is not null ? "Resume monitoring" : "Continue",
            2 => _inspection?.OwnerReady == true ? "Verify and repair Lucia" : "Set up Lucia",
            3 => _busy ? "Stop watching" : "Reconnect to Spark",
            _ => "Open Lucia"
        };
        ContinueButton.IsEnabled = CurrentStep switch
        {
            0 => !_busy && (_pendingKey is null || HostKeyConsent.IsChecked == true),
            1 => !_busy && CanProceedFromReview(),
            3 => true,
            4 => !_trustBusy,
            _ => !_busy
        };
        TrustButton.IsEnabled = TrustConsent.IsChecked == true && !_trustBusy;
        TrustConsent.IsEnabled = !_trustBusy;
        BusyText.Text = _busy && CurrentStep == 0 ? "Checking the connection" : "";
    }

    private void StartOperation()
    {
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        _busy = true;
        UpdateButtons();
    }

    private void StopOperation()
    {
        _busy = false;
        UpdateButtons();
    }

    private string Redact(string message)
    {
        foreach (var secret in _secrets)
            message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return message.Length > 2500 ? message[..2500] + "..." : message;
    }

    private void ShowError(string message, string heading = "We couldn't continue")
    {
        ErrorHeading.Text = heading;
        ErrorMessage.Text = Redact(message);
        ErrorPanel.IsVisible = true;
        PageScroll.Offset = default;
    }

    private void GoBack(object? sender, RoutedEventArgs args)
    {
        if (_busy)
        {
            if (CurrentStep == 0)
                _operation?.Cancel();
            return;
        }
        if (CurrentStep == 2)
        {
            OwnerPasswordInput.Text = ConfirmPasswordInput.Text = SudoPasswordInput.Text = null;
            ShowStep(1);
        }
        else
            ResetConnection();
    }

    private void ClearCredentials()
    {
        SshPasswordInput.Text = KeyPassphraseInput.Text = OwnerPasswordInput.Text =
            ConfirmPasswordInput.Text = SudoPasswordInput.Text = null;
        _connection = null;
        _secrets = [];
    }

    private void ResetConnection()
    {
        ClearCredentials();
        _inspection = null;
        _installation = null;
        _pendingKey = null;
        HostKeyPanel.IsVisible = false;
        HostKeyConsent.IsChecked = false;
        ReviewConsent.IsChecked = false;
        _events.Clear();
        ShowStep(0);
        HostInput.Focus();
    }

    private void CancelHostKey(object? sender, RoutedEventArgs args)
    {
        _pendingKey = null;
        HostKeyPanel.IsVisible = false;
        ClearCredentials();
        UpdateButtons();
    }

    private void ConsentChanged(object? sender, RoutedEventArgs args) => UpdateButtons();

    private void ModelDirectoryChanged(object? sender, TextChangedEventArgs args)
    {
        if (_initialized)
            ReviewConsent.IsChecked = false;
    }

    private void OpenAuthentik(object? sender, RoutedEventArgs args)
    {
        try
        {
            if (_installation is not null)
                Process.Start(new ProcessStartInfo(_installation.AuthentikUrl) { UseShellExecute = true });
        }
        catch (Exception error) when (Expected(error))
        {
            ShowError(error.Message, "Authentik couldn't be opened");
        }
    }

    private void AuthenticationChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_initialized)
            return;
        var useKey = AuthenticationChoice.SelectedIndex == 1;
        SshPasswordPanel.IsVisible = !useKey;
        PrivateKeyPanel.IsVisible = useKey;
        SshPasswordInput.Text = KeyPassphraseInput.Text = null;
    }

    private void AppearanceChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_initialized || Application.Current is null)
            return;
        Application.Current.RequestedThemeVariant = AppearanceChoice.SelectedIndex switch
        {
            1 => ThemeVariant.Light,
            2 => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private void UpdateLayoutMode()
    {
        if (!_initialized)
            return;
        var compact = Bounds.Width < 960;
        StepRail.IsVisible = !compact;
        BodyGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 208);
        CompactStep.IsVisible = compact;
    }

    private async void BrowseKey(object? sender, RoutedEventArgs args)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose your SSH private key", AllowMultiple = false
            });
            if (files.Count == 0)
                return;
            var path = files[0].TryGetLocalPath();
            if (path is null)
                throw new IOException("Choose a private key stored on this computer, not a virtual or cloud-only file.");
            KeyPathInput.Text = path;
        }
        catch (Exception error) when (Expected(error))
        {
            ShowError(error.Message, "The key file couldn't be selected");
        }
    }

    private async void InstallTrust(object? sender, RoutedEventArgs args)
    {
        if (_installation is null || TrustConsent.IsChecked != true || _trustBusy)
            return;
        _trustBusy = true;
        UpdateButtons();
        TrustStatus.Text = "Waiting for certificate trust approval...";
        try
        {
            var result = await CertificateTrust.InstallAsync(_installation);
            TrustStatus.Text = result.Message;
            if (result.Installed)
            {
                TrustButton.Content = "Certificate trusted";
                TrustConsent.IsChecked = false;
            }
        }
        catch (Exception error) when (Expected(error))
        {
            TrustStatus.Text = "Certificate trust was not completed.";
            ShowError(error.Message, "Your services are ready, but this computer isn't trusted yet");
        }
        finally
        {
            _trustBusy = false;
            UpdateButtons();
        }
    }

    private async void ExportTrust(object? sender, RoutedEventArgs args)
    {
        if (_installation is null)
            return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Lucia's public CA", SuggestedFileName = "lucia-root-ca.crt",
                DefaultExtension = "crt", ShowOverwritePrompt = true
            });
            if (file is null)
                return;
            var path = file.TryGetLocalPath();
            if (path is null)
                throw new IOException("Choose a local destination for the public certificate.");
            await CertificateTrust.ExportAsync(_installation, path);
            TrustStatus.Text = $"Public certificate exported to {path}. No private keys or passwords were exported.";
        }
        catch (Exception error) when (Expected(error))
        {
            ShowError(error.Message, "The certificate couldn't be exported");
        }
    }

    private async void CopyLog(object? sender, RoutedEventArgs args)
    {
        try
        {
            if (Clipboard is null)
                throw new NotSupportedException("The clipboard is not available on this computer.");
            await Clipboard.SetTextAsync(EventLog.Text ?? "");
        }
        catch (Exception error) when (Expected(error))
        {
            ShowError(error.Message, "Setup details couldn't be copied");
        }
    }
}