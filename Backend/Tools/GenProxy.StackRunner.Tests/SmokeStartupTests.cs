using System.Diagnostics;
using Xunit;

namespace GenProxy.StackRunner.Tests;

public sealed class SmokeStartupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "smoke-startup-" + Guid.NewGuid().ToString("N"));

    public SmokeStartupTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task LiveTrackedProcess_IsPreservedWithItsRecord()
    {
        using var process = Process.GetCurrentProcess();
        var path = Path.Combine(_directory, "api.pid");
        await ProcessIdentityFile.WriteAsync(path, ProcessIdentity.Capture(process), CancellationToken.None);
        var original = await File.ReadAllTextAsync(path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new StackRunnerApplicationInstance(Options()).RunAsync(CancellationToken.None));
        Assert.False(process.HasExited);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task NestedTrackedProcess_IsAlsoPreserved()
    {
        var nested = Path.Combine(_directory, "previous-run");
        Directory.CreateDirectory(nested);
        using var process = Process.GetCurrentProcess();
        var path = Path.Combine(nested, "main.pid");
        await ProcessIdentityFile.WriteAsync(path, ProcessIdentity.Capture(process), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StackRunnerApplicationInstance.EnsureNoTrackedStackAsync(_directory));
        Assert.False(process.HasExited);
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("invalid JSON")]
    public async Task UnverifiedRecord_IsPreservedForInspection(string record)
    {
        var path = Path.Combine(_directory, "main.pid");
        await File.WriteAllTextAsync(path, record);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StackRunnerApplicationInstance.EnsureNoTrackedStackAsync(_directory));
        Assert.Equal(record, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EmptyOrMissingRunDirectory_AllowsStartup()
    {
        await StackRunnerApplicationInstance.EnsureNoTrackedStackAsync(_directory);
        await StackRunnerApplicationInstance.EnsureNoTrackedStackAsync(Path.Combine(_directory, "absent"));
    }
    private StackRunnerOptions Options() => new(
        StackRunnerMode.Smoke, SmokeSuite.Basic, _directory, "unused.csproj", "https://localhost:7001",
        "unused-main.gguf", "main", "unused-reducer.gguf", "reducer", Path.Combine(_directory, ".cache"),
        Path.Combine(_directory, ".logs"), _directory, "version.txt", Path.Combine(_directory, "main.pid"),
        Path.Combine(_directory, "reducer.pid"), Path.Combine(_directory, "api.pid"),
        "invalid-owner", "invalid-repository", StackRunnerDefaults.LlamaRuntimeVersion, "public-test-key", "runtime-test-key", 50051, 50052,
        TimeSpan.FromSeconds(1), 0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1, 4096, 768, 4096, 4096,
        "test-platform", "unreachable-archive.tar.gz", "runtime-fixture");

}
