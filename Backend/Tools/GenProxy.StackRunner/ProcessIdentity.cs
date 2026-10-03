using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GenProxy.StackRunner;

internal sealed record ProcessIdentity(int ProcessId, long StartTimeUtcTicks, string ExecutablePath)
{
    public bool IsValid => ProcessId > 0 &&
        StartTimeUtcTicks > 0 && StartTimeUtcTicks <= DateTime.MaxValue.Ticks &&
        !string.IsNullOrWhiteSpace(ExecutablePath) && Path.IsPathFullyQualified(ExecutablePath);

    public static ProcessIdentity Capture(Process process)
    {
        if (process.HasExited)
        {
            throw new InvalidOperationException("Cannot record an exited process.");
        }

        var executablePath = process.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Cannot verify the process executable.");
        }

        return new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(executablePath));
    }

    public bool Matches(Process process)
    {
        if (!IsValid)
        {
            return false;
        }

        try
        {
            var actual = Capture(process);
            return ProcessId == actual.ProcessId && StartTimeUtcTicks == actual.StartTimeUtcTicks &&
                string.Equals(ExecutablePath, actual.ExecutablePath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

internal static class ProcessIdentityFile
{
    public static async Task<ProcessIdentity?> ReadAsync(string path)
    {
        try
        {
            var content = await File.ReadAllTextAsync(path);
            var identity = JsonSerializer.Deserialize<ProcessIdentity>(content);
            return identity is { IsValid: true } ? identity : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A legacy PID, partial write, or unreadable record cannot establish ownership.
            return null;
        }
    }

    public static async Task WriteAsync(string path, ProcessIdentity identity, CancellationToken cancellationToken)
    {
        if (!identity.IsValid)
        {
            throw new InvalidOperationException("Cannot write an incomplete process identity.");
        }

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(identity) + Environment.NewLine, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}

internal static class ProcessUtilities
{
    public static async Task StopAsync(ProcessIdentity identity, TimeSpan timeout)
    {
        using var process = OpenMatchingProcess(identity);
        if (process is null)
        {
            return;
        }

        if (!OperatingSystem.IsWindows() && SendSignal(identity.ProcessId, 15) == 0 &&
            await WaitForExitAsync(process, timeout))
        {
            return;
        }

        // Re-open the PID before escalation. The original process may have exited or
        // changed executable while handling TERM, and its PID may have been reused.
        using var escalationProcess = OpenMatchingProcess(identity);
        if (escalationProcess is null)
        {
            return;
        }

        try
        {
            escalationProcess.Kill(entireProcessTree: true);
            await WaitForExitAsync(escalationProcess, timeout);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    private static Process? OpenMatchingProcess(ProcessIdentity identity)
    {
        if (!identity.IsValid)
        {
            return null;
        }

        Process? process = null;
        try
        {
            process = Process.GetProcessById(identity.ProcessId);
            if (identity.Matches(process))
            {
                return process;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }

        process?.Dispose();
        return null;
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int processId, int signal);
}
