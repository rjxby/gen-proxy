using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace GenProxy.StackRunner;

internal sealed class BuildProcess : IAsyncDisposable
{
    private const string LauncherArgument = "--internal-api-build-launcher";
    private const int TerminateSignal = 15;
    private const int KillSignal = 9;
    private readonly Process _launcher;
    private readonly AnonymousPipeServerStream[] _pipes;
    private readonly StreamReader _stdoutReader;
    private readonly StreamReader _stderrReader;
    private readonly StreamReader _controlReader;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task<string> _stdout;
    private readonly Task<string> _stderr;
    private readonly Task _launcherExit;
    private Task<int>? _buildExit;
    private bool _ownsGroup;
    private bool _groupKillRequested;

    private BuildProcess(Process launcher, AnonymousPipeServerStream[] pipes, CancellationToken cancellationToken)
    {
        _launcher = launcher;
        _pipes = pipes;
        _controlReader = new StreamReader(pipes[0]);
        _stdoutReader = new StreamReader(pipes[1]);
        _stderrReader = new StreamReader(pipes[2]);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _stdout = _stdoutReader.ReadToEndAsync(_cancellation.Token);
        _stderr = _stderrReader.ReadToEndAsync(_cancellation.Token);
        _launcherExit = launcher.WaitForExitAsync(_cancellation.Token);
    }

    public static bool IsLauncher(string[] args) => args.Length == 5 && args[0] == LauncherArgument;

    public static async Task<int> RunLauncherAsync(string[] args)
    {
        // Establish a private session before any build descendant can exist.
        if (setsid() == -1)
        {
            throw new InvalidOperationException($"Failed to create API build process group: {Marshal.GetLastPInvokeError()}");
        }

        using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true);
        using var control = OpenLauncherPipe(args[1]);
        await using var stdout = OpenLauncherPipe(args[2]);
        await using var stderr = OpenLauncherPipe(args[3]);
        await using var writer = new StreamWriter(control) { AutoFlush = true };
        await writer.WriteLineAsync(Environment.ProcessId.ToString());
        if (await Console.In.ReadLineAsync() != "start")
        {
            return 1;
        }

        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "build", args[4], "--nologo", "--verbosity", "quiet" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var exitCode = 1;
        try
        {
            using var build = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start API build process.");
            var stdoutCopy = build.StandardOutput.BaseStream.CopyToAsync(stdout);
            var stderrCopy = build.StandardError.BaseStream.CopyToAsync(stderr);
            var buildExit = build.WaitForExitAsync();
            ObserveFailure(Task.WhenAll(stdoutCopy, stderrCopy, buildExit));
            var pendingTasks = new List<Task> { stdoutCopy, stderrCopy, buildExit };
            while (pendingTasks.Count > 0)
            {
                var completed = await Task.WhenAny(pendingTasks);
                await completed;
                pendingTasks.Remove(completed);
                if (completed == buildExit)
                {
                    break;
                }
            }

            exitCode = build.ExitCode;
            await writer.WriteLineAsync(exitCode.ToString());
            await Task.WhenAll(stdoutCopy, stderrCopy);
            await stdout.DisposeAsync();
            await stderr.DisposeAsync();
        }
        catch (Exception exception)
        {
            await writer.WriteLineAsync($"error: {exception.Message.ReplaceLineEndings(" ")}");
        }
        finally
        {
            // Keep the group leader alive after dotnet exits or forwarding fails so
            // its group cannot be reused before the owner verifies and stops it.
            await Console.In.ReadLineAsync();
        }

        return exitCode;
    }

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string rootDirectory,
        string projectPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not resolve the stack runner executable.");
        var pipes = Enumerable.Range(0, 3)
            .Select(_ => new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable)).ToArray();
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = rootDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(typeof(BuildProcess).Assembly.Location);
        }

        foreach (var argument in new[] { LauncherArgument, pipes[0].GetClientHandleAsString(), pipes[1].GetClientHandleAsString(), pipes[2].GetClientHandleAsString(), projectPath })
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process launcher;
        try
        {
            launcher = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start API build launcher.");
        }
        catch
        {
            foreach (var pipe in pipes) pipe.Dispose();
            throw;
        }

        foreach (var pipe in pipes) pipe.DisposeLocalCopyOfClientHandle();
        await using var owner = new BuildProcess(launcher, pipes, cancellationToken);
        owner._buildExit = owner.ReadBuildExitAsync();
        var pendingTasks = new List<Task> { owner._buildExit, owner._stdout, owner._stderr };
        while (pendingTasks.Count > 0)
        {
            var completed = await Task.WhenAny(pendingTasks.Append(owner._launcherExit)).WaitAsync(cancellationToken);
            await completed;
            if (completed == owner._launcherExit)
            {
                throw new InvalidOperationException("API build launcher exited before output collection completed.");
            }

            pendingTasks.Remove(completed);
            if (completed == owner._buildExit)
            {
                break;
            }
        }

        await owner.StopDescendantsAndDrainAsync(cancellationToken);
        return (await owner._buildExit, await owner._stdout, await owner._stderr);
    }

    private async Task StopDescendantsAndDrainAsync(CancellationToken cancellationToken)
    {
        // The launcher suppresses TERM and continues forwarding buffered output while
        // descendants stop. It remains the ownership anchor until the final group KILL.
        SignalGroup(TerminateSignal);
        var output = Task.WhenAll(_stdout, _stderr);
        ObserveFailure(output);
        try
        {
            await output.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (TimeoutException)
        {
            SignalGroup(KillSignal);
            await output.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private void SignalGroup(int signal)
    {
        if (_launcher.HasExited || getpgid(_launcher.Id) != _launcher.Id || kill(-_launcher.Id, signal) == -1)
        {
            throw new InvalidOperationException($"Failed to stop API build process group: {Marshal.GetLastPInvokeError()}");
        }

        _groupKillRequested = signal == KillSignal;
    }

    private async Task<int> ReadBuildExitAsync()
    {
        var ready = await _controlReader.ReadLineAsync(_cancellation.Token);
        if (!int.TryParse(ready, out var groupId) || groupId != _launcher.Id || getpgid(_launcher.Id) != groupId)
        {
            throw new InvalidOperationException("API build launcher did not establish its owned process group.");
        }

        _ownsGroup = true;
        _cancellation.Token.ThrowIfCancellationRequested();
        await _launcher.StandardInput.WriteLineAsync("start".AsMemory(), _cancellation.Token);
        await _launcher.StandardInput.FlushAsync(_cancellation.Token);
        var status = await _controlReader.ReadLineAsync(_cancellation.Token);
        return int.TryParse(status, out var exitCode)
            ? exitCode
            : throw new InvalidOperationException($"API build launcher did not report a build exit code. {status}");
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (!_launcher.HasExited && !_groupKillRequested)
            {
                if (_ownsGroup)
                {
                    SignalGroup(KillSignal);
                }
                else
                {
                    // No acknowledgment was sent, so the launcher cannot have started dotnet.
                    _launcher.Kill();
                }
            }

            await _launcher.WaitForExitAsync(timeout.Token);
        }
        catch (Exception exception)
        {
            ConsoleStyling.Warning($"Failed to stop API build process group: {exception.Message}");
        }
        finally
        {
            _cancellation.Cancel();
            var tasks = new List<Task> { _stdout, _stderr, _launcherExit };
            if (_buildExit is not null) tasks.Add(_buildExit);
            var completion = Task.WhenAll(tasks);
            ObserveFailure(completion);
            try
            {
                await completion.WaitAsync(timeout.Token);
            }
            catch
            {
                // Preserve the build result after observing canceled reads and secondary failures.
            }
            _controlReader.Dispose();
            _stdoutReader.Dispose();
            _stderrReader.Dispose();
            foreach (var pipe in _pipes) pipe.Dispose();
            _launcher.Dispose();
            _cancellation.Dispose();
        }
    }

    private static void ObserveFailure(Task task) =>
        _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    private static AnonymousPipeClientStream OpenLauncherPipe(string handle)
    {
        var pipe = new AnonymousPipeClientStream(PipeDirection.Out, handle);
        // Build descendants use redirected output, never the owner's private protocol pipes.
        // POSIX F_SETFD = 2 and FD_CLOEXEC = 1 on the supported macOS and Linux platforms.
        if (fcntl(int.Parse(handle), 2, 1) == -1)
        {
            pipe.Dispose();
            throw new InvalidOperationException("Failed to protect API build control pipe inheritance.");
        }

        return pipe;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int command, int argument);
}
