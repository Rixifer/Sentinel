# Sentinel — FFXIV Dalamud Plugin: Complete Project Context
## For continuing development in a new conversation

---

## What Sentinel Does
Sentinel is a Dalamud plugin for FFXIV that enhances AoE mechanic indicators. It has two capabilities:
1. **Hijacks native game omens** — recolors them per-frame with a cast-progress gradient (yellow→red by default), with optional Dawntrail light wall enforcement
2. **Spawns custom omens for untelegraphed mechanics** — creates native-style ground-projected VFX for attacks the game doesn't telegraph, using 2,604 action→shape mappings extracted from BossModReborn

## Current State: Phase 2 Complete
- Per-frame gradient recoloring via UpdateVfxColor (game function)
- Dawntrail light wall enforcement (er_ omen remap, 44 mappings)
- Indicator style: Default / Force Enhanced / Force Legacy
- Custom omen spawning for untelegraphed mechanics (2,604 BMR actions)
- InterframeResourceTracker pattern for automatic VFX lifecycle
- Managed IBattleChara API for crash-safe cast detection
- Opacity slider, accessibility presets (Deuteranopia, Protanopia, Tritanopia)
- Config version 9

## Repository
`C:\Github\Sentinel-Dalamud\Sentinel\`

## Key Source Files
| File | Purpose |
|------|---------|
| `Plugin.cs` | Entry point. Wires all components. Framework.Update → Scan + Tracker.Update |
| `Configuration.cs` | Version 9. OmenStyle enum, colors, opacity, shape toggles |
| `Core/OmenManager.cs` | CreateOmen hook (light wall remap + initial color). RecolorInstance (UpdateVfxColor). ComputeProgressColor |
| `Core/CastScanner.cs` | Managed IBattleChara cast detection. Native omen recolor + custom omen spawning |
| `Core/CustomOmenSpawner.cs` | Converts BmrShapeEntry → ShapeDefinition → OmenPathDecoder → OmenVfx spawn |
| `Core/VfxFunctions.cs` | 6 game function pointers (CreateVfx, DestroyVfx, UpdateVfxColor, UpdateVfxTransform, RotateMatrix, VfxInitDataCtor) |
| `Core/OmenVfx.cs` | Single VFX handle with 16-byte aligned matrix, Create/UpdateTransform/UpdateColor/Dispose |
| `Core/OmenVfxTracker.cs` | Double-buffer lifecycle management |
| `Core/OmenPathDecoder.cs` | Shape→VFX path mapper (16 cone angles, 36 donut ratios) |
| `Core/OmenSheetReader.cs` | Omen excel sheet (735 entries), enhanced remap (44 pairs), reverse remap |
| `Core/ShapeDefinition.cs` | ShapeType enum, ShapeDefinition record, ActiveCast record |
| `Core/AoEResolver.cs` | Lumina CastType→shape fallback (not used in hot path, kept for future) |
| `Data/BmrShapeLoader.cs` | Loads BmrShapes.json into Dictionary<uint, BmrShapeEntry> |
| `Data/BmrShapes.json` | 2,604 action→shape mappings from BossModReborn |
| `Data/Overrides.cs` | ~1,101 legacy overrides (not used in current hot path) |
| `Structs/VfxOmenData.cs` | VfxOmenData (0x1B8→Instance), VfxOmenResourceInstance (0xA0→Color) |
| `UI/ConfigWindow.cs` | Settings: indicator style, colors, presets, opacity, shapes (coming soon) |
| `UI/DebugWindow.cs` | Active casts table with YES/CUSTOM/NO omen status |

## Architecture
```
OnFrameworkUpdate:
  CastScanner.Scan():
    foreach entity in ObjectTable:
      managed IBattleChara → IsCasting, CastActionId, progress
      
      if VfxContainer[6] non-null:
        OmenManager.RecolorInstance(instancePtr, progress)  → UpdateVfxColor
        hasOmen = true
      
      elif omenId == 0 (untelegraphed):
        BmrShapeLoader.GetShape(actionId)
        CustomOmenSpawner.SpawnOrUpdate() → OmenPathDecoder → OmenVfx.Create/Update
        hasOmen = true
  
  OmenVfxTracker.Update()  → destroy VFX for ended casts

CreateOmenDetour (hook):
  Remap a1 for indicator style (ForceNew/ForceOld)
  Write Vector4.One to Instance->Color at creation
```

## Key Struct Offsets
```
BattleChara + 0x19D0 → VfxContainer._vfxData[6] (omen VFX pointer)
BattleChara + 0x231C → Character.CastRotation
BattleChara + 0x27B0 → CastInfo.TargetLocation (Vector3)
VfxData + 0x1B8 → VfxResourceInstance*
VfxResourceInstance + 0x0A0 → Vector4 Color
```

## Game Function Signatures
```
CreateOmen:         E8 ?? ?? ?? ?? 48 89 84 FB ?? ?? ?? ?? 48 85 C0 74 53
CreateVfx:          E8 ?? ?? ?? ?? 48 8B D8 48 8D 95
DestroyVfx:         E8 ?? ?? ?? ?? 4D 89 A4 DE ?? ?? ?? ??
UpdateVfxColor:     E8 ?? ?? ?? ?? 8B 4B F3
UpdateVfxTransform: E8 ?? ?? ?? ?? EB 19 48 8B 0B
RotateMatrix:       E8 ?? ?? ?? ?? 4C 8D 76 20
VfxInitDataCtor:    E8 ?? ?? ?? ?? 8D 57 06 48 8D 4C 24 ??
```

## Phase 3 Roadmap
- Pixel Perfect hitbox display
- Action name labels (floating text above AoE)
- Hit detection exclamation mark (player standing in AoE)
- Per-duty color profiles
- Fade-out animation on cast completion
- Light wall controls (height, separate color)

## Reference Documents
- `Research/SYNTHESIS.md` — Unified VFX system reference
- `Research/PICTOMANCY_VFX.md` — All game function sigs and VFX lifecycle
- `Research/CLIENTSTRUCTS_VFX.md` — Struct layouts and pointer chains
- `Research/GOODOMEN_ANALYSIS.md` — CreateOmen hook mechanics
- `Research/SPLATOON_RENDERING.md` — Alternative rendering approaches
- `Research/BMR_AOE_DATA.md` — Shape data extraction strategy
- `ROADMAP.md` — Full feature roadmap

## Build & Deploy
```bash
cd C:\Github\Sentinel-Dalamud\Sentinel
dotnet build -c Release
# Copy to: %appdata%\XIVLauncher\devPlugins\Sentinel\
# Include Data\BmrShapes.json in the copy
```
