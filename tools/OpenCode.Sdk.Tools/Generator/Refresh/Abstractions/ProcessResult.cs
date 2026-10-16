using System.Text;

namespace OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;

/// <summary>Captures one completed external process run.</summary>
internal sealed record ProcessResult
{
    /// <summary>Gets the process exit code.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Gets the raw standard-output bytes, byte-faithful for artifact reads such as <c>git show</c>.</summary>
    public required byte[] StandardOutput
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets the standard-error text.</summary>
    public required string StandardError
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets the standard output decoded as UTF-8 text.</summary>
    public string StandardOutputText => Encoding.UTF8.GetString(StandardOutput);
}
