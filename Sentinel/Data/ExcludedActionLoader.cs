using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Sentinel.Data;

public class ExcludedActionLoader
{
    private readonly HashSet<uint>          _excluded     = new();
    private readonly Dictionary<string, int> _reasonCounts = new();

    public IReadOnlySet<uint>               ExcludedActions => _excluded;
    public IReadOnlyDictionary<string, int> ReasonCounts    => _reasonCounts;
    public int Count => _excluded.Count;

    public ExcludedActionLoader(string pluginDir)
    {
        Load(pluginDir);
    }

    private void Load(string pluginDir)
    {
        var path = Path.Combine(pluginDir, "Data", "ExcludedActions.json");
        if (!File.Exists(path))
        {
            Plugin.Log.Warning("[Sentinel] ExcludedActions.json not found at {Path}.", path);
            return;
        }

        try
        {
            var json    = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var entries = JsonSerializer.Deserialize<List<ExcludedActionEntry>>(json, options);
            if (entries == null) return;

            foreach (var entry in entries)
            {
                _excluded.Add(entry.ActionId);
                _reasonCounts[entry.Reason] =
                    _reasonCounts.TryGetValue(entry.Reason, out var c) ? c + 1 : 1;
            }

            Plugin.Log.Information("[Sentinel] Loaded {Count} excluded action IDs.", _excluded.Count);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error("[Sentinel] Failed to load ExcludedActions.json: {Msg}", ex.Message);
        }
    }

    public bool IsExcluded(uint actionId) => _excluded.Contains(actionId);
}

public class ExcludedActionEntry
{
    public uint   ActionId   { get; set; }
    public string ActionName { get; set; } = "";
    public string Reason     { get; set; } = "";
    public string Source     { get; set; } = "";
}
