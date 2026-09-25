using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lucia.Homelab.Server.Domains;

internal interface ICertbotExecutor
{
    Task<int> ExecuteAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

internal sealed class CertbotProcessExecutor : ICertbotExecutor
{
    internal const string Executable = "/opt/certbot/bin/certbot";

    public Task<int> ExecuteAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() == 0)
            throw new CertbotException("certbot_host_unsupported",
                "Run Certbot in the managed Linux host as a non-root service account.");
        var start = new ProcessStartInfo(Executable);
        start.Environment.Clear();
        start.Environment["PATH"] = "/opt/certbot/bin:/usr/bin:/bin";
        start.Environment["LANG"] = "C.UTF-8";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return RunAsync(start, timeout, ct);
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    // Internal seam runs a known test child, never an inherited-PATH replacement for Certbot.
    internal static async Task<int> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct)
    {
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        Task? output = null;
        Task? error = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!process.Start()) throw new Win32Exception();
            output = DrainAsync(process.StandardOutput.BaseStream, deadline.Token);
            error = DrainAsync(process.StandardError.BaseStream, deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error).WaitAsync(deadline.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            await StopOwnedChildAsync(process);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw new CertbotException("certbot_timeout",
                "Certbot exceeded its operation deadline and its owned child was stopped. Check DNS propagation and retry.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            await StopOwnedChildAsync(process);
            throw new CertbotException("certbot_execution_failed",
                "Cannot execute the pinned Certbot installation. Check the managed host installation and service permissions.");
        }
        finally
        {
            deadline.Cancel();
            // A descendant that retained a pipe must not keep this operation alive.
            if (output is not null || error is not null)
            {
                process.StandardOutput.Dispose();
                process.StandardError.Dispose();
                try { await Task.WhenAll(output ?? Task.CompletedTask, error ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException) { }
            }
        }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        // Discard rather than capture: neither provider output nor secrets can reach diagnostics.
        while (await stream.ReadAsync(buffer, ct) != 0) { }
    }

    private static async Task StopOwnedChildAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception)
        {
            throw new CertbotException("certbot_child_stop_failed",
                "The owned Certbot child could not be stopped. Check its service identity before retrying.");
        }
        catch (TimeoutException)
        {
            throw new CertbotException("certbot_child_stop_failed",
                "The owned Certbot child did not stop in time. Check the service before retrying.");
        }
    }
}
