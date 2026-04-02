# Sentinel — Idea Board & Phase Roadmap

## Phase 1 — COMPLETE ✓
Native omen hijacking: per-frame gradient recoloring, Dawntrail light wall enforcement, opacity control, managed crash-safe cast detection.

## Phase 2 — Untelegraphed Mechanic Spawning
Spawn native-style omens for actions where omenId=0 (game provides no telegraph).

### Key Components:
1. **VFX Spawning** — Use `CreateVfx` (sig: `E8 ?? ?? ?? ?? 48 8B D8 48 8D 95`) with `VfxInitData` to spawn omen VFX at world positions. Use `DestroyVfx` for cleanup.
2. **Lifecycle Management** — InterframeResourceTracker double-buffer pattern (from Pictomancy). Each frame: touch existing → update, touch new → create, end of frame → destroy untouched.
3. **Per-frame Updates** — `UpdateVfxColor` for gradient, `UpdateVfxTransform` with 16-byte aligned Matrix4x4 for position/rotation.
4. **Shape→VFX Mapping** — OmenPathDecoder: map ShapeType + parameters to omen VFX paths (cone angles, donut ratios from Pictomancy tables).
5. **Action→Shape Data** — Rework overrides table. Extract ~2,887 SimpleAOEs entries from BMR modules. Store as CreateVfx-ready final values (no hitbox math at runtime).
6. **CastType→Shape Fallback** — For actions not in the override table, use Lumina CastType + EffectRange as a best-guess shape.

### Data Sources:
- BMR: ~2,887 SimpleAOEs (regex or Roslyn extractable)
- Existing Overrides.cs: ~1,101 entries (need format rework)
- Lumina Action sheet: CastType, EffectRange, XAxisModifier, TargetArea
- Omen sheet: 735 entries with VFX paths

## Phase 3 — Polish & Advanced Features

### Pixel Perfect Hitbox Display
Implement a PixelPerfect-style player hitbox ring. This is a natural complement to AoE visualization — knowing exactly where your hitbox is relative to the AoE edge is the whole point.
- Draw a small circle at player position with radius matching player hitbox
- Customizable color, thickness, opacity
- Toggle on/off in settings
- Implementation: Could use Pictomancy's AddCircle or native VFX, or an ImGui overlay ring projected to world space

### Action Name Labels
Restore floating action name text above AoE indicators.
- Off by default (toggle in settings)
- Use FFXIV's TrumpGothic font for native appearance
- Render via PictoService.Draw (DX11 text path) or ImGui world-to-screen projection
- Position: slightly above the omen center, billboard-facing camera

### Hit Detection — Exclamation Mark Warning
Instead of coloring the AoE differently when the player is inside it, spawn a floating exclamation mark (!) above the AoE.
- Cleaner than color-based detection (no color clutter)
- Strong visual cue: "you are in danger"
- Could use a game VFX lockon/marker, or rendered text/icon via ImGui
- Check: is player position within the omen's bounding shape? For native omens, we know the shape from the Omen sheet + CreateOmen parameters. For custom omens, we computed the shape.
- Consider: pulsing animation, sound cue option

### Color Refinement
- Investigate whether VFX particle emitter colors can be overridden directly for fully clean per-frame tinting (currently UpdateVfxColor provides clean colors based on testing)
- Per-duty color profiles (like GoodOmen)

### Light Wall Controls
- Light wall height adjustment (if the VFX supports scale.Y control)
- Light wall color separate from fill color

### Fade-out Animation
- Smooth omen disappearance when cast ends (currently omens just vanish)
- Could reduce opacity over 0.5s after cast completion before destroying

## Technical References
- `Research/SYNTHESIS.md` — Unified VFX system reference
- `Research/PICTOMANCY_VFX.md` — All game function sigs and VFX lifecycle
- `Research/CLIENTSTRUCTS_VFX.md` — Struct layouts and pointer chains
- `Research/GOODOMEN_ANALYSIS.md` — CreateOmen hook mechanics
- `Research/SPLATOON_RENDERING.md` — Alternative rendering approaches
- `Research/BMR_AOE_DATA.md` — Shape data extraction strategy
