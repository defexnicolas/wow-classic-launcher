// Estado del servidor. Dos fuentes:
//   1. Sonda directa: conexion TCP (y nada mas) al login y al mundo. Dice la verdad en tiempo real.
//   2. status.json publicado en GitHub (server/publish_status.py): mantenimiento, noticias, enlaces
//      y la ultima version del launcher. Si esta viejo (> 15 min) se ignora lo que dice del estado.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ForeverLauncher
{
    // Campo traducible del JSON: "title" es el texto base y "title_en" (etc.) su traduccion; si falta, se usa el base.
    public sealed class FeedEntry
    {
        readonly Dictionary<string, object> d;
        public FeedEntry(Dictionary<string, object> d) { this.d = d ?? new Dictionary<string, object>(); }
        public string Get(string key)
        {
            object v;
            if (d.TryGetValue(key + "_" + L.Lang, out v) && v != null && v.ToString().Length > 0) return v.ToString();
            return d.TryGetValue(key, out v) && v != null ? v.ToString() : null;
        }
    }

    public sealed class ServerStatus
    {
        public bool LoginUp, WorldUp;
        public long LoginMs = -1;
        public bool FeedOk, FeedFresh, FeedLoginUp, FeedWorldUp, Maintenance;
        public FeedEntry Root = new FeedEntry(null);   // "message" / "message_en"
        public string LatestLauncher, LauncherUrl;
        public List<FeedEntry> News = new List<FeedEntry>();
        public List<FeedEntry> Links = new List<FeedEntry>();
    }

    public static class StatusClient
    {
        public const string Host = Patcher.Portal;
        public const int LoginPort = 1119, WorldPort = 8085;
        public const string FeedUrl = "https://raw.githubusercontent.com/defexnicolas/wow-classic-launcher/status/status.json";

        public static async Task<ServerStatus> FetchAsync()
        {
            var s = new ServerStatus();
            var login = ProbeAsync(Host, LoginPort);
            var world = ProbeAsync(Host, WorldPort);
            var feed = FeedAsync(s);
            s.LoginMs = await login;
            s.LoginUp = s.LoginMs >= 0;
            s.WorldUp = await world >= 0;
            await feed;
            return s;
        }

        // Tiempo de conexion en ms, o -1 si no conecta en 3 s. Se cierra al instante: no se envia nada.
        static async Task<long> ProbeAsync(string host, int port)
        {
            var c = new TcpClient();
            try
            {
                var sw = Stopwatch.StartNew();
                var connect = c.ConnectAsync(host, port);
                if (await Task.WhenAny(connect, Task.Delay(3000)) != connect) return -1;
                await connect;
                return sw.ElapsedMilliseconds;
            }
            catch { return -1; }
            finally { try { c.Close(); } catch { } }
        }

        static async Task FeedAsync(ServerStatus s)
        {
            try
            {
                string json;
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers[HttpRequestHeader.CacheControl] = "no-cache";
                    json = await wc.DownloadStringTaskAsync(FeedUrl + "?t=" + DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute);
                }
                var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) return;
                s.FeedOk = true;

                DateTime updated;
                if (DateTime.TryParse(Str(root, "updated"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out updated))
                    s.FeedFresh = (DateTime.UtcNow - updated).TotalMinutes < 15;

                var realm = root.ContainsKey("realm") ? root["realm"] as Dictionary<string, object> : null;
                if (realm != null && s.FeedFresh)
                {
                    s.FeedLoginUp = Bool(realm, "login");
                    s.FeedWorldUp = Bool(realm, "world");
                }
                s.Maintenance = Bool(root, "maintenance");
                s.Root = new FeedEntry(root);

                var launcher = root.ContainsKey("launcher") ? root["launcher"] as Dictionary<string, object> : null;
                if (launcher != null) { s.LatestLauncher = Str(launcher, "version"); s.LauncherUrl = Str(launcher, "url"); }

                foreach (var o in List(root, "news"))
                {
                    var d = o as Dictionary<string, object>;
                    if (d != null) s.News.Add(new FeedEntry(d));
                }
                foreach (var o in List(root, "links"))
                {
                    var d = o as Dictionary<string, object>;
                    if (d != null && Str(d, "label") != null && Str(d, "url") != null) s.Links.Add(new FeedEntry(d));
                }
            }
            catch { }
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : null;
        }
        static bool Bool(Dictionary<string, object> d, string k)
        {
            object v; return d.TryGetValue(k, out v) && v is bool && (bool)v;
        }
        static IEnumerable List(Dictionary<string, object> d, string k)
        {
            object v; return d.TryGetValue(k, out v) && v is IEnumerable && !(v is string) ? (IEnumerable)v : new object[0];
        }

        // "1.2.0" > "1.1.9"
        public static bool IsNewer(string latest, string current)
        {
            Version a, b;
            return latest != null && Version.TryParse(latest, out a) && Version.TryParse(current, out b) && a > b;
        }
    }
}
