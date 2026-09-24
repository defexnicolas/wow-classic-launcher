// Textos de la interfaz en espanol e ingles. Para anadir un idioma: una columna mas en cada fila y su codigo en Langs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ForeverLauncher
{
    // Mensaje traducible: se guarda la clave y los argumentos para poder repintarlo al cambiar de idioma.
    public sealed class Msg
    {
        public readonly string Key;
        public readonly object[] Args;
        public readonly MsgKind Kind;
        public Msg(MsgKind kind, string key, params object[] args) { Kind = kind; Key = key; Args = args; }
        public override string ToString() { return L.Get(Key, Args); }
        public string ToString(string lang) { return L.GetIn(lang, Key, Args); }
    }

    public static class L
    {
        public static readonly string[] Langs = { "es", "en" };
        public static string Lang = "es";

        static readonly Dictionary<string, string[]> T = new Dictionary<string, string[]>
        {
            // estado del servidor
            { "status.checking",    new[] { "COMPROBANDO…", "CHECKING…" } },
            { "status.online",      new[] { "EN LÍNEA", "ONLINE" } },
            { "status.worlddown",   new[] { "MUNDO NO DISPONIBLE", "WORLD UNAVAILABLE" } },
            { "status.unreachable", new[] { "NO LLEGO DESDE TU RED", "UNREACHABLE FROM YOUR NETWORK" } },
            { "status.maintenance", new[] { "MANTENIMIENTO", "MAINTENANCE" } },
            { "status.offline",     new[] { "FUERA DE LÍNEA", "OFFLINE" } },
            { "status.noresponse",  new[] { "El servidor no responde.", "The server is not responding." } },

            // ventana
            { "ui.subtitle",     new[] { "Servidor para el cliente beta 1.60.1", "Server for the 1.60.1 beta client" } },
            { "ui.news",         new[] { "NOVEDADES", "NEWS" } },
            { "ui.login",        new[] { "Login", "Login" } },
            { "ui.world",        new[] { "Mundo", "World" } },
            { "ui.play",         new[] { "JUGAR", "PLAY" } },
            { "ui.ingame",       new[] { "EN JUEGO", "IN GAME" } },
            { "ui.changefolder", new[] { "Cambiar carpeta", "Change folder" } },
            { "ui.pickfolder",   new[] { "Elegir carpeta", "Choose folder" } },
            { "ui.viewlog",      new[] { "Ver registro", "View log" } },
            { "ui.download",     new[] { "Descargar", "Download" } },
            { "ui.update",       new[] { "Hay una versión nueva del launcher (v{0})", "A new launcher version is available (v{0})" } },
            { "ui.loading",      new[] { "Cargando…", "Loading…" } },
            { "ui.newsfail",     new[] { "No se pudieron cargar las novedades.", "Couldn't load the news." } },
            { "ui.nonews",       new[] { "Sin novedades.", "No news." } },
            { "ui.client",       new[] { "Cliente {0}", "Client {0}" } },
            { "ui.ready",        new[] { "Listo para jugar.", "Ready to play." } },
            { "ui.nolog",        new[] { "Todavía no hay registro (se crea al jugar).", "No log yet (it is created when you play)." } },
            { "ui.folderdlg",    new[] { "Elige la carpeta _classic_beta_ (la que tiene WowB.exe)", "Choose the _classic_beta_ folder (the one with WowB.exe)" } },

            // bandeja y avisos
            { "tray.readytitle", new[] { "Classic Forever: listo", "Classic Forever: ready" } },
            { "tray.readytext",  new[] { "Si el primer intento de entrar al reino falló, vuelve a entrar sin cerrar el juego.",
                                         "If your first attempt to enter the realm failed, enter again without closing the game." } },
            { "tray.open",       new[] { "Abrir launcher", "Open launcher" } },
            { "tray.exit",       new[] { "Salir del launcher", "Exit launcher" } },
            { "tray.hidden",     new[] { "El launcher sigue aquí mientras juegas. Se cierra solo con el juego.",
                                         "The launcher stays here while you play. It closes by itself with the game." } },
            { "exit.confirm",    new[] { "Si cierras el launcher mientras juegas y el juego se reconecta, no se volverá a poner la clave y no podrás entrar al reino.\n\n¿Salir de todas formas?",
                                         "If you close the launcher while playing and the game reconnects, the key won't be applied again and you won't be able to enter the realm.\n\nExit anyway?" } },
            { "app.already",     new[] { "El launcher ya está abierto (mira en la bandeja del sistema, junto al reloj).",
                                         "The launcher is already running (check the system tray, next to the clock)." } },
            { "app.crash",       new[] { "El launcher tuvo un error inesperado y se cerrará.\n\nDetalle guardado en:\n{0}",
                                         "The launcher hit an unexpected error and will close.\n\nDetails saved to:\n{0}" } },

            // carpeta del juego y parcheo
            { "dir.choose",    new[] { "Elige la carpeta _classic_beta_ del juego.", "Choose the game's _classic_beta_ folder." } },
            { "dir.noexe",     new[] { "No encuentro WowB.exe en esa carpeta (tiene que ser _classic_beta_).", "WowB.exe is not in that folder (it must be _classic_beta_)." } },
            { "dir.badbuild",  new[] { "Tu cliente es la build {0}; el servidor admite {1}.", "Your client is build {0}; the server supports {1}." } },
            { "p.wtfcreated",  new[] { "Creado WTF\\{0} a partir de tu Config.wtf", "Created WTF\\{0} from your Config.wtf" } },
            { "p.wtfupdated",  new[] { "Configuración actualizada: WTF\\{0} (portal {1})", "Settings updated: WTF\\{0} (portal {1})" } },
            { "p.error",       new[] { "Error: {0}", "Error: {0}" } },
            { "p.multi",       new[] { "Hay varios WowB.exe abiertos desde esta carpeta: ciérralos todos y vuelve a empezar.",
                                       "Several WowB.exe are running from this folder: close them all and start again." } },
            { "p.attached",    new[] { "El juego ya estaba abierto: me engancho a él.", "The game was already running: attaching to it." } },
            { "p.opening",     new[] { "Abriendo el juego...", "Opening the game..." } },
            { "p.nostart",     new[] { "El juego no arrancó en 60 s.", "The game didn't start within 60 s." } },
            { "p.closed",      new[] { "El juego se cerró. Hasta la próxima.", "The game closed. See you next time." } },
            { "p.restored",    new[] { "El cliente restauró la clave original: la vuelvo a poner.", "The client restored the original key: applying it again." } },
            { "p.released",    new[] { "El almacén se liberó; si vuelves a conectar, lo buscaré de nuevo.", "The key store was released; if you reconnect, I'll look for it again." } },
            { "p.already",     new[] { "La clave ya estaba puesta.", "The key was already applied." } },
            { "p.found",       new[] { "Almacén de certificados encontrado: aplicando la clave del servidor...", "Certificate store found: applying the server key..." } },
            { "p.patchfail",   new[] { "No se pudo parchear: {0}. Reintento.", "Couldn't patch: {0}. Retrying." } },
            { "p.many",        new[] { "Hay {0} almacenes válidos a la vez: no escribo hasta que quede uno.", "{0} valid stores at once: waiting until only one remains." } },
            { "p.waiting",     new[] { "Esperando a que entres al reino...", "Waiting for you to enter the realm..." } },
            { "p.ready",       new[] { "LISTO. Si el primer intento de entrar falló, vuelve a entrar SIN cerrar el juego.",
                                       "READY. If your first attempt to enter failed, enter again WITHOUT closing the game." } },
        };

        public static string Get(string key, params object[] args) { return GetIn(Lang, key, args); }

        public static string GetIn(string lang, string key, params object[] args)
        {
            string[] row;
            if (!T.TryGetValue(key, out row)) return key;
            int i = Array.IndexOf(Langs, lang);
            string s = row[i < 0 || i >= row.Length ? 0 : i];
            return args == null || args.Length == 0 ? s : string.Format(s, args);
        }

        static string LangFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassicForeverLauncher", "lang.txt"); }
        }

        // Idioma guardado; si no hay, el de Windows (espanol si la interfaz esta en espanol, ingles en otro caso).
        public static void Load()
        {
            try
            {
                string saved = File.ReadAllText(LangFile).Trim();
                if (Array.IndexOf(Langs, saved) >= 0) { Lang = saved; return; }
            }
            catch { }
            Lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en";
        }

        public static void Save()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(LangFile)); File.WriteAllText(LangFile, Lang); } catch { }
        }
    }
}
