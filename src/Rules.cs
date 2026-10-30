using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace Tessera
{
    public class Rule
    {
        public string App;     // process name without .exe, e.g. "spotify"
        public string Action;  // float | monitor | tabs
        public string Monitor; // for "monitor": the display's device name, e.g. \\.\DISPLAY2
    }

    // rules.json next to Tessera.exe: what to do with an app's windows when they open.
    static class Rules
    {
        static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rules.json");
        static readonly string[] Actions = { "float", "monitor", "tabs" };
        public static List<Rule> All = new List<Rule>();

        public static void Load()
        {
            try { All = File.Exists(FilePath) ? Clean(new JavaScriptSerializer().Deserialize<List<Rule>>(File.ReadAllText(FilePath))) : new List<Rule>(); }
            catch (Exception) { All = new List<Rule>(); } // unreadable file: no rules
        }

        public static void Save(List<Rule> rules)
        {
            var clean = Clean(rules);
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(clean));
            All = clean;
        }

        // Trims names and drops ".exe", unknown actions, monitor rules without a monitor and repeats of an app + action.
        public static List<Rule> Clean(IEnumerable<Rule> rules)
        {
            var result = new List<Rule>();
            foreach (var r in rules ?? new Rule[0])
            {
                if (r == null || r.App == null || !Actions.Contains(r.Action)) continue;
                var app = r.App.Trim();
                if (app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app = app.Substring(0, app.Length - 4).Trim();
                if (app.Length == 0 || (r.Action == "monitor" && string.IsNullOrEmpty(r.Monitor))) continue;
                if (Find(result, app, r.Action) == null)
                    result.Add(new Rule { App = app, Action = r.Action, Monitor = r.Action == "monitor" ? r.Monitor : null });
            }
            return result;
        }

        public static Rule Find(string app, string action) { return Find(All, app, action); }

        static Rule Find(List<Rule> rules, string app, string action)
        {
            return rules.FirstOrDefault(r => r.Action == action && string.Equals(r.App, app, StringComparison.OrdinalIgnoreCase));
        }
    }
}
