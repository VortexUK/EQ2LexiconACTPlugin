# CLAUDE.md — EQ2 Lexicon ACT Plugin

## What this is

A .NET Framework 4.8 class library that ACT (Advanced Combat Tracker) loads as a plugin. Watches the active zone for finished EverQuest 2 encounters, builds an ACT-shaped JSON payload, and POSTs it to the EQ2 Lexicon ingest API (`https://parses.eq2lexicon.com/api/parses/ingest`) under the user's API token. The site stores the parse and surfaces it under their guild on `/parses`. The endpoint is an owned subdomain that maps to the production Railway service; v0.1.11+ uses it as the default, with a one-shot load-time migration for users still on the legacy `eq2lexicon.up.railway.app` default.

Companion repo: [VortexUK/EQ2Lexicon](https://github.com/VortexUK/EQ2Lexicon) — the FastAPI + React site that owns the ingest endpoint, persistence, mirror grouping, and delete permissions.

## Architecture

Two distinct assemblies in the same solution:

- **`src/EQ2Lexicon.ACTPlugin.csproj` (UI)** — `Plugin.cs`, `SettingsPanel.cs`, `EncounterCapture.cs`, `ActHelpers.cs`. Touches `Advanced_Combat_Tracker.*` types and `System.Windows.Forms`. Cannot be built on a machine without ACT.
- **`src/Core/EQ2Lexicon.ACTPlugin.Core.csproj` (Core)** — `PayloadBuilder.cs`, `Snapshots.cs`, `PluginConfig.cs`, `UploadClient.cs`. Depends only on the .NET BCL. Unit-tested via xUnit without any ACT mocks. Builds cleanly on CI runners that lack ACT.

The UI assembly `<ProjectReference>`s Core and excludes `Core/**/*.cs` from its own compile (the SDK-style `*.cs` glob would otherwise compile every Core file into both assemblies). `EncounterCapture.CaptureSnapshot()` is the one-way adapter from ACT types to the pure Core layer.

The split also unlocks GitHub Actions CI: the workflow at `.github/workflows/ci.yml` builds Core + tests + runs coverage on `windows-latest`, never touching the UI assembly.

**Single-file distribution via ILRepack** (added in v0.1.7): the split is a build/test convenience; users still get one DLL. On Release builds the `ILRepackMergeCore` target in `src/EQ2Lexicon.ACTPlugin.csproj` merges `EQ2Lexicon.ACTPlugin.Core.dll` into the UI DLL (via `ILRepack.Lib.MSBuild.Task`) and deletes the standalone Core DLL/PDB from the UI bin folder. The Core project still produces its own DLL in `src/Core/bin/Release/net48/` for the test project to reference. Debug builds skip the merge so the VS debugger isn't slowed. v0.1.6 shipped without this and users got `Could not load file or assembly 'EQ2Lexicon.ACTPlugin.Core'` because the release workflow only uploads the UI DLL — see commit `3d4051b`.

## Key files

| File | Purpose |
|---|---|
| `src/Plugin.cs` | `IActPluginV1` entry point. ACT calls `InitPlugin`/`DeInitPlugin`. Resolves the config path via `ActHelpers.GetConfigPath()` and passes it to `PluginConfig.Load`/`Save`. Owns lifecycles of `PluginConfig`, `SettingsPanel`, `EncounterCapture`, `UploadClient`. |
| `src/SettingsPanel.cs` | WinForms settings UI hosted in ACT's plugin tab. Dark-theme card layout (`T` static class holds every colour). Three cards: Configuration / Logging As / Last Captured. Persistence happens via the `_onSave` callback so the panel stays path-free. |
| `src/EncounterCapture.cs` | 2 s polling timer over `ActGlobals.oFormActMain.ActiveZone`. Detects "settled" encounters (no `EndTime` update for `SettleSeconds=15`), converts to `EncounterSnapshot` via the public static `CaptureSnapshot` (reused by the manual-upload path), then calls `PayloadBuilder.BuildPayload` → `SanitizePayload` → `SerializeJson`. Skip reasons (all raise `OnSkipped` with a user-visible message): Import/Merge zone, encounter started before `_instanceStartedAt - InstanceStartGraceSeconds` (= log imports + pre-existing fights), placeholder title that didn't resolve within `MaxPlaceholderWaitSeconds`. The three skip branches share `MarkProcessedNoCapture` so the encid eviction logic isn't duplicated. |
| `src/ActMenuExtension.cs` | Adds "Upload to EQ2 Lexicon" to ACT's right-click encounter menu. See "ACT UI extension" below for the (undocumented) wiring details. |
| `src/Core/EncounterTitle.cs` | `IsPlaceholder(title)` — the shared predicate used by both the polling and manual-upload paths to reject encounters ACT hasn't named yet ("Encounter", "Unknown", empty/whitespace). Lives in Core so it's testable without ACT. |
| `src/Core/EncounterZone.cs` | `IsImportOrMerge(zoneName)` — the shared predicate used by the polling path, the right-click menu's Opening handler (greys out the item), and Plugin's manual-upload handler (defensive re-check). The Import/Merge bucket holds imported logs and merged/edited encounters that we must never upload. |
| `src/Core/LogPathParser.cs` | `ParseServerName(logPath)` — extracts the EQ2 server name (Varsoon, Kaladim, …) from ACT's active log file path. EQ2 writes logs at `<install>/logs/<server>/eq2log_<character>.txt`, so the parent directory IS the server. Returns "" for the legacy generic `logs/eq2log.txt` path (no server subdirectory) or anything else that doesn't fit; server falls back to `EQ2_WORLD` in that case. ActHelpers.GetLoggingServerName() is the UI-side wrapper. |
| `src/ActHelpers.cs` | Reads the logging character name by parsing the active log file path (`eq2log_<name>.txt`). `ActGlobals.charName` returns `"YOU"` in EQ2 so it can't be used. Also owns `GetConfigPath()` — the `%APPDATA%\Advanced Combat Tracker\Config\` resolver kept here so `PluginConfig` stays ACT-free. |
| `src/Core/PayloadBuilder.cs` | Pure transform: `EncounterSnapshot` → ingest-JSON dict shape. Owns `OutgoingGroupToSwingType`, `FormatTime` (emits ISO-8601 + Z), `SanitizePayload` (replaces NaN/∞ with 0), and `SerializeJson` (JavaScriptSerializer with an 8 MB ceiling). |
| `src/Core/Snapshots.cs` | Plain DTOs (`EncounterSnapshot`, `CombatantSnapshot`, `DamageTypeAggregate`, `AttackTypeSnapshot`) that mirror the slice of ACT's data model the payload builder reads. |
| `src/Core/PluginConfig.cs` | User settings persisted as XML. `Load(path)` and `Save(path)` take the path from the caller (resolved in `Plugin.cs` via `ActHelpers.GetConfigPath()`). API token is DPAPI-encrypted on disk (current-user scope) with a `DPAPI:` prefix; legacy plaintext from v0.1.0–v0.1.4 still loads. |
| `src/Core/UploadClient.cs` | `HttpClient` wrapper. `TestConnectionAsync` (GET `/api/auth/whoami`), `UploadEncounterAsync` (POST `/api/parses/ingest`). `ValidateServerUrl` rejects non-https except `localhost`/`127.0.0.1`/`[::1]`. Adds `X-Lexicon-Signature` HMAC header to every upload (see PayloadSigner) and blocks uploads when `UpdateStatus.UploadBlocked == true`. |
| `src/Core/UpdateChecker.cs` | GitHub-releases-feed fetcher + pure version-comparison logic. `Compute(currentVersion, releasedVersions)` returns an `UpdateCheckResult` with `Current`/`SlightlyStale`/`TooOld`/`DevBuild`/`Unknown`. Threshold = `StaleThresholdVersions` (2) — older than that blocks uploads. Failure modes (network down, GH 5xx, rate-limit) all collapse to `Unknown` so the gate fails OPEN. `ParseLatestDllAsset` extracts the latest-release DLL URL + SHA-256 digest for the self-update flow. |
| `src/Core/PluginUpdater.cs` | Download + SHA-256 verify for the self-update flow (v0.1.12+). Pure (`IDllAssetFetcher` injects HTTP). Constant-time hash compare. Refuses to install without a digest, on size overflow (10 MB cap vs ~76 KB DLL), and on any hash mismatch. |
| `src/UpdateInstaller.cs` | UI-side staging for the self-update flow. Writes `<dll>.new` next to the running DLL, drops a manual-recovery readme, spawns a hidden PowerShell `Wait-Process -Id <act_pid>; Move-Item -Force *.new *.dll` that swaps the file once ACT exits. Recovery readme covers the case where the helper script gets killed by AV. |
| `src/Core/PayloadSigner.cs` | HMAC-SHA256 of the upload body keyed by the user's API token. See "Payload integrity" below for the threat model and what this does/doesn't defend. |
| `tests/EQ2Lexicon.ACTPlugin.Tests/` | xUnit project, `net48`. 72 tests. References Core only (UI types aren't testable without ACT). Covers PayloadBuilder, PluginConfig (incl. DPAPI roundtrip), UploadClient (incl. URL validator). Coverage collected via `coverlet.collector`. |

## Versioning + releases

- Version lives in `src/EQ2Lexicon.ACTPlugin.csproj` `<Version>`. Bump **both** csproj files (UI + Core) in lockstep when shipping — the Core version is referenced in error-message text and stays meaningful for diagnostics even after ILRepack folds it in.
- Releases are tags `v0.1.x` pushed to GitHub, with the DLL attached as a release asset.
- DLL output path after `dotnet build -c Release`: `src/bin/Release/net48/EQ2Lexicon.ACTPlugin.dll` — a single self-contained DLL (Core is ILRepacked in; see Architecture above).

Release recipe — fully automated end-to-end:

```powershell
# 1. Bump <Version> in BOTH csproj files (UI + Core stay in lockstep)
# 2. Commit + push
git commit -am "vX.Y.Z: <summary>"
git push                          # pre-push hook + CI workflow run
# 3. Tag + push tag — fires the release workflow
git tag -a vX.Y.Z -m "vX.Y.Z - <summary>"
git push origin vX.Y.Z
# 4. (automatic) .github/workflows/release.yml:
#    - fetches ACTv3.zip from upstream to get the strong-named ACT reference
#    - builds the UI DLL on windows-latest
#    - generates release notes from the commits since the previous tag
#    - creates a DRAFT release with EQ2Lexicon.ACTPlugin.dll attached
# 5. Review the draft notes on github.com, then publish:
gh release edit vX.Y.Z --draft=false
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

See the audit findings + fixes in commits `9eb39e0` (v0.1.4) and `5f9e11a` (v0.1.5). A follow-up audit covering v0.1.5 → v0.1.12 shipped in v0.1.13 — findings were all defence-in-depth (no exploitable boundary breaches), summarised in that commit. Top items:

- `UpdateInstaller`'s PowerShell helper now escapes single quotes in paths and uses `-LiteralPath` everywhere — Windows usernames containing apostrophes (`O'Brien`, `D'Souza`) previously broke the on-exit swap silently.
- `HttpDllAssetFetcher` enforces HTTPS + a GitHub-host allowlist + manual redirect handling (max 5 hops, scheme + host re-validated at each) + `MaxResponseContentBufferSize` cap. The SHA-256 verify in `PluginUpdater` is still the actual integrity gate; these are belt-and-brace defence against a tampered release-feed JSON.
- `UpdateChecker.FindDllAsset` now scopes its search to the `"assets":[ ... ]` array via bracket-counting, so a maintainer's display name ending in `.dll` or a release-notes blob containing literal `"tag_name"` can't confuse the asset lookup.
- `ExtractJsonString` rejects lone UTF-16 surrogates (`\uD83D` with no following low surrogate).
- Server-side: `_sanitize_world` regex on `logger_server` + `_VALID_CHARACTER_NAME_RE` (1-15 letters) on `logger_name` keep malformed payloads out of Census URLs and `character_cache` keys.

## Payload integrity (HMAC, v0.1.8+)

Every upload includes an `X-Lexicon-Signature` header: HMAC-SHA256 of the request body, keyed by the user's API token. Implementation in `src/Core/PayloadSigner.cs`. The server in the [EQ2Lexicon](https://github.com/VortexUK/EQ2Lexicon) repo re-computes the HMAC from the body + the token the Bearer header resolved to, and compares with constant-time equality.

**What this defends**:

- Casual payload tampering by a user editing JSON in a debugging proxy and replaying.
- An attacker who somehow MITMs a TLS session and tries to mutate the body in flight.
- An attacker who steals only the token (e.g. via a hypothetical XSS on the site) still needs to know the HMAC scheme to forge a valid request — small but non-zero friction.

**What this does NOT defend**:

- The legitimate holder of an API token can sign anything — they have the key. Forging a fake parse for *yourself* is unfixable client-side. Real integrity has to come from server-side sanity checks (DPS-vs-level caps, encounter duration plausibility, cross-validation between multiple plugin uploads of the same encounter).
- A determined user who runs the DLL through `dnSpy`/`ILSpy` can read the signing code; the algorithm is intentionally not a secret.

**Why key = API token, not an embedded shared secret**: no new secret to manage / rotate / leak. The token is already in memory. We get a per-user key as a side effect, which means cross-user replay also doesn't work.

**Rollout coordination with the server**: ship the server's signature validator in *opportunistic* mode first (validate only if header present, reject only if validation fails) so existing v0.1.7 installs keep uploading. Flip to *strict* mode (require the header) once telemetry shows ≥98% of uploads carry it — pulled from the `User-Agent` header which UploadClient sets to `EQ2LexiconACTPlugin/<assembly version>`.

## Required server-side changes (pending in [VortexUK/EQ2Lexicon](https://github.com/VortexUK/EQ2Lexicon))

The plugin already wires UI for two fields that the production server does not yet emit. Until the server PRs land, the plugin falls back to safe defaults (URL field stays locked; allowed-servers card shows a built-in list).

### `is_admin` on `/api/auth/whoami`

Add a boolean `is_admin` to the JSON response. Drives the Server URL field's editability gate in the plugin — only admins can change the endpoint. Fail-CLOSED in the plugin: if the field is missing the URL stays locked, so a non-admin can never see a writable URL field by simply pointing at an old build of the server. Same opportunistic-rollout pattern as the HMAC header in v0.1.8 — additive field, ignored by old clients, used by new ones.

### `allowed_servers` on `/api/auth/whoami`

Add an array of EQ2 server name strings (e.g. `["Varsoon", "Wuoshi"]`) the user is permitted to upload from. Drives the ALLOWED SERVERS card in the settings panel — read-only display so the user knows up-front which characters' parses will reach the site. The list could be a global default, per-guild, or per-user — server's choice; the plugin just renders what it's given. Sanitisation in the plugin caps each entry at 64 chars and the whole array at 32 entries to keep a hostile/buggy server from breaking the UI layout.

When absent, the plugin uses a built-in default of `["Varsoon", "Wuoshi"]` (the two active English-language EQ2 TLE servers as of 2026). When the server starts returning the field, the built-in default is replaced by whatever the site says on the next whoami round-trip.

Future enforcement: the server should *also* validate `logger_server` on incoming uploads against the same list and reject mismatched uploads with a 403. The plugin's display is courtesy/transparency; the real gate is the server.

## Update awareness (v0.1.8+)

The plugin fetches `https://api.github.com/repos/VortexUK/EQ2LexiconACTPlugin/releases` once per ACT session, compares the assembly version to the published tags, and:

- Shows a green/yellow/red banner above the Configuration card in SettingsPanel (`SetUpdateStatus`).
- Blocks uploads when the installed version is more than `UpdateChecker.StaleThresholdVersions` (=2) releases behind. Block lives in `UploadClient.UploadEncounterAsync` — see `UpdateStatus.UploadBlocked`.

Failure modes (offline, GitHub 5xx, rate-limit, malformed JSON) all collapse to `UpdateStatus.Unknown`, which intentionally **fails open** — uploads still proceed. A GitHub incident must not silently brick every user's parse upload.

`Compute()` returns `DevBuild` when the local version is strictly greater than the latest published tag (i.e. you bumped `<Version>` but haven't tagged yet) — UI shows a muted "dev build" label so maintainers don't get nagged about their own un-released version.

No caching of the GitHub response — request volume is at most a handful per user per day, well under the unauthenticated 60/h/IP limit, and re-fetching on every ACT start means a user who just installed an update sees the banner clear instantly after restarting ACT.

## Self-update (v0.1.12+)

The version banner gets a primary "Install update" button (alongside the existing "Download in browser" secondary fallback) when both an asset URL and a SHA-256 digest were resolved from the GitHub release feed. Click flow:

1. **Download** the DLL bytes from `LatestDllUrl` via HttpClient. Capped at 10 MB; refused on any non-2xx response. Bytes held in memory only.
2. **Verify** SHA-256 against `LatestDllSha256` (pulled from the release feed's `digest` field) via constant-time compare. Mismatch → refuse, surface error, don't touch disk. **No digest** → also refuse — shipping unverified code into a running .NET process is unacceptable, the user gets nudged to the browser fallback.
3. **Stage** by writing the bytes to `EQ2Lexicon.ACTPlugin.dll.new` next to the loaded DLL. (Windows allows creating this file; it does NOT allow overwriting the loaded `.dll` itself — that's why we don't just overwrite.)
4. **Drop a recovery readme** (`EQ2Lexicon.ACTPlugin.UPDATE_READ_ME.txt`) in the same folder with manual swap instructions, in case the helper script fails.
5. **Spawn a hidden PowerShell helper**: `Wait-Process -Id <act_pid>; Move-Item -Force *.new *.dll`. Sleeps in the background until ACT exits, then atomic-renames. User sees nothing.
6. **UI message**: "Update staged. Close and reopen ACT to apply — the swap happens automatically." Buttons disable.

User effort: one click + restart ACT whenever convenient. The Railway URL fallback covers users who don't restart for days — the v0.1.12 staging sits there, the v0.1.11 keeps uploading via parses.eq2lexicon.com (same Railway service), no breakage.

**What it doesn't try to do**: relaunch ACT itself. Spawning a "wait then re-launch host" helper from a plugin is the kind of behaviour that gets flagged by AV. The user restart is small friction; the auto-relaunch isn't worth the suspicion budget.

## Language assumptions (English-only EQ2)

The plugin assumes English ACT EQ2-parser output throughout. ACT does have German + French EQ2 parser plugins on record, but as of 2026 there are **no active non-English EQ2 servers** — the EU/JP servers (Storms = French, Valor = German, Sebilis = Japanese) were consolidated into the English-only Thurgadin server in 2016, and the current TLE servers (Varsoon, Kaladim) are English-only. So this assumption is safe today; documenting it so future-you doesn't have to re-research it.

Four spots in the code depend on specific English strings ACT emits:

| Where | English string assumed |
|---|---|
| `src/Core/PayloadBuilder.cs` `OutgoingGroupToSwingType` | `combatant.Items` dict keys: `"Auto-Attack (Out)"`, `"Skill/Ability (Out)"`, `"Healed (Out)"`, `"Cure/Dispel (Out)"`, `"Threat (Out)"` |
| `src/Core/EncounterTitle.cs` `IsPlaceholder` | Placeholder encounter titles `"Encounter"` and `"Unknown"` |
| `src/EncounterCapture.cs` `Poll` | Zone-aggregate pseudo-encounter title `"All"` |
| `src/Core/EncounterZone.cs` `IsImportOrMerge` | Synthetic-zone name `"Import/Merge"` |

**If a non-English EQ2 community ever materialises** (a private server, a Daybreak re-launch, …): the swing-type dict fails *gracefully* — unknown keys just return 0 — so a foreign-language client would still upload but with swings unclassified. The other three are predicates that return safe defaults (don't-skip / not-placeholder / not-import-merge) when their string doesn't match, so the worst outcome is a German user's "Begegnung"-titled fight uploading under that name instead of being deferred. Audit pass at that point: localise each table, ideally pull the canonical strings out of the non-English parser DLL rather than guessing.

## ACT UI extension (v0.1.9+)

ACT has **no documented extension point for its context menu** — plugins reach into the WinForms control tree by name and mutate the existing `ContextMenuStrip`. Reference implementation: [ActStatter](https://github.com/eq2reapp/ActStatter/blob/main/StatterMain.cs). The wiring lives in `src/ActMenuExtension.cs`; the gotchas worth knowing if you ever extend it:

- **Encounter view is a TreeView named `tvDG`** — not `lvEncounters` or anything ListView-shaped. `ActGlobals.oFormActMain.MainTreeView` exists as a public property but is documented "do not enumerate" (nodes populate lazily on expand) — use the `tvDG` lookup instead.
- **Controls don't exist at `InitPlugin` time** — ActMenuExtension uses a one-shot WinForms Timer with a 5s delay. If `tvDG` still isn't found at 5s the menu silently doesn't appear (rare; user can disable+reenable the plugin to retry).
- **Encounter nodes are tagged with the literal string `"EncounterData"`** — `GetSelectedEncounter()` walks up parents until it finds one. Zone nodes have a different Tag, and the "All" pseudo-encounter isn't a tagged tree node, so this filter is sufficient.
- **Resolve via `ActGlobals.oFormActMain.ZoneList[parent.Index].Items[node.Index]`** — returns the live `EncounterData`.
- **`DeInitPlugin` MUST unsubscribe handlers and `Items.Remove` the menu item** — ACT's auto-updater swaps plugin DLLs by disabling+reenabling, and leftover dead delegates accumulate (the menu item appears twice after a reload otherwise).
- **Threading**: WinForms Timer Tick + `ContextMenuStrip.Opening` + Click all fire on the UI thread. No marshalling needed inside ActMenuExtension; the upload work is kicked to a worker by the callback handler in `Plugin.cs`.

### Manual upload gate matrix (`Plugin.OnManualUploadRequested`)

Different from the polling path — bypasses user opt-in gates because the click IS the opt-in:

| Gate | Manual upload | Auto upload |
|---|---|---|
| Blacklist (don't-upload-as) | **bypassed** | enforced |
| "Enable automatic upload" checkbox | **bypassed** | enforced |
| Import/Merge zone (`EncounterZone.IsImportOrMerge`) | enforced (menu greyed + defensive re-check) | enforced (skipped with reason) |
| Pre-plugin-startup encounter | **bypassed** (manual is the escape hatch) | enforced (skipped with reason) |
| Placeholder title (`EncounterTitle.IsPlaceholder`) | enforced (rejects with "rename in ACT first") | deferred up to 60s, then skipped |
| API token configured | enforced | enforced |
| Version not too old | enforced | enforced |
| HMAC signing | applied | applied |

## ACT API gotchas worth remembering

- `ActGlobals.charName` returns the string `"YOU"` for EQ2 — useless. Use `ActHelpers.GetLoggingCharacterName()` instead (parses the log filename).
- `ActGlobals.oFormActMain.LogFilePath` is also the source of the EQ2 server name — EQ2 writes per-character logs into `<install>/logs/<server>/eq2log_<character>.txt`, so the parent directory of the log file is the server (Varsoon, Kaladim, etc.). Use `ActHelpers.GetLoggingServerName()` which wraps the pure `LogPathParser.ParseServerName`. Returns "" on the legacy `logs/eq2log.txt` path — server then falls back to its `EQ2_WORLD` env var.
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
- **Single self-contained DLL** — even though the source is split across UI + Core assemblies, the release is one file. ILRepack merges Core into the UI DLL on Release builds; the release workflow uploads only that merged DLL. Do not switch to a two-file release without updating `release.yml`'s upload list **and** the install-instructions block in its release-notes generator (line ~85) together — v0.1.6 forgot the upload step and broke every fresh install.
- The DLL is unsigned. Windows SmartScreen may warn on first run; users click through. A code-signing certificate would fix this but costs ~$200/yr — not currently worth it.

## CI/CD gaps

Comprehensive pipeline status as of v0.1.5 + the B2.16 sprint:

| Item | Status |
|---|---|
| Pre-push gate (format, build, test, vuln scan) | ✅ `.githooks/pre-push` |
| Pinned SDK | ✅ `global.json` |
| GitHub Actions build/test on PR | ✅ `.github/workflows/ci.yml` (builds Core, UI via extracted ACT, runs tests) |
| Code coverage report | ✅ Coverlet + ReportGenerator, posted to job summary + artifact |
| Vulnerability scanning | ✅ `dotnet list package --vulnerable` in pre-push + CI |
| Dependabot | ✅ `.github/dependabot.yml` |
| Core/UI assembly split (so CI can build without ACT) | ✅ `src/Core/` |
| Single-DLL distribution despite the split | ✅ ILRepack merges Core into the UI DLL on Release builds (`ILRepackMergeCore` target) |
| Auto-release on `v*` tag push | ✅ `.github/workflows/release.yml` — builds UI DLL on the runner via extracted ACT, drafts release with notes + DLL attached, maintainer just publishes |
| Authenticode-signed DLL | ❌ Skipped — ~$200/yr cert not worth it; users click through SmartScreen |
| Changelog automation | ❌ Skipped — release notes hand-written per release |
