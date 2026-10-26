# 🔌 Connection modes

Date: 2026-10-26

There are three ways to get a client bound to a running opencode server: **let the SDK start one**,
**point it at one you already have**, or **discover the background service the opencode CLI runs
for every client on the machine**. Dependency injection is not a fourth way in — it is how you
register any of them with a container.

- [🚀 The SDK starts the server](#-the-sdk-starts-the-server)
- [🔗 A server you already run](#-a-server-you-already-run)
- [🛰️ Discovering the background service](#️-discovering-the-background-service)
  - [🛑 Stopping the background service](#-stopping-the-background-service)
  - [🟢 Ensuring the background service](#-ensuring-the-background-service)
- [🧩 Registering with dependency injection](#-registering-with-dependency-injection)

## 🚀 The SDK starts the server

`OpenCodeServer.StartAsync()` launches a private `opencode serve` child, waits for it to report
readiness, mints its credential, and hands you an owner object. No ambient process, no endpoint to
configure, no port to pick.

```csharp
await using var server = await OpenCodeServer.StartAsync();
using var client = server.CreateClient();

Console.WriteLine($"started {server.Endpoint} (pid {server.ProcessId})");

var info = await client.Server.GetInfoAsync();

Console.WriteLine($"opencode {info.ServerInfo.Version} (pid {info.ServerInfo.Pid})");
```

The signature is
`OpenCodeServer.StartAsync(OpenCodeServerOptions? options = null, CancellationToken cancellationToken = default)`.

**Every start is a fresh private server on port zero.** It never discovers, attaches to, or shuts
down a server somebody else is running, so coexisting with your own dev server is safe by
construction. The returned `OpenCodeServer` is the only owner of that child, and it tells you what
it started:

| Member | Meaning |
|---|---|
| `Endpoint` | The `http://127.0.0.1:{port}` address the child actually bound |
| `Password` / `Username` | The generated lease credential this server accepts |
| `ProcessId` | The PID of the child it owns — the `cmd.exe` host when the command resolved to a Windows batch shim, see [below](#how-the-command-is-resolved) |
| `Exited` | A task that completes with an `OpenCodeServerExit` once the server has ended, on its own or through disposal |

`Exited` lets you notice a server that dies under you. It is a task, not an event, and it never
faults: a signal that ends the server is a value, not an exception. `Kind` says which members are
set. `ExitCode` is the code; on Windows it is the full 32-bit code, as `Process.ExitCode` gives it.
`Signal` is the signal's name, such as `SIGKILL`, and `SignalNumber` is its number on the host.
`Unknown` means the exit status could not be read, for example because something other than the
launcher reaped the server.

```csharp
var exit = await server.Exited;
if (exit.Kind == OpenCodeServerExitKind.Signaled)
{
    Console.WriteLine($"opencode was ended by {exit.Signal ?? "signal " + exit.SignalNumber}");
}
```

The server handles `SIGTERM` and `SIGINT` itself and normally exits with code **130** (a failed
finalizer changes the code). Disposal on Linux and macOS therefore reports `Exited` with code 130,
not `Signaled`, on a normal shutdown. `Signaled` appears only for a signal the server does not
handle, such as the `SIGKILL` disposal sends when the server outlives `GracefulShutdownTimeout`.
On Windows, disposal reports `Exited` with code 1.

It completes once the exit is observed and the server's output has ended, or one second after the
exit when a process the server started still holds the output open. A handle from `DiscoverAsync`
or `EnsureAsync` owns no process, and its `Exited` throws `InvalidOperationException`.

### Shaping the launch

```csharp
await using var server = await OpenCodeServer.StartAsync(new OpenCodeServerOptions
{
    Command = ["/opt/opencode/bin/opencode", "serve"],
    WorkingDirectory = "/srv/my-project",
    Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["OPENCODE_LOG_LEVEL"] = "debug" },
    ReadinessTimeout = TimeSpan.FromSeconds(90),
    GracefulShutdownTimeout = TimeSpan.FromSeconds(5),
});
```

| Option | Default | What it does |
|---|---|---|
| `Command` | `["opencode", "serve"]` | The executable plus its leading arguments. The launcher resolves `Command[0]` the way a shell would — see [How the command is resolved](#how-the-command-is-resolved) — and appends `--stdio --port 0` itself. |
| `WorkingDirectory` | `null` | The child's working directory; `null` inherits yours. |
| `Environment` | `null` | Extra environment entries for the child. |
| `ReadinessTimeout` | 60 s | How long to wait for the readiness line before failing and ending the child: above zero and at most `int.MaxValue` milliseconds (about 24.8 days), or `Timeout.InfiniteTimeSpan` to wait until your cancellation token fires. |
| `GracefulShutdownTimeout` | 3 s | The grace before disposal forces the end again. On Windows it starts with the first `taskkill /T /F` of the server's tree and bounds it; a server still running when it runs out is killed again. On Linux and macOS it runs from `SIGTERM` to the server's process group to `SIGKILL` to whatever is left of the group. Zero to at most `int.MaxValue` milliseconds. |
| `Output` | `null` | An `OpenCodeServerOutput` collector that keeps a bounded tail of the child's stdout and stderr; read its snapshot whenever you like, even after a failed start. |

> **🔒 Your `Environment` entries can never shadow the credential.** The launcher writes its own
> generated `OPENCODE_PASSWORD` entry *after* yours, so a stray value in your dictionary cannot
> take over the child's authentication.

### How the command is resolved

`Command[0]` is resolved **before** anything is spawned, following the same rules a shell does.
This is not a detail you normally think about — until you install the CLI with npm on Windows,
where npm writes shim files (`opencode`, `opencode.cmd`, `opencode.ps1`) and keeps the real
binary inside `node_modules`. No `opencode.exe` is on `PATH` there, only the shims; a raw
`Process.Start` only ever appends `.exe`. Resolving first is what makes the shipped default work there.

The `@opencode/cli` npm package keeps its executable at `<package>/bin/opencode.exe` on every OS,
including the `.exe` name on Linux and macOS. With a global install, the package is at
`<prefix>/lib/node_modules/@opencode/cli` on Linux and macOS and
`<prefix>\node_modules\@opencode\cli` on Windows, where `<prefix>` is what `npm prefix -g` prints.
See [npm's folders page](https://docs.npmjs.com/cli/v11/configuring-npm/folders) for that layout.

The rules, in full:

- A command containing a directory separator, or a rooted path, is used exactly as written. No
  search happens, so pointing `Command` at an absolute path always wins.
- A bare name is looked up through the `PATH` directories in order. Empty entries are skipped, and
  a relative entry is resolved against the process's current directory.
- **On Windows**, a bare name with no extension is tried with each `PATHEXT` extension, in `PATHEXT`
  order (falling back to `.COM;.EXE;.BAT;.CMD` when `PATHEXT` is unset). A name that already carries
  an extension — `opencode.cmd` — is tried exactly as written. So an npm `.cmd` shim is found and
  started.
- **On Unix**, the `PATH` directories are searched for the name itself; the operating system still
  decides at spawn time whether the file is executable.

When a bare name matches nothing, the start fails with `OpenCodeServerException` before any process
exists, naming how many directories were searched and which extensions were tried — see
[errors and responses](errors-and-responses.md#-when-a-local-server-door-fails).

> **⚙️ Batch shims run through `cmd.exe`.** When resolution lands on a `.cmd` or `.bat` file, the
> launcher starts the system `cmd.exe` explicitly (`/d /s /c`) with the script and every argument
> quoted, rather than relying on Windows' implicit batch handling. Two consequences are worth
> knowing:
>
> - **`ProcessId` is then the `cmd.exe` host**, not the server itself — the shim's own child. The
>   stdin ownership lease and the stdout readiness line pass straight through it, and disposal's
>   whole-tree kill covers the server underneath, so disposal is unchanged. If your process dies
>   without disposing, the job ends only `cmd.exe`: the real server ends when it sees its stdin
>   lease close, after its own graceful shutdown, which upstream does not bound. It can outlive your
>   process for some seconds; upstream's own test expects exit within 5 s on an idle server.
>   Name the full path of the package's `opencode.exe` in `Command` to put the server itself in the
>   job, so it ends with your process; if Windows refuses that job assignment, it too waits for its
>   stdin lease. Ask the server for its own pid
>   (`info.ServerInfo.Pid`) if you need that one.
> - **Arguments carrying `cmd` metacharacters are refused**, not escaped. `cmd.exe` re-parses the
>   line it is handed, so any leading argument of yours containing `&`, `|`, `<`, `>`, `^`, `%`,
>   `!`, `"`, a carriage return, or a line feed fails the start with `OpenCodeServerException`
>   before anything runs. This is the same fail-closed stance Rust and Node took for BatBadBut
>   (CVE-2024-24576). If you need such an argument, point `Command` at the real executable instead
>   of the shim.

### What `StartAsync` does not isolate

A started server is a **fresh process on a fresh port** — it is not a sandbox. Unless you say
otherwise, it reads and writes the same user data, state, cache, and config roots as any other
opencode process on the machine, including the one your editor is running. Sessions, credentials,
and configuration are shared, on Windows as much as on Linux and macOS.

Redirect those roots through `Environment` when you want a private one:

```csharp
var root = Path.Combine(Path.GetTempPath(), "my-app", Guid.NewGuid().ToString("N"));

await using var server = await OpenCodeServer.StartAsync(new OpenCodeServerOptions
{
    Environment = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
        ["XDG_STATE_HOME"] = Path.Combine(root, "state"),
        ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
        ["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
    },
});
```

Stored provider credentials live in the same database as sessions, under the server's data root.
**A separate data root is a separate database**: redirecting `XDG_DATA_HOME`, or selecting a fresh
database with `OPENCODE_DB`, gives the server none of your existing sessions and no stored login.
An absolute `OPENCODE_DB` still pointing at the shared database defeats data-root isolation.
On its first start, a fresh database imports legacy credentials from `<data>/opencode/auth.json`
if that file exists. Provider keys inherited through the environment still work.

Even after redirecting all four roots, a private launch still inherits:

- `OPENCODE_CONFIG` and `OPENCODE_CONFIG_CONTENT`, if set; `OPENCODE_CONFIG_DIR` also overrides
  the redirected global config directory.
- The system config directory, which is always read and is not moved by XDG redirection.
- The project config walk from the location directory upward to the filesystem root:
  `opencode.json(c)` and `.opencode/`, unless project discovery is disabled.
- Project/ancestor `AGENTS.md` within the instruction walk's home/project boundary, and configured
  instruction sources. Access-denied instruction files are skipped; this is not the config walk.
- Home-based `~/.claude/skills` and `~/.agents/skills`, which follow `HOME` (`USERPROFILE` on
  Windows), not XDG. Scanning skips hidden entries and dependency/cache directories, descends at
  most six levels, and stops descending a directory once it finds `SKILL.md`.
- Enabled MCP servers from every loaded config. They connect asynchronously when a location first
  initializes; startup skips entries already settled by a concurrent connect/disconnect.

The default location is the server's working directory, so `WorkingDirectory` matters. Redirecting
the roots is state separation, not a guarantee of configuration or tool isolation.

### Configuration updates across locations

`client.Experimental.UpdateConfigAsync` writes the global config and schedules a reload only of
the calling location, after a short debounce. It returns before that reload runs. Other loaded
locations reload only through their file watchers, which can be disabled or unavailable.
To make every loaded location see the change, call `client.ReloadLocationsAsync()`: it rebuilds
them, cancelling pending permissions and forms; running sessions continue with fresh services at
the next step boundary.

### Clients from a started server

`CreateClient(Action<OpenCodeClientOptions>? configure = null)` builds a client already pinned to
that server's endpoint and lease credential. The delegate is for **behaviour only** — setting
`Endpoint`, `Username`, or `Password` inside it is refused with `InvalidOperationException`,
because a started server's identity is not yours to reassign:

```csharp
using var client = server.CreateClient(options => options.Location = new LocationSelector
{
    Directory = "/srv/my-project",
});
```

Each call builds a new client over its own transport, so dispose each one. Disposing the *server*
ends the child the way upstream's own launcher does: on Windows it ends the server's whole process
tree at once with `taskkill /T /F`; on Linux and macOS it sends `SIGTERM` to the server's process
group and `SIGKILL` to whatever is left when `GracefulShutdownTimeout` runs out. Every step is
bounded, so disposal never hangs your shutdown, and stdin, the ownership lease, closes last. If your
process dies before disposal runs, the server still ends: on Windows the job the launcher placed it
in ends it with your process (a batch shim's server, which runs outside that job, ends through the
lease), and on Linux and macOS the operating system closes the lease and the server exits.

To delete a temp directory the server used while it still runs, evict its location first with
`Debug.EvictLocationAsync` (see [location eviction](errors-and-responses.md#when-a-worktree-remove-is-refused)),
or dispose the server first. On Windows, retry the delete a few times: Windows can release a file
a moment after the process that held it exits. Upstream's own test helper retries deletes for this
reason.

Startup failures throw `OpenCodeServerException`, carrying a bounded tail of the child's stderr
whenever a child actually ran — see
[errors and responses](errors-and-responses.md#-when-a-local-server-door-fails).

A caller token already canceled at the pre-launch check prevents process creation. A `StartAsync`
that throws `OperationCanceledException` ends the child it started, if any, before throwing the
exception with that token. Cancellation during teardown does not replace an earlier failure. A
cancellation that arrives after readiness returns a running server, which you dispose.
See the [launcher contract](../architecture/client-runtime.md#launcher) for readiness arbitration
and bounded cleanup.

## 🔗 A server you already run

If you already know an endpoint, construct the client directly. There is no separate verb for this
door: the endpoint and the credential *are* the connection.

```csharp
var endpoint = new Uri("http://127.0.0.1:4096");

using var client = new OpenCodeClient(new OpenCodeClientOptions
{
    Endpoint = endpoint,
    Password = Environment.GetEnvironmentVariable("OPENCODE_PASSWORD"),
});

using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var info = await client.Server.GetInfoAsync(cancellationToken: probe.Token);

Console.WriteLine($"opencode {info.ServerInfo.Version} (pid {info.ServerInfo.Pid})");
```

That bounded info call is the whole validation recipe for *reaching* the server, and it is
deliberately yours to write: the SDK carries no version comparand of its own, and its `NetworkTimeout`
bounds a call that stops making progress (no response headers, or no new body bytes), not the whole
call, so a `CancellationTokenSource` is the honest deadline and your own expectation is the honest
version check. It is liveness only — a server answering status can still have an empty model catalog
for a second or two while its plugins activate, which
[choosing a model](getting-started.md#choosing-a-model) covers.

**About `OPENCODE_PASSWORD`**: that is the variable *the opencode CLI* reads to decide which
password its server will accept —

```sh
OPENCODE_PASSWORD=your-password opencode serve --hostname 127.0.0.1 --port 4096
```

— and the client must present the same value as its Basic password. `OPENCODE_SERVER_PASSWORD` is
the CLI's legacy name for the same value, still honored as a fallback, so an older setup keeps
working. The client never reads either one, or any other environment variable, for you. Reading it
in the snippet above is your application's choice; a configuration section or a secret store works
exactly as well. The one door that does read the environment is
[background-service discovery](#️-discovering-the-background-service), and it reads exactly the four
variables that section names — never a credential.

### An app-owned server on a fixed endpoint

For a restartable endpoint, start `opencode serve --hostname 127.0.0.1 --port <N>` yourself, with
`OPENCODE_PASSWORD` in its environment. Connect to that endpoint with username `opencode` and the
same password. A fixed port in use fails the start, so make sure the previous instance has exited.
Without `--port`, the server takes the first free port from 4096 upward.

For readiness, wait for the stdout line `server listening on http://127.0.0.1:<N>`, or retry
`Server.GetInfoAsync` until it succeeds. Retry on any failure until a deadline, without depending
on which exception a boot-window failure produces. With `port`, `password`, `readinessTimeout`
and `retryInterval` supplied by your application:

```csharp
using var client = new OpenCodeClient(new OpenCodeClientOptions
{
    Endpoint = new Uri($"http://127.0.0.1:{port}"),
    Username = "opencode",
    Password = password,
});

using var deadline = new CancellationTokenSource(readinessTimeout);
while (true)
{
    try
    {
        var info = await client.Server.GetInfoAsync(cancellationToken: deadline.Token).ConfigureAwait(false);
        Console.WriteLine($"opencode {info.ServerInfo.Version} (pid {info.ServerInfo.Pid})");
        break;
    }
    catch (Exception failure) when (!deadline.IsCancellationRequested)
    {
        await Console.Error.WriteLineAsync($"Waiting for server: {failure.Message}").ConfigureAwait(false);
        await Task.Delay(retryInterval, deadline.Token).ConfigureAwait(false);
    }
}
```

Add `--stdio` when the server must end with your process: it prints `{"url":"..."}` instead and
exits when its stdin closes, so keep the stdin pipe open for its lifetime. Without `--stdio`, it
outlives your process.

### 🤝 Pairing another client

A client that holds the server password can let another client in without handing the password
over. `CreatePairingCodeAsync` issues a short-lived, single-use code; the other side redeems it
with no credential at all and gets a session token the server accepts anywhere the password is.

```csharp
// On the side that holds the password:
var issued = await client.Server.CreatePairingCodeAsync();
Console.WriteLine($"code valid for {issued.PairingCode.ExpiresIn} s"); // send issued.PairingCode.Code over your own channel

// On the other side, which holds nothing yet:
using var anonymous = new OpenCodeClient(new OpenCodeClientOptions { Endpoint = endpoint });
var session = await anonymous.Server.RedeemPairingCodeAsync(code);

using var paired = new OpenCodeClient(new OpenCodeClientOptions
{
    Endpoint = endpoint,
    Password = session.PairingSession.Token,
});
```

- **The code works once.** Redeeming it again, or after it expires, answers 401 with the typed
  `UnauthorizedError`.
- **The token is a credential.** The server signs it with a key derived from its password, so it
  lasts 30 days and ends when the password changes. Treat it like the password.
  `PairingSession.ToString()` and `PairingCode.ToString()` mask both values.

## 🛰️ Discovering the background service

The opencode CLI runs one background service per user and publishes where it listens in a
registration file: the URL, the daemon's pid, its version, and the Basic password. Every opencode
client on the machine — the CLI, the desktop app, now this SDK — finds the same daemon through that
file. `OpenCodeServer.DiscoverAsync` is that lookup, and nothing more: it starts nothing, owns
nothing, and stops nothing.

```csharp
var server = await OpenCodeServer.DiscoverAsync();
if (server is null)
{
    // No ready registered service: start a private one with StartAsync, or run `opencode` once.
    return;
}

using var client = server.CreateClient();
var info = await client.Server.GetInfoAsync();
Console.WriteLine($"discovered opencode {info.ServerInfo.Version} (pid {server.ProcessId})");
// server.DisposeAsync() is a no-op here: OwnsProcess is false, and the service stays up for
// everyone else.
```

The answer is the daemon's identity or **null** — never a half-usable handle. Null covers every
ordinary way a service can be absent or unusable: no registration file, one that does not decode,
one without a password, a daemon that is still starting or has failed, an info probe that did not
answer within its two-second bound, or a version other than the one you asked for. You decide what
null means for your application; discovery does not start a daemon on your behalf —
[`EnsureAsync`](#-ensuring-the-background-service) does, when you ask it to.

### What discovery reads

Discovery follows the CLI's own rules for locating the registration, which makes it the one door
in the SDK that reads the environment. It reads exactly four variables, all of them paths:

| Variable | Role |
|---|---|
| `XDG_STATE_HOME` | the state root the registration file lives under (`<state>/opencode/…`) |
| `XDG_CONFIG_HOME` | the config root the CLI's service configuration lives under |
| `OPENCODE_CONFIG_DIR` | replaces the whole config root when set, with no `opencode` segment beneath it |
| the user profile | `USERPROFILE` first on Windows, `HOME` elsewhere; the fallback root when an XDG variable is unset |

When you isolate a background service, give `XDG_STATE_HOME` and `XDG_CONFIG_HOME` different
directories. The registration (`<state>/opencode/service.json`) and the CLI's service configuration
(`<config>/opencode/service.json`) share a file name, so one folder for both roots makes them the
same file. `EnsureAsync` then fails now and then on Windows, because the CLI's atomic rename of its
new registration is refused with `EPERM`.

No credential is ever read from the environment: the password comes from the registration file the
daemon wrote, which is why the file is created readable by its owner only.

### An isolated background service

Use a released CLI and four separate roots. This recipe assumes `OPENCODE_DB` and
`OPENCODE_CONFIG_DIR` are unset. Choose a free fixed port distinct from the user's service;
the released CLI's default is 49374, or 28894 on Windows. The example pins 49375 through
`<config>/opencode/service.json`, not through `Command` arguments.

Set `RegistrationFilePath` to `<state>/opencode/service.json` on **every Ensure, Discover and Stop
call**. The SDK finds the registration from its own process environment, not the options'
`Environment`. Without the explicit path, Ensure can reuse the user's shared service or wait for
a registration in the wrong place. **A parameterless `StopAsync()` ends the user's shared service.**

```csharp
using OpenCode.Sdk;

var root = Path.Combine(Path.GetTempPath(), "my-service", Guid.NewGuid().ToString("N"));
var state = Path.Combine(root, "state");
var config = Path.Combine(root, "config");
var registration = Path.Combine(state, "opencode", "service.json");
var serviceConfig = Path.Combine(config, "opencode");
Directory.CreateDirectory(serviceConfig);
await File.WriteAllTextAsync(Path.Combine(serviceConfig, "service.json"), """{"port": 49375}""").ConfigureAwait(false);

try
{
    var server = await OpenCodeServer.EnsureAsync(new OpenCodeServerEnsureOptions
    {
        RegistrationFilePath = registration,
        Environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
            ["XDG_STATE_HOME"] = state,
            ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
            ["XDG_CONFIG_HOME"] = config,
        },
    }).ConfigureAwait(false);
    var discovered = await OpenCodeServer.DiscoverAsync(new OpenCodeServerDiscoverOptions
    {
        RegistrationFilePath = registration,
    }).ConfigureAwait(false);
    Console.WriteLine($"ensured {server.Endpoint}; discovered {discovered?.Endpoint}");
    // Use server.CreateClient() here. Both handles are non-owning; disposal does not stop the service.
}
finally
{
    await OpenCodeServer.StopAsync(new OpenCodeServerStopOptions
    {
        RegistrationFilePath = registration,
    }).ConfigureAwait(false);
}
```

The [inheritance limits above](#what-startasync-does-not-isolate) still apply. Service mode only
tries to change to the home directory; if that fails, it logs a warning and keeps its launch
working directory. That resulting directory is the default location for requests without one.

### Shaping the lookup

`OpenCodeServerDiscoverOptions` has four optional members. Leave the whole thing out to read the
shared release registration.

| Member | Meaning |
|---|---|
| `Channel` | The CLI's service channel. Null reads the shared release registration (`service.json`), which the `latest`, `dev`, `beta`, and `next` builds all publish to. `local` and any other name read `service-<channel>.json`; for a channel that ever had the CLI's earlier hashed filename, discovery first performs the same one-time copy the CLI performs. |
| `RegistrationFilePath` | An absolute path to read directly. It bypasses the channel rules and the environment entirely — the test double's door, and the door for a daemon whose file you already know. Cannot be combined with `Channel` or `InstalledVersion`. |
| `ExpectedVersion` | Accept only a daemon whose health answer reports exactly this version; null accepts any ready daemon. |
| `InstalledVersion` | The version the channel migration compares a legacy registration against, the way the CLI compares its own compiled version. Defaults to `ExpectedVersion`; when both are set they must be equal. |

Every member is validated at the call: a blank string, a relative path, or a contradictory pair
throws `ArgumentException` before anything is read.

### How the daemon is checked

A decoded registration is probed with `GET /api/info` under Basic authentication using the
registered password, bounded at two seconds. A 2xx answer whose pid matches the registration is a
ready service; a 404 identifies a registered daemon that speaks another protocol version, a 500
is a daemon that failed to boot, and anything else is a daemon still starting — and only the
first becomes a handle. The probe follows no redirects and sends the credential to the
registered origin only. It never goes through a proxy for a loopback address, so `HTTP_PROXY`
without `NO_PROXY` does not hide a running service from discovery, and a registration whose
daemon is gone answers null in milliseconds on every operating system, including Windows.

### What the handle is

A discovered `OpenCodeServer` carries the same `Endpoint`, `Username`, `Password`, and `ProcessId`
a started one does, and `CreateClient()` binds a client to it the same way. The difference is
ownership, and it is visible: **`OwnsProcess` is false**, `DisposeAsync` ends nothing, and the pid
is the daemon's own rather than a child of yours. Do not kill that pid: it is shared with every
other opencode client on the machine.

### What can fail

Three things throw; everything else is null.

- `ArgumentException` — the options were blank or contradictory (see above).
- `OpenCodeServerException` — an XDG variable was unset and no user home resolved either, so the
  registration roots cannot be located at all. This is the same failure plane as the launcher's;
  see [when a local-server door fails](errors-and-responses.md#-when-a-local-server-door-fails).
- `OperationCanceledException` — your own token was cancelled. The internal two-second probe bound
  never surfaces as cancellation; it is a null.

> **🔑 `opencode serve` always has a password.** Setting neither variable does not start an open
> server — it makes the CLI generate one and print it as `server password <pw>` on startup, and no
> serve flag disables authentication. A client for a CLI-started server therefore always needs
> `Password`. Leaving it `null` is right only for a host that genuinely runs without one: a server
> embedded through the opencode server library, as this repository's own simulation host does for
> its tests. Point a passwordless client at `opencode serve` and every call answers **401 with the
> typed `UnauthorizedError`** (`Authentication required`), the same answer a wrong password gets.
> The SDK adds a sentence to the exception message when no password was configured; see
> [a 401 with no credential](errors-and-responses.md#a-401-with-no-credential).

### 🛑 Stopping the background service

`OpenCodeServer.StopAsync` is `opencode service stop` for your code: it ends the daemon the
registration names and removes the registration. Nothing else in the SDK ever stops the shared
service — disposing a discovered handle is a no-op by contract — so this is the one call to make
deliberately, for the reasons the CLI has the command: replacing a version, shutting the service
down before an uninstall, or ending a daemon that no longer answers.

```csharp
await OpenCodeServer.StopAsync();
```

What it does, in order:

1. Resolves the registration exactly as discovery does — the shared release registration by
   default, a named `Channel`, or a `RegistrationFilePath` — including the CLI's legacy migration.
2. Asks a ready, compatible daemon to shut down its persistent terminals
   (`PersistentPtys.ShutdownAsync`); a daemon that refuses or does not answer is not an error.
3. Removes the handoff sidecar the CLI keeps beside the registration.
4. Ends the registered process: a request to stop first (`SIGTERM`), a hard kill (`SIGKILL`) if it
   is still there about five seconds later, then the same wait again. On Windows both rungs are a
   hard kill, because another process cannot be signalled there.
5. Removes the registration once the process is gone.

Before the first signal and before the removal it re-reads the registration and checks that it
still names the same service (`id`, `version`, `url`, `pid`), so a service that re-registered under
the file before the stop began is never signalled and a successor's registration is never removed.
Once the first signal is sent, the hard kill follows that process alone, even when its registration
disappears or changes hands meanwhile. It identifies the process by pid *and* start time — before
every signal and at every look while it waits — so a pid the operating system handed to an
unrelated process is never signalled.

`OpenCodeServerStopOptions` has three optional members; leave the whole thing out to stop the
shared release registration.

| Member | Meaning |
|---|---|
| `Channel` | The CLI's service channel, as for discovery. |
| `RegistrationFilePath` | An absolute path to stop directly, as for discovery. Cannot be combined with `Channel` or `InstalledVersion`. |
| `InstalledVersion` | The migration comparand, as for discovery. A stop never filters by version. |

The call completes successfully when there is nothing to stop: no registration, or one that does
not decode. It throws `ArgumentException` for blank or contradictory options;
`OpenCodeServerException` when the registration roots cannot be located, or when the process is
still running after the hard kill — the registration is then left in place; and
`OperationCanceledException` for your own token, where cancelling before a signal prevents it and
cancelling after one ends the wait without undoing the signal.

On Windows, the service's files can stay locked for a few tens of milliseconds after `StopAsync`
returns. The call completes once the process has exited, the same moment `opencode service stop`
waits for, but Windows releases the daemon's database files (`opencode.db-shm` and `-wal`) a moment
later; runs measured the gap between 12 and 38 ms. Deleting an isolated data directory right after
the stop can therefore fail with an `IOException` naming one of those files. Code that removes the
directory should be ready to retry the deletion for a short bound of its own.

### 🟢 Ensuring the background service

`OpenCodeServer.EnsureAsync` is what the CLI does before it connects: reuse the registered service
when one is ready, and start one when nothing usable is registered. It is the same election every
opencode client runs, so ten callers starting at once end up sharing one daemon.

```csharp
await using var server = await OpenCodeServer.EnsureAsync();

using var client = server.CreateClient();
var info = await client.Server.GetInfoAsync();
Console.WriteLine($"opencode {info.ServerInfo.Version} (pid {server.ProcessId})");
// Disposal is a no-op: the service is shared. StopAsync is the one way to end it.
```

What it does:

1. Reads the registration the options name, the way discovery does, and reuses a ready,
   compatible daemon.
2. When nothing usable is registered, spawns a detached `opencode serve --service` and waits for it
   to register. A contender that exits because another one won is released; at most two are live at
   a time, and the call gives up after 120 seconds.
3. When a registered daemon stops answering — three probe timeouts in a row — ends it the way
   `StopAsync` does and starts a replacement. Like `StopAsync`, this can end a service other
   clients share.
4. Hands a replaced daemon's persistent terminals over to its successor through the handoff
   sidecar, as the CLI does.

`OpenCodeServerEnsureOptions` is optional as a whole:

| Member | Meaning |
|---|---|
| `Channel`, `RegistrationFilePath`, `InstalledVersion` | Which registration to read, as for discovery. |
| `ExpectedVersion` | The version a reused service must report. Null accepts any ready, compatible service. |
| `VersionPolicy` | What a service at another version gets: `Ignore` (the default) reuses it, `Replace` ends it and starts one at the expected version, `Error` throws without touching it. `Replace` and `Error` need `ExpectedVersion`. |
| `Command` | The service command, `opencode serve --service` by default. The first entry is resolved the way `StartAsync` resolves it. |
| `Environment` | Extra variables for a spawned service, over the channel's service-config `env` at spawn; a 2.0.32 service re-applies its saved `env` over them once it starts, so a key set in both takes the saved value for everything read after startup: set a key in one place, the caller's `Environment` or the saved `env`, not both. |
| `OnStart` | Called at most once, before a new service process starts, with the reason (`Missing` or `VersionMismatch`) and the previous version. |

The spawned command registers where its own build and environment say. A released `opencode` build
registers the shared `service.json`; a source run registers the `local` channel. `Channel`,
`Command`, and `Environment` must describe the same registration, or the call waits out its bound
for a registration that appears somewhere else.

It throws `ArgumentException` for blank or contradictory options, an empty command, a NUL in a
command or environment entry, or `Replace` and `Error` without `ExpectedVersion`;
`OpenCodeServerException` when the roots cannot be located,
the command cannot be resolved, a contender or the registered service failed to start, the registered
service speaks an incompatible health protocol at a version the call accepts, `Error` met a service
at another version, or the 120-second bound expired (a contender's failure when one failed, and
otherwise the timeout with the last failed replacement as the inner exception, when there was one);
and `OperationCanceledException` for your own token.

## 🧩 Registering with dependency injection

`OpenCode.Sdk.Extensions` adds `AddOpenCode` to `IServiceCollection`. It registers **one
`OpenCodeClient` singleton** and **each of the 27 families as its own singleton**, each only when
the container does not already hold that service type. A consumer's earlier registration of the
root client or any sub-client wins; repeated calls add no duplicate client registrations. Existing
registrations keep their lifetimes and disposal ownership. The default root holds the transport
open for the container's lifetime, and default family registrations resolve from the registered
root, sharing its pipeline. A service can ask for the family it uses — `EventsClient`,
`PtysClient`, or `WorktreesClient` — directly.

There are two overloads, and the difference between them matters more than it looks:

| Overload | Options come from | Trimming / native AOT |
|---|---|---|
| `AddOpenCode(Action<OpenCodeClientOptions>)` | a delegate you write | ✅ safe — no reflection |
| `AddOpenCode(IConfiguration)` | a bound configuration section | ⚠️ annotated, see below |

Both go through the standard options pattern, so `IOptions<OpenCodeClientOptions>`, options
validation, and anything else layered on options behaves exactly as it does for any other library.

Each call still contributes its options configuration through the standard options pattern;
preserving a client registration does not suppress configuration callbacks or binding.

Binding from configuration keeps the endpoint out of your code entirely:

```csharp
var builder = Host.CreateApplicationBuilder(args);

// Binds Endpoint, Password, Username, and Location from the "OpenCode" section.
builder.Services.AddOpenCode(builder.Configuration.GetSection("OpenCode"));
builder.Services.AddHostedService<EventLogger>();

await builder.Build().RunAsync();
```

```json
{
  "OpenCode": {
    "Endpoint": "http://127.0.0.1:4096"
  }
}
```

Leave `Password` out of that file. Configuration is layered, so user secrets in development and an
environment variable or a secret store in production bind onto the same section without the
credential ever reaching source control.

Then inject whichever family the service needs — here the event bus, straight into a hosted
service:

```csharp
internal sealed class EventLogger(EventsClient events, ILogger<EventLogger> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var @event in events.SubscribeAsync(cancellationToken: stoppingToken))
        {
            logger.LogInformation("opencode event {EventType}", @event.Type);
        }
    }
}
```

> **⚡ Trimming and native AOT**: `AddOpenCode(IConfiguration)` carries `[RequiresDynamicCode]`
> and `[RequiresUnreferencedCode]`, because configuration binding reflects over the options type —
> so a trimmed or AOT publish reports **IL3050** and **IL2026** at that call. Nothing is wrong with
> your code; the annotation is doing its job. Switch to the configure-action overload there, which
> needs no reflection at all. Both packages declare `IsAotCompatible` on every modern target.

The configure-action overload, with a `SessionsClient` worker doing a paged read, is the worked
example in the root README's [dependency-injection quickstart](../../README.md#dependency-injection)
— worth reading side by side with the binding above.

Nothing about DI changes which door you came in through: an `AddOpenCode` registration is the
explicit-endpoint door. A launcher-started or a discovered server joins a container through the
same door — hand its identity to the configure-action overload, and the container builds and owns
the client and the 27 families exactly as it would for an endpoint you typed in:

```csharp
await using var server = await OpenCodeServer.DiscoverAsync()
    ?? await OpenCodeServer.StartAsync();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOpenCode(options =>
{
    options.Endpoint = server.Endpoint;
    options.Password = server.Password;
});

using var host = builder.Build();
await host.RunAsync();
```

The `OpenCodeServer` handle stays yours, and the `await using` around `RunAsync` is what ends it:
the server is disposed after the host has stopped, so no hosted service loses its server while it
shuts down. Disposal ends a started child; a discovered service keeps running.

Do not hand the handle to the container with `AddSingleton(server)` instead. A container disposes
only the instances it created, never one you registered, so a started server would outlive the host
until the launcher's own process-death protection ends it. Registering `server.CreateClient()` as a
singleton on its own registers only the root; calling `AddOpenCode` also registers any missing
families from that root without replacing it. An instance you supply this way remains yours to
dispose.
