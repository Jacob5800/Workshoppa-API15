# Workshoppa API 15 compatibility build

This is a local compatibility copy of Workshoppa based on VeraNala/Workshoppa
commit `b90245c` and updated for Dalamud API 15 / FFXIV Patch 7.5.

Changes:

- Bumped the plugin and helper library to Dalamud API 15.
- Updated the target framework and build pipeline to .NET 10.
- Set the local plugin version to 9.3.

Build from the repository root with the .NET 10 SDK and Dalamud's API 15
assemblies available to the build pipeline. The release pipeline sets
`DalamudLibPath` from the `download-dalamud` step.
