<div align="center">

English • [Русский](CONTRIBUTING_ru.md)
  
</div>
# Contributing to DeadCellsCoopPlus

Thank you for your interest in improving DeadCellsCoopPlus. This document describes how to build the project and what we expect from contributions.

## Prerequisites

- **Windows** (the project targets `net10.0` with `SupportedOSPlatform` Windows).
- **.NET SDK** compatible with **.NET 10** (see `TargetFramework` in `DeadCellsMultiplayerMod.csproj`).
- **Dead Cells** with **DCCM (Dead Cells Core Modding API)** installed for local testing.
- Optional: **`DCCM_MDK_ROOT`** environment variable pointing at your DCCM MDK/tools folder if you need Steamworks references (`Steamworks.NET`, `steam_api64.dll`) resolved via the paths in the project file.

## Build

From the repository root:

```powershell
dotnet build -c Release
```

Output artifacts are produced under `bin/Release/net10.0/` (and the packaged mod layout as configured by the DCCM MDK targets).

For iterative development with automatic install into your DCCM layout, use **Debug** configuration (`AutoInstallMod` is enabled for Debug in the csproj).

## Project layout (high level)

- `ModEntry.cs` — mod entry point and lifecycle.
- `Mobs/` — mob synchronization, wire codecs, tracing.
- `Ghost/` — remote player ghost and related hooks.
- `UI/` — in-game UI.
- `Resourcefile/lang/` — localization (`.po` / `.pot`).
- `server/` — networking (`NetNode`, wire protocol).

## How to contribute
1. **Open an issue** or discuss a **small, scoped** change before large refactors.
2. **Fork** the repository and create a **branch** focused on one feature or fix.
3. **Keep pull requests focused** — avoid unrelated formatting, renames, or drive-by cleanups in files you are not changing for the task.
4. **Match existing style** — naming, patterns, and comment density should match surrounding code.
5. **Build** in Release before submitting; fix any new compiler warnings relevant to your change.
6. **Describe** what changed and **why** in the PR description (plain language, complete sentences).

## Code review expectations

- Changes should be **minimal** and **directly related** to the stated goal.
- Do **not** delete unrelated comments or rewrite large sections without need.
- Prefer **one clear code path** over many special cases when possible.
- New user-facing strings belong in **localization** (`Resourcefile/lang/`) when applicable.

## Testing

There is no automated test suite in this repository. For gameplay changes:

- Run the game **through DCCM** with the mod loaded.
- For multiplayer, verify **host** and **client** behavior when you touch sync or networking code.

## Questions

For DCCM installation and API documentation, see the official DCCM docs and GitHub:

- [DCCM install (Steam Workshop)](https://dead-cells-core-modding.github.io/docs/docs/tutorial/install-workshop/)
- [dead-cells-core-modding/core](https://github.com/dead-cells-core-modding/core)
