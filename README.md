# EQ2 Lexicon — ACT Plugin

[![CI](https://github.com/VortexUK/EQ2LexiconACTPlugin/actions/workflows/ci.yml/badge.svg)](https://github.com/VortexUK/EQ2LexiconACTPlugin/actions/workflows/ci.yml)
[![CodeQL](https://github.com/VortexUK/EQ2LexiconACTPlugin/actions/workflows/codeql.yml/badge.svg)](https://github.com/VortexUK/EQ2LexiconACTPlugin/actions/workflows/codeql.yml)
[![Latest release](https://img.shields.io/github/v/release/VortexUK/EQ2LexiconACTPlugin)](https://github.com/VortexUK/EQ2LexiconACTPlugin/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

An [Advanced Combat Tracker](https://advancedcombattracker.com) plugin that uploads parsed EverQuest 2 encounters to the [EQ2 Lexicon](https://eq2lexicon.up.railway.app) website automatically after each fight ends.

## Install (end users)

1. Sign in at <https://eq2lexicon.up.railway.app> and mint an API token at `/settings/tokens`.
2. Download `EQ2Lexicon.ACTPlugin.dll` from the [latest release](https://github.com/VortexUK/EQ2LexiconACTPlugin/releases/latest).
3. Drop the DLL into `%APPDATA%\Advanced Combat Tracker\Plugins\`.
4. In ACT: **Options → Plugins → Browse**, pick the DLL, then **Add/Enable Plugin**.
5. Open the new **EQ2 Lexicon** tab in ACT and paste your token. Hit **Test Connection** to verify, then **Save**.

You're done. ACT will continue parsing your combat log as normal; finished encounters get uploaded in the background.

## How it works

1. The plugin polls ACT's active zone every 2 seconds for encounters that have settled (no new combat activity for 15 s).
2. When one settles, it snapshots the ACT data (encounter, combatants, damage / heal / cure / threat rollups, per-ability breakdowns) and builds an ACT-shaped JSON payload.
3. The payload `POST`s to `/api/parses/ingest` with your bearer token over HTTPS.
4. The site resolves your guild via the Daybreak Census API, stores the parse, and surfaces it under your guild on `/parses` for any signed-in user to browse.

Your EQ2 character name comes from parsing the active log filename (`eq2log_<character>.txt`) — `ActGlobals.charName` returns the string `"YOU"` for EQ2 and can't be used directly.

## Building from source

### Prerequisites

- **.NET Framework 4.8 Developer Pack** — [download from Microsoft](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48) (pick "Developer Pack", not "Runtime"). ~80 MB.
- **.NET SDK 8.0.421** — pinned via [`global.json`](global.json). `dotnet --version` should match or be a higher 8.0.x.
- **Advanced Combat Tracker** installed locally so the build can reference `Advanced Combat Tracker.exe`. Default expected location is `C:\Program Files (x86)\Advanced Combat Tracker\`; override via the `ACT_INSTALL_DIR` environment variable.

### Build the DLL

```powershell
dotnet build src\EQ2Lexicon.ACTPlugin.csproj -c Release
```

Output lands at `src\bin\Release\net48\EQ2Lexicon.ACTPlugin.dll`. Copy it to `%APPDATA%\Advanced Combat Tracker\Plugins\` and restart ACT.

## Project layout

Two assemblies in one solution, plus a test project:

```
EQ2Lexicon.ACTPlugin.sln
src/
  EQ2Lexicon.ACTPlugin.csproj        # UI — net48, references ACT.exe + WinForms
  Plugin.cs                          # IActPluginV1 entry point
  SettingsPanel.cs                   # dark-themed settings UI
  EncounterCapture.cs                # 2 s polling + ACT → snapshot adapter
  ActHelpers.cs                      # ActGlobals shims (char name, config path)
  Core/
    EQ2Lexicon.ACTPlugin.Core.csproj # Pure — net48, no ACT / WinForms refs
    PayloadBuilder.cs                # snapshot → ingest-JSON transform
    Snapshots.cs                     # DTOs decoupling payload logic from ACT
    PluginConfig.cs                  # XML-serialised settings (DPAPI-wrapped token)
    UploadClient.cs                  # HttpClient wrapper + JSON helpers
tests/
  EQ2Lexicon.ACTPlugin.Tests/        # xUnit, net48 — references Core only
.github/
  workflows/ci.yml                   # build + test + coverage on every push
  workflows/release.yml              # auto-publish DLL on v* tag push
  dependabot.yml                     # weekly dep update PRs
.githooks/pre-push                   # format + build + test + vuln scan
global.json                          # pinned .NET SDK version
```

The pure layer exists so the build/sanitise/upload logic is testable without instantiating ACT's sealed types, and so CI can compile + test it on runners that don't have ACT.

## Running checks locally

Activate the pre-push hook once after cloning:

```powershell
git config core.hooksPath .githooks
```

After that, every `git push` runs:

| Step | Command |
|------|---------|
| Format check | `dotnet format EQ2Lexicon.ACTPlugin.sln --verify-no-changes` |
| Build | `dotnet build EQ2Lexicon.ACTPlugin.sln -c Release` |
| Tests | `dotnet test EQ2Lexicon.ACTPlugin.sln --no-build -c Release` |
| Vulnerability scan | `dotnet list EQ2Lexicon.ACTPlugin.sln package --vulnerable --include-transitive` |

To fix formatting issues:

```powershell
dotnet format EQ2Lexicon.ACTPlugin.sln
```

GitHub Actions runs the same gates on every push and PR, plus collects code coverage via Coverlet + ReportGenerator (Markdown summary posted to the job page, HTML uploaded as an artifact). It builds the UI assembly too by fetching `ACTv3.zip` from the upstream ACT release.

The test suite covers `PayloadBuilder` (shape, NaN sanitiser, UTC formatter, JSON serialiser), `PluginConfig` (defaults, blacklist, XML round-trip, DPAPI round-trip), and `UploadClient`'s JSON / truncate helpers + URL validator.

The ACT-coupled bits (`EncounterCapture.CaptureSnapshot`, `SettingsPanel`, `Plugin`) aren't unit-tested — they're integration-tested by running the DLL in ACT.

## Releasing

Releases are fully automated. Bump `<Version>` in both csproj files, commit, push, then:

```powershell
git tag -a vX.Y.Z -m "vX.Y.Z - <summary>"
git push origin vX.Y.Z
```

The [release workflow](.github/workflows/release.yml) fetches ACT on the runner, builds the UI DLL, generates release notes from the commit log since the previous tag, and creates a draft release with the DLL attached. Review the notes on GitHub, then publish:

```powershell
gh release edit vX.Y.Z --draft=false
```

## License

[MIT](LICENSE).
