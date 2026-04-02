# Sentinel

An enhanced AoE indicator system for FFXIV. Sentinel improves combat readability by recoloring native ground indicators with a cast-progress gradient and spawning indicators for attacks the game doesn't telegraph.

## Features

### Enhanced Omen Recoloring
- Native game AoE indicators are recolored with a cast-progress gradient (yellow → red by default)
- Color transitions from start color to end color as the cast progresses, providing clear visual urgency
- Configurable start/end colors with accessibility presets (Deuteranopia, Protanopia, Tritanopia)
- Adjustable opacity

### AoE Indicator Style
- **Default** — uses whichever indicator the game assigns
- **Force Enhanced (Dawntrail)** — upgrades all indicators to the Dawntrail-era style with vertical light wall borders
- **Force Legacy** — reverts to the original flat ground indicators

### Untelegraphed Mechanic Detection
- Spawns native-style ground indicators for enemy attacks the game doesn't telegraph
- 3,100+ action→shape mappings sourced from BossModReborn's curated boss module data
- Lumina CastType fallback for actions not in the curated database
- 1,500+ excluded action IDs to prevent false indicators on stack markers, raidwides, towers, and gazes

### Detection System
- Network-level cast detection via packet interception (catches casts instantly)
- CreateOmen hook tracking (catches every native omen the game creates)
- Multi-target AoE support (boss fires 5 rockets → all 5 get gradients)
- Automatic VFX lifecycle management

## Installation

### Requirements
- FFXIV with [XIVLauncher](https://goatcorp.github.io/) and Dalamud
- .NET 10 SDK (for building from source)

### Building
```bash
cd Sentinel
dotnet build -c Release
```

### Installing
Copy the build output to your Dalamud dev plugins folder:
```
%appdata%\XIVLauncher\devPlugins\Sentinel\
```

Make sure `Data\BmrShapes.json` and `Data\ExcludedActions.json` are included in the copy.

## Usage

- `/sentinel` — open settings
- `/sentinel on` / `/sentinel off` — enable/disable
- `/sentinel debug` — open debug overlay

## Configuration

Open settings with `/sentinel` to configure:
- **General** — detection range, AoE indicator style
- **Colors** — start/end gradient colors, presets, opacity
- **Shapes** — shape type filters (coming soon)

## Credits

- AoE shape data extracted from [BossModReborn](https://github.com/FFXIV-CombatReborn/BossmodReborn)
- VFX system research informed by [Pictomancy](https://github.com/sourpuh/ffxiv_pictomancy), [GoodOmen](https://github.com/sourpuh/GoodOmen), and [Splatoon](https://github.com/PunishXIV/Splatoon)
- Built on [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs)
