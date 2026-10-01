# Changelog

Notable changes to this repository, newest first. All SDK repositories share one version
number per release; see [Compatibility](https://github.com/Salacious-Oni-Dev/oni-sdk-docs/blob/main/guides/compatibility.md).

## 0.1.0-alpha.1 (2026-10-01)

First public release.

- The `OniFramework` mod, API level 0.1. It adds no gameplay; each surface is switched on by
  the mod that uses it.
- Carries the SDK's simulation library and, if the player accepts when the main menu asks,
  puts it in place each time the game starts and puts the game's own back at quit. It checks
  the game build and the library's SHA-256 first, and leaves alone a library installed with
  the release zip's installer or by hand.
- Replaces the game's build watermark with one line naming the simulation library in use (the
  first characters of its file's SHA-256, and its version), the framework's version and the
  game build. A launch with `--stamp-release=<label>` shows that label in place of the
  versions and the hash, for recordings.
- `FrameworkVersion` and `SimVersion`, so a mod can check the API level it needs and whether
  the replacement simulation library is installed.
- Surfaces for gas mixtures, material properties, pipes, heat that is moved rather than
  deleted, dissolved gas and bubbles, the extension registries, the simulation's own tables
  (the frame's phases, the extension message table, the grid raycast, the frame profile and
  the energy ledger), checkpoints and
  building colour. The README lists the types in each area.
- `SimTunables`: read and set the simulation's tunable numbers, and a `sim-tunables.json` in
  the framework's folder to set them at load.
- `SimTunable.CellEnergyCarry`, the simulation's rounding carry for `ModifyCellEnergy`. Off
  unless a mod sets it.
- `EnergyLedgerFacade.Snapshot.CellEnergyCarriedKJ`, the energy the carries hold, and
  `HasCarryBucket`, which is false against a simulation library that does not publish it.
  `CellEnergyMsgKJ` is still what reached the grid, so a payment splits into that,
  `CellEnergyRefusedKJ` and the change in `CellEnergyCarriedKJ`.
- Keeps the mod load order working: when a mod that uses the framework is listed above it,
  the framework moves itself above that mod, explains it in a message, and restarts the game
  once.
- Test and demonstration tooling for mod authors: `RigHarness`, `BlueprintWorld`,
  `DemoNarrator`, `SimCorpus`, `SimExtSelfTest` and others.
- `DebugInspectorServer`, an HTTP debug server for development, which oni-sim-visualizer's
  `--attach` reads. Nothing starts it unless a mod calls it. It listens on every network
  interface with no authentication, so a mod should start it only behind an explicit opt-in,
  such as Mod 1's `--oni-debug-inspector` launch option.
- `build.ps1` builds the framework on Windows in PowerShell, without WSL or bash.
