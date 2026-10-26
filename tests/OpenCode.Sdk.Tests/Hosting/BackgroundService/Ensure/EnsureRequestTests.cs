using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Ensure;

/// <summary>The Ensure door refuses command entries that a native spawn would cut short.</summary>
public sealed class EnsureRequestTests
{
    private static class EnsureRequestData
    {
        public const string PublicParameter = "options";
        public const string Executable = "bun";
        public const string NulExecutable = "/opt/oc/bun\0";
        public const string LeadingArgument = "serve";
        public const string NulLeadingArgument = "serve\0";
        public const string RefusalMessage = "OpenCodeServerEnsureOptions.Command entries cannot contain NUL.";
        public const string BlankRefusalMessage = "OpenCodeServerEnsureOptions.Command entries cannot be blank.";
        public const string EnvironmentName = "OPENCODE_TEST";
        public const string EnvironmentValue = "value";
        public const string NulEnvironmentName = EnvironmentName + "\0";
        public const string NulEnvironmentValue = EnvironmentValue + "\0";
        public const string EnvironmentRefusalMessage = "OpenCodeServerEnsureOptions.Environment keys cannot be blank, values cannot be null, and neither can contain NUL.";
    }

    [Test]
    [Arguments(CommandNul.Executable)]
    [Arguments(CommandNul.LeadingArgument)]
    public async Task Snapshot_Should_Refuse_A_Nul_In_A_Command_Entry(CommandNul entry)
    {
        var options = new OpenCodeServerEnsureOptions
        {
            Command = entry switch
            {
                CommandNul.Executable => [EnsureRequestData.NulExecutable, EnsureRequestData.LeadingArgument],
                CommandNul.LeadingArgument => [EnsureRequestData.Executable, EnsureRequestData.NulLeadingArgument],
                _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, null),
            },
        };

        await RefusedAsync(options, EnsureRequestData.RefusalMessage);
    }

    [Test]
    public async Task Snapshot_Should_Refuse_A_Null_Command_Entry_As_Blank()
    {
        var options = new OpenCodeServerEnsureOptions
        {
            Command = [EnsureRequestData.Executable, null!],
        };

        await RefusedAsync(options, EnsureRequestData.BlankRefusalMessage);
    }

    [Test]
    [Arguments(EnvironmentNul.Name)]
    [Arguments(EnvironmentNul.Value)]
    public async Task Snapshot_Should_Refuse_A_Nul_In_An_Environment_Entry(EnvironmentNul entry)
    {
        var (name, value) = entry switch
        {
            EnvironmentNul.Name => (EnsureRequestData.NulEnvironmentName, EnsureRequestData.EnvironmentValue),
            EnvironmentNul.Value => (EnsureRequestData.EnvironmentName, EnsureRequestData.NulEnvironmentValue),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, null),
        };
        var options = new OpenCodeServerEnsureOptions
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value },
        };

        await RefusedAsync(options, EnsureRequestData.EnvironmentRefusalMessage);
    }

    private static async Task RefusedAsync(OpenCodeServerEnsureOptions options, string message)
    {
        var exception = await Assert.That(() => EnsureRequest.Snapshot(options)).Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo(EnsureRequestData.PublicParameter);
        await Assert.That(exception.Message).IsEqualTo(new ArgumentException(message, exception.ParamName).Message);
    }

    /// <summary>The environment entries that native marshalling would cut short.</summary>
    public enum EnvironmentNul
    {
        /// <summary>An environment name containing a NUL.</summary>
        Name,

        /// <summary>An environment value containing a NUL.</summary>
        Value,
    }

    /// <summary>The command entries that native marshalling would cut short.</summary>
    public enum CommandNul
    {
        /// <summary>An executable containing a NUL.</summary>
        Executable,

        /// <summary>A leading argument containing a NUL.</summary>
        LeadingArgument,
    }
}
