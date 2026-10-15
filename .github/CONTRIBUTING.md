# Contributing to the opencode SDK for .NET

🎉 **Thank you for your interest in contributing to the opencode .NET SDK!**

We welcome contributions of all kinds — from bug reports and feature requests to code improvements
and documentation updates. This guide will help you get started and give your contribution the best
chance of being accepted.

## 📋 Quick Reference

- 🐛 **Found a bug?** → [Create an Issue](https://github.com/opencode-dotnet/opencode-sdk-dotnet/issues/new/choose)
- 💡 **Have an idea?** → [Open an Issue](https://github.com/opencode-dotnet/opencode-sdk-dotnet/issues/new) and describe it (Discussions are not enabled on this repository yet)
- ❓ **Need help?** → Ask in an issue; usage questions are welcome
- 🚨 **Security issue?** → See our [Security Policy](SECURITY.md) — never open a public issue for one
- 🔧 **Ready to code?** → [Submit a Pull Request](https://github.com/opencode-dotnet/opencode-sdk-dotnet/compare)

## 🤝 Code of Conduct

This project follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). By
participating, you are expected to uphold it.

## 📝 Licensing your contribution

**Important**: by submitting a pull request, you agree to license your contribution under the
[MIT License](../LICENSE), the same terms as the rest of the project. There is no separate CLA to
sign.

## 🚀 Getting Started

### Prerequisites

- [.NET SDK 11.0](https://dotnet.microsoft.com/download/dotnet/11.0) — the release candidate is
  pinned in [`global.json`](../global.json) (`11.0.100-rc.2.26504.105`,
  `rollForward: latestFeature`, `allowPrerelease: true`)
- The **.NET 10 runtime**, for the `net10.0` test legs and the repository tools, which run on the
  LTS release (the .NET 10 SDK carries it)
- On Windows, the **.NET Framework 4.7.2 targeting pack** for the `net472` legs
- [Bun](https://bun.sh/) at the exact version in `external/opencode/package.json`'s
  `packageManager` field, first on `PATH` — the pinned-server fixture and CI use that runtime, and
  `OpenCode.Sdk.Tests` refuses to run under any other Bun
- [ripgrep 15.1.0](https://github.com/BurntSushi/ripgrep/releases/tag/15.1.0) on `PATH` — the
  filesystem live tests run against an offline simulated server that refuses the on-demand download
  upstream would otherwise perform
- [Git](https://git-scm.com/downloads) with submodule support
- IDE: [Rider](https://www.jetbrains.com/rider/), [Visual Studio](https://visualstudio.microsoft.com/),
  or [VS Code](https://code.visualstudio.com/)

### Development Environment Setup

1. **Fork and Clone**

   ```bash
   # Fork the repository on GitHub, then clone your fork with its submodules
   git clone --recurse-submodules https://github.com/YOUR-USERNAME/opencode-sdk-dotnet.git
   cd opencode-sdk-dotnet

   # Add the upstream remote
   git remote add upstream https://github.com/opencode-dotnet/opencode-sdk-dotnet.git
   ```

   `external/` holds read-only upstream checkouts used as protocol evidence and as the pinned-server
   test fixture. They are **never** hand-edited. If you cloned without `--recurse-submodules`, run
   `git submodule update --init --depth 1 external/opencode`. Then install the pinned server's
   dependencies once, with the pinned Bun first on `PATH`:
   `cd external/opencode && bun install --frozen-lockfile --ignore-scripts`.

2. **Build the Project**

   ```bash
   dotnet restore
   dotnet build --configuration Release
   ```

3. **Run Tests**

   This project uses [TUnit](https://tunit.dev/) on Microsoft.Testing.Platform.

   ```bash
   # The main pass (the completion gate below adds the ServiceElection and NativeAot passes)
   dotnet test --configuration Release --no-build -- --treenode-filter "/*/*/*/*[(Category!=ServiceElection)&(Category!=NativeAot)]"

   # One project
   dotnet test --project "$PWD/tests/OpenCode.Sdk.Tests/OpenCode.Sdk.Tests.csproj"

   # A named subset: every test class whose name contains Pipeline
   dotnet test --project "$PWD/tests/OpenCode.Sdk.Tests/OpenCode.Sdk.Tests.csproj" -- --treenode-filter "/*/*/*Pipeline*/*"
   ```

## 🐛 Reporting Issues

### Before Creating an Issue

1. **Search existing issues** to avoid duplicates
2. **Check the [Known Issues](../README.md#known-issues)** section of the README
3. **Confirm which server you hit** — this SDK is built against a pinned OpenAPI snapshot
   ([`spec/SNAPSHOT.md`](../spec/SNAPSHOT.md)), and upstream ships releases on its own cadence, so
   a mismatch between your installed `@opencode/cli` release and the pinned one is worth ruling out
   first
4. **Test against the nightly package** when you can; the fix may already be on `master`

### Creating a Bug Report

Use the [bug report form](https://github.com/opencode-dotnet/opencode-sdk-dotnet/issues/new/choose), which asks for:

- **Environment details** (SDK version, opencode server version, target framework, OS)
- **A minimal reproduction** — the smallest snippet that shows the problem
- **Expected vs actual** behavior
- **The operation and connection mode** involved, plus any error messages or stack traces

## 💡 Suggesting Features

GitHub Discussions are not enabled on this repository yet, so feature ideas go to
[Issues](https://github.com/opencode-dotnet/opencode-sdk-dotnet/issues/new) as well. Describe the
use case first and the API shape second — what you are trying to do carries more weight than a
proposed signature.

Note that the **callable surface is generated** from the pinned snapshot. "Please add operation X"
is usually a curation or generator question rather than a hand-written API request; the
[protocol and generation](../docs/architecture/protocol-and-generation.md) document explains which
one you are asking for.

## 🔧 Contributing Code

### Before You Start

1. **Open an issue first** for anything beyond a small fix, so the approach can be agreed before
   the work
2. **Check for existing work** — someone may already be on it
3. **Read the canon for the area you are touching.** [`AGENTS.md`](../AGENTS.md) has the routing
   table; the short version is
   [`docs/engineering/coding-style.md`](../docs/engineering/coding-style.md) for code,
   [`docs/engineering/testing-style.md`](../docs/engineering/testing-style.md) for tests, and
   [`docs/engineering/quality-gates.md`](../docs/engineering/quality-gates.md) for what "done"
   means

### Pull Request Process

1. **Create a feature branch**

   ```bash
   git checkout -b feature/your-feature-name
   # or
   git checkout -b fix/issue-number-description
   ```

2. **Make your changes**
   - Follow the existing code style and architectural decisions
   - **Never hand-edit generated output** under `src/OpenCode.Sdk` — change the generator or the
     curation config in `tools/` and regenerate
   - **Never hand-edit anything under `external/`** — those are read-only upstream checkouts
   - Add tests for new behavior
   - Update the affected documentation in the same change

3. **Run the full gate** — see below. "Builds" and "works" are different claims.

4. **Commit with [Conventional Commits](https://www.conventionalcommits.org/)**

   Allowed types: `feat`, `fix`, `perf`, `docs`, `test`, `refactor`, `build`, `ci`, `chore`.

   ```bash
   git commit -m "feat: add the worktree refresh operation"
   git commit -m "fix: escape dot segments in bound route values"
   git commit -m "docs: document the NoThrow error model"
   ```

   **Do not add AI-attribution trailers** (`Co-Authored-By: <assistant>`, session links, or
   similar) to commit messages or PR bodies. This is repository policy, recorded in
   [`docs/engineering/workflow.md`](../docs/engineering/workflow.md).

5. **Submit the Pull Request**
   - Fill in the [PR template](PULL_REQUEST_TEMPLATE.md)
   - Describe what changed and why
   - Link the related issue

### The Completion Gate

Run this from the repository root before you claim a change is finished. It is the same gate CI
runs, and it is canonically owned by
[`docs/engineering/quality-gates.md`](../docs/engineering/quality-gates.md), which explains why the
tests run in three passes. A fresh clone or worktree is prepared once first, with the Bun named by
`external/opencode/package.json`'s `packageManager` field first on `PATH`:

```bash
git submodule update --init --depth 1 external/opencode
cd external/opencode && bun install --frozen-lockfile --ignore-scripts
```

```bash
dotnet tool run slopwatch analyze --exclude ".scratchpad/**,external/**" --fail-on warning
dotnet build --configuration Release
dotnet format whitespace --verify-no-changes --no-restore
dotnet format style --verify-no-changes --no-restore --severity warn
dotnet test --configuration Release --no-build -- --treenode-filter "/*/*/*/*[(Category!=ServiceElection)&(Category!=NativeAot)]"
dotnet test --project "$PWD/tests/OpenCode.Sdk.Tests/OpenCode.Sdk.Tests.csproj" --configuration Release --no-build --max-parallel-test-modules 1 -- --treenode-filter "/*/*/*/*[Category=ServiceElection]"
dotnet test --project "$PWD/tests/OpenCode.Sdk.Tools.Tests/OpenCode.Sdk.Tools.Tests.csproj" --configuration Release --no-build --max-parallel-test-modules 1 -- --treenode-filter "/*/*/*/*[Category=NativeAot]"
```

Changes to the protocol, generator, curation, or generated output also run:

```bash
dotnet run --file tools/opencode-tool.cs -- --help
dotnet run --file tools/opencode-tool.cs -- generate --verify
```

### Code Quality Standards

- ✅ **The analyzer wall is fail-closed and final.** Warnings are errors, `AnalysisMode` is `All`,
  and the policy is not relaxed to make a change pass. When a rule genuinely misfires, use a
  narrowly scoped arbitration comment that names the winning rule — never a global rollback.
- ✅ **XML documentation on every public API** — `GenerateDocumentationFile` is on and load-bearing.
- ✅ **`ConfigureAwait(false)` in product code**, enforced three ways (CA2007, MA0004, VSTHRD111).
  Tests are exempt.
- ✅ **The public API is locked by a reviewed baseline.** A deliberate surface change updates
  `tests/OpenCode.Sdk.Tests/Snapshots/PublicApi.verified.txt` in the same commit; an accidental one
  fails the suite.
- ✅ **Multi-target reality.** Code must build on every target, `netstandard2.0` included, and pass
  on `net472`, whose Windows legs stand in for the `netstandard2.0` asset's downlevel behavior —
  not only on modern targets.
- ✅ **No suppressions to make a test green.** If a test is wrong, fix the test and say why.

### Testing Guidelines

- **Unit and contract tests** — deterministic, no live server; fakes only for published contracts
- **Integration tests** — real process boundaries where the process boundary *is* the product
  (the launcher, the PTY doors)
- **Live tests** — run against a server their fixture owns, started from the pinned `external/opencode` checkout; a missing checkout, install or pinned Bun is an instructive error, never a skipped test

When adding tests, put them in the project that owns the seam, follow the existing naming, cover
both the success and the refusal path, and respect the parallelism rules in
[`docs/engineering/testing-style.md`](../docs/engineering/testing-style.md) — a verdict never rides the
clock, a test whose hang guard a loaded host can still reach carries `[NotInParallel]`, and
background-service elections and the native AOT consumer run in gate passes of their own.

Scratch work belongs under the gitignored `.scratchpad/`; nothing permanent may reference it.

## 📚 Documentation

- **Code comments** explain the *why*, not the *what*
- **XML documentation** is required on public APIs
- **The consumer guide** under [`docs/guide/`](../docs/guide) is the user-facing home; keep it
  current with behavior changes
- **The CHANGELOG** gets an entry for every user-facing change
- **Every current fact has one canonical owner**; other mentions relay to it rather than restating
  it. [`docs/engineering/documentation.md`](../docs/engineering/documentation.md) owns this rule.

## 🔍 Review Process

1. **Automated checks** must pass (build, format, analyzers, tests, on all three OSes)
2. **Maintainer review** — we aim to respond within 48 hours
3. **Iterative improvements** — address feedback promptly
4. **Final approval** and merge

## 🎉 Recognition

Contributors are recognized on the
[Contributors](https://github.com/opencode-dotnet/opencode-sdk-dotnet/graphs/contributors) page and
in the release notes for significant contributions.

---

**By contributing to this project, you agree to abide by our [Code of Conduct](CODE_OF_CONDUCT.md)
and understand that your contributions will be licensed under the MIT License.**

Thank you for making the opencode .NET SDK better! 🚀
