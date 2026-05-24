# EQ2 Lexicon — ACT Plugin

An [Advanced Combat Tracker](https://advancedcombattracker.com) plugin that uploads parsed EverQuest 2 encounters to the [EQ2 Lexicon](https://github.com/VortexUK/EQ2CensusBot) website after each fight ends.

> v0.1.0 — scaffold only. The end-of-combat hook, settings UI, and HTTP upload land in subsequent commits.

## How it works (when complete)

1. You configure the plugin once with an API token minted from the EQ2 Lexicon website (`/settings/tokens`)
2. ACT continues to parse your EQ2 combat log as normal
3. Each time an encounter ends, the plugin builds an ACT-shaped JSON payload of the encounter / combatants / damage / heal / cure / threat data and `POST`s it to `/api/parses/ingest` with your bearer token
4. The website resolves your guild via the Census API, stores the parse, and surfaces it on `/parses` for any signed-in user to browse

Your EQ2 character name is read from ACT directly (`ActGlobals.charName`) so the website knows whose parse it is without you configuring anything beyond the token.

## Building

### Prerequisites

- **.NET Framework 4.8 Developer Pack** — [download from Microsoft](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48) (pick "Developer Pack", not "Runtime"). ~80 MB single installer.
- **`dotnet` CLI** (.NET SDK 6 or newer). Check with `dotnet --version`. If missing, install the [latest .NET SDK](https://dotnet.microsoft.com/en-us/download).
- **Advanced Combat Tracker** installed locally — the build references `Advanced Combat Tracker.exe` as a .NET assembly. Default expected location is `C:\Program Files (x86)\Advanced Combat Tracker\`. If yours is elsewhere, set the `ACT_INSTALL_DIR` environment variable.

### Build the DLL

From the repo root:

```powershell
dotnet build src\EQ2Lexicon.ACTPlugin.csproj -c Release
```

The output DLL lands at `src\bin\Release\net48\EQ2Lexicon.ACTPlugin.dll`.

### Install into ACT

Copy the DLL into ACT's plugins directory:

```powershell
Copy-Item src\bin\Release\net48\EQ2Lexicon.ACTPlugin.dll `
  -Destination "$env:APPDATA\Advanced Combat Tracker\Plugins\"
```

Restart ACT. In **Plugins → Plugin Listing**, the "EQ2 Lexicon Uploader" should appear with a green check. Click the new **EQ2 Lexicon** tab to confirm it's alive.

## Project layout

```
EQ2Lexicon.ACTPlugin.sln               # solution file for Visual Studio users
src/
  EQ2Lexicon.ACTPlugin.csproj          # net48 class library, references ACT.exe
  Plugin.cs                            # IActPluginV1 entry point
  SettingsPanel.cs                     # dark-themed settings UI
  EncounterCapture.cs                  # 2 s polling + ACT → snapshot adapter
  PayloadBuilder.cs                    # pure snapshot → ingest-JSON transform
  Snapshots.cs                         # DTOs decoupling payload logic from ACT
  PluginConfig.cs                      # XML-serialised user settings
  UploadClient.cs                      # HttpClient wrapper + JSON helpers
  ActHelpers.cs                        # ActGlobals shims (logger char name)
tests/
  EQ2Lexicon.ACTPlugin.Tests/          # xUnit, targets net48
.editorconfig                          # source of truth for dotnet format
.githooks/pre-push                     # format + build + test on every push
```

## Running checks locally

The pre-push hook runs three checks. **Activate it once after cloning:**

```powershell
git config core.hooksPath .githooks
```

After that, every `git push` runs:

| Step | Command |
|------|---------|
| Format check | `dotnet format EQ2Lexicon.ACTPlugin.sln --verify-no-changes` |
| Build (acts as type-check) | `dotnet build EQ2Lexicon.ACTPlugin.sln -c Release` |
| Tests | `dotnet test EQ2Lexicon.ACTPlugin.sln --no-build -c Release` |

To fix formatting issues:

```powershell
dotnet format EQ2Lexicon.ACTPlugin.sln
```

To run the tests on their own:

```powershell
dotnet test
```

The test suite covers `PayloadBuilder` (encounter/combatant/damage-type/attack-type shape, NaN sanitiser, UTC formatter), `PluginConfig` (defaults, IsBlacklisted, XML round-trip), and `UploadClient`'s JSON / truncate helpers.

The ACT-coupled bits (`EncounterCapture.CaptureSnapshot`, `SettingsPanel`, `Plugin`) aren't unit-tested — they're integration-tested by running the DLL in ACT.

## License

TBD.
