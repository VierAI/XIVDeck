// VierAI-fied fork support (AI generated code).
//
// Dalamud hides custom-repo plugins whose InternalName is also in the official repo, so this
// fork ships under InternalName "XIVDeck.FFXIVPluginVierAIfied". Dalamud keys config files by InternalName, so:
//  - ImportOfficialSettings copies the official plugin's settings once, when the fork has none yet.
//    It only ever reads the official files; it never modifies or deletes them.
//  - OfficialConfigDirectory points shared data (nothing; XIVDeck keeps no data files) at the official plugin's folder,
//    so history stays in one place whichever build is installed.
// Do not run the official plugin and this fork at the same time: they share that data.
using System;
using System.IO;
using Dalamud.Plugin;

namespace XIVDeck.FFXIVPlugin;

internal static class VierAIfiedMigration
{
    internal const string OfficialInternalName = "XIVDeck.FFXIVPlugin";

    // True when this build runs under a different InternalName than the official plugin.
    internal static bool IsRenamed(IDalamudPluginInterface pi) =>
        !string.Equals(pi.InternalName, OfficialInternalName, StringComparison.OrdinalIgnoreCase);

    // pluginConfigs\<official InternalName>, the official plugin's config folder (created if missing).
    internal static string OfficialConfigDirectory(IDalamudPluginInterface pi)
    {
        if (!IsRenamed(pi))
            return pi.GetPluginConfigDirectory();
        var dir = Path.Combine(pi.ConfigFile.Directory!.FullName, OfficialInternalName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Call once at startup, before the plugin loads its configuration.
    internal static void ImportOfficialSettings(IDalamudPluginInterface pi, Action<string> log)
    {
        try
        {
            if (!IsRenamed(pi))
                return;

            var root = pi.ConfigFile.Directory!.FullName;
            var newJson = pi.ConfigFile.FullName;
            var newDir = Path.Combine(root, pi.InternalName);
            var oldJson = Path.Combine(root, OfficialInternalName + ".json");
            var oldDir = Path.Combine(root, OfficialInternalName);

            if (HasOwnConfig(newJson, newDir))
                return;

            var copied = 0;
            if (File.Exists(oldJson))
            {
                File.Copy(oldJson, newJson, false);
                copied++;
            }

            if (Directory.Exists(oldDir))
                copied += CopyFolder(oldDir, newDir, true);

            log($"[VierAI-fied] Imported {copied} settings file(s) from the official {OfficialInternalName} plugin.");
        }
        catch (Exception e)
        {
            log($"[VierAI-fied] Settings import from {OfficialInternalName} failed: {e.Message}");
        }
    }

    private static int CopyFolder(string from, string to, bool topLevel)
    {
        var count = 0;
        foreach (var file in Directory.GetFiles(from))
        {
            var name = Path.GetFileName(file);
            if (IsShared(name, topLevel))
                continue;
            var target = Path.Combine(to, name);
            if (File.Exists(target))
                continue;
            Directory.CreateDirectory(to);
            File.Copy(file, target, false);
            count++;
        }

        foreach (var dir in Directory.GetDirectories(from))
        {
            var name = Path.GetFileName(dir);
            if (IsSharedFolder(name, topLevel))
                continue;
            count += CopyFolder(dir, Path.Combine(to, name), false);
        }

        return count;
    }

    private static bool HasOwnConfig(string newJson, string newDir) => File.Exists(newJson);

    private static bool IsShared(string fileName, bool topLevel) => false;

    private static bool IsSharedFolder(string folderName, bool topLevel) => false;
}