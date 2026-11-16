#!/usr/bin/env -S dotnet --
#:project ./OpenCode.Sdk.Tools/OpenCode.Sdk.Tools.csproj
#:property TargetFramework=$(LatestTargetFramework)

return await OpenCode.Sdk.Tools.ToolApp.RunUntilInterruptedAsync(args).ConfigureAwait(false);
