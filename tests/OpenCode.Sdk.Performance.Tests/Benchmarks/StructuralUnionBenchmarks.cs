using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// Structural-union dispatch in isolation, through the generated carriers' converters: a
/// token-distinct union (<see cref="FormValue"/>), a first-match union whose three object branches
/// share the object token (the migration status), with its running branch read directly as the
/// no-dispatch control, and a mixed union (<see cref="ConfigReferenceEntry"/>).
/// </summary>
/// <remarks>
/// The token-distinct union picks its arm from the token alone. The first-match union parses the
/// object once and tries its claims in declaration order: the first case is claimed by the first
/// claim, the second by the second, the third by the third, and the unclaimed object tries all
/// three before it is kept as the unknown arm. The cases differ in size and in what they
/// materialize too, so the rows show what each case costs, not what one more claim costs. Every
/// case's expected arm is checked once before measuring. The rows return <see cref="object"/>
/// because BenchmarkDotNet's generated runner names a benchmark's return type and does not inherit
/// this project's experimental-diagnostic arbitration, and the migration status is experimental.
/// </remarks>
[MemoryDiagnoser]
public class StructuralUnionBenchmarks
{
    private const string RunningStatus = "{\"status\":\"running\",\"progress\":{\"label\":\"sessions\",\"numerator\":3,\"denominator\":10}}";

    public static IEnumerable<WireFixture> FormValueFixtures()
    {
        yield return Value("text", "\"yes\"");
        yield return Value("number", "42");
        yield return Value("boolean", "true");
        yield return Value("text-list", "[\"staging\",\"production\",\"canary\"]");
        yield return Value("unknown-object", "{\"choice\":1}");
    }

    public static IEnumerable<WireFixture> MigrationStatusFixtures()
    {
        yield return Value("idle-first-claim", "{\"status\":\"completed\"}");
        yield return Value("running-second-claim", RunningStatus);
        yield return Value("error-third-claim", "{\"status\":\"error\",\"error\":\"the V1 database is locked\"}");
        yield return Value("unclaimed-object", "{\"status\":\"running\"}");
    }

    public static IEnumerable<WireFixture> RunningStatusFixtures()
    {
        yield return Value("running-second-claim", RunningStatus);
    }

    public static IEnumerable<WireFixture> ReferenceFixtures()
    {
        yield return Value("text", "\"https://opencode.ai/docs\"");
        yield return Value("git-first-claim", "{\"repository\":\"https://github.com/anomalyco/opencode\",\"branch\":\"v2\"}");
        yield return Value("local-second-claim", "{\"path\":\"../sibling\",\"hidden\":true}");
        yield return Value("unclaimed-object", "{\"url\":\"https://example.invalid\"}");
    }

    [GlobalSetup]
    public void Setup()
    {
        Expect(FormValueFixtures(), fixture => (DeserializeFormValue(fixture) as FormValue)?.Kind.ToString(),
            ["Text", "Number", "Boolean", "TextList", "Unknown"]);
        Expect(MigrationStatusFixtures(), fixture => (DeserializeMigrationStatus(fixture) as ExperimentalMigrationV1Status)?.Kind.ToString(),
            ["ExperimentalMigrationV1StatusIdle", "ExperimentalMigrationV1StatusRunning", "ExperimentalMigrationV1StatusError", "Unknown"]);
        Expect(ReferenceFixtures(), fixture => (DeserializeReference(fixture) as ConfigReferenceEntry)?.Kind.ToString(),
            ["Text", "ConfigReferenceGit", "ConfigReferenceLocal", "Unknown"]);
        if (RunningStatusFixtures().Any(fixture => DeserializeConcreteRunningStatus(fixture) is not ExperimentalMigrationV1StatusRunning { Progress.Denominator: 10 }))
        {
            throw new InvalidOperationException("The running status control did not materialize its record.");
        }
    }

    /// <summary>Token-distinct dispatch: the token alone picks the arm.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(FormValueFixtures))]
    public object? DeserializeFormValue(WireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return JsonSerializer.Deserialize(fixture.Bytes, OpenCodeJsonContext.Default.FormValue);
    }

    /// <summary>First-match dispatch over three object claims: parse, claim in order, deserialize the winner.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(MigrationStatusFixtures))]
    public object? DeserializeMigrationStatus(WireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return JsonSerializer.Deserialize(fixture.Bytes, OpenCodeJsonContext.Default.ExperimentalMigrationV1Status);
    }

    /// <summary>The control: the running branch's bytes read into its record without the union's claims.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(RunningStatusFixtures))]
    public object? DeserializeConcreteRunningStatus(WireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return JsonSerializer.Deserialize(fixture.Bytes, OpenCodeJsonContext.Default.ExperimentalMigrationV1StatusRunning);
    }

    /// <summary>Mixed dispatch: a string by its token, an object by two claims.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(ReferenceFixtures))]
    public object? DeserializeReference(WireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return JsonSerializer.Deserialize(fixture.Bytes, OpenCodeJsonContext.Default.ConfigReferenceEntry);
    }

    /// <summary>Checks each fixture lands on the arm its name claims, in order, by the carrier's kind.</summary>
    private static void Expect(IEnumerable<WireFixture> fixtures, Func<WireFixture, string?> kindOf, string[] kinds)
    {
        var index = 0;
        foreach (var fixture in fixtures)
        {
            var kind = kindOf(fixture);
            if (index >= kinds.Length || !string.Equals(kind, kinds[index], StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Fixture '{fixture.Name}' dispatched to '{kind}', not '{(index < kinds.Length ? kinds[index] : "<none>")}'.");
            }

            index++;
        }

        if (index != kinds.Length)
        {
            throw new InvalidOperationException("A union's fixtures and expected arms differ in number.");
        }
    }

    private static WireFixture Value(string name, string json)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        return new WireFixture(name, body, items: 1, payloadBytesPerItem: body.Length);
    }
}
