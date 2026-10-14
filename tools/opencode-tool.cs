#!/usr/bin/env -S dotnet --
#:project ./OpenCode.Sdk.Tools/OpenCode.Sdk.Tools.csproj
#:property TargetFramework=$(DefaultTargetFramework)

return await OpenCode.Sdk.Tools.ToolApp.RunAsync(args).ConfigureAwait(false);
