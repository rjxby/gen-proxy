using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace GenProxy.StackRunner;

internal static class StackRunnerApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = StackRunnerOptions.Load(args);
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        try
        {
            if (options.Mode == StackRunnerMode.Smoke)
            {
                foreach (var suite in SmokeSuiteUtilities.Expand(options.SmokeSuite))
                {
                    var exitCode = await new StackRunnerApplicationInstance(options with { SmokeSuite = suite }).RunAsync(cts.Token);
                    if (exitCode != 0)
                    {
                        return exitCode;
                    }
                }

                return 0;
            }

            var application = new StackRunnerApplicationInstance(options);
            return await application.RunAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return 130;
        }
    }
}

internal sealed class StackRunnerApplicationInstance(StackRunnerOptions options)
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly StackRunnerOptions _options = options;
    private readonly List<ManagedProcess> _managedProcesses = [];
    private int _cleanupStarted;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.RuntimeCacheDir);
        Directory.CreateDirectory(_options.RuntimeLogDir);
        Directory.CreateDirectory(_options.RuntimeRunDir);

        try
        {
            await StopManagedProcessesFromPidFilesAsync();

            var runtimeVersion = await ResolveRuntimeVersionAsync(cancellationToken);
            File.WriteAllText(_options.RuntimeVersionFile, runtimeVersion + Environment.NewLine);

            var runtimeDir = await EnsureRuntimeExtractedAsync(runtimeVersion, cancellationToken);
            await EnsureHttpsDevelopmentCertificateAsync(cancellationToken);

            var mainRuntime = await StartRuntimeAsync(
                name: "main",
                port: _options.GenerationRuntimePort,
                modelPath: _options.MainModelPath,
                workerCount: _options.MainWorkerCount,
                contextSize: SmokeSuiteUtilities.GetEffectiveMainContextSize(
                    _options.Mode,
                    _options.SmokeSuite,
                    _options.MainContextSize,
                    _options.SmokeMainContextSize),
                runtimeDir,
                _options.MainRuntimePidFile,
                echoToConsole: false,
                cancellationToken);

            var summarizerRuntime = await StartRuntimeAsync(
                name: "summarizer",
                port: _options.SummarizerRuntimePort,
                modelPath: _options.SummarizerModelPath,
                workerCount: _options.SummarizerWorkerCount,
                contextSize: SmokeSuiteUtilities.GetEffectiveSummarizerContextSize(
                    _options.Mode,
                    _options.SmokeSuite,
                    _options.SummarizerContextSize,
                    _options.SmokeSummarizerContextSize),
                runtimeDir,
                _options.SummarizerRuntimePidFile,
                echoToConsole: false,
                cancellationToken);

            ConsoleStyling.Info($">>> Waiting for main runtime on port {_options.GenerationRuntimePort}");
            await WaitForRuntimeReadyAsync("main", mainRuntime, _options.GenerationRuntimePort, cancellationToken);
            ConsoleStyling.Success(">>> main runtime is ready");

            ConsoleStyling.Info($">>> Waiting for summarizer runtime on port {_options.SummarizerRuntimePort}");
            await WaitForRuntimeReadyAsync("summarizer", summarizerRuntime, _options.SummarizerRuntimePort, cancellationToken);
            ConsoleStyling.Success(">>> summarizer runtime is ready");

            var apiProcess = await StartApiAsync(cancellationToken);
            ConsoleStyling.Info(">>> Waiting for gen-proxy API startup");
            await apiProcess.WaitForStartupAsync(_options.ApiStartupTimeout, cancellationToken);
            ConsoleStyling.Success(">>> gen-proxy API is ready");

            if (_options.Mode == StackRunnerMode.Smoke)
            {
                return await RunSmokeAsync(cancellationToken);
            }

            return await apiProcess.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private async Task<int> RunSmokeAsync(CancellationToken cancellationToken)
    {
        using var smokeRunner = new SmokeRunner(_options);
        return await smokeRunner.RunAsync(cancellationToken);
    }

    private async Task<string> ResolveRuntimeVersionAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_options.LlamaRuntimeVersion, "latest", StringComparison.OrdinalIgnoreCase))
        {
            return _options.LlamaRuntimeVersion;
        }

        ConsoleStyling.Info(">>> Resolving latest llama-runtime release");
        using var response = await HttpClient.GetAsync(_options.LatestReleaseApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken);
        if (string.IsNullOrWhiteSpace(release?.TagName))
        {
            throw new InvalidOperationException(
                $"Failed to resolve latest llama-runtime release version from {_options.LatestReleaseApiUrl}. " +
                "Set LLAMA_RUNTIME_VERSION explicitly to bypass release discovery.");
        }

        return release.TagName;
    }

    private async Task<string> EnsureRuntimeExtractedAsync(string version, CancellationToken cancellationToken)
    {
        var artifactDir = Path.Combine(_options.RuntimeCacheDir, version);
        var artifactPath = Path.Combine(artifactDir, _options.RuntimeArtifactName);
        var extractDir = Path.Combine(artifactDir, _options.PlatformMoniker);

        if (!File.Exists(artifactPath))
        {
            Directory.CreateDirectory(artifactDir);
            var downloadUrl = $"{_options.ReleaseDownloadBaseUrl}/{version}/{_options.RuntimeArtifactName}";
            ConsoleStyling.Info($">>> Downloading {downloadUrl}");

            var tempPath = artifactPath + ".tmp";
            await using (var destination = File.Create(tempPath))
            await using (var source = await HttpClient.GetStreamAsync(downloadUrl, cancellationToken))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            File.Move(tempPath, artifactPath, overwrite: true);
        }
        else
        {
            ConsoleStyling.Info($">>> Using cached llama-runtime artifact {artifactPath}");
        }

        var runtimeBinaryPath = Path.Combine(extractDir, _options.RuntimeBinaryName);
        if (!File.Exists(runtimeBinaryPath))
        {
            ConsoleStyling.Info($">>> Extracting {artifactPath}");
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }

            Directory.CreateDirectory(extractDir);

            await using var archiveStream = File.OpenRead(artifactPath);
            await using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzipStream, extractDir, overwriteFiles: true);
        }
        else
        {
            ConsoleStyling.Info($">>> Using extracted llama-runtime at {extractDir}");
        }

        await RemoveMacOsQuarantineAttributeAsync(extractDir, cancellationToken);

        return extractDir;
    }

    private async Task<ManagedProcess> StartRuntimeAsync(
        string name,
        int port,
        string modelPath,
        int workerCount,
        int? contextSize,
        string runtimeDir,
        string pidFile,
        bool echoToConsole,
        CancellationToken cancellationToken)
    {
        var logFile = Path.Combine(_options.RuntimeLogDir, $"{name}.log");
        var binaryPath = Path.Combine(runtimeDir, _options.RuntimeBinaryName);

        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            WorkingDirectory = runtimeDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.Environment["ASPNETCORE_URLS"] = $"https://localhost:{port}";
        startInfo.Environment["HostedModel__ModelPath"] = modelPath;
        startInfo.Environment["ApiKeys__Keys__0"] = _options.RuntimeApiKey;
        startInfo.Environment["Inference__WorkerCount"] = workerCount.ToString();

        if (contextSize is not null)
        {
            startInfo.Environment["Llama__Native__ContextSize"] = contextSize.Value.ToString();
        }

        ConsoleStyling.Info($">>> Starting {name} runtime on https://localhost:{port}");
        var process = await ManagedProcess.StartAsync(
            name,
            startInfo,
            logFile,
            echoToConsole,
            startupDetector: line =>
                line.Contains("Application started. Press Ctrl+C to shut down.", StringComparison.Ordinal) ||
                line.Contains("Now listening on:", StringComparison.Ordinal),
            cancellationToken);

        _managedProcesses.Add(process);
        await File.WriteAllTextAsync(pidFile, process.ProcessId.ToString() + Environment.NewLine, cancellationToken);
        ConsoleStyling.Info($">>> {name} runtime pid {process.ProcessId} (log: .runtime-logs/{name}.log)");
        return process;
    }

    private async Task<ManagedProcess> StartApiAsync(CancellationToken cancellationToken)
    {
        var logFile = Path.Combine(_options.RuntimeLogDir, "api.log");
        await BuildApiAsync(cancellationToken);

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _options.RootDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add(GetApiAssemblyPath());

        startInfo.Environment["ApiKeys__Keys__0"] = _options.PublicApiKey;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = _options.ApiBaseUrl;
        startInfo.Environment["GenerationRuntime__Address"] = $"https://localhost:{_options.GenerationRuntimePort}";
        startInfo.Environment["PromptReducerRuntime__Address"] = $"https://localhost:{_options.SummarizerRuntimePort}";
        startInfo.Environment["PromptReduction__UsePromptReducerRuntime"] = bool.TrueString;
        startInfo.Environment["GenerationRuntime__ApiKey"] = _options.RuntimeApiKey;
        startInfo.Environment["PromptReducerRuntime__ApiKey"] = _options.RuntimeApiKey;

        ConsoleStyling.Info(">>> Starting gen-proxy against localhost HTTPS runtimes");
        var process = await ManagedProcess.StartAsync(
            "api",
            startInfo,
            logFile,
            echoToConsole: true,
            startupDetector: line =>
                line.Contains("Application started. Press Ctrl+C to shut down.", StringComparison.Ordinal) ||
                line.Contains("Now listening on:", StringComparison.Ordinal),
            cancellationToken);

        _managedProcesses.Add(process);
        return process;
    }

    private async Task BuildApiAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _options.RootDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(_options.DotnetProjectPath);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("quiet");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start API build process.");
        }

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Failed to build gen-proxy API.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}".Trim());
        }
    }

    private string GetApiAssemblyPath()
    {
        var projectDirectory = Path.GetDirectoryName(_options.DotnetProjectPath)
            ?? throw new InvalidOperationException($"Could not resolve project directory for {_options.DotnetProjectPath}.");
        return Path.Combine(projectDirectory, "bin", "Debug", "net10.0", "GenProxy.Api.Host.dll");
    }

    private async Task WaitForRuntimeReadyAsync(string name, ManagedProcess process, int port, CancellationToken cancellationToken)
    {
        await process.WaitForStartupAsync(_options.RuntimeStartupTimeout, cancellationToken);

        var timeoutAt = DateTimeOffset.UtcNow + _options.RuntimeStartupTimeout;

        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.ThrowIfExitedUnexpectedly();

            if (await CanConnectToPortAsync(port, cancellationToken))
            {
                await EnsureStableAsync(name, process, cancellationToken);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException($"{name} runtime did not become ready in time. See .runtime-logs/{name}.log");
    }

    private static async Task<bool> CanConnectToPortAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cancellationToken);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task EnsureStableAsync(string name, ManagedProcess process, CancellationToken cancellationToken)
    {
        for (var i = 0; i < _options.RuntimeReadyStabilitySeconds; i++)
        {
            process.ThrowIfExitedUnexpectedly();
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task StopManagedProcessesFromPidFilesAsync()
    {
        await StopProcessFromPidFileAsync(_options.SummarizerRuntimePidFile, "summarizer");
        await StopProcessFromPidFileAsync(_options.MainRuntimePidFile, "main");
    }

    private static async Task StopProcessFromPidFileAsync(string pidFile, string name)
    {
        if (!File.Exists(pidFile))
        {
            return;
        }

        var content = await File.ReadAllTextAsync(pidFile);
        if (!int.TryParse(content.Trim(), out var pid))
        {
            File.Delete(pidFile);
            return;
        }

        if (!ProcessUtilities.TryGetProcess(pid, out var process))
        {
            File.Delete(pidFile);
            return;
        }

        ConsoleStyling.Warning($">>> Stopping {name} runtime (pid {pid})");
        await ProcessUtilities.StopAsync(process, TimeSpan.FromSeconds(10));
        File.Delete(pidFile);
    }

    private async Task CleanupAsync()
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) == 1)
        {
            return;
        }

        foreach (var process in _managedProcesses.AsEnumerable().Reverse())
        {
            await process.DisposeAsync();
        }

        DeleteFileIfExists(_options.MainRuntimePidFile);
        DeleteFileIfExists(_options.SummarizerRuntimePidFile);
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static async Task RemoveMacOsQuarantineAttributeAsync(string directory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "xattr",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory
        };

        startInfo.ArgumentList.Add("-rd");
        startInfo.ArgumentList.Add("com.apple.quarantine");
        startInfo.ArgumentList.Add(directory);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("gen-proxy-stack-runner/1.0");
        return client;
    }

    private static async Task EnsureHttpsDevelopmentCertificateAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("dev-certs");
        startInfo.ArgumentList.Add("https");
        startInfo.ArgumentList.Add("--check");
        startInfo.ArgumentList.Add("--trust");
        startInfo.ArgumentList.Add("--quiet");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start 'dotnet dev-certs https --check'.");
        }

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "A trusted local ASP.NET Core HTTPS development certificate is required to start the HTTPS llama-runtime processes. " +
            "Run 'dotnet dev-certs https --trust' and retry.");
    }

    private sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string TagName);
}

internal sealed record StackRunnerOptions(
    StackRunnerMode Mode,
    SmokeSuite SmokeSuite,
    string RootDir,
    string DotnetProjectPath,
    string ApiBaseUrl,
    string MainModelPath,
    string SummarizerModelPath,
    string RuntimeCacheDir,
    string RuntimeLogDir,
    string RuntimeRunDir,
    string RuntimeVersionFile,
    string MainRuntimePidFile,
    string SummarizerRuntimePidFile,
    string LlamaRuntimeOwner,
    string LlamaRuntimeRepo,
    string LlamaRuntimeVersion,
    string PublicApiKey,
    string RuntimeApiKey,
    int GenerationRuntimePort,
    int SummarizerRuntimePort,
    TimeSpan RuntimeStartupTimeout,
    int RuntimeReadyStabilitySeconds,
    TimeSpan ApiStartupTimeout,
    TimeSpan SmokeRequestTimeout,
    int MainWorkerCount,
    int SummarizerWorkerCount,
    int? MainContextSize,
    int? SmokeMainContextSize,
    int? SummarizerContextSize,
    int? SmokeSummarizerContextSize,
    string PlatformMoniker,
    string RuntimeArtifactName,
    string RuntimeBinaryName)
{
    public string LatestReleaseApiUrl => $"https://api.github.com/repos/{LlamaRuntimeOwner}/{LlamaRuntimeRepo}/releases/latest";
    public string ReleaseDownloadBaseUrl => $"https://github.com/{LlamaRuntimeOwner}/{LlamaRuntimeRepo}/releases/download";

    public static StackRunnerOptions Load(string[] args)
    {
        var (mode, smokeSuite) = ParseMode(args);
        var rootDir = FindRootDirectory();
        var dotnetProjectPath = Path.Combine(rootDir, "Backend", "Api", "Host", "GenProxy.Api.Host.csproj");

        var options = new StackRunnerOptions(
            Mode: mode,
            SmokeSuite: smokeSuite,
            RootDir: rootDir,
            DotnetProjectPath: dotnetProjectPath,
            ApiBaseUrl: ReadEnvironment("GEN_PROXY_BASE_URL", "https://localhost:7001"),
            MainModelPath: RequireExistingFile("MAIN_MODEL_PATH"),
            SummarizerModelPath: RequireExistingFile("SUMMARIZER_MODEL_PATH"),
            RuntimeCacheDir: Path.Combine(rootDir, ".runtime-cache"),
            RuntimeLogDir: Path.Combine(rootDir, ".runtime-logs"),
            RuntimeRunDir: Path.Combine(rootDir, ".runtime-run"),
            RuntimeVersionFile: Path.Combine(rootDir, ".runtime-run", "llama-runtime-version.txt"),
            MainRuntimePidFile: Path.Combine(rootDir, ".runtime-run", "main.pid"),
            SummarizerRuntimePidFile: Path.Combine(rootDir, ".runtime-run", "summarizer.pid"),
            LlamaRuntimeOwner: ReadEnvironment("LLAMA_RUNTIME_OWNER", "rjxby"),
            LlamaRuntimeRepo: ReadEnvironment("LLAMA_RUNTIME_REPO", "llama-runtime"),
            LlamaRuntimeVersion: ReadEnvironment("LLAMA_RUNTIME_VERSION", "latest"),
            PublicApiKey: ReadEnvironmentAny(["ApiKeys__Keys__0", "APIKEYS__KEYS__0"], "dev-local-key"),
            RuntimeApiKey: ReadEnvironment("LLAMA_RUNTIME_API_KEY", ReadEnvironment("LLAMA_RUNTIME_DEFAULT_API_KEY", "runtime-local-key")),
            GenerationRuntimePort: ReadEnvironmentInt("GENERATION_RUNTIME_PORT", 50051),
            SummarizerRuntimePort: ReadEnvironmentInt("SUMMARIZER_RUNTIME_PORT", 50052),
            RuntimeStartupTimeout: TimeSpan.FromSeconds(ReadEnvironmentInt("RUNTIME_STARTUP_TIMEOUT", 60)),
            RuntimeReadyStabilitySeconds: ReadEnvironmentInt("RUNTIME_READY_STABILITY_SECONDS", 3),
            ApiStartupTimeout: TimeSpan.FromSeconds(ReadEnvironmentInt("API_STARTUP_TIMEOUT", 60)),
            SmokeRequestTimeout: TimeSpan.FromSeconds(ReadEnvironmentInt("SMOKE_REQUEST_TIMEOUT", 30)),
            MainWorkerCount: ReadEnvironmentInt("MAIN_WORKER_COUNT", 4),
            SummarizerWorkerCount: ReadEnvironmentInt("SUMMARIZER_WORKER_COUNT", 1),
            MainContextSize: ReadEnvironmentNullableInt("MAIN_CONTEXT_SIZE"),
            SmokeMainContextSize: ReadEnvironmentNullableInt("SMOKE_MAIN_CONTEXT_SIZE"),
            SummarizerContextSize: ReadEnvironmentNullableInt("SUMMARIZER_CONTEXT_SIZE"),
            SmokeSummarizerContextSize: ReadEnvironmentNullableInt("SMOKE_SUMMARIZER_CONTEXT_SIZE"),
            PlatformMoniker: DetectPlatformMoniker(),
            RuntimeArtifactName: $"llama-runtime-grpc-{DetectPlatformMoniker()}.tar.gz",
            RuntimeBinaryName: "LlamaRuntime.Presentation.Grpc");

        return options;
    }

    private static (StackRunnerMode Mode, SmokeSuite SmokeSuite) ParseMode(string[] args)
    {
        if (args.Length == 0)
        {
            return (StackRunnerMode.StackRun, SmokeSuite.Basic);
        }

        if (string.Equals(args[0], "stack-run", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1)
            {
                throw new InvalidOperationException("Mode 'stack-run' does not accept additional arguments.");
            }

            return (StackRunnerMode.StackRun, SmokeSuite.Basic);
        }

        if (string.Equals(args[0], "smoke", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length == 1)
            {
                return (StackRunnerMode.Smoke, SmokeSuite.All);
            }

            if (args.Length == 2)
            {
                return (StackRunnerMode.Smoke, ParseSmokeSuite(args[1]));
            }

            throw new InvalidOperationException("Mode 'smoke' accepts at most one suite argument: basic, budget, or all.");
        }

        throw new InvalidOperationException($"Unsupported mode '{args[0]}'. Supported modes: stack-run, smoke.");
    }

    private static SmokeSuite ParseSmokeSuite(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "basic" => SmokeSuite.Basic,
            "budget" => SmokeSuite.Budget,
            "all" => SmokeSuite.All,
            _ => throw new InvalidOperationException($"Unsupported smoke suite '{value}'. Supported suites: basic, budget, all.")
        };
    }

    private static string FindRootDirectory()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var makefilePath = Path.Combine(current.FullName, "Makefile");
            var solutionPath = Path.Combine(current.FullName, "Backend", "GenProxy.sln");
            if (File.Exists(makefilePath) && File.Exists(solutionPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static string RequireExistingFile(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        if (!File.Exists(value))
        {
            throw new FileNotFoundException($"{name} does not exist: {value}");
        }

        return value;
    }

    private static string ReadEnvironment(string name, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static string ReadEnvironmentAny(IEnumerable<string> names, string defaultValue)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return defaultValue;
    }

    private static int ReadEnvironmentInt(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static int? ReadEnvironmentNullableInt(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string DetectPlatformMoniker()
    {
        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "osx-arm64",
                _ => throw new PlatformNotSupportedException($"Unsupported macOS architecture '{RuntimeInformation.ProcessArchitecture}' for llama-runtime releases.")
            };
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => throw new PlatformNotSupportedException($"Unsupported Linux architecture '{RuntimeInformation.ProcessArchitecture}' for llama-runtime releases.")
            };
        }

        throw new PlatformNotSupportedException($"Unsupported host OS '{Environment.OSVersion.Platform}'.");
    }
}

internal sealed class ManagedProcess : IAsyncDisposable
{
    private readonly Process _process;
    private Task _stdoutPump;
    private Task _stderrPump;
    private readonly TaskCompletionSource _startupTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextWriter? _logWriter;
    private int _stopStarted;

    private ManagedProcess(Process process, Task stdoutPump, Task stderrPump, TextWriter? logWriter)
    {
        _process = process;
        _stdoutPump = stdoutPump;
        _stderrPump = stderrPump;
        _logWriter = logWriter;
    }

    public int ProcessId => _process.Id;

    public static Task<ManagedProcess> StartAsync(
        string name,
        ProcessStartInfo startInfo,
        string logPath,
        bool echoToConsole,
        Func<string, bool> startupDetector,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {name} process.");
        }

        var writer = TextWriter.Synchronized(new StreamWriter(File.Open(logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        });

        var managedProcess = new ManagedProcess(process, Task.CompletedTask, Task.CompletedTask, writer);
        managedProcess._stdoutPump = PumpAsync(
            process.StandardOutput,
            writer,
            echoToConsole ? Console.Out : null,
            startupDetector,
            managedProcess._startupTcs,
            cancellationToken);
        managedProcess._stderrPump = PumpAsync(
            process.StandardError,
            writer,
            echoToConsole ? Console.Error : null,
            startupDetector,
            managedProcess._startupTcs,
            cancellationToken);

        process.Exited += (_, _) =>
        {
            managedProcess._startupTcs.TrySetException(
                new InvalidOperationException($"{name} exited before startup completed. See {logPath}."));
        };

        return Task.FromResult(managedProcess);
    }

    public async Task WaitForStartupAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await _startupTcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Process '{_process.StartInfo.FileName}' did not report startup within {timeout.TotalSeconds:0} seconds.");
        }
    }

    public void ThrowIfExitedUnexpectedly()
    {
        if (_process.HasExited)
        {
            throw new InvalidOperationException($"{_process.StartInfo.FileName} exited with code {_process.ExitCode}.");
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(_stdoutPump, _stderrPump);
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopStarted, 1) == 1)
        {
            return;
        }

        await ProcessUtilities.StopAsync(_process, TimeSpan.FromSeconds(10));
        await Task.WhenAll(_stdoutPump, _stderrPump);
        _logWriter?.Dispose();
        _process.Dispose();
    }

    private static async Task PumpAsync(
        StreamReader reader,
        TextWriter logWriter,
        TextWriter? consoleWriter,
        Func<string, bool> startupDetector,
        TaskCompletionSource startupTcs,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (startupDetector(line))
                {
                    startupTcs.TrySetResult();
                }
                await logWriter.WriteLineAsync(line);
                if (consoleWriter is not null)
                {
                    await consoleWriter.WriteLineAsync(line);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}

internal static class ProcessUtilities
{
    public static bool TryGetProcess(int pid, out Process process)
    {
        try
        {
            process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            process = null!;
            return false;
        }
    }

    public static async Task StopAsync(Process process, TimeSpan timeout)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        if (await TrySendSignalAsync(process.Id, "TERM", timeout))
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch
        {
        }
    }

    private static async Task<bool> TrySendSignalAsync(int pid, string signal, TimeSpan timeout)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "kill",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.StartInfo.ArgumentList.Add($"-{signal}");
            process.StartInfo.ArgumentList.Add(pid.ToString());

            if (!process.Start())
            {
                return false;
            }

            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
            if (process.ExitCode != 0)
            {
                return false;
            }

            var waitUntil = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < waitUntil)
            {
                try
                {
                    using var target = Process.GetProcessById(pid);
                    if (target.HasExited)
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
