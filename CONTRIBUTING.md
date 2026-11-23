# Contributing to the opencode SDK for .NET

Bug reports, documentation fixes, ideas and pull requests are welcome. A small fix needs no
agreement in advance.

You can contribute with Git and the .NET tools below. Whatever tools you use, AI tools included,
review your changes yourself before you submit them, and be ready to explain them.

## A small pull request

1. **Branch, or edit on GitHub.** Fork the repository and create a branch:

   ```bash
   git checkout -b fix/short-description
   ```

   For a documentation fix, you can also edit the file on GitHub, which creates the fork and the
   branch for you.
2. **Change one thing.** A pull request that does one thing is reviewed sooner.
3. **Run the check that applies.**
   - A change to only Markdown files or `LICENSE` needs no local check.
   - Any other change needs the local check: see [Build and test](#build-and-test).
4. **Open the pull request against `master`.** Fill in the
   [pull request template](.github/PULL_REQUEST_TEMPLATE.md): what changed and why, the linked issue, and
   what you ran. Commit messages follow the rules under [Commits](#commits).

Then:

- **CI.** Your pull request shows the required `CI result` check. A code change normally runs on
  Windows, Linux and macOS. A pull request that changes only Markdown runs the build and the tests on Linux.
  CI compiles the complete C# examples in the README and the guide, so when you change one, CI
  tells you whether it still builds.
- **Review.** The maintainer reviews the change. We aim to respond within 48 hours.
- **Merge.** When its `CI result` check is green, the pull request can join the merge queue, which
  tests it on top of current `master` before it merges.
  [Commits and CI](docs/engineering/workflow.md#commits-and-ci) has the exact rules, including when
  the queue can reuse a result. The change is complete when the CI run of the merge commit on
  `master` is green. A merge of only `LICENSE` starts no run on `master`.

For anything larger than a small fix, open an issue first, so that the approach is agreed before
you spend the time. Issues with the
[`good first issue`](https://github.com/opencode-dotnet/opencode-sdk-dotnet/labels/good%20first%20issue)
label, when there are any, are small, self-contained, and do not require reading the generator to
start.

The issue list holds work that a person outside the project can act on or follow. The maintainer's
own backlog is on the public [project board](https://github.com/orgs/opencode-dotnet/projects/1)
as draft items.

To ask a question, report a bug or propose a feature, see [SUPPORT.md](.github/SUPPORT.md). To report a
vulnerability, follow [SECURITY.md](.github/SECURITY.md); never open a public issue for one.

## Set up for a code change

You need:

- the .NET SDK that [`global.json`](global.json) pins, and the latest .NET 10 runtime patch for
  the `net10.0` tests (the .NET 10 SDK carries it);
- [Bun](https://bun.sh/), at the version that the `packageManager` field of
  `external/opencode/package.json` names, first on `PATH`;
- [ripgrep 15.1.0](https://github.com/BurntSushi/ripgrep/releases/tag/15.1.0), first on `PATH`;
- Git.

Bun and ripgrep are there because the tests start a real opencode server from the
`external/opencode` submodule. In your clone:

```bash
git clone --recurse-submodules https://github.com/YOUR-USERNAME/opencode-sdk-dotnet.git
cd opencode-sdk-dotnet
dotnet tool restore
cd external/opencode
bun install --frozen-lockfile --ignore-scripts
cd ../../tools/jscpd
bun install --frozen-lockfile --ignore-scripts
cd ../..
```

`dotnet tool restore` installs the repository's local tools. The first `bun install` installs the
server's dependencies, and the second installs the clone detector. If you cloned without
`--recurse-submodules`, run `git submodule update --init --depth 1 external/opencode` before the
installs.

The core test project, `OpenCode.Sdk.Tests`, checks three things before it runs any test: the
submodule checkout, its installed dependencies, and the Bun version. When one is missing, the run
stops with one error that names it and the command that fixes it. ripgrep is not part of that
check: without it, the file-search tests fail with HTTP 500. If your machine has a different Bun
version, set `OPENCODE_SDK_TESTS_BUN_MISMATCH=allow` for the test command. That result is local
evidence only; CI runs the exact Bun.

`external/` holds read-only upstream checkouts. Never edit them by hand.

## Build and test

Build, then run the tests you are working on. This runs every test class whose name contains
`Pipeline`:

```bash
dotnet build --configuration Release
dotnet test --project "$PWD/tests/OpenCode.Sdk.Tests/OpenCode.Sdk.Tests.csproj" --configuration Release --no-build -- --treenode-filter "/*/*/*Pipeline*/*"
```

The tests use [TUnit](https://tunit.dev/) on Microsoft.Testing.Platform.

**Before you open a pull request for a code change, run the local check.**
[Completion gate](docs/engineering/quality-gates.md#completion-gate) lists its steps and their
commands: the static steps, the repository's own guard tests, and the tests of the area you
changed. It also says which extra steps a change to packaging, to the generator or to the
background service needs. Run it on a machine that is doing nothing else, because several tests
start real processes and have time limits.

The local check is not the verdict: CI is.

## What the analyzers ask of you

Every compiler and analyzer warning is an error, so the first build of new code can fail. A
diagnostic names the rule id and what the rule wants. Example diagnostic:

```text
error CA2007: Consider calling ConfigureAwait on the awaited task
```

| Rule id | What it asks |
|---|---|
| `CA2007`, `MA0004`, `VSTHRD111` | Product code awaits with `ConfigureAwait(false)`. Tests are exempt. |
| `CS1591` | A public type or member in product code has an XML documentation comment. Tests are exempt. |
| `RS0030` | The code calls a banned API. The message names the replacement. [`BannedSymbols.txt`](BannedSymbols.txt) and [`BannedSymbols.Clock.txt`](BannedSymbols.Clock.txt) hold the lists: for example, product code takes time from an injected `TimeProvider`, so `DateTime.UtcNow` and `Task.Delay(TimeSpan)` are banned there. |
| A formatting difference from `dotnet format` | Run the same command without `--verify-no-changes` to apply the fix. |

Other ids come from the .NET SDK's analyzers (`CA`, `IDE`), Meziantou (`MA`), Roslynator (`RCS`),
Sonar (`S`), Visual Studio Threading (`VSTHRD`), the System.IO.Abstractions analyzers, and, in
tests, NSubstitute (`NS`). Tools and tests reach the file system through an injected `IFileSystem`,
not through `File`, `Directory` or `Path` directly.

When a rule fires:

1. **Fix the code.** The message nearly always says how.
2. **If the rule is wrong for valid code, say so** in your issue or pull request, with the rule id
   and a small example. The maintainer decides. The answer is a narrow exception for that one rule
   that names its reason.
3. **Do not silence the rule yourself.** A suppression of any kind (`#pragma warning disable`,
   `[SuppressMessage]`, `<NoWarn>`, an `.editorconfig` severity) needs the maintainer's approval
   and is named in the pull request description. A test counts every suppression in the
   repository, so one that is not recorded fails that test.

Two failures that are not analyzers:

- **The public API test.** A reviewed snapshot holds the public API. A deliberate change updates
  `tests/OpenCode.Sdk.Tests/Snapshots/PublicApi.verified.txt`, or the one under
  `tests/OpenCode.Sdk.Extensions.Tests/Snapshots/`, in the same commit. An accidental change fails
  the tests.
- **A target that does not compile.** The packages target `netstandard2.0`, `net472`, `net10.0`
  and `net11.0`. Code must build on every one of them. The `net472` tests run only on Windows, so
  on Linux or macOS your pull request's CI is the first place that runs them.

## What slopwatch asks of you

Slopwatch is a .NET tool that [`dotnet-tools.json`](dotnet-tools.json) pins. It is the first
step of the local check, and CI runs it on every code change. It looks for shortcuts that make a
build or a test pass without fixing the cause. Its rules include:

- an empty `catch` block (`SW003`);
- an arbitrary delay in a test (`SW004`);
- a warning that a project file switches off (`SW005`).

It applies to every change, whoever or whatever wrote it. If it flags your change, fix the cause it
names. Where the flagged code is right, such as cleanup after the outcome is already decided, the
code carries `[SlopwatchSuppress("SW003", "…")]` with a reason that says where the failure is
observed. That is a suppression, so it needs the maintainer's approval like any other.
[`docs/engineering/slop-review.md`](docs/engineering/slop-review.md#suppression-policy) owns the
policy.

## Rules for every change

- **Generated code changes through the generator.** A file under `src/OpenCode.Sdk` that begins
  with `// Generated by OpenCode.Sdk.Tools` is never edited by hand. Change the generator or
  `tools/curation.json`, then run `dotnet run --file tools/opencode-tool.cs -- generate`.
- **A behaviour change comes with a test.** [Testing style](docs/engineering/testing-style.md)
  has the conventions. No test is skipped, weakened or deleted to make the tests pass.
- **A change that a consumer can see** updates the guide or README section that describes it, and
  adds an entry under `[Unreleased]` in `CHANGELOG.md`, in the same pull request.
- **Follow the code around your change.** [Coding style](docs/engineering/coding-style.md) has
  the rules for hand-written code.
- **Keep scratch files** under the gitignored `.scratchpad/` folder.

## Commits

- Use [Conventional Commits](https://www.conventionalcommits.org/): `feat`, `fix`, `perf`, `docs`,
  `test`, `refactor`, `build`, `ci` or `chore`. For example:
  `fix: escape dot segments in bound route values`.
- Commit messages and pull request descriptions carry no AI-attribution trailers: no co-author
  line for a tool, no session link, no "generated with" note.
- Never put `[skip ci]` in a commit. The required check would not report, and the pull request
  could not join the merge queue.

## License and conduct

By submitting a pull request, you agree to license your contribution under the
[MIT License](LICENSE), the same terms as the rest of the project. There is no separate CLA.
Everyone who takes part follows the organization's
[Code of Conduct](https://github.com/opencode-dotnet/.github/blob/master/CODE_OF_CONDUCT.md).
Contributors appear on the
[Contributors](https://github.com/opencode-dotnet/opencode-sdk-dotnet/graphs/contributors) page,
and the release notes name significant contributions.
