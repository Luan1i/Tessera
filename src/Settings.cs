using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Tessera
{
    // Serves settings.html (embedded in the exe) and its JSON API on 127.0.0.1 and shows it in an Edge app window.
    // API calls need this run's random token, so other web pages open in the browser can't change settings.
    class SettingsServer
    {
        readonly List<KeyAction> actions;
        readonly Action keysChanged;
        readonly WM wm;
        readonly SynchronizationContext ui;
        readonly string token = Guid.NewGuid().ToString("N");
        TcpListener listener;
        int port;

        public SettingsServer(List<KeyAction> actions, Action keysChanged, WM wm)
        {
            this.actions = actions;
            this.keysChanged = keysChanged;
            this.wm = wm;
            ui = SynchronizationContext.Current; // created on the UI thread; API work runs there
        }

        public void Open()
        {
            if (listener == null)
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
                new Thread(Serve) { IsBackground = true, Name = "settings" }.Start();
            }
            var url = "http://127.0.0.1:" + port + "/?t=" + token;
            var edge = EdgePath();
            if (edge != null) Process.Start(edge, "--app=" + url + " --window-size=1040,780");
            else Process.Start(url); // default browser
        }

        static string EdgePath()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"))
            {
                var path = k == null ? null : k.GetValue(null) as string;
                return path != null && File.Exists(path) ? path : null;
            }
        }

        void Serve()
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) { return; }
                ThreadPool.QueueUserWorkItem(_ => Handle(client));
            }
        }

        void Handle(TcpClient client)
        {
            using (client)
                try
                {
                    client.ReceiveTimeout = 5000;
                    var stream = client.GetStream();
                    string method, path, body;
                    Dictionary<string, string> headers;
                    if (!ReadRequest(stream, out method, out path, out headers, out body)) return;

                    string host, sentToken;
                    headers.TryGetValue("Host", out host);
                    headers.TryGetValue("X-Tessera-Token", out sentToken);
                    var route = path.Split('?')[0];
                    if (host != "127.0.0.1:" + port) Write(stream, "403 Forbidden", "text/plain", "Forbidden"); // blocks DNS-rebinding pages
                    else if (method == "GET" && route == "/") Write(stream, "200 OK", "text/html; charset=utf-8", Page());
                    else if (!route.StartsWith("/api/")) Write(stream, "404 Not Found", "text/plain", "Not found");
                    else if (sentToken != token) Write(stream, "403 Forbidden", "text/plain", "Forbidden");
                    else
                    {
                        string result = null;
                        ui.Send(_ => result = Api(method, route, body), null);
                        Write(stream, "200 OK", "application/json; charset=utf-8", result);
                    }
                }
                catch (Exception) { } // a broken request only affects that request
        }

        string Api(string method, string route, string body)
        {
            var json = new JavaScriptSerializer();
            try
            {
                if (method == "GET" && route == "/api/state")
                    return json.Serialize(new
                    {
                        version = "1.0",
                        startup = Startup.Enabled(),
                        layout = wm.Mode,
                        actions = actions.Select(a => new { id = a.Id, section = a.Section, label = a.Label, description = a.Description, keys = a.Keys, defaultKeys = a.Default }),
                    });
                if (method == "POST" && route == "/api/keys")
                {
                    var error = KeyBindings.Apply(actions, json.Deserialize<Dictionary<string, string>>(body));
                    if (error == null)
                    {
                        KeyBindings.Save(actions);
                        keysChanged();
                    }
                    return json.Serialize(new { ok = error == null, error });
                }
                if (method == "POST" && route == "/api/startup")
                {
                    var request = json.Deserialize<Dictionary<string, object>>(body);
                    object enabled;
                    Startup.Set(request != null && request.TryGetValue("enabled", out enabled) && enabled is bool && (bool)enabled);
                    return json.Serialize(new { ok = true });
                }
                if (method == "POST" && route == "/api/layout")
                {
                    var request = json.Deserialize<Dictionary<string, object>>(body);
                    object layout;
                    bool ok = request != null && request.TryGetValue("layout", out layout) && layout is string && wm.SetLayout((string)layout);
                    return json.Serialize(new { ok, error = ok ? null : "Unknown layout." });
                }
                if (method == "POST" && route == "/api/open-folder")
                {
                    Process.Start("explorer.exe", "\"" + AppDomain.CurrentDomain.BaseDirectory + "\"");
                    return json.Serialize(new { ok = true });
                }
                return json.Serialize(new { ok = false, error = "Unknown request." });
            }
            catch (Exception e) { return json.Serialize(new { ok = false, error = e.Message }); }
        }

        static byte[] page;
        static byte[] Page()
        {
            if (page == null)
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("settings.html"))
                using (var m = new MemoryStream())
                {
                    s.CopyTo(m);
                    page = m.ToArray();
                }
            return page;
        }

        static bool ReadRequest(NetworkStream s, out string method, out string path, out Dictionary<string, string> headers, out string body)
        {
            method = path = body = null;
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var data = new MemoryStream();
            var chunk = new byte[4096];
            int end;
            while ((end = HeaderEnd(data)) < 0)
            {
                int n = s.Read(chunk, 0, chunk.Length);
                if (n <= 0 || data.Length > 16384) return false;
                data.Write(chunk, 0, n);
            }
            var all = data.ToArray();
            var lines = Encoding.ASCII.GetString(all, 0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 2) return false;
            method = first[0];
            path = first[1];
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            int length = 0;
            string lengthText;
            if (headers.TryGetValue("Content-Length", out lengthText) && (!int.TryParse(lengthText, out length) || length < 0 || length > 65536)) return false;
            var rest = new MemoryStream();
            rest.Write(all, end + 4, all.Length - end - 4);
            while (rest.Length < length)
            {
                int n = s.Read(chunk, 0, chunk.Length);
                if (n <= 0) return false;
                rest.Write(chunk, 0, n);
            }
            body = Encoding.UTF8.GetString(rest.ToArray(), 0, length);
            return true;
        }

        static int HeaderEnd(MemoryStream m)
        {
            var b = m.GetBuffer();
            for (int i = 0; i + 3 < m.Length; i++)
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }

        static void Write(Stream s, string status, string type, string text) { Write(s, status, type, Encoding.UTF8.GetBytes(text)); }

        static void Write(Stream s, string status, string type, byte[] content)
        {
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + content.Length +
                "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            s.Write(head, 0, head.Length);
            s.Write(content, 0, content.Length);
        }
    }

    // The "start with Windows" entry. Windows only runs a Run entry that also has an "approved" mark
    // (first byte even) under StartupApproved; Settings > Apps > Startup flips that mark.
    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string Name = "Tessera";

        public static bool Enabled()
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey))
            {
                var mark = approved == null ? null : approved.GetValue(Name) as byte[];
                return run != null && run.GetValue(Name) != null && mark != null && mark.Length > 0 && mark[0] % 2 == 0;
            }
        }

        public static void Set(bool on)
        {
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey))
            {
                if (on)
                {
                    run.SetValue(Name, "\"" + System.Windows.Forms.Application.ExecutablePath + "\"");
                    approved.SetValue(Name, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                }
                else
                {
                    run.DeleteValue(Name, false);
                    approved.DeleteValue(Name, false);
                }
            }
        }
    }
}
