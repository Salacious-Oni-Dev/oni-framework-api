# oni-framework-api

The managed API of the Oxygen Not Included simulation SDK. Mods call it instead of the
simulation library directly.

It ships as its own mod, `OniFramework`, containing one assembly, `OniFramework.dll`, and, in
`native/`, the SDK's simulation library. It adds no gameplay, and every surface that changes game
behaviour is switched on by a call from the mod that wants it. By itself it does three things:
with the player's consent it puts the SDK library in place for each session, it keeps the mod
load order working (both under Installing), and it replaces the game's build watermark with a
line naming the simulation library, API and game build in use, so a screenshot shows what ran.

This is one part of the SDK. The replacement simulation library is in
[oni-sim-replacement](https://github.com/Salacious-Oni-Dev/oni-sim-replacement), the example mods built on this
API are in [oni-flagship-mods](https://github.com/Salacious-Oni-Dev/oni-flagship-mods), and the guides that
cover the SDK as a whole are in [oni-sdk-docs](https://github.com/Salacious-Oni-Dev/oni-sdk-docs).

**Status: alpha.** Interfaces can still change between releases.

## What it provides

Most surfaces need the SDK's replacement `SimDLL.dll`. On the game's own library,
`SimVersion.IsCustom` is false. Calls that only send the simulation a setting are ignored there.
Calls that must return a physical quantity throw, because no value would be safe to return.
Check `SimVersion.IsCustom` before relying on the replacement.

| area | types |
|---|---|
| versions and diagnostics | [`FrameworkVersion`](OniFramework/FrameworkVersion.cs), [`SimVersion`](OniFramework/SimVersion.cs), [`FrameworkLog`](OniFramework/FrameworkLog.cs), [`BuildStamp`](OniFramework/BuildStamp.cs) |
| gas mixtures | [`GasMixtureFacade`](OniFramework/GasMixtureFacade.cs), [`AtmosphereFacade`](OniFramework/AtmosphereFacade.cs), [`SimRooms`](OniFramework/SimRooms.cs), [`SimGasSleeping`](OniFramework/SimGasSleeping.cs) |
| material properties | [`MaterialProperties`](OniFramework/MaterialProperties.cs), [`MaterialPropertyRegistry`](OniFramework/MaterialProperties.cs#L287), [`ElementRegistry`](OniFramework/ElementRegistry.cs) |
| pipes | [`PipeNetworkFacade`](OniFramework/PipeNetworkFacade.cs), [`PipeMatterFacade`](OniFramework/PipeMatterFacade.cs), [`ConduitNetworks`](OniFramework/ConduitNetworks.cs), [`SimConduitNetworks`](OniFramework/SimConduitNetworks.cs), [`ConduitBackpressure`](OniFramework/ConduitBackpressure.cs), [`PipeHeatExchange`](OniFramework/PipeHeatExchange.cs) |
| heat that is moved, not deleted | [`PowerHeat`](OniFramework/PowerHeat.cs), [`ExhaustHeat`](OniFramework/ExhaustHeat.cs), [`TurbineHeat`](OniFramework/TurbineHeat.cs), [`ColdBreatherHeat`](OniFramework/ColdBreatherHeat.cs), [`GeneratorEnthalpy`](OniFramework/GeneratorEnthalpy.cs), [`ConversionEnthalpy`](OniFramework/ConversionEnthalpy.cs), [`Convection`](OniFramework/Convection.cs), [`Radiation`](OniFramework/Radiation.cs), [`ThermalMassBonus`](OniFramework/ThermalMassBonus.cs), [`ElementChunkFacade`](OniFramework/ElementChunkFacade.cs), [`PhaseVessel`](OniFramework/PhaseVessel.cs), [`StirlingCycleRule`](OniFramework/StirlingCycleRule.cs) |
| dissolved gas and bubbles | [`DissolvedGas`](OniFramework/DissolvedGas.cs), [`DissolvedCargo`](OniFramework/DissolvedCargo.cs), [`Solubility`](OniFramework/Solubility.cs), [`Effervescence`](OniFramework/Effervescence.cs), [`Bubbles`](OniFramework/Bubbles.cs), [`LiquidPayload`](OniFramework/LiquidPayload.cs), [`LiquidTint`](OniFramework/LiquidTint.cs) |
| extension registries | [`SimExtCellProperties`](OniFramework/SimExtCellProperties.cs), [`SimExtElementAttributes`](OniFramework/SimExtElementAttributes.cs), [`SimExtEventStreams`](OniFramework/SimExtEventStreams.cs), [`SimFields`](OniFramework/SimFields.cs), [`SimExtRegistrar`](OniFramework/SimExtRegistrar.cs), [`SimExtFrame`](OniFramework/SimExtFrame.cs) |
| the simulation's own tables | [`SimExtPhases`](OniFramework/SimExtPhases.cs), [`SimExtMessages`](OniFramework/SimExtMessages.cs), [`SimRaycast`](OniFramework/SimRaycast.cs), [`SimProfile`](OniFramework/SimProfile.cs), [`EnergyLedgerFacade`](OniFramework/EnergyLedgerFacade.cs) |
| simulation tunables | [`SimTunables`](OniFramework/SimTunables.cs), [`SimTunable`](OniFramework/SimTunable.cs) (and `sim-tunables.json` in the framework's folder) |
| checkpoints | [`SimExtCellState`](OniFramework/SimExtCellState.cs), [`SimRandom`](OniFramework/SimRandom.cs), [`SimScheduling`](OniFramework/SimScheduling.cs), [`SimStableTicks`](OniFramework/SimStableTicks.cs), [`SimDiseaseGrowth`](OniFramework/SimDiseaseGrowth.cs), [`SimCellRadiation`](OniFramework/SimCellRadiation.cs), [`SimVisibilityState`](OniFramework/SimVisibilityState.cs), [`SimRegistryState`](OniFramework/SimRegistryState.cs) |
| building colour | [`BuildingPaint`](OniFramework/BuildingPaint.cs), [`BuildingPaintAll`](OniFramework/BuildingPaintAll.cs), [`PaintableBuilding`](OniFramework/PaintableBuilding.cs), [`BuildingPaintSideScreen`](OniFramework/BuildingPaintSideScreen.cs) |
| test and demonstration tooling | [`RigHarness`](OniFramework/RigHarness.cs), [`RigBlueprint`](OniFramework/RigBlueprint.cs), [`BlueprintWorld`](OniFramework/BlueprintWorld.cs), [`BlueprintBuilder`](OniFramework/BlueprintBuilder.cs), [`DemoRig`](OniFramework/DemoRig.cs), [`DemoNarrator`](OniFramework/DemoNarrator.cs), [`RigDiagnostics`](OniFramework/RigDiagnostics.cs), [`SimCorpus`](OniFramework/SimCorpus.cs), [`SimExtSelfTest`](OniFramework/SimExtSelfTest.cs), [`DebugInspectorServer`](OniFramework/DebugInspectorServer.cs), [`DebugBulkRoutes`](OniFramework/DebugBulkRoutes.cs) |

Each type's XML documentation is its reference. The simulation behaviour behind each surface is
documented in [`oni-sim-replacement/docs`](https://github.com/Salacious-Oni-Dev/oni-sim-replacement/tree/main/docs).

`DebugInspectorServer` is development tooling. It listens on all network interfaces, with no
authentication, so that a WSL guest can reach a game on its Windows host. Start it only behind
an explicit opt-in, as Mod 1 does with the `--oni-debug-inspector` launch option, never by
default.

## Using it from a mod

Reference `OniFramework.dll` with Copy Local off, and never ship a copy of it with your mod:

```xml
<Reference Include="OniFramework">
  <HintPath>path\to\OniFramework.dll</HintPath>
  <Private>false</Private>
</Reference>
```

Mono loads one assembly per simple name. If two mods each ship a copy, whichever loads first is
the one every mod uses, and a mod built against the other copy fails with a
`TypeLoadException`.

Declare the API level you need in `OnLoad`:

```csharp
public override void OnLoad(Harmony harmony)
{
    base.OnLoad(harmony);
    if (!OniFramework.FrameworkVersion.Require(0, 1, "MyMod"))
        return;
    // ...
}
```

`Require` logs and returns false when the loaded framework is too old or has another major
version. It never throws. It also reports a second copy of the framework if one is loaded.

The SDK guide [Writing a mod](https://github.com/Salacious-Oni-Dev/oni-sdk-docs/blob/main/guides/mod-authors.md)
walks through this in order.

## Building

Requirements:
- the .NET SDK, version 6 or later
- bash
- an installed copy of the game

```sh
ONI_GAME=/path/to/OxygenNotIncluded ./build.sh
```

`ONI_GAME` is the game's folder, the one that contains `OxygenNotIncluded_Data`. The build
references the game's managed assemblies from there and never copies them into this repository.
The output is `OniFramework/bin/Release/`, holding `OniFramework.dll`, `mod.yaml` and
`mod_info.yaml`. When a built simulation library is found, in `../oni-sim-replacement` or the
repository `ONI_SIM_REPO` names, the build also fills `native/` with it: `SimDLL.dll`, its
`SHA256SUMS`, `VERSION`, and the installer's `supported-builds.txt`. Without one the framework
builds as before and never touches the game's library.

### Building on Windows

`build.ps1` does what `build.sh` does, in Windows PowerShell, without WSL or bash: it stamps the
version, builds `OniFramework\bin\Release\OniFramework.dll`, and fills `native\` with the SDK
simulation library from a built `oni-sim-replacement` beside this repository.

#### Tools

Install these once, from PowerShell, then open a new PowerShell window so they are on `PATH`:

```powershell
winget install Microsoft.DotNet.SDK.8
winget install Git.Git                            # optional: puts the commit in the version stamp
```

To bundle the simulation library, build `oni-sim-replacement` first; its README lists the
compiler and Python it needs. Windows does not run PowerShell scripts by default: run
`Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` once, or use
`powershell -ExecutionPolicy Bypass -File` as below.

#### Build

Clone both repositories into the same folder:

```powershell
git clone https://github.com/Salacious-Oni-Dev/oni-sim-replacement.git
git clone https://github.com/Salacious-Oni-Dev/oni-framework-api.git
$env:ONI_GAME = "C:\Program Files (x86)\Steam\steamapps\common\OxygenNotIncluded"
powershell -ExecutionPolicy Bypass -File oni-sim-replacement\sim\build.ps1
powershell -ExecutionPolicy Bypass -File oni-framework-api\build.ps1
```

`ONI_GAME` (or `-OniGame "<folder>"`) is the folder that contains `OxygenNotIncluded_Data`. Set
`$env:ONI_SIM_REPO` if `oni-sim-replacement` is somewhere else. Without a built library the
framework still builds, with a warning, and carries no `native\` folder.

A `SimDLL.dll` built with `build.ps1` is a development build (see the Windows section of
`oni-sim-replacement`'s README); releases bundle one built with `sim/build.sh`.

## Installing

From a [release](https://github.com/Salacious-Oni-Dev/oni-framework-api/releases), unpack
`OniFramework-<version>.zip` and copy its `OniFramework` folder into the game's local mods folder.
From a build, copy the contents of `OniFramework/bin/Release/` into
`Documents/Klei/OxygenNotIncluded/mods/Local/OniFramework/`. Either way, enable **OniFramework**
in the game's Mods screen.

The game loads mods in the order the Mods screen lists them, and a mod that uses OniFramework
cannot load if it comes first. Keep **OniFramework** above every mod that uses it; drag it up
the list if needed. A newly installed mod goes to the bottom, so installing the framework
together with mods that use it usually lists it below them. When the framework finds a mod that
uses it listed above it, it moves itself above that mod, shows a message, and restarts the game
once. After the restart a dialog on the main menu names the mods it moved.

**The simulation library.** When the framework carries one in `native/`, the first start shows a
dialog on the main menu that explains what it does and asks whether to use it. If the player
accepts, then at each start, before any world loads, the framework checks the game's own
`SimDLL.dll` against `supported-builds.txt`, renames it to `SimDLL.dll.vanilla`, copies the SDK
library in and checks its SHA-256, and writes `SimDLL.dll.oniframework` recording what it put
there. At quit it puts the game's own back. The answer is kept in `OniFramework-simdll.txt` in the
game's mods folder; deleting it asks again. The framework leaves the library alone on a game
build it does not support, when the release zip's installer or a manual copy installed it, and
off Windows. The guide to installing and removing the SDK is in `oni-sdk-docs`.

## Version

`VERSION` holds the API level as `MAJOR.MINOR`. MINOR goes up when the API gains a surface, and
MAJOR goes up when an existing one changes shape. `build.sh` appends the revision and commit.
That build version is not the SDK release; the release is the repository's tag.

## License

MIT, see `LICENSE`.

## Credits

Oxygen Not Included is developed and published by Klei Entertainment. This project is not
affiliated with or endorsed by Klei.

Development of this project uses AI coding assistants. All changes are reviewed and released
by the maintainer.
