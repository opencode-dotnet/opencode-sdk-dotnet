using System.Reflection;
using OpenCode.Sdk.Tools.Generator.Ingestion;
using OpenCode.Sdk.Tools.Generator.Ingestion.Abstractions;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion;

/// <summary>
/// Boundary guards: the mutable Microsoft.OpenApi DOM must never escape the ingestion
/// implementation — <c>SpecDocument</c> is the deep-module seam the Binder builds on.
/// </summary>
public sealed class IngestionBoundaryTests
{
    [Test]
    public async Task Ingestion_Surface_Should_Not_Expose_Microsoft_OpenApi_Types()
    {
        var toolsAssembly = typeof(SpecIngestion).Assembly;
        var openApiAssembly = typeof(Microsoft.OpenApi.OpenApiDocument).Assembly;
        var ingestionTypes = toolsAssembly
            .GetTypes()
            .Where(static type => type == typeof(SpecIngestion)
                || type.Namespace is "OpenCode.Sdk.Tools.Generator.Ingestion.Abstractions"
                    or "OpenCode.Sdk.Tools.Generator.Ingestion.Models")
            .ToArray();

        await Assert.That(ingestionTypes).Contains(typeof(ISpecIngestion));
        await Assert.That(ingestionTypes).Contains(typeof(SpecDocument));

        var leaks = new List<string>();
        foreach (var type in ingestionTypes)
        {
            leaks.AddRange(ReferencedTypes(type)
                .Where(referenced => referenced.Assembly == openApiAssembly)
                .Select(referenced => $"{type.FullName} exposes {referenced.FullName}"));
        }

        await Assert.That(leaks).IsEmpty();
    }

    [Test]
    public async Task Only_The_Ingestion_Slice_Should_Use_Microsoft_OpenApi()
    {
        var fileSystem = new RealFileSystem();
        var toolsRoot = FindToolsSourceRoot(fileSystem);
        var offenders = new List<string>();
        foreach (var path in fileSystem.Directory.EnumerateFiles(toolsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsInsideIngestion(fileSystem, path))
            {
                continue;
            }

            var source = await fileSystem.File.ReadAllTextAsync(path);
            if (source.Contains("using Microsoft.OpenApi", StringComparison.Ordinal))
            {
                offenders.Add(path);
            }
        }

        await Assert.That(offenders).IsEmpty();
    }

    private static bool IsInsideIngestion(RealFileSystem fileSystem, string path)
    {
        var normalized = path.Replace(fileSystem.Path.DirectorySeparatorChar, '/');
        return normalized.Contains("/Generator/Ingestion/", StringComparison.Ordinal);
    }

    private static string FindToolsSourceRoot(RealFileSystem fileSystem) =>
        fileSystem.Path.Combine(new RepositoryRoot(fileSystem).Locate(), "tools", "OpenCode.Sdk.Tools");

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const BindingFlags declaredPublic = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var referenced = new List<Type?>
        {
            type.BaseType,
        };
        referenced.AddRange(type.GetProperties(declaredPublic).Select(static property => property.PropertyType));
        foreach (var method in type.GetMethods(declaredPublic))
        {
            referenced.Add(method.ReturnType);
            referenced.AddRange(method.GetParameters().Select(static parameter => parameter.ParameterType));
        }

        foreach (var constructor in type.GetConstructors())
        {
            referenced.AddRange(constructor.GetParameters().Select(static parameter => parameter.ParameterType));
        }

        return referenced.OfType<Type>().SelectMany(Expand).Distinct();
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(Expand))
            {
                yield return argument;
            }
        }

        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var expanded in Expand(element))
            {
                yield return expanded;
            }
        }
    }
}
