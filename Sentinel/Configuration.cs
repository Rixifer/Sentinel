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
    public int Version { get; set; } = 12;

    // Master switch
    public bool Enabled = true;

    // Colors — lerps from Start to End over cast duration (applied to native omens)
    public Vector4 ColorStart = new(0.996f, 0.961f, 0.471f, 1.0f);  // warm yellow
    public Vector4 ColorEnd   = new(0.996f, 0.235f, 0.235f, 1.0f);  // red

    // Opacity of the omen color tint (0.0 = invisible, 1.0 = full color)
    public float OmenOpacity = 0.5f;

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

    // Action name floating labels (above AoE indicators)
    public bool    ShowActionNames  = false;
    public Vector4 ActionNameColor  = new(1f, 1f, 1f, 1f);

    // Danger warning when player stands inside an active AoE
    public bool    ShowHitWarning   = true;
    public Vector4 HitWarningColor  = new(1f, 0.2f, 0.2f, 1f);

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
