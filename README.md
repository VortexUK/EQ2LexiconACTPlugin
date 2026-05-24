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
EQ2Lexicon.ACTPlugin.sln       # solution file for Visual Studio users
src/
  EQ2Lexicon.ACTPlugin.csproj  # net48 class library, references ACT.exe
  Plugin.cs                    # IActPluginV1 entry point (scaffold today,
                               #   gets the encounter hook + UI in later commits)
```

## License

TBD.
