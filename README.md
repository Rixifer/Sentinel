# Sentinel

An enhanced AoE indicator system for FFXIV. Sentinel improves combat readability by recoloring native ground indicators with a cast-progress gradient and spawning indicators for attacks the game doesn't telegraph.

## Installation

1. Open FFXIV with [XIVLauncher](https://goatcorp.github.io/) and Dalamud enabled
2. Type `/xlsettings` in game chat
3. Go to the **Experimental** tab
4. Under **Custom Plugin Repositories**, paste the following URL into an empty box:
   ```
   https://raw.githubusercontent.com/Rixifer/Sentinel/main/pluginmaster.json
   ```
5. Click the **+** button, then **Save and Close**
6. Open the plugin installer with `/xlplugins`
7. Find **Sentinel** in the list and click **Install**

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
- Multi-target AoE support (boss fires multiple AoEs → all get gradients)
- Automatic VFX lifecycle management

## Usage

- `/sentinel` — open settings
- `/sentinel on` / `/sentinel off` — enable/disable
- `/sentinel debug` — open debug overlay

## Configuration

Open settings with `/sentinel` to configure:
- **General** — detection range, AoE indicator style
- **Colors** — start/end gradient colors, accessibility presets, opacity
- **Shapes** — shape type filters (coming soon)

## Building from Source

### Requirements
- .NET 10 SDK
- Dalamud dev environment

### Build
```bash
cd Sentinel
dotnet build -c Release
```

## Credits

- AoE shape data extracted from [BossModReborn](https://github.com/FFXIV-CombatReborn/BossmodReborn)
- VFX system research informed by [Pictomancy](https://github.com/sourpuh/ffxiv_pictomancy), [GoodOmen](https://github.com/sourpuh/GoodOmen), and [Splatoon](https://github.com/PunishXIV/Splatoon)
- Built on [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs)
