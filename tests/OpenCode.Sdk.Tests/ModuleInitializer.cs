using System.Runtime.CompilerServices;

namespace OpenCode.Sdk.Tests;

internal static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        UseProjectRelativeDirectory("Snapshots");
    }
}
