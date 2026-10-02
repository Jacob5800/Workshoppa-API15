# Workshoppa API 15

Workshoppa version 9.3.4.0, built for Dalamud API 15. This continuation includes a retainer depositor with item exclusions and automatically skips the short cutscenes between workshop production phases when the game exposes its skip prompt.

## Credit and permission

Original plugin by Liza Carvelli.

## Install

1. Open Dalamud Settings and go to Experimental.
2. Add this custom repository URL: https://raw.githubusercontent.com/Jacob5800/Workshoppa-API15/main/repo.json
3. Open the Plugin Installer, find Workshoppa, and install it.

## Commands

- /ws opens the plugin UI.
- /buy-tanks buys a requested number of ceruleum tank stacks.
- /fill-tanks fills inventory with a requested number of ceruleum tank stacks.

## Build from source

Open `Workshoppa.sln` with the Dalamud API 15 development SDK installed, or build `Workshoppa/Workshoppa.csproj` in Release configuration. The install package is published at the repository root and referenced by `repo.json`.

The workshop transition cutscene skip is armed only after choosing an advance or completion option. It opens and confirms the game's own skip dialog while that transition is pending.
