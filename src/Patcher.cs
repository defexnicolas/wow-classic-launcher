// Parcheo del cliente: port a C# de play-beta.bat (Jugar-Beta.bat).
//
// Que hace, y nada mas:
//   1. Asegura WTF\BetaSuspendedTest.wtf con la linea `SET portal "auth.gpon.com.co"`.
//   2. Abre WowB.exe con `-config BetaSuspendedTest.wtf` (o se engancha al que ya este abierto desde esa carpeta).
//   3. Busca en el HEAP del cliente (memoria privada, lectura/escritura; nunca el codigo) el almacen de 12 claves
//      publicas Ed25519 de NetClient::CertStore y sustituye la del grupo 8 por la clave PUBLICA del servidor.
//      Son 32 bytes, solo si el bloque entero valida (ids, las 12 claves conocidas, flags y colas) y solo si los
//      bytes actuales son los esperados justo antes de escribir.
//   4. Sigue vigilando y reaplica si el cliente recrea el almacen. Termina cuando se cierra el juego.
// No toca ficheros del juego (salvo esa linea del .wtf), no pide administrador, no descarga nada.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ForeverLauncher
{
    public enum MsgKind { Info, Good, Warn, Error, Dim }
    public enum PatchPhase { Starting, WaitingLogin, Ready, Closed, Failed }

    public sealed class Patcher
    {
        public const string Portal = "auth.gpon.com.co";   // el certificado del servidor esta emitido para este nombre: NO una IP
        public const string ConfigName = "BetaSuspendedTest.wtf";
        public const string ExeName = "WowB.exe";
        public const string ExpectedVersion = "1.60.1.70009";   // build con el que se MIDIERON las claves
        public static readonly string[] SupportedVersions = { "1.60.1.69913", "1.60.1.69977", "1.60.1.70009" };

        // Las 12 claves del almacen, medidas el 2026-09-20 y reverificadas en 69977 (2026-09-23) y 70009 (2026-09-25).
        // Entrada = u32 id + 32 B clave + flag + 7F 00 00.
        static readonly string[] KnownKeys = {
            "9B0671C815DFF513BFD4A2B26AE1F84EC9106841B2FB620DB65F6ADE7C21AD06", // 1  ancla de busqueda (nunca se toca)
            "112451B9843C2799E4750AA3D9B9AFABF536A645B863C4ADB2078B932B354E04", // 2
            "A6B858485748BF38BE193517AB90F8DE169EFF0989EA9360DB346A378B0FFE15", // 3
            "50FFDEB807F8FF73A299A16000AA6575C5945BE875ADFC8795374ED3415249AC", // 4
            "9F842D078755647C008E5FE3E12B839A981DE02A1F520CB2545CC04CD2323E0E", // 5
            "B8CC75864D8EF461D8DD6AA7425E2C63C24D3982066B8D773A1549BFE24E05EE", // 6
            "76BB7BBDD9F34E124A4573C3AA227E3C87CC603506697D054F6FDEC342E56EBF", // 7
            "1FD6DD8FA0EC30D39E3F72E755B8A045BDE0F70449DC71008B767C2EAA89FB9F", // 8  <- la que se sustituye
            "15D618BD7DB577BD9A8D45769C59E4FC631633BF447398A4B489B4C26FBC03AD", // 9  flag=0, NO TOCAR
            "B34EC592D2AC4993F5FFC85B15C3DA9078694051CB224345592AF7136D796C99", // 10
            "9E91586DD4B115AB0568B21959E181F6545910BC5E2F30B8975CA09D7BC3EFCE", // 11
            "769C824A1DADD19AEEFA420414D1732DAF44537F98AA229FC5477BCA55F9D59A"  // 12
        };
        // Clave PUBLICA del servidor (no es un secreto: el cliente la usa para verificar al servidor).
        const string NewKeyHex = "02596F0D0C061A8B30745988FD72C59E29EC367FB0F341F28E0F08D037BAFC69";
        const int TargetGroup = 8, EntrySize = 40, EntryCount = 12, BlockSize = EntrySize * EntryCount;
        static readonly byte[] ExpectedFlags = { 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1 };

        enum StoreState { Invalid, Original, Patched }

        public event Action<Msg> Message;
        public event Action<PatchPhase> PhaseChanged;

        readonly string gameDir, exePath, logFile;
        volatile bool stopRequested;
        string lastScan = "";

        public Patcher(string gameDir)
        {
            this.gameDir = gameDir;
            exePath = Path.GetFullPath(Path.Combine(gameDir, ExeName));
            logFile = Path.Combine(gameDir, "Logs", "launcher.log");
        }

        public string LogFile { get { return logFile; } }

        // Devuelve null si la carpeta vale; si no, el motivo. 'version' = FileVersion de WowB.exe (o null).
        public static Msg CheckGameDir(string dir, out string version)
        {
            version = null;
            if (string.IsNullOrEmpty(dir)) return new Msg(MsgKind.Warn, "dir.choose");
            string exe = Path.Combine(dir, ExeName);
            if (!File.Exists(exe)) return new Msg(MsgKind.Warn, "dir.noexe");
            version = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            if (Array.IndexOf(SupportedVersions, version) < 0)
                return new Msg(MsgKind.Warn, "dir.badbuild", version, string.Join(", ", SupportedVersions));
            return null;
        }

        public void Stop() { stopRequested = true; }

        void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFile));
                File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine);
            }
            catch { }
        }

        // El registro va siempre en espanol (lo lee el administrador); la ventana, en el idioma elegido.
        void Say(MsgKind kind, string key, params object[] args)
        {
            var m = new Msg(kind, key, args);
            Log(m.ToString("es"));
            var h = Message; if (h != null) h(m);
        }

        void Say(Msg m)
        {
            Log(m.ToString("es"));
            var h = Message; if (h != null) h(m);
        }

        void Phase(PatchPhase p)
        {
            var h = PhaseChanged; if (h != null) h(p);
        }

        // ------------------------------------------------------------------ configuracion (portal)
        public void EnsureConfig()
        {
            string wtfDir = Path.Combine(gameDir, "WTF");
            string wtf = Path.Combine(wtfDir, ConfigName);
            Directory.CreateDirectory(wtfDir);
            // Si no existe, se parte del Config.wtf de ESTA maquina (su GPU, su sonido). Solo el portal es obligatorio.
            // Config.wtf no se toca: abrir el juego desde Battle.net sigue yendo al beta oficial.
            string baseConfig = Path.Combine(wtfDir, "Config.wtf");
            if (!File.Exists(wtf) && File.Exists(baseConfig))
            {
                File.Copy(baseConfig, wtf);
                Say(MsgKind.Dim, "p.wtfcreated", ConfigName);
            }
            var lines = File.Exists(wtf) ? File.ReadAllLines(wtf).ToList() : new List<string>();
            string wanted = "SET portal \"" + Portal + "\"";
            bool changed = false;
            if (!lines.Any(l => l.StartsWith("SET portal "))) { lines.Add(wanted); changed = true; }
            else if (!lines.Contains(wanted))
            {
                lines = lines.Select(l => l.StartsWith("SET portal ") ? wanted : l).ToList();
                changed = true;
            }
            if (!lines.Any(l => l.StartsWith("SET textLocale "))) { lines.Add("SET textLocale \"enUS\""); changed = true; }
            if (changed)
            {
                File.WriteAllLines(wtf, lines);
                Say(MsgKind.Dim, "p.wtfupdated", ConfigName, Portal);
            }
        }

        // ------------------------------------------------------------------ bucle principal (hilo propio)
        public void Run()
        {
            try { RunCore(); }
            catch (Exception ex)
            {
                Say(MsgKind.Error, "p.error", ex.Message);
                Log("----- fin (error) " + ex);
                Phase(PatchPhase.Failed);
            }
        }

        List<Process> FindClients()
        {
            var list = new List<Process>();
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            {
                try
                {
                    if (string.Equals(Path.GetFullPath(p.MainModule.FileName), exePath, StringComparison.OrdinalIgnoreCase))
                        list.Add(p);
                }
                catch { }
            }
            return list;
        }

        static bool Alive(int pid)
        {
            try { return !Process.GetProcessById(pid).HasExited; }
            catch { return false; }
        }

        void RunCore()
        {
            Log("----- inicio (launcher " + App.Version + ")");
            string version;
            Msg err = CheckGameDir(gameDir, out version);
            if (err != null) { Say(new Msg(MsgKind.Error, err.Key, err.Args)); Phase(PatchPhase.Failed); return; }
            if (version != ExpectedVersion) Log("AVISO: build " + version + "; las claves se midieron con " + ExpectedVersion + ".");
            Phase(PatchPhase.Starting);
            EnsureConfig();

            var clients = FindClients();
            Process client;
            if (clients.Count > 1) { Say(MsgKind.Error, "p.multi"); Phase(PatchPhase.Failed); return; }
            if (clients.Count == 1)
            {
                client = clients[0];
                Say(MsgKind.Info, "p.attached");
            }
            else
            {
                Say(MsgKind.Info, "p.opening");
                var psi = new ProcessStartInfo(exePath, "-config " + ConfigName) { WorkingDirectory = gameDir, UseShellExecute = false };
                Process.Start(psi);
                client = null;
                for (int i = 0; i < 60 && client == null && !stopRequested; i++)
                {
                    Thread.Sleep(1000);
                    clients = FindClients();
                    if (clients.Count >= 1) client = clients.OrderBy(c => { try { return c.StartTime; } catch { return DateTime.MinValue; } }).Last();
                }
                if (client == null) { Say(MsgKind.Error, "p.nostart"); Phase(PatchPhase.Failed); return; }
            }
            int pid = client.Id;
            Log("PID " + pid);
            Phase(PatchPhase.WaitingLogin);

            long store = 0;            // direccion del almacen vivo (0 = ninguno)
            bool everPatched = false, waitingSaid = false;
            while (!stopRequested)
            {
                if (!Alive(pid))
                {
                    Say(MsgKind.Dim, "p.closed");
                    Log("----- fin (juego cerrado)");
                    Phase(PatchPhase.Closed);
                    return;
                }
                if (store != 0)
                {
                    var st = GetState(pid, store);
                    if (st == StoreState.Patched) { Thread.Sleep(3000); continue; }
                    if (st == StoreState.Original)
                    {
                        Say(MsgKind.Warn, "p.restored");
                        Patch(pid, store); Ready(); continue;
                    }
                    Say(MsgKind.Dim, "p.released");
                    store = 0;
                    continue;
                }

                var found = FindStores(pid);
                if (found.Count == 1)
                {
                    store = found[0].Key;
                    Log(string.Format("almacen en 0x{0:X} ({1}); {2}", store, found[0].Value, lastScan));
                    if (found[0].Value == StoreState.Patched)
                        Say(MsgKind.Good, "p.already");
                    else
                    {
                        Say(MsgKind.Info, "p.found");
                        try { Patch(pid, store); }
                        catch (Exception ex)
                        {
                            Say(MsgKind.Warn, "p.patchfail", ex.Message);
                            store = 0; Thread.Sleep(2000); continue;
                        }
                    }
                    everPatched = true;
                    Ready();
                    continue;
                }
                if (found.Count > 1)
                    Say(MsgKind.Warn, "p.many", found.Count);
                else if (!waitingSaid)
                {
                    Say(MsgKind.Dim, "p.waiting");
                    Log("busqueda: " + lastScan);
                    waitingSaid = true;
                }
                // Antes del primer parche se busca casi sin pausa: si la clave llega antes de ENTER_ENCRYPTED_MODE,
                // el PRIMER login ya entra (medido 2026-09-22). Jugando, cada 15 s.
                Thread.Sleep(everPatched ? 15000 : 100);
            }
            Log("----- fin (launcher cerrado)");
        }

        void Ready()
        {
            Say(MsgKind.Good, "p.ready");
            Phase(PatchPhase.Ready);
        }

        // ------------------------------------------------------------------ validacion del almacen
        StoreState GetState(int pid, long array)
        {
            byte[] raw;
            try { raw = Native.ReadHeapBlock(pid, array, BlockSize); } catch { return StoreState.Invalid; }
            if (raw == null) return StoreState.Invalid;
            for (int e = 0; e < EntryCount; e++)
            {
                int o = e * EntrySize;
                if (BitConverter.ToInt32(raw, o) != e + 1) return StoreState.Invalid;
                string key = Hex(raw, o + 4, 32);
                bool ok = e == TargetGroup - 1 ? (key == KnownKeys[e] || key == NewKeyHex) : key == KnownKeys[e];
                if (!ok) return StoreState.Invalid;
                if (raw[o + 36] != ExpectedFlags[e]) return StoreState.Invalid;
                if (raw[o + 37] != 0x7F || raw[o + 38] != 0 || raw[o + 39] != 0) return StoreState.Invalid;
            }
            string key8 = Hex(raw, (TargetGroup - 1) * EntrySize + 4, 32);
            return key8 == NewKeyHex ? StoreState.Patched : StoreState.Original;
        }

        List<KeyValuePair<long, StoreState>> FindStores(int pid)
        {
            var sw = Stopwatch.StartNew();
            long[] hits = Native.Scan(pid, FromHex(KnownKeys[0]));
            var found = new List<KeyValuePair<long, StoreState>>();
            foreach (long keyAddr in hits)
            {
                long array = keyAddr - 4;
                var st = GetState(pid, array);
                if (st != StoreState.Invalid) found.Add(new KeyValuePair<long, StoreState>(array, st));
            }
            lastScan = string.Format("{0:N0} MB en {1} regiones, {2:N1} s, {3} copias de la clave, {4} almacen(es) validos",
                Native.LastBytesScanned / (1024 * 1024), Native.LastRegionsScanned, sw.Elapsed.TotalSeconds, hits.Length, found.Count);
            return found;
        }

        void Patch(int pid, long array)
        {
            long keyAddr = array + (TargetGroup - 1) * EntrySize + 4;
            for (int attempt = 1; ; attempt++)
            {
                try { Native.Write32(pid, keyAddr, FromHex(KnownKeys[TargetGroup - 1]), FromHex(NewKeyHex)); break; }
                catch (BytesChangedException) { if (attempt >= 20) throw; Thread.Sleep(300); }
            }
            if (GetState(pid, array) != StoreState.Patched) throw new Exception("tras escribir, el almacen no valida como parcheado");
            Log(string.Format("clave del grupo 8 escrita en 0x{0:X} (almacen 0x{1:X})", keyAddr, array));
        }

        static string Hex(byte[] b, int offset, int count)
        {
            return BitConverter.ToString(b, offset, count).Replace("-", "");
        }

        static byte[] FromHex(string h)
        {
            var b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
            return b;
        }
    }

    sealed class BytesChangedException : Exception
    {
        public BytesChangedException() : base("los bytes cambiaron justo antes de escribir") { }
    }

    // Acceso a la memoria del cliente. Identico a la clase JugarBeta del .bat.
    static class Native
    {
        const uint MEM_COMMIT = 0x1000, MEM_PRIVATE = 0x20000, PAGE_READWRITE = 4;
        const uint PROCESS_QUERY_INFORMATION = 0x400, PROCESS_VM_READ = 0x10, PROCESS_VM_WRITE = 0x20, PROCESS_VM_OPERATION = 0x8;

        [StructLayout(LayoutKind.Sequential)]
        struct Region
        {
            public IntPtr BaseAddress, AllocationBase;
            public uint AllocationProtect;
            public UIntPtr RegionSize;
            public uint State, Protect, Type;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr p, IntPtr a, byte[] d, UIntPtr n, out UIntPtr r);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr p, IntPtr a, byte[] d, UIntPtr n, out UIntPtr w);
        [DllImport("kernel32.dll", SetLastError = true)] static extern UIntPtr VirtualQueryEx(IntPtr p, IntPtr a, out Region r, UIntPtr n);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr p);

        public static long LastBytesScanned;
        public static int LastRegionsScanned;

        static IntPtr Open(int pid, bool forWrite)
        {
            uint access = PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | (forWrite ? PROCESS_VM_WRITE | PROCESS_VM_OPERATION : 0);
            IntPtr p = OpenProcess(access, false, pid);
            if (p == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess");
            return p;
        }
        static Region Query(IntPtr p, long a)
        {
            Region r;
            if (VirtualQueryEx(p, new IntPtr(a), out r, new UIntPtr((uint)Marshal.SizeOf(typeof(Region)))) == UIntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualQueryEx");
            return r;
        }
        static bool IsHeap(Region r) { return r.State == MEM_COMMIT && r.Type == MEM_PRIVATE && r.Protect == PAGE_READWRITE; }
        static byte[] Read(IntPtr p, long a, int n)
        {
            byte[] d = new byte[n]; UIntPtr r;
            if (!ReadProcessMemory(p, new IntPtr(a), d, new UIntPtr((uint)n), out r) || r.ToUInt64() != (ulong)n)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadProcessMemory @0x" + a.ToString("X"));
            return d;
        }

        // Busca 'needle' en el heap (MEM_PRIVATE+PAGE_READWRITE committed), regiones en paralelo.
        // El almacen siempre ha salido alineado a 4, asi que basta probar posiciones multiplo de 4.
        public static long[] Scan(int pid, byte[] needle)
        {
            IntPtr p = Open(pid, false);
            try
            {
                var regions = new List<long[]>();
                long a = 0;
                while (a < 0x7FFFFFFF0000L)
                {
                    Region r;
                    if (VirtualQueryEx(p, new IntPtr(a), out r, new UIntPtr((uint)Marshal.SizeOf(typeof(Region)))) == UIntPtr.Zero) break;
                    long b = r.BaseAddress.ToInt64(), s = (long)r.RegionSize.ToUInt64();
                    if (s <= 0) break;
                    if (IsHeap(r)) regions.Add(new long[] { b, s });
                    if (b + s <= a) break;
                    a = b + s;
                }
                var hits = new List<long>();
                long total = 0;
                const int chunk = 4 * 1024 * 1024;
                byte n0 = needle[0], n1 = needle[1], n2 = needle[2], n3 = needle[3];
                Parallel.ForEach(regions,
                    () => new byte[chunk],
                    (reg, state, buf) =>
                    {
                        long rb = reg[0], rs = reg[1], off = 0, scanned = 0;
                        while (off < rs)
                        {
                            int take = (int)Math.Min((long)chunk, rs - off);
                            UIntPtr got;
                            if (ReadProcessMemory(p, new IntPtr(rb + off), buf, new UIntPtr((uint)take), out got) && got.ToUInt64() > 0)
                            {
                                int valid = (int)got.ToUInt64();
                                scanned += valid;
                                int last = valid - needle.Length;
                                for (int i = 0; i <= last; i += 4)
                                {
                                    if (buf[i] != n0 || buf[i + 1] != n1 || buf[i + 2] != n2 || buf[i + 3] != n3) continue;
                                    int j = 4;
                                    while (j < needle.Length && buf[i + j] == needle[j]) j++;
                                    if (j == needle.Length) lock (hits) hits.Add(rb + off + i);
                                }
                            }
                            if (take < chunk) break;
                            off += chunk - needle.Length;   // solape (multiplo de 4) para no perder coincidencias
                        }
                        Interlocked.Add(ref total, scanned);
                        return buf;
                    },
                    buf => { });
                LastBytesScanned = total;
                LastRegionsScanned = regions.Count;
                hits.Sort();
                return hits.ToArray();
            }
            finally { CloseHandle(p); }
        }

        // Lee [a, a+n) solo si cae entero en UNA region de heap; si no, null.
        public static byte[] ReadHeapBlock(int pid, long a, int n)
        {
            IntPtr p = Open(pid, false);
            try
            {
                Region r = Query(p, a);
                long end = r.BaseAddress.ToInt64() + (long)r.RegionSize.ToUInt64();
                if (!IsHeap(r) || a < r.BaseAddress.ToInt64() || a + n > end) return null;
                return Read(p, a, n);
            }
            finally { CloseHandle(p); }
        }

        // Escribe 32 bytes si (y solo si) la pagina es heap RW y los bytes actuales son 'before'.
        public static void Write32(int pid, long a, byte[] before, byte[] after)
        {
            IntPtr p = Open(pid, true);
            try
            {
                Region r = Query(p, a);
                long end = r.BaseAddress.ToInt64() + (long)r.RegionSize.ToUInt64();
                if (!IsHeap(r) || a + 32 > end) throw new Exception("ABORTA: la clave no esta en heap MEM_PRIVATE+PAGE_READWRITE");
                byte[] now = Read(p, a, 32);
                for (int i = 0; i < 32; i++) if (now[i] != before[i]) throw new BytesChangedException();
                UIntPtr w;
                if (!WriteProcessMemory(p, new IntPtr(a), after, new UIntPtr(32), out w) || w.ToUInt64() != 32)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory");
                byte[] check = Read(p, a, 32);
                for (int i = 0; i < 32; i++) if (check[i] != after[i]) throw new Exception("Verificacion de relectura fallida");
            }
            finally { CloseHandle(p); }
        }
    }
}
