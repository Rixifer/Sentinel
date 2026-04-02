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
    public int Version { get; set; } = 9;

    // Master switch
    public bool Enabled = true;

    // Detection
    public float MaxRange = 30f;

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

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
