# Sentinel — Dalamud Plugin for Ground-Projected AoE Visualization

## Overview

Sentinel is a Dalamud plugin that draws AoE mechanic indicators directly on the game ground as filled, world-projected shapes. It complements BossModReborn (which draws on a 2D radar minimap) by rendering AoE zones in the actual 3D game world — matching or improving on what FFXIV's native telegraphs show, while also revealing untelegraphed/invisible AoEs.

## Why This Exists

BossModReborn is excellent but draws AoEs only on a radar minimap — you must look away from the game world to see them. Native FFXIV telegraphs appear on the ground but are often delayed, missing for untelegraphed attacks, or unclear in chaotic fights. Sentinel fills the gap: ground-projected shapes that appear the instant a cast begins, for ALL content, with no per-boss configuration needed.

## Data Flow (Per Frame)

```
ObjectTable iteration (distance-culled)
    ↓
BattleNpc with active cast detected
    ↓
Action ID → Lumina Action sheet lookup (cached)
    ↓
CastType + EffectRange + XAxisModifier → ShapeDefinition
    ↓
Override table check (1,101 entries for known corrections)
    ↓
ShapeDefinition + entity position/heading → world-space triangle list
    ↓
Player safety check (am I inside this shape?)
    ↓
WorldToScreen projection per vertex
    ↓
ImGui DrawList: AddTriangleFilled (filled shape on ground)
```

## CastType → Shape Mapping

| CastType | Shape | EffectRange | XAxisModifier |
|----------|-------|-------------|---------------|
| 2 | Circle (caster-centered) | Radius | — |
| 3 | Cone | Range | — |
| 4 | Rectangle (from caster) | Length | Width |
| 5 | Circle / proximity | Radius | — |
| 7 | Ground-targeted circle | Radius | — |
| 8 | Ground-targeted rect | — | Width |
| 10 | Donut | Outer radius | — |
| 11 | Cross / plus | Arm length | Arm width |
| 12 | Rectangle (variant) | Length | Width |
| 13 | Cone (variant) | Range | — |

Filtered out: CastType 1 (single target), CastType 6 (raid-wide unavoidable).

## Key Dalamud APIs Used

- **Lumina** (`DataManager.GetExcelSheet<Action>()`) — game data sheets, always current
- **FFXIVClientStructs** (`BattleChara.GetCastInfo()`) — live entity cast state
- **ObjectTable** — entity iteration
- **GameGui.WorldToScreen** — 3D→2D projection
- **ImGui.GetBackgroundDrawList()** — render behind game UI

## Environment

- Dalamud API level 14 (version 14.0.4.3)
- Target: net8.0-windows
- Deploy to: `%appdata%\XIVLauncher\devPlugins\Sentinel\`
