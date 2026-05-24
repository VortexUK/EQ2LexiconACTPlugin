# Contributing

Thanks for considering a contribution. This is a small plugin maintained mostly by one person, so the process is light.

## Before you start

For non-trivial changes, please [open an issue](https://github.com/VortexUK/EQ2LexiconACTPlugin/issues/new/choose) first to discuss the approach. This saves both of us time if the change isn't a fit for the project's direction.

Small fixes (typos, doc improvements, obvious bug fixes) can go straight to a PR.

For security issues, see [SECURITY.md](SECURITY.md) — please don't file public issues for vulnerabilities.

## Dev setup

See [README.md → Building from source](README.md#building-from-source) for prerequisites and build commands. In short:

1. Install .NET Framework 4.8 Developer Pack + .NET SDK 8.0.421 (pinned by [global.json](global.json)).
2. Have ACT installed at the default path (or set `ACT_INSTALL_DIR`).
3. Clone the repo.
4. Activate the pre-push hook:
   ```powershell
   git config core.hooksPath .githooks
   ```

That hook runs format check + build + tests + vulnerability scan on every `git push`. It's the same gate CI runs.

## Code conventions

- `.editorconfig` is the source of truth. `dotnet format` will fix most things; `dotnet format --verify-no-changes` is what CI checks.
- IDE0055 (formatting) is promoted to error so style drift fails the build.
- Two assemblies, one solution:
  - `src/Core/EQ2Lexicon.ACTPlugin.Core.csproj` — pure code (no ACT or WinForms refs). Goes here unless you genuinely need ACT's types.
  - `src/EQ2Lexicon.ACTPlugin.csproj` — ACT-coupled UI layer. The integration boundary.
- Tests reference Core only — UI types aren't unit-testable (they're integration-tested by running the DLL in ACT).
- Comments are reserved for non-obvious *why*, not *what*. Lean on names.

## PR checklist

- [ ] Pre-push hook passes locally (format / build / tests / vuln scan)
- [ ] New behaviour is covered by xUnit tests where the code is in Core
- [ ] Public-facing changes are reflected in [CLAUDE.md](CLAUDE.md) and/or [README.md](README.md)
- [ ] Commit messages explain the *why*, not just the *what*
- [ ] Bumping `<Version>` is the *maintainer's* job — leave it alone in your PR

## Releasing (maintainer only)

See [README.md → Releasing](README.md#releasing). One tag push, the workflow does the rest.
