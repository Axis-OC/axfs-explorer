using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AxfsExplorer;

public class AppSettings
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AxfsExplorer"
    );
    static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public string Theme { get; set; } = "Dark";
    public int WindowWidth { get; set; } = 1200;
    public int WindowHeight { get; set; } = 750;
    public bool ConfirmDelete { get; set; } = true;
    public bool AutoSave { get; set; } = true;
    public string DefaultVolumeLabel { get; set; } = "AxisFS";
    public int DefaultImageSizeKB { get; set; } = 512;

    // Code Editor
    public bool SyntaxHighlighting { get; set; } = true;
    public bool ShowLineNumbers { get; set; } = true;
    public int EditorFontSize { get; set; } = 13;
    public bool LuaLspEnabled { get; set; } = false;

    // Sounds
    public bool GeneralSoundsEnabled { get; set; } = true;
    public bool BeepSoundsEnabled { get; set; } = true;
    public int MasterVolume { get; set; } = 80;
    public bool GeneralMuted { get; set; } = false;
    public bool BeepMuted { get; set; } = false;

    // Layout
    public bool PreviewPaneVisible { get; set; } = false;
    public bool ActivityLogVisible { get; set; } = false;

    // Sorting
    public string SortColumn { get; set; } = "Name";
    public bool SortAscending { get; set; } = true;

    // Recent files & bookmarks
    public List<string> RecentFiles { get; set; } = new();
    public List<string> Bookmarks { get; set; } = new();

    public void AddRecentFile(string path)
    {
        RecentFiles.Remove(path);
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 10)
            RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
        Save();
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })
            );
        }
        catch { }
    }

    public void Reset()
    {
        Theme = "Dark";
        WindowWidth = 1200;
        WindowHeight = 750;
        ConfirmDelete = true;
        AutoSave = true;
        DefaultVolumeLabel = "AxisFS";
        DefaultImageSizeKB = 512;
        SyntaxHighlighting = true;
        ShowLineNumbers = true;
        EditorFontSize = 13;
        LuaLspEnabled = false;
        GeneralSoundsEnabled = true;
        BeepSoundsEnabled = true;
        MasterVolume = 80;
        GeneralMuted = false;
        BeepMuted = false;
        PreviewPaneVisible = false;
        ActivityLogVisible = false;
        SortColumn = "Name";
        SortAscending = true;
        RecentFiles = new();
        Bookmarks = new();
        Save();
    }
}
