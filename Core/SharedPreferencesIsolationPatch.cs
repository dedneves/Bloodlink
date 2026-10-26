using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ULTRAKILL.Bloodlink.Core
{
    /// <summary>
    /// ULTRAKILL opens Preferences/Prefs.json with an exclusive file handle for the
    /// lifetime of the process. A second game process from the same install otherwise
    /// throws IOException during normal gameplay and can fail while loading a level.
    /// Only a process that finds the normal file locked is redirected to an isolated
    /// local profile; the first process keeps the game's normal Steam Cloud files.
    /// </summary>
    [HarmonyPatch(typeof(PrefsManager), "get_PrefsPath")]
    internal static class SharedPreferencesIsolationPatch
    {
        private static readonly object sync = new object();
        private static bool initialized;
        private static string isolatedPath;
        private static Mutex profileMutex;

        private static bool Prefix(ref string __result)
        {
            try
            {
                string path;
                if (!TryGetIsolatedPath(out path)) return true;
                __result = path;
                return false;
            }
            catch (Exception ex)
            {
                var plugin = BloodlinkPlugin.Instance;
                if (plugin != null) plugin.LogLevelTransition("Could not activate the isolated secondary-instance preferences profile: " + ex.Message);
                return true;
            }
        }

        private static bool TryGetIsolatedPath(out string path)
        {
            lock (sync)
            {
                if (!initialized)
                {
                    initialized = true;
                    var installDirectory = Directory.GetParent(Application.dataPath);
                    if (installDirectory == null)
                    {
                        path = null;
                        return false;
                    }

                    var normalPath = Path.Combine(installDirectory.FullName, "Preferences");
                    if (!IsFileLocked(Path.Combine(normalPath, "Prefs.json")))
                    {
                        path = null;
                        return false;
                    }

                    isolatedPath = ClaimProfilePath(normalPath);
                    Directory.CreateDirectory(isolatedPath);
                    var plugin = BloodlinkPlugin.Instance;
                    if (plugin != null) plugin.LogIsolatedPreferences(isolatedPath);
                }

                path = isolatedPath;
                return !string.IsNullOrEmpty(isolatedPath);
            }
        }

        private static bool IsFileLocked(string filePath)
        {
            if (!File.Exists(filePath)) return false;
            try
            {
                using (new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        private static string ClaimProfilePath(string normalPath)
        {
            var installHash = HashPath(normalPath);
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var profileRoot = Path.Combine(localData, "ULTRAKILL", "BloodlinkInstances", installHash);
            for (var slot = 2; slot <= 4; slot++)
            {
                var mutex = new Mutex(false, "Local\\BloodlinkPrefs_" + installHash + "_" + slot);
                try
                {
                    if (mutex.WaitOne(0))
                    {
                        profileMutex = mutex;
                        return Path.Combine(profileRoot, "Instance-" + slot);
                    }
                }
                catch (AbandonedMutexException)
                {
                    profileMutex = mutex;
                    return Path.Combine(profileRoot, "Instance-" + slot);
                }
                catch (Exception)
                {
                    mutex.Dispose();
                }
            }

            // Preserve isolation even if more than four game processes share one install.
            return Path.Combine(profileRoot, "Process-" + System.Diagnostics.Process.GetCurrentProcess().Id);
        }

        private static string HashPath(string path)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
                var result = new StringBuilder(16);
                for (var i = 0; i < 8; i++) result.Append(bytes[i].ToString("x2"));
                return result.ToString();
            }
        }
    }
}
