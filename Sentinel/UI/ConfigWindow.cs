using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Sentinel.UI;

public class ConfigWindow : Window
{
    private readonly Plugin _plugin;
    private Configuration Config => _plugin.Config;

    public ConfigWindow(Plugin plugin) : base("Sentinel###SentinelConfig")
    {
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 360),
            MaximumSize = new Vector2(700, 700),
        };
    }

    public override void Draw()
    {
        bool changed = false;

        // ── Header ──────────────────────────────────────────────────────────
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.65f, 0.0f, 1.0f));
        ImGui.SetWindowFontScale(1.4f);
        ImGui.Text("Sentinel");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.TextDisabled("  Enhanced Omen System");

        ImGui.Separator();

        // ── Enable / Disable ─────────────────────────────────────────────────
        bool enabled = Config.Enabled;
        ImGui.PushStyleColor(ImGuiCol.Button, enabled
            ? new Vector4(0.15f, 0.55f, 0.15f, 1.0f)
            : new Vector4(0.45f, 0.10f, 0.10f, 1.0f));

        if (ImGui.Button(enabled ? "  ENABLED  " : "  DISABLED  "))
        {
            Config.Enabled = !Config.Enabled;
            changed = true;
        }
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.TextDisabled("(or /sentinel on / off)");

        // ── Status ───────────────────────────────────────────────────────────
        ImGui.Spacing();
        int casts     = _plugin._scanner.LastScanCastCount;
        int recolored = _plugin._omenManager.LastRecolorCount;
        ImGui.TextDisabled(
            $"Active: {casts} cast{(casts != 1 ? "s" : "")}  |  " +
            $"Recoloring: {recolored} omen{(recolored != 1 ? "s" : "")}");
        ImGui.Separator();
        ImGui.Spacing();

        // ── Tabs ─────────────────────────────────────────────────────────────
        if (ImGui.BeginTabBar("SentinelTabs"))
        {
            DrawGeneralTab(ref changed);
            DrawColorsTab(ref changed);
            DrawShapesTab(ref changed);
            DrawPerformanceTab(ref changed);
            ImGui.EndTabBar();
        }

        if (changed) Config.Save();
    }

    // ── General ──────────────────────────────────────────────────────────────
    private void DrawGeneralTab(ref bool changed)
    {
        if (!ImGui.BeginTabItem("General")) return;

        ImGui.Text("Detection range (yalms):");
        changed |= ImGui.SliderFloat("##range", ref Config.MaxRange, 5f, 50f, "%.0f");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("AoE Indicator Style:");

        int style = (int)Config.IndicatorStyle;

        if (ImGui.RadioButton("Default", ref style, 0))
        { Config.IndicatorStyle = OmenStyle.Default; changed = true; }
        ImGui.TextDisabled("  Uses whichever indicator style the game assigns to each attack.");

        if (ImGui.RadioButton("Force Enhanced (Dawntrail)", ref style, 1))
        { Config.IndicatorStyle = OmenStyle.ForceNew; changed = true; }
        ImGui.TextDisabled("  Upgrades all indicators to the Dawntrail-era style with vertical");
        ImGui.TextDisabled("  light wall borders for improved edge visibility.");

        if (ImGui.RadioButton("Force Legacy", ref style, 2))
        { Config.IndicatorStyle = OmenStyle.ForceOld; changed = true; }
        ImGui.TextDisabled("  Uses the original flat ground indicators without light wall borders.");

        ImGui.EndTabItem();
    }

    // ── Colors ───────────────────────────────────────────────────────────────
    private void DrawColorsTab(ref bool changed)
    {
        if (!ImGui.BeginTabItem("Colors")) return;

        ImGui.Text("Cast start color:");
        ImGui.TextDisabled("  Color at the beginning of the cast.");
        changed |= ImGui.ColorEdit4("##start", ref Config.ColorStart);

        ImGui.Spacing();
        ImGui.Text("Cast end color:");
        ImGui.TextDisabled("  Color when the cast is about to resolve.");
        changed |= ImGui.ColorEdit4("##end", ref Config.ColorEnd);

        ImGui.Separator();
        ImGui.Text("Presets:");

        if (ImGui.Button("Default (Yellow > Red)"))
        {
            Config.ColorStart = new Vector4(0.996f, 0.961f, 0.471f, 1.0f);
            Config.ColorEnd   = new Vector4(0.996f, 0.235f, 0.235f, 1.0f);
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Deuteranopia"))
        {
            Config.ColorStart = new Vector4(0.2f, 0.6f, 1.0f, 1.0f);
            Config.ColorEnd   = new Vector4(0.7f, 0.2f, 1.0f, 1.0f);
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Protanopia"))
        {
            Config.ColorStart = new Vector4(0.0f, 0.9f, 0.9f, 1.0f);
            Config.ColorEnd   = new Vector4(0.9f, 0.0f, 0.9f, 1.0f);
            changed = true;
        }
        if (ImGui.Button("Tritanopia"))
        {
            Config.ColorStart = new Vector4(1.0f, 0.5f, 0.7f, 1.0f);
            Config.ColorEnd   = new Vector4(0.9f, 0.1f, 0.1f, 1.0f);
            changed = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Opacity:");
        ImGui.TextDisabled("  Controls how strong the color tint appears on omens.");
        changed |= ImGui.SliderFloat("##opacity", ref Config.OmenOpacity, 0.05f, 1.0f, "%.2f");

        ImGui.EndTabItem();
    }

    // ── Shapes ───────────────────────────────────────────────────────────────
    private void DrawShapesTab(ref bool changed)
    {
        if (!ImGui.BeginTabItem("Shapes")) return;

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Coming Soon");
        ImGui.TextDisabled("Shape filters for custom AoE indicators will be available in a future update.");

        ImGui.Spacing();

        // Grey out all checkboxes — display only, not interactive
        ImGui.BeginDisabled();
        bool circles = Config.ShowCircles;
        bool cones   = Config.ShowCones;
        bool rects   = Config.ShowRects;
        bool donuts  = Config.ShowDonuts;
        bool crosses = Config.ShowCrosses;
        ImGui.Checkbox("Circles",    ref circles);
        ImGui.Checkbox("Cones",      ref cones);
        ImGui.Checkbox("Rectangles", ref rects);
        ImGui.Checkbox("Donuts",     ref donuts);
        ImGui.Checkbox("Crosses",    ref crosses);
        ImGui.Separator();
        bool unavoidable = Config.HideUnavoidable;
        ImGui.Checkbox("Hide unavoidable (raidwide) attacks", ref unavoidable);
        ImGui.EndDisabled();

        ImGui.EndTabItem();
    }

    // ── Performance ──────────────────────────────────────────────────────────
    private void DrawPerformanceTab(ref bool changed)
    {
        if (!ImGui.BeginTabItem("Performance")) return;

        int casts     = _plugin._scanner.LastScanCastCount;
        int entities  = _plugin._scanner.LastScanEntityCount;
        int recolored = _plugin._omenManager.LastRecolorCount;

        ImGui.Text("Omen recoloring:");
        ImGui.Separator();
        ImGui.Text($"  Omens recolored this frame: {recolored}");
        ImGui.Text($"  Active casts: {casts}");

        ImGui.Spacing();
        ImGui.Text("Entity scan:");
        ImGui.Separator();
        ImGui.Text($"  Objects iterated: {entities}");

        ImGui.EndTabItem();
    }
}
