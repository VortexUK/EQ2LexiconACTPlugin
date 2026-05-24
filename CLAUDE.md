# CLAUDE.md — EQ2 Lexicon ACT Plugin

## What this is

A .NET Framework 4.8 class library that ACT (Advanced Combat Tracker) loads as a plugin. Watches the active zone for finished EverQuest 2 encounters, builds an ACT-shaped JSON payload, and POSTs it to the EQ2 Lexicon site (`https://eq2lexicon.up.railway.app/api/parses/ingest`) under the user's API token. The site stores the parse and surfaces it under their guild on `/parses`.

Companion repo: [VortexUK/EQ2CensusBot](https://github.com/VortexUK/EQ2CensusBot) — the FastAPI + React site that owns the ingest endpoint, persistence, mirror grouping, and delete permissions.

## Architecture

Two distinct assemblies in the same solution:

- **`src/EQ2Lexicon.ACTPlugin.csproj` (UI)** — `Plugin.cs`, `SettingsPanel.cs`, `EncounterCapture.cs`, `ActHelpers.cs`. Touches `Advanced_Combat_Tracker.*` types and `System.Windows.Forms`. Cannot be built on a machine without ACT.
- **`src/Core/EQ2Lexicon.ACTPlugin.Core.csproj` (Core)** — `PayloadBuilder.cs`, `Snapshots.cs`, `PluginConfig.cs`, `UploadClient.cs`. Depends only on the .NET BCL. Unit-tested via xUnit without any ACT mocks. Builds cleanly on CI runners that lack ACT.

The UI assembly `<ProjectReference>`s Core and excludes `Core/**/*.cs` from its own compile (the SDK-style `*.cs` glob would otherwise compile every Core file into both assemblies). `EncounterCapture.CaptureSnapshot()` is the one-way adapter from ACT types to the pure Core layer.

The split also unlocks GitHub Actions CI: the workflow at `.github/workflows/ci.yml` builds Core + tests + runs coverage on `windows-latest`, never touching the UI assembly.

## Key files

| File | Purpose |
|---|---|
| `src/Plugin.cs` | `IActPluginV1` entry point. ACT calls `InitPlugin`/`DeInitPlugin`. Resolves the config path via `ActHelpers.GetConfigPath()` and passes it to `PluginConfig.Load`/`Save`. Owns lifecycles of `PluginConfig`, `SettingsPanel`, `EncounterCapture`, `UploadClient`. |
| `src/SettingsPanel.cs` | WinForms settings UI hosted in ACT's plugin tab. Dark-theme card layout (`T` static class holds every colour). Three cards: Configuration / Logging As / Last Captured. Persistence happens via the `_onSave` callback so the panel stays path-free. |
| `src/EncounterCapture.cs` | 2 s polling timer over `ActGlobals.oFormActMain.ActiveZone`. Detects "settled" encounters (no `EndTime` update for `SettleSeconds=15`), converts to `EncounterSnapshot`, then calls `PayloadBuilder.BuildPayload` → `SanitizePayload` → `SerializeJson`. |
| `src/ActHelpers.cs` | Reads the logging character name by parsing the active log file path (`eq2log_<name>.txt`). `ActGlobals.charName` returns `"YOU"` in EQ2 so it can't be used. Also owns `GetConfigPath()` — the `%APPDATA%\Advanced Combat Tracker\Config\` resolver kept here so `PluginConfig` stays ACT-free. |
| `src/Core/PayloadBuilder.cs` | Pure transform: `EncounterSnapshot` → ingest-JSON dict shape. Owns `OutgoingGroupToSwingType`, `FormatTime` (emits ISO-8601 + Z), `SanitizePayload` (replaces NaN/∞ with 0), and `SerializeJson` (JavaScriptSerializer with an 8 MB ceiling). |
| `src/Core/Snapshots.cs` | Plain DTOs (`EncounterSnapshot`, `CombatantSnapshot`, `DamageTypeAggregate`, `AttackTypeSnapshot`) that mirror the slice of ACT's data model the payload builder reads. |
| `src/Core/PluginConfig.cs` | User settings persisted as XML. `Load(path)` and `Save(path)` take the path from the caller (resolved in `Plugin.cs` via `ActHelpers.GetConfigPath()`). API token is DPAPI-encrypted on disk (current-user scope) with a `DPAPI:` prefix; legacy plaintext from v0.1.0–v0.1.4 still loads. |
| `src/Core/UploadClient.cs` | `HttpClient` wrapper. `TestConnectionAsync` (GET `/api/auth/whoami`), `UploadEncounterAsync` (POST `/api/parses/ingest`). `ValidateServerUrl` rejects non-https except `localhost`/`127.0.0.1`/`[::1]`. |
| `tests/EQ2Lexicon.ACTPlugin.Tests/` | xUnit project, `net48`. 72 tests. References Core only (UI types aren't testable without ACT). Covers PayloadBuilder, PluginConfig (incl. DPAPI roundtrip), UploadClient (incl. URL validator). Coverage collected via `coverlet.collector`. |

## Versioning + releases

- Version lives in `src/EQ2Lexicon.ACTPlugin.csproj` `<Version>`. Bump it when shipping.
- Releases are tags `v0.1.x` pushed to GitHub, with the DLL attached as a release asset.
- DLL output path after `dotnet build -c Release`: `src/bin/Release/net48/EQ2Lexicon.ACTPlugin.dll`.

Release recipe:

```powershell
# 1. Bump <Version> in src/EQ2Lexicon.ACTPlugin.csproj
# 2. Build
dotnet build src/EQ2Lexicon.ACTPlugin.csproj -c Release
# 3. Commit + push
git commit -am "vX.Y.Z: <summary>"
git push   # pre-push hook runs format + build + test
# 4. Tag + push tag
git tag -a vX.Y.Z -m "vX.Y.Z - <summary>"
git push origin vX.Y.Z
# 5. Cut release
gh release create vX.Y.Z `
  "src/bin/Release/net48/EQ2Lexicon.ACTPlugin.dll" `
  --title "vX.Y.Z - <summary>" `
  --notes-file _release_notes.md   # write this temp file first, delete after
```

## Build / test / lint

Prerequisites: .NET SDK 8+ (`dotnet --version`), ACT installed at `C:\Program Files (x86)\Advanced Combat Tracker\` (override via `ACT_INSTALL_DIR` env var).

```powershell
dotnet build EQ2Lexicon.ACTPlugin.sln -c Release       # production DLL
dotnet test EQ2Lexicon.ACTPlugin.sln                   # run all xUnit tests
dotnet format EQ2Lexicon.ACTPlugin.sln                 # apply formatting fixes
dotnet format EQ2Lexicon.ACTPlugin.sln --verify-no-changes  # CI-style check
```

`.editorconfig` is the source of truth for style. IDE0055 is promoted to error so `dotnet format --verify-no-changes` fails loudly.

## Pre-push hook

`.githooks/pre-push` runs `dotnet format --verify-no-changes` + `dotnet build -c Release` + `dotnet test --no-build -c Release` + `dotnet list package --vulnerable --include-transitive` on every `git push`. Activate per-clone with:

```powershell
git config core.hooksPath .githooks
```

## GitHub Actions CI

`.github/workflows/ci.yml` runs on every push and PR to `main`. Steps mirror the pre-push hook plus coverage and artifact upload:

1. `setup-dotnet` reads `global.json` so the runner uses the same SDK version as local dev.
2. `dotnet format --verify-no-changes` (style gate).
3. Builds `src/Core/EQ2Lexicon.ACTPlugin.Core.csproj` + the tests project — NOT the UI assembly, which would need ACT installed on the runner.
4. `dotnet test --collect:"XPlat Code Coverage"` — `coverlet.collector` drops a Cobertura XML into `TestResults/`.
5. `dotnet list package --vulnerable --include-transitive` (CVE gate).
6. `ReportGenerator` renders the Cobertura XML into HTML + a Markdown summary posted to `$GITHUB_STEP_SUMMARY` (visible inline on the job page).
7. Uploads `coverage-report/` and the `.trx` test-result files as artifacts (30-day retention).

Runner is `windows-latest` — net48 tests need the .NET Framework reference assemblies that only the Windows runner ships. Free for public repos.

Dependabot (`.github/dependabot.yml`) opens weekly PRs for NuGet packages in the tests project and for the GitHub Actions versions used in the workflows.

## Threat model

- The DLL runs at the user's Windows privilege on their own machine. We don't defend against "attacker already on the host" (that's game over).
- The user's API token is the most sensitive asset — it grants upload + delete access on the site. It's:
  - Stored DPAPI-encrypted at rest (current-user scope).
  - Sent only as a `Bearer` header to the configured `ServerUrl`, which `ValidateServerUrl` constrains to `https://` (or `http://` to localhost only).
  - Never echoed in status labels, error messages, log lines, or the "Show payload" dialog.
- The site's response is parsed by a hand-rolled `ExtractJsonString` (not a full JSON parser). It's bounded by the response body size and only extracts the single `detail` / `status` / `discord_name` string fields — no nested-structure trust.

See the audit findings + fixes in commits `9eb39e0` (v0.1.4) and `5f9e11a` (v0.1.5).

## ACT API gotchas worth remembering

- `ActGlobals.charName` returns the string `"YOU"` for EQ2 — useless. Use `ActHelpers.GetLoggingCharacterName()` instead (parses the log filename).
- `EncounterData.EndTime` is updated to the time of the last combat action continuously while a fight is in progress — it is NOT set only when the encounter ends. `EncounterCapture.Poll` uses a settle-time debounce (no EndTime change for `SettleSeconds` = 15 s) to detect "fight is actually over".
- ACT puts a zone-aggregate pseudo-encounter with `Title="All"` at the front of `zone.Items`. Skip it — it duplicates real data and has no real mob.
- `EncounterData.GetAllies()` returns ACT's authoritative ally list (the EQ2 parser already attributes pets to their owners). Don't try to write your own name-shape heuristic for player vs enemy.
- `CombatantData.Items[<category>]` keys are strings like `"Skill/Ability (Out)"`. ACT does NOT expose `swingtype` on `AttackType` at the version we target — derive it from the category name via `PayloadBuilder.OutgoingGroupToSwingType`.
- ACT timestamps come from EQ2's log file which is written in the player's local clock — they are `DateTime` with `Kind == Unspecified`. `PayloadBuilder.FormatTime` converts to UTC and tags with a trailing `Z` so cross-timezone viewers on the site see the right time.
- ACT returns `NaN` for any stat that involved divide-by-zero (`average = damage / hits` when hits = 0). `SanitizePayload` walks the payload before serializing and replaces `NaN`/`±∞` with 0 — `JavaScriptSerializer` emits the literal text `NaN` which is invalid JSON otherwise.

## Theme constants

`SettingsPanel.cs` has a `static class T` holding every colour as `Color.FromArgb(...)`. To recolour the panel (e.g. light-mode variant) change `T` and nothing else — every widget reads from it.

## Distribution

- GitHub Releases is the only distribution channel. Direct download URL pattern:
  `https://github.com/VortexUK/EQ2LexiconACTPlugin/releases/download/vX.Y.Z/EQ2Lexicon.ACTPlugin.dll`
- The DLL is unsigned. Windows SmartScreen may warn on first run; users click through. A code-signing certificate would fix this but costs ~$200/yr — not currently worth it.

## CI/CD gaps

Comprehensive pipeline status as of v0.1.5 + the B2.16 sprint:

| Item | Status |
|---|---|
| Pre-push gate (format, build, test, vuln scan) | ✅ `.githooks/pre-push` |
| Pinned SDK | ✅ `global.json` |
| GitHub Actions build/test on PR | ✅ `.github/workflows/ci.yml` (builds Core + tests) |
| Code coverage report | ✅ Coverlet + ReportGenerator, posted to job summary + artifact |
| Vulnerability scanning | ✅ `dotnet list package --vulnerable` in pre-push + CI |
| Dependabot | ✅ `.github/dependabot.yml` |
| Core/UI assembly split (so CI can build without ACT) | ✅ `src/Core/` |
| Auto-release on `v*` tag push | ⏳ Pending — manual `gh release create` recipe documented above |
| Authenticode-signed DLL | ❌ Skipped — ~$200/yr cert not worth it; users click through SmartScreen |
| Changelog automation | ❌ Skipped — release notes hand-written per release |
