using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Implementation.Clients;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Implementation.Services;
using Xunit;

namespace GenProxy.Api.UnitTests.Architecture;

public class ArchitectureBoundaryTests
{
    public static TheoryData<string, string[]> ProjectDependencies => new()
    {
        { "Host", ["GenProxy.Api.Services.Contracts", "GenProxy.Api.Services.Implementation", "GenProxy.Api.Integrations.Implementation"] },
        { "Services/Contracts", [] },
        { "Services/Implementation", ["GenProxy.Api.Services.Contracts", "GenProxy.Api.Integrations.Contracts"] },
        { "Integrations/Contracts", [] },
        { "Integrations/Implementation", ["GenProxy.Api.Integrations.Contracts"] }
    };

    [Theory]
    [MemberData(nameof(ProjectDependencies))]
    public void ApiProjects_OnlyReferenceTheirDeclaredLayers(string folder, string[] allowed)
    {
        var root = FindRepositoryRoot();
        var project = Directory.GetFiles(Path.Combine(root, "Backend/Api", folder), "*.csproj").Single();
        var references = XDocument.Load(project).Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')));

        references.Should().BeSubsetOf(allowed, $"{folder} must preserve docs/architecture.md dependencies");
    }

    [Fact]
    public void ServiceAndContractAssemblies_DoNotAcquireTransportOrImplementationDependencies()
    {
        Assembly[] assemblies =
        [
            typeof(IResponseGenerationService).Assembly,
            typeof(IGenerationRuntimeClient).Assembly,
            typeof(ResponseGenerationService).Assembly
        ];
        foreach (var assembly in assemblies)
        {
            assembly.GetReferencedAssemblies().Select(reference => reference.Name!).Should().NotContain(name =>
                name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
                name.StartsWith("Grpc", StringComparison.Ordinal) ||
                name == "Google.Protobuf" || name == "GenProxy.Api.Host" ||
                name == typeof(GrpcLlamaRuntimeClient).Assembly.GetName().Name);
        }
    }

    [Fact]
    public void ApiSolutionAndTests_DoNotReferenceLocalTools()
    {
        var root = FindRepositoryRoot();
        File.ReadAllText(Path.Combine(root, "Backend/GenProxy.sln")).Should().NotContain("Tools/").And.NotContain("Tools\\");
        foreach (var project in Directory.GetFiles(Path.Combine(root, "Backend/Tests"), "*.csproj", SearchOption.AllDirectories))
        {
            var references = XDocument.Load(project).Descendants("ProjectReference")
                .Select(reference => reference.Attribute("Include")!.Value.Replace('\\', '/'));
            references.Should().NotContain(reference => reference.Contains("/Tools/", StringComparison.Ordinal));
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Backend/GenProxy.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Cannot locate Backend/GenProxy.sln.");
    }
}
