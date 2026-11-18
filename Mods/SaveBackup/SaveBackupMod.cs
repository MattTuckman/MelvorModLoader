using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using MelvorModLoader;

namespace SaveBackup;

/// <summary>
/// Every time the game starts, copies all save files into a dated folder under the game's user data:
/// "mod_save_backups\2026-11-18_21-30-05\...". Keeps the newest 20 backups and skips a start where nothing changed.
/// To restore: close the game and copy a .sav file from a backup folder back over the one in "saves".
/// </summary>
public class SaveBackupMod : IMod
{
    public const string FolderName = "mod_save_backups";
    public const int Keep = 20;

    /// <summary>The backup made (or found unchanged) on this start; empty if there was nothing to back up.</summary>
    public static string LastBackupDir { get; private set; } = "";

    public void OnLoad(ModContext ctx)
    {
        try
        {
            string result = Run(OS.GetUserDataDir());
            ctx.Log(result);
        }
        catch (Exception e)
        {
            ctx.Log("The backup failed: " + e.Message);
        }
    }

    public static string Run(string userDir)
    {
        string root = Path.Combine(userDir, FolderName);
        // The game's own files only: skip our backups and the game's "save_backups" folders.
        List<string> saves = Directory.EnumerateFiles(userDir, "*.sav", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(userDir, p).Split(Path.DirectorySeparatorChar).Any(part => part is FolderName or "save_backups"))
            .ToList();
        if (saves.Count == 0) return "There are no saves to back up.";

        List<string> existing = Directory.Exists(root)
            ? Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal).ToList()
            : new List<string>();

        if (existing.Count > 0 && SameAs(existing[^1], userDir, saves))
        {
            LastBackupDir = existing[^1];
            return $"The saves have not changed since the last backup ({Path.GetFileName(existing[^1])}).";
        }

        string dest = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        foreach (string save in saves)
        {
            string target = Path.Combine(dest, Path.GetRelativePath(userDir, save));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(save, target, overwrite: true);
        }
        LastBackupDir = dest;
        existing.Add(dest);

        foreach (string old in existing.Take(Math.Max(0, existing.Count - Keep)))
            Directory.Delete(old, recursive: true);

        return $"{saves.Count} save file(s) backed up to {dest}";
    }

    /// <summary>True if the backup already holds every save with the same size and modification time.</summary>
    static bool SameAs(string backupDir, string userDir, List<string> saves)
    {
        foreach (string save in saves)
        {
            var copy = new FileInfo(Path.Combine(backupDir, Path.GetRelativePath(userDir, save)));
            var live = new FileInfo(save);
            if (!copy.Exists || copy.Length != live.Length || copy.LastWriteTimeUtc != live.LastWriteTimeUtc) return false;
        }
        return true;
    }
}
