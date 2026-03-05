using System;
using System.Collections.Generic;
using System.Text;

namespace AxfsExplorer.Helpers;

record ActivityEntry(DateTime Time, string Category, string Message);

class ActivityLogger
{
    readonly List<ActivityEntry> _entries = new();
    public event Action<ActivityEntry>? EntryAdded;

    public void Log(string category, string message)
    {
        var e = new ActivityEntry(DateTime.Now, category, message);
        _entries.Add(e);
        if (_entries.Count > 500)
            _entries.RemoveAt(0);
        EntryAdded?.Invoke(e);
    }

    public IReadOnlyList<ActivityEntry> Entries => _entries;

    public void Clear() => _entries.Clear();

    public string Format()
    {
        var sb = new StringBuilder();
        foreach (var e in _entries)
            sb.AppendLine($"[{e.Time:HH:mm:ss}] [{e.Category}] {e.Message}");
        return sb.ToString();
    }
}
