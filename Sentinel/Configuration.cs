using Dalamud.Configuration;
using System.Numerics;

namespace Sentinel;

public enum OmenStyle
{
    Default  = 0,  // Game decides which VFX variant to use
    ForceNew = 1,  // Force Dawntrail-era enhanced indicators (with light walls)
    ForceOld = 2,  // Force legacy flat indicators (no light walls)
}

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 14;

    // Master switch
    public bool Enabled = true;

    // Single omen color — written once at VFX spawn time via direct 0xA0 write.
    // Bypasses UpdateVfxColor tinting, so all colors (including blue) work correctly.
    public Vector4 OmenColor = new(1.0f, 0.545f, 0.239f, 1.0f);  // warm orange

    // Opacity applied to OmenColor.W at spawn time (0.0 = invisible, 1.0 = full)
    public float OmenOpacity = 0.50f;

    // HDR glow — multiplies RGB at spawn-time write to push into bloom range
    public float GlowIntensity = 1.20f;

    // Cast bar overlay — world-projected bar at the AoE center
    public bool    ShowCastBar      = true;
    public Vector4 CastBarFillColor = new(1.0f, 0.976f, 0.365f, 1.0f);  // yellow
    public Vector4 CastBarBgColor   = new(0.0f, 0.0f, 0.0f, 0.7f);
    public float   CastBarWidth     = 120f;
    public float   CastBarHeight    = 10f;
    public bool    ShowCastBarTime  = false;
    public bool    ShowCastBarName  = false;

    // AoE indicator visual style
    public OmenStyle IndicatorStyle = OmenStyle.Default;

    // Shape visibility (kept for Phase 2 forward compatibility, not used in Phase 1)
    public bool ShowCircles     = true;
    public bool ShowCones       = true;
    public bool ShowRects       = true;
    public bool ShowDonuts      = true;
    public bool ShowCrosses     = true;
    public bool HideUnavoidable = true;

    // ── Hitbox & Rings ────────────────────────────────────────────────────
    // Player position dot (AoE hitbox is literally this single point)
    public bool    ShowHitbox           = false;
    public bool    HitboxOnlyInCombat   = false;
    public Vector4 HitboxColor          = new(1f, 1f, 1f, 1f);
    public float   HitboxDotSize        = 4f;                          // screen pixels
    public bool    HitboxOutline        = true;
    public Vector4 HitboxOutlineColor   = new(0f, 0f, 0f, 0.8f);

    // Custom range Ring 1
    public bool    ShowHitboxRing       = false;
    public float   HitboxRingRadius     = 6f;
    public Vector4 HitboxRingColor      = new(0.4f, 0.8f, 1f, 0.6f);
    public float   HitboxRingThickness  = 2f;

    // Custom range Ring 2
    public bool    ShowHitboxRing2      = false;
    public float   HitboxRingRadius2    = 15f;
    public Vector4 HitboxRingColor2     = new(1f, 0.8f, 0.4f, 0.4f);
    public float   HitboxRingThickness2 = 1.5f;

    // Custom range Ring 3
    public bool    ShowHitboxRing3      = false;
    public float   HitboxRingRadius3    = 25f;
    public Vector4 HitboxRingColor3     = new(0.8f, 0.4f, 1f, 0.4f);
    public float   HitboxRingThickness3 = 1.5f;

    // Action name floating labels (above AoE indicators / cast bar)
    public bool    ShowActionNames  = false;
    public Vector4 ActionNameColor  = new(1f, 1f, 1f, 1f);

    // Danger warning when player stands inside an active AoE
    public bool    ShowHitWarning   = true;
    public Vector4 HitWarningColor  = new(0.988f, 1.0f, 0.200f, 1.0f);
    public float   HitWarningSize   = 36f;     // pixel size of the warning text

    // Action name floating labels — size
    public float   ActionNameSize   = 18f;     // pixel size of action name labels

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
