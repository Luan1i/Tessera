using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Tessera
{
    // One rebindable command. Keys use the settings page's format "Win+Ctrl+Alt+Shift+Key"; "" = not set.
    class KeyAction
    {
        public string Id, Section, Label, Description, Default, Keys;
        public Action Run;
        public bool WhilePaused; // still works while tiling is paused
    }

    // Parses, checks and stores key bindings (keys.json next to the exe).
    static class KeyBindings
    {
        public const int Win = 0x80000, Ctrl = 0x40000, Alt = 0x20000, Shift = 0x10000;
        public static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keys.json");
        static readonly Dictionary<string, int> Names = BuildNames();

        static Dictionary<string, int> BuildNames()
        {
            var n = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (char c = 'A'; c <= 'Z'; c++) n[c.ToString()] = c;
            for (char c = '0'; c <= '9'; c++) n[c.ToString()] = c;
            for (int i = 1; i <= 24; i++) n["F" + i] = 0x6F + i;
            // shortcut: punctuation uses US-layout virtual keys; other layouts may put them on different physical keys
            string[] named = { "Left", "Up", "Right", "Down", "PageUp", "PageDown", "Home", "End", "Insert", "Delete", "Space", "Enter", "Tab", ";", "=", ",", "-", ".", "/", "`", "[", "\\", "]", "'" };
            int[] vks = { 0x25, 0x26, 0x27, 0x28, 0x21, 0x22, 0x24, 0x23, 0x2D, 0x2E, 0x20, 0x0D, 0x09, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE };
            for (int i = 0; i < named.Length; i++) n[named[i]] = vks[i];
            return n;
        }

        // "Win+Alt+E" -> modifier bits | virtual key. 0 for "" (not set), -1 if it can't be used.
        public static int Parse(string keys)
        {
            if (string.IsNullOrEmpty(keys)) return 0;
            var parts = keys.Split('+');
            int mods = 0, vk;
            for (int i = 0; i < parts.Length - 1; i++)
                switch (parts[i])
                {
                    case "Win": mods |= Win; break;
                    case "Ctrl": mods |= Ctrl; break;
                    case "Alt": mods |= Alt; break;
                    case "Shift": mods |= Shift; break;
                    default: return -1;
                }
            if (mods == 0 || !Names.TryGetValue(parts[parts.Length - 1], out vk)) return -1; // a modifier keeps normal typing working
            if ((mods & Win) != 0 && vk == 'L') return -1; // Windows always keeps Win+L (lock)
            return mods | vk;
        }

        // Defaults, then whatever keys.json overrides. A broken file just means defaults.
        public static void Load(List<KeyAction> actions)
        {
            foreach (var a in actions) a.Keys = a.Default;
            try
            {
                if (!File.Exists(FilePath)) return;
                var saved = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
                Apply(actions, saved);
            }
            catch (Exception) { }
        }

        public static void Save(List<KeyAction> actions)
        {
            var map = new Dictionary<string, string>();
            foreach (var a in actions) map[a.Id] = a.Keys;
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(map));
        }

        // Takes the settings page's map (id -> keys). Changes nothing and returns the problem if any binding
        // is invalid or used twice; returns null when applied.
        public static string Apply(List<KeyAction> actions, Dictionary<string, string> requested)
        {
            if (requested == null) return "Nothing to save.";
            var used = new Dictionary<int, string>();
            foreach (var a in actions)
            {
                string keys;
                if (!requested.TryGetValue(a.Id, out keys)) keys = a.Keys;
                int combo = Parse(keys);
                if (combo < 0) return a.Label + ": \"" + keys + "\" can't be used.";
                string other;
                if (combo > 0 && used.TryGetValue(combo, out other)) return keys + " is set for both " + other + " and " + a.Label + ".";
                if (combo > 0) used[combo] = a.Label;
            }
            foreach (var a in actions)
            {
                string keys;
                if (requested.TryGetValue(a.Id, out keys)) a.Keys = keys ?? "";
            }
            return null;
        }
    }
}
