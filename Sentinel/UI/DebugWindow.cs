using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Sentinel.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Sentinel.UI;

public class DebugWindow : Window
{
    private readonly Plugin _plugin;
    private IReadOnlyList<ActiveCast> _casts = new List<ActiveCast>();

    // Colour palette
    private static readonly Vector4 ColGreen   = new(0.40f, 1.00f, 0.40f, 1f);
    private static readonly Vector4 ColRed     = new(1.00f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 ColYellow  = new(1.00f, 1.00f, 0.30f, 1f);
    private static readonly Vector4 ColCyan    = new(0.30f, 1.00f, 1.00f, 1f);
    private static readonly Vector4 ColBlue    = new(0.40f, 0.60f, 1.00f, 1f);
    private static readonly Vector4 ColGray    = new(0.60f, 0.60f, 0.60f, 1f);
    private static readonly Vector4 ColDimGray = new(0.45f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 ColWhite   = new(1.00f, 1.00f, 1.00f, 1f);

    public DebugWindow(Plugin plugin) : base("Sentinel Debug###SentinelDebug")
    {
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(800, 340),
            MaximumSize = new Vector2(1600, 900),
        };
    }

    public void Update(IReadOnlyList<ActiveCast> casts) => _casts = casts;

    public override void Draw()
    {
        DrawStatusBar();

        // Right-aligned snapshot export buttons
        ImGui.SameLine(ImGui.GetWindowWidth() - 200f);
        if (ImGui.Button("Copy Snapshot"))
        {
            var snapshot = BuildSnapshot();
            ImGui.SetClipboardText(snapshot);
            try
            {
                var dir = Plugin.PluginInterface.AssemblyLocation.DirectoryName!;
                File.WriteAllText(Path.Combine(dir, "debug_snapshot.txt"), snapshot);
            }
            catch { }
        }
        ImGui.SameLine();
        if (ImGui.Button("Copy Log"))
            ImGui.SetClipboardText(BuildEventLog());

        ImGui.Separator();
        DrawActiveCastsTable();
        DrawHookOmensSection();
        DrawEventLogSection();
        DrawNetworkStatsSection();
        DrawDataSourcesSection();
    }

    // ── Section 1: Status Bar ─────────────────────────────────────────────

    private void DrawStatusBar()
    {
        bool netActive  = _plugin._netListener.IsActive;
        bool hookActive = _plugin._omenManager.IsHookActive;

        // Row 1: component health
        ImGui.Text("VFX:");
        ImGui.SameLine();
        ImGui.TextColored(VfxFunctions.IsFullyResolved ? ColGreen : ColYellow,
            $"{VfxFunctions.ResolvedCount}/6{(VfxFunctions.IsFullyResolved ? " OK" : " !")}");

        ImGui.SameLine();
        ImGui.TextDisabled("  |  Net Hook:");
        ImGui.SameLine();
        ImGui.TextColored(netActive ? ColGreen : ColRed, netActive ? "Active" : "Inactive");

        ImGui.SameLine();
        ImGui.TextDisabled("  |  CreateOmen Hook:");
        ImGui.SameLine();
        ImGui.TextColored(hookActive ? ColGreen : ColRed, hookActive ? "Active" : "Inactive");

        // Row 2: live counters
        int entities  = _plugin._scanner.LastScanEntityCount;
        int activeCnt = _plugin._scanner.ActiveCastCount;
        int hookCnt   = _plugin._scanner.HookOmenCount;
        int customCnt = _plugin._vfxTracker.ActiveCount;
        int recolored = _plugin._omenManager.LastRecolorCount;

        ImGui.TextDisabled(
            $"Entities: {entities}  |  Active Casts: {activeCnt}  |  " +
            $"Hook Omens: {hookCnt}  |  Custom VFX: {customCnt}  |  Recolored: {recolored}");
    }

    // ── Section 2: Active Casts Table ─────────────────────────────────────

    private void DrawActiveCastsTable()
    {
        ImGui.Text($"Active Casts ({_casts.Count})");

        if (ImGui.BeginTable("casts", 9,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new Vector2(0f, 190f)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Entity",    ImGuiTableColumnFlags.WidthFixed,   88f);
            ImGui.TableSetupColumn("Action",    ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("CT",        ImGuiTableColumnFlags.WidthFixed,   36f);
            ImGui.TableSetupColumn("OmenId",    ImGuiTableColumnFlags.WidthFixed,   54f);
            ImGui.TableSetupColumn("Indicator", ImGuiTableColumnFlags.WidthFixed,   86f);
            ImGui.TableSetupColumn("Shape",     ImGuiTableColumnFlags.WidthFixed,  116f);
            ImGui.TableSetupColumn("Progress",  ImGuiTableColumnFlags.WidthFixed,   82f);
            ImGui.TableSetupColumn("Time",      ImGuiTableColumnFlags.WidthFixed,   64f);
            ImGui.TableSetupColumn("Position",  ImGuiTableColumnFlags.WidthFixed,   94f);
            ImGui.TableHeadersRow();

            foreach (var cast in _casts)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.Text($"{cast.EntityId:X}");

                ImGui.TableSetColumnIndex(1);
                ImGui.Text($"{cast.ActionName} ({cast.ActionId})");

                ImGui.TableSetColumnIndex(2);
                string ctLabel = cast.CastType switch
                {
                    2  => "Cir",  3 => "Con",  4 => "Rct",
                    5  => "Cir",  7 => "CiG",  8 => "RcG",
                    10 => "Dnt", 11 => "Crs", 12 => "Rc",  13 => "Cn",
                    _  => $"C{cast.CastType}",
                };
                ImGui.Text(ctLabel);

                ImGui.TableSetColumnIndex(3);
                ImGui.Text(cast.OmenId > 0 ? $"{cast.OmenId}" : "-");

                ImGui.TableSetColumnIndex(4);
                var (indColor, indLabel) = cast.IndicatorType switch
                {
                    "NATIVE"     => (ColGreen,   "NATIVE"),
                    "CUSTOM:BMR" => (ColBlue,    "CUST:BMR"),
                    "CUSTOM:LUM" => (ColCyan,    "CUST:LUM"),
                    "EXCLUDED"   => (ColDimGray, "EXCLUDED"),
                    _            => (ColRed,     "NONE"),
                };
                ImGui.TextColored(indColor, indLabel);

                ImGui.TableSetColumnIndex(5);
                if (!string.IsNullOrEmpty(cast.ShapeInfo))
                    ImGui.TextColored(ColCyan, cast.ShapeInfo);
                else
                    ImGui.TextDisabled("-");

                ImGui.TableSetColumnIndex(6);
                ImGui.ProgressBar(cast.Progress, new Vector2(-1f, 0f), $"{cast.Progress:P0}");

                ImGui.TableSetColumnIndex(7);
                float remaining = cast.TotalCastTime * (1f - cast.Progress);
                ImGui.Text($"{remaining:F1}/{cast.TotalCastTime:F1}s");

                ImGui.TableSetColumnIndex(8);
                ImGui.Text($"({cast.CasterPosition.X:F1},{cast.CasterPosition.Z:F1})");
            }

            ImGui.EndTable();
        }
    }

    // ── Section 3: Hook-Tracked Omens ─────────────────────────────────────

    private void DrawHookOmensSection()
    {
        var hookOmens = _plugin._scanner.HookOmens;
        if (!ImGui.CollapsingHeader($"Hook-Tracked Omens ({hookOmens.Count})###HookOmens"))
            return;

        if (hookOmens.Count == 0)
        {
            ImGui.TextDisabled("  (none tracked this frame)");
            return;
        }

        if (ImGui.BeginTable("hookOmens", 4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new Vector2(0f, 110f)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("VfxData*",    ImGuiTableColumnFlags.WidthFixed, 140f);
            ImGui.TableSetupColumn("Entity Addr", ImGuiTableColumnFlags.WidthFixed, 140f);
            ImGui.TableSetupColumn("Age (s)",     ImGuiTableColumnFlags.WidthFixed,  62f);
            ImGui.TableSetupColumn("Progress",    ImGuiTableColumnFlags.WidthFixed,  88f);
            ImGui.TableHeadersRow();

            long now = Environment.TickCount64;
            foreach (var kvp in hookOmens)
            {
                var o       = kvp.Value;
                float age   = (now - o.CreationTicks) / 1000f;
                float prog  = Math.Clamp(age / 5f, 0f, 1f); // approx using 5s default

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.Text($"0x{kvp.Key:X}");
                ImGui.TableSetColumnIndex(1);
                ImGui.Text($"0x{o.EntityAddress:X}");
                ImGui.TableSetColumnIndex(2);
                ImGui.Text($"{age:F1}s");
                ImGui.TableSetColumnIndex(3);
                ImGui.ProgressBar(prog, new Vector2(-1f, 0f), $"{prog:P0}");
            }

            ImGui.EndTable();
        }
    }

    // ── Section 4: Event Log ──────────────────────────────────────────────

    private void DrawEventLogSection()
    {
        var entries = DebugLog.Entries;
        if (!ImGui.CollapsingHeader($"Event Log ({entries.Count} entries)###EventLog"))
            return;

        if (ImGui.SmallButton("Clear"))
            DebugLog.Clear();

        ImGui.SameLine();
        ImGui.TextDisabled("(last 200 events, newest at bottom)");

        if (ImGui.BeginChild("eventlog", new Vector2(0f, 148f),
            false, ImGuiWindowFlags.HorizontalScrollbar))
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var color = entry.Category switch
                {
                    "NET-CAST"    => ColGreen,
                    "HOOK-OMEN"   => ColYellow,
                    "CUSTOM-BMR"  => ColBlue,
                    "LUMINA-LUM"  => ColCyan,
                    "RESOLVE"     => ColGray,
                    "CANCEL"      => ColGray,
                    "HOOK-REMOVE" => ColRed,
                    _             => ColWhite,
                };
                ImGui.TextColored(color,
                    $"[{entry.Time:HH:mm:ss.fff}] [{entry.Category,-12}] {entry.Message}");
            }

            // Auto-scroll to bottom when near it
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20f)
                ImGui.SetScrollHereY(1.0f);
        }
        ImGui.EndChild();
    }

    // ── Section 5: Network Stats ──────────────────────────────────────────

    private void DrawNetworkStatsSection()
    {
        if (!ImGui.CollapsingHeader("Network Stats###NetStats"))
            return;

        var nl = _plugin._netListener;

        ImGui.Text("Packet Hook:");
        ImGui.SameLine();
        ImGui.TextColored(nl.IsActive ? ColGreen : ColRed, nl.IsActive ? "Active" : "Inactive");

        ImGui.Text($"Queue depths  — CastStart: {nl.CastStarts.Count}  " +
                   $"ActionResolve: {nl.ActionResolves.Count}  " +
                   $"CastCancel: {nl.CastCancels.Count}");

        ImGui.Text($"Total received — CastStarts: {nl.TotalCastStarts:N0}  " +
                   $"ActionResolves: {nl.TotalActionResolves:N0}  " +
                   $"CastCancels: {nl.TotalCastCancels:N0}");
    }

    // ── Section 6: Data Sources ───────────────────────────────────────────

    private void DrawDataSourcesSection()
    {
        if (!ImGui.CollapsingHeader("Data Sources###DataSrc"))
            return;

        // BMR shapes
        var bmr = _plugin._bmrShapes;
        ImGui.Text($"BMR Shapes loaded: {bmr.Shapes.Count:N0}");
        ImGui.Indent();
        foreach (var kvp in bmr.ShapeTypeCounts)
            ImGui.TextDisabled($"{kvp.Key}: {kvp.Value}");
        ImGui.Unindent();

        // Excluded actions
        var exc = _plugin._excludedActions;
        ImGui.Text($"Excluded actions: {exc.Count:N0}");
        ImGui.Indent();
        foreach (var kvp in exc.ReasonCounts)
            ImGui.TextDisabled($"{kvp.Key}: {kvp.Value}");
        ImGui.Unindent();

        // Omen sheet
        var rd = _plugin._omenReader;
        ImGui.Text($"Omen Paths loaded: {rd.OmenPaths.Count:N0}");
        ImGui.Text($"Enhanced Remap pairs: {rd.EnhancedRemap.Count:N0}");

        ImGui.Separator();

        // VFX function pointers
        ImGui.Text($"VFX Functions: {VfxFunctions.ResolvedCount}/6 resolved");
        ImGui.Indent();
        DrawVfxRow("UpdateVfxColor",    VfxFunctions.HasUpdateVfxColor,    VfxFunctions.AddrUpdateVfxColor);
        DrawVfxRow("VfxInitDataCtor",   VfxFunctions.HasVfxInitDataCtor,   VfxFunctions.AddrVfxInitDataCtor);
        DrawVfxRow("CreateVfx",         VfxFunctions.HasCreateVfx,         VfxFunctions.AddrCreateVfx);
        DrawVfxRow("DestroyVfx",        VfxFunctions.HasDestroyVfx,        VfxFunctions.AddrDestroyVfx);
        DrawVfxRow("UpdateVfxTransform",VfxFunctions.HasUpdateVfxTransform,VfxFunctions.AddrUpdateVfxTransform);
        DrawVfxRow("RotateMatrix",      VfxFunctions.HasRotateMatrix,      VfxFunctions.AddrRotateMatrix);
        ImGui.Unindent();
    }

    private static void DrawVfxRow(string name, bool ok, nint addr)
    {
        if (ok)
            ImGui.TextColored(ColGreen, $"{name}: 0x{addr:X}");
        else
            ImGui.TextColored(ColRed, $"{name}: FAILED");
    }

    // ── Snapshot export ───────────────────────────────────────────────────

    private string BuildSnapshot()
    {
        var sb  = new StringBuilder();
        var nl  = _plugin._netListener;
        var sc  = _plugin._scanner;
        var om  = _plugin._omenManager;
        var bmr = _plugin._bmrShapes;
        var rd  = _plugin._omenReader;

        sb.AppendLine("=== Sentinel Debug Snapshot ===");
        sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // System status
        sb.AppendLine("--- System Status ---");
        sb.AppendLine($"VFX Functions: {VfxFunctions.ResolvedCount}/6 resolved");
        sb.AppendLine($"Network Hook: {(nl.IsActive ? "Active" : "Inactive")}");
        sb.AppendLine($"CreateOmen Hook: {(om.IsHookActive ? "Active" : "Inactive")}");
        sb.AppendLine(
            $"Entities: {sc.LastScanEntityCount}  |  Casts: {sc.ActiveCastCount}  |  " +
            $"Hook Omens: {sc.HookOmenCount}  |  Custom VFX: {_plugin._vfxTracker.ActiveCount}  |  " +
            $"Recolored: {om.LastRecolorCount}");
        sb.AppendLine();

        // Active casts
        sb.AppendLine("--- Active Casts ---");
        if (_casts.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var c in _casts)
            {
                sb.AppendLine(
                    $"  0x{c.EntityId:X}  [{c.ActionId}] {c.ActionName}  " +
                    $"CT={c.CastType}  omenId={c.OmenId}  ind={c.IndicatorType}  " +
                    $"shape={c.ShapeInfo}  prog={c.Progress:P0}  " +
                    $"time={c.TotalCastTime * (1f - c.Progress):F1}/{c.TotalCastTime:F1}s  " +
                    $"pos=({c.CasterPosition.X:F1},{c.CasterPosition.Z:F1})");
            }
        }
        sb.AppendLine();

        // Hook-tracked omens
        sb.AppendLine("--- Hook-Tracked Omens ---");
        var hookOmens = sc.HookOmens;
        if (hookOmens.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            long now = Environment.TickCount64;
            foreach (var kvp in hookOmens)
            {
                var o   = kvp.Value;
                float age = (now - o.CreationTicks) / 1000f;
                sb.AppendLine(
                    $"  VfxData=0x{kvp.Key:X}  entity=0x{o.EntityAddress:X}  age={age:F1}s");
            }
        }
        sb.AppendLine();

        // Network stats
        sb.AppendLine("--- Network Stats ---");
        sb.AppendLine(
            $"CastStarts: {nl.TotalCastStarts:N0}  |  " +
            $"ActionResolves: {nl.TotalActionResolves:N0}  |  " +
            $"CastCancels: {nl.TotalCastCancels:N0}");
        sb.AppendLine(
            $"Queue depths — CastStart: {nl.CastStarts.Count}  " +
            $"ActionResolve: {nl.ActionResolves.Count}  " +
            $"CastCancel: {nl.CastCancels.Count}");
        sb.AppendLine();

        // Data sources
        var exc = _plugin._excludedActions;
        sb.AppendLine("--- Data Sources ---");
        sb.AppendLine($"BMR Shapes: {bmr.Shapes.Count:N0}");
        foreach (var kvp in bmr.ShapeTypeCounts)
            sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
        sb.AppendLine($"Excluded actions: {exc.Count:N0}");
        foreach (var kvp in exc.ReasonCounts)
            sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
        sb.AppendLine($"Omen Paths: {rd.OmenPaths.Count:N0}  |  Enhanced Remap: {rd.EnhancedRemap.Count:N0}");
        sb.AppendLine("VFX Functions:");
        sb.AppendLine($"  UpdateVfxColor:    {(VfxFunctions.HasUpdateVfxColor    ? $"0x{VfxFunctions.AddrUpdateVfxColor:X}"    : "FAILED")}");
        sb.AppendLine($"  VfxInitDataCtor:   {(VfxFunctions.HasVfxInitDataCtor   ? $"0x{VfxFunctions.AddrVfxInitDataCtor:X}"   : "FAILED")}");
        sb.AppendLine($"  CreateVfx:         {(VfxFunctions.HasCreateVfx         ? $"0x{VfxFunctions.AddrCreateVfx:X}"         : "FAILED")}");
        sb.AppendLine($"  DestroyVfx:        {(VfxFunctions.HasDestroyVfx        ? $"0x{VfxFunctions.AddrDestroyVfx:X}"        : "FAILED")}");
        sb.AppendLine($"  UpdateVfxTransform:{(VfxFunctions.HasUpdateVfxTransform? $"0x{VfxFunctions.AddrUpdateVfxTransform:X}": "FAILED")}");
        sb.AppendLine($"  RotateMatrix:      {(VfxFunctions.HasRotateMatrix      ? $"0x{VfxFunctions.AddrRotateMatrix:X}"      : "FAILED")}");
        sb.AppendLine();

        // Recent events
        var entries = DebugLog.Entries;
        int start   = Math.Max(0, entries.Count - 30);
        sb.AppendLine($"--- Recent Events (last {entries.Count - start} of {entries.Count}) ---");
        for (int i = start; i < entries.Count; i++)
        {
            var e = entries[i];
            sb.AppendLine($"[{e.Time:HH:mm:ss.fff}] [{e.Category,-12}] {e.Message}");
        }

        return sb.ToString();
    }

    private string BuildEventLog()
    {
        var sb = new StringBuilder();
        foreach (var entry in DebugLog.Entries)
            sb.AppendLine($"[{entry.Time:HH:mm:ss.fff}] [{entry.Category}] {entry.Message}");
        return sb.ToString();
    }
}
