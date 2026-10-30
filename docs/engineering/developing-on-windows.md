# Developing on Windows

Date: 2026-10-16

Operational guidance for a Windows workstation. Everything here is about the developer's machine;
the test suite's own isolation rules live in `testing-style.md` and the gates in `quality-gates.md`.
A fresh worktree is prepared as `quality-gates.md` describes before its first gate; a workstation
keeps one Bun, and when that is not the pinned one the gate runs with the Bun mismatch switch that
`quality-gates.md` describes.

## Native AOT publishing

The gate's `NativeAot` pass publishes a consumer with ILCompiler, which links with the platform
linker: a workstation needs the Visual Studio Build Tools workload "Desktop development with C++"
(MSVC and a Windows SDK). Without it the publish fails with "Platform linker not found".

With the Visual Studio 2026 Build Tools, ILCompiler finds the linker through `VsDevCmd.bat`, which
calls `vswhere.exe` by its bare name from the Visual Studio Installer folder. The packed-consumer
fixture puts that folder on its publish's `PATH` itself. A native AOT publish run by hand, from a
shell that sets `NoDefaultCurrentDirectoryInExePath` and has no Installer folder on `PATH`, fails
with "'vswhere.exe' is not recognized" inside the linker command and then MSB3073: put
`C:\Program Files (x86)\Microsoft Visual Studio\Installer` on that process's `PATH` rather than
clearing the variable.

## Live legs against a Linux-hosted server

The `opencode-pty` daemon (`@opencode-ai/pty`) ships a win32 package too, so a persistent PTY live
run directly on Windows drives the round trip. To prove the live leg against a Linux server from a
Windows workstation, `PinnedOpenCodeServerFixture`'s external-endpoint mode plus
`PersistentPtyDaemonGate`'s override run it against a server hosted in WSL2.

Clone into the WSL filesystem instead of reusing the Windows checkout over `/mnt/<drive>`: one
shared `external/opencode/node_modules` holds the platform package of whichever OS installed last,
so the two installs clobber each other's daemon binary.

1. In WSL2, clone the repository and initialize the submodule:
   ```sh
   git clone https://github.com/opencode-dotnet/opencode-sdk-dotnet.git ~/repos/opencode-sdk-dotnet
   cd ~/repos/opencode-sdk-dotnet
   git submodule update --init --depth 1 external/opencode
   ```
2. Install the bun the pin names in its `packageManager` field, not the workstation's own:
   ```sh
   curl -fsSL https://bun.sh/install | bash -s "bun-v$(grep -o '"packageManager": *"bun@[0-9.]*"' \
     external/opencode/package.json | grep -o '[0-9][0-9.]*')"
   ```
3. Install the dependencies — this is what places `@opencode-ai/pty-<platform>/bin/opencode-pty`,
   which `packages/core` resolves as its `binaryPath`:
   ```sh
   cd ~/repos/opencode-sdk-dotnet/external/opencode
   bun install --frozen-lockfile --ignore-scripts
   ```
4. Serve from `packages/cli` under isolated XDG roots and a fixed password, so the run touches no
   real profile and the Windows side can authenticate:
   ```sh
   cd ~/repos/opencode-sdk-dotnet/external/opencode/packages/cli
   mkdir -p /tmp/ocsdk/data /tmp/ocsdk/cache /tmp/ocsdk/config /tmp/ocsdk/state
   XDG_DATA_HOME=/tmp/ocsdk/data XDG_CACHE_HOME=/tmp/ocsdk/cache \
   XDG_CONFIG_HOME=/tmp/ocsdk/config XDG_STATE_HOME=/tmp/ocsdk/state \
   OPENCODE_CONFIG_CONTENT='{}' OPENCODE_DISABLE_MODELS_FETCH=1 OPENCODE_PASSWORD=<pw> \
     bun src/index.ts serve --port 4097
   ```
5. On Windows, point the fixture at it and run the persistent PTY legs:
   ```powershell
   $env:OPENCODE_SDK_TESTS_ENDPOINT = "http://localhost:4097"
   $env:OPENCODE_SDK_TESTS_PASSWORD = "<pw>"
   $env:OPENCODE_SDK_TESTS_PTY_DAEMON = "1"
   dotnet test --project "$PWD/tests/OpenCode.Sdk.Tests/OpenCode.Sdk.Tests.csproj" --configuration Release --no-build `
     -- --treenode-filter "/*/*/PersistentPtyLiveTests/*" --report-trx
   ```

The filter keeps the run to the live legs, which is what the recipe was proven with; the whole suite
can also run against the same endpoint, because the fixture-driven live legs create their sessions
without a Windows location, so no path from the workstation reaches the WSL2 server. The
blank-password guard is real: an unset or whitespace `OPENCODE_SDK_TESTS_PASSWORD` fails
initialization by name rather than reaching the server.

The exact-pin discipline is the operator's: the WSL2 server must be built from the same submodule
commit; the fixture prints both so a mismatch is visible, it cannot verify a source run's version.

The sandbox (`tests/OpenCode.Sdk.Sandbox`) can be pointed at the same endpoint with `--endpoint`,
but it is not the proof: its earlier session legs answer 500 on a provider-less isolated server, so the walkthrough
never reaches the persistent PTY leg there. `PersistentPtyLiveTests` is what proves the round trip.

## Driving WSL from a Windows shell

Put the WSL side in a script file and run it as `MSYS_NO_PATHCONV=1 wsl.exe -- bash
/mnt/c/<path>.sh` — Git Bash otherwise mangles the quoting and rewrites `/tmp` paths into Windows
ones. Stop the server by pattern (`pkill -f "index.ts serve --port 4097"`) rather than by a pid
file, because a backgrounded `setsid nohup … &` leaves the file empty; the `opencode-pty` daemon
exits with the server.
