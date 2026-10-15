<!--
Thank you for contributing to the opencode SDK for .NET!
Please fill out this template to help us review your pull request.
-->

## 📝 Description

**What does this PR do?**
Provide a clear and concise description of the changes.

**Related Issue(s):**

- Fixes #(issue number)
- Closes #(issue number)
- Related to #(issue number)

## 🔄 Type of Change

- [ ] 🐛 Bug fix (non-breaking change that fixes an issue)
- [ ] ✨ New feature (non-breaking change that adds functionality)
- [ ] 💥 Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] 📚 Documentation update
- [ ] 🧹 Code cleanup/refactoring
- [ ] ⚡ Performance improvement
- [ ] 🧪 Test improvements
- [ ] 🔧 Build, CI, or tooling

## 🎯 Protocol & API Surface

- [ ] No change to the public API surface
- [ ] Public API changed **deliberately**, and `tests/OpenCode.Sdk.Tests/Snapshots/PublicApi.verified.txt` is updated in this PR
- [ ] Generated output changed, and it was produced by the generator — **not** hand-edited
- [ ] `dotnet run --file tools/opencode-tool.cs -- generate --verify` passes
- [ ] The pinned snapshot (`spec/`) is untouched, or the refresh follows `spec/SNAPSHOT.md`
- [ ] Nothing under `external/` was hand-edited

## 🧪 Testing

**How has this been tested?**

- [ ] Unit / contract tests added or updated
- [ ] Integration tests added or updated
- [ ] Verified against a real `opencode serve` (say which version below)
- [ ] Verified on the downlevel targets (`net472` / `netstandard2.0`)
- [ ] Sandbox walkthrough run (`tests/OpenCode.Sdk.Sandbox`)

**Test Environment:**

- opencode server version:
- Target frameworks tested:
- Operating systems:

## ✅ Completion Gate

Run from the repository root — see [`docs/engineering/quality-gates.md`](../docs/engineering/quality-gates.md):

- [ ] `dotnet tool run slopwatch analyze --exclude ".scratchpad/**,external/**" --fail-on warning` — zero findings
- [ ] `dotnet build --configuration Release`
- [ ] `dotnet format whitespace --verify-no-changes --no-restore`
- [ ] `dotnet format style --verify-no-changes --no-restore --severity warn`
- [ ] The three test passes in `quality-gates.md` (main, `ServiceElection`, `NativeAot`), with the pinned Bun first on `PATH`

## 📚 Documentation

- [ ] Code is self-documenting with clear naming
- [ ] XML documentation comments added/updated for public APIs
- [ ] `README.md` updated (if needed)
- [ ] The consumer guide under `docs/guide/` updated (if needed)
- [ ] `CHANGELOG.md` entry added for user-facing changes
- [ ] Breaking changes documented with impact and migration path

## ✅ Code Quality Checklist

- [ ] Code follows the project's coding standards
- [ ] No new analyzer warnings, and no rules disabled or suppressed to make the build pass
- [ ] No tests skipped, weakened, or deleted to make the suite pass
- [ ] All tests pass locally
- [ ] Branch is up to date with the target branch, no merge conflicts
- [ ] Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/) and carry **no AI-attribution trailers**

## 🔍 Additional Notes

**Breaking Changes:**
If this is a breaking change, describe the impact and the migration path for users.

**Performance Impact:**
Describe any performance implications. If this is a performance change, attach the benchmark
evidence rather than an estimate.

**Dependencies:**
List any new dependencies or version changes.

## 🎯 Reviewer Focus Areas

**Please pay special attention to:**

- [ ] Correctness against the pinned protocol contract
- [ ] Public API and wire compatibility
- [ ] Multi-target behavior (`netstandard2.0` / `net472` differences)
- [ ] Cancellation, disposal, and stream lifetime
- [ ] Security implications (credentials, process lifetime, untrusted server responses)
- [ ] Test coverage, including the refusal paths
- [ ] Documentation completeness

## 📸 Examples

If applicable, show the change in action.

```csharp
// Example usage
```

---

By submitting this pull request, I confirm that:

- [ ] I have read and agree to the project's [Code of Conduct](CODE_OF_CONDUCT.md)
- [ ] My contribution is licensed under the same terms as the project (MIT License)
