using System;
using System.Collections.Generic;

namespace Sentinel.Core;

/// <summary>
/// Lightweight in-memory event log for the Sentinel debug window.
/// Written from CastScanner, OmenManager, and CustomOmenSpawner on the game thread.
/// Read by DebugWindow.Draw (also game thread) — no synchronisation needed.
/// </summary>
public static class DebugLog
{
    public struct LogEntry
    {
        public DateTime Time;
        public string   Category; // NET-CAST, HOOK-OMEN, RESOLVE, CANCEL, CUSTOM-BMR, LUMINA-LUM, HOOK-REMOVE
        public string   Message;
    }

    private static readonly List<LogEntry> _entries = new();
    private const int MaxEntries = 200;

    public static IReadOnlyList<LogEntry> Entries => _entries;

    public static void Add(string category, string message)
    {
        _entries.Add(new LogEntry { Time = DateTime.Now, Category = category, Message = message });
        if (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);
    }

    public static void Clear() => _entries.Clear();
}
