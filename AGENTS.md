# Workshoppa update workflow

- When making a Workshoppa update intended for use in Dalamud, update both the repository release package and the local dev plugin. Updating source files alone does not update either installation.
- Bump `Workshoppa/Workshoppa.csproj` `<Version>` and keep `repo.json` `AssemblyVersion`, download-link zip names, `LastUpdate`, the root release zip, and the README version in sync.
- Build Release, create the versioned root zip from the resulting `Workshoppa/dist` package, then inspect `%APPDATA%/XIVLauncher/dalamudConfig.json` `DevPluginLoadLocations` and sync the active, enabled Workshoppa DLL path. The current entry is `%APPDATA%/XIVLauncher/devPlugins/Workshoppa-9.3-API15/Workshoppa.dll`; a path under the `devPlugins` folder is active here because it is explicitly registered, not because Dalamud scans that legacy folder automatically. Verify the copied files match.
- If the user wants Dalamud to offer an update, ensure the manifest and zip are published on the configured GitHub `main` branch; local manifest changes alone cannot create an update notice. Treat a direct request to make the update show in Dalamud as authorization to publish that update.
