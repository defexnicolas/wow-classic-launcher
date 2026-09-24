using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.IO.Path;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

[assembly: AssemblyTitle("Classic Forever Launcher")]
[assembly: AssemblyProduct("Classic Forever Launcher")]
[assembly: AssemblyDescription("Launcher del servidor Classic Forever (codigo abierto)")]
[assembly: AssemblyVersion(ForeverLauncher.App.Version + ".0")]
[assembly: AssemblyFileVersion(ForeverLauncher.App.Version + ".0")]
[assembly: AssemblyInformationalVersion(ForeverLauncher.App.Version)]

namespace ForeverLauncher
{
    public static class App
    {
        public const string Version = "1.0.0";
        public const string ReleasesUrl = "https://github.com/defexnicolas/wow-classic-launcher/releases/latest";

        [STAThread]
        public static void Main()
        {
            bool first;
            using (var mutex = new Mutex(true, "ClassicForeverLauncher-single", out first))
            {
                if (!first)
                {
                    MessageBox.Show("El launcher ya esta abierto (mira en la bandeja del sistema, junto al reloj).", "Classic Forever");
                    return;
                }
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) => { Crash(e.Exception); e.Handled = true; app.Shutdown(); };
                var ui = new LauncherWindow(app);
                app.Run(ui.Window);
            }
        }

        public static string CrashFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassicForeverLauncher", "crash.txt"); }
        }

        static void Crash(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashFile));
                File.AppendAllText(CrashFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " v" + Version + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
                MessageBox.Show("El launcher tuvo un error inesperado y se cerrará.\n\nDetalle guardado en:\n" + CrashFile, "Classic Forever", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }
    }

    public sealed class LauncherWindow
    {
        public readonly Window Window;
        readonly Application app;
        readonly Dispatcher ui;

        Button playButton, folderButton, logButton, updateLink;
        TextBlock statusText, playersText, pingText, messageText, stepText, clientText, folderText, versionText, updateText;
        Ellipse statusDot, statusHalo, loginDot, worldDot;
        StackPanel newsList;
        WrapPanel linksPanel;
        Border updateBanner;
        Canvas stars;

        string gameDir;
        Patcher patcher;
        bool gameRunning;
        WinForms.NotifyIcon tray;
        string updateUrl = App.ReleasesUrl;

        static readonly Color Green = Color.FromRgb(0x5C, 0xD6, 0x7A), Red = Color.FromRgb(0xE0, 0x5A, 0x4E),
                              Amber = Color.FromRgb(0xF0, 0xB2, 0x3E), Grey = Color.FromRgb(0x8A, 0x8A, 0x8A);

        public LauncherWindow(Application app)
        {
            this.app = app;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ForeverLauncher.MainWindow.xaml"))
                Window = (Window)XamlReader.Load(s);
            ui = Window.Dispatcher;
            try { Window.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(AppIcon().Handle, Int32Rect.Empty, null); } catch { }

            playButton = Find<Button>("PlayButton"); folderButton = Find<Button>("FolderButton"); logButton = Find<Button>("LogButton");
            updateLink = Find<Button>("UpdateLink");
            statusText = Find<TextBlock>("StatusText"); playersText = Find<TextBlock>("PlayersText"); pingText = Find<TextBlock>("PingText");
            messageText = Find<TextBlock>("MessageText"); stepText = Find<TextBlock>("StepText"); clientText = Find<TextBlock>("ClientText");
            folderText = Find<TextBlock>("FolderText"); versionText = Find<TextBlock>("VersionText"); updateText = Find<TextBlock>("UpdateText");
            statusDot = Find<Ellipse>("StatusDot"); statusHalo = Find<Ellipse>("StatusHalo");
            loginDot = Find<Ellipse>("LoginDot"); worldDot = Find<Ellipse>("WorldDot");
            newsList = Find<StackPanel>("NewsList"); linksPanel = Find<WrapPanel>("LinksPanel");
            updateBanner = Find<Border>("UpdateBanner"); stars = Find<Canvas>("Stars");

            versionText.Text = "v" + App.Version;
            Find<Grid>("TitleBar").MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) Window.DragMove(); };
            Find<Button>("MinButton").Click += (s, e) => Window.WindowState = WindowState.Minimized;
            Find<Button>("CloseButton").Click += (s, e) => OnCloseClicked();
            playButton.Click += (s, e) => Play();
            folderButton.Click += (s, e) => PickFolder();
            logButton.Click += (s, e) => OpenLog();
            updateLink.Click += (s, e) => OpenUrl(updateUrl);
            Window.Closing += (s, e) => { if (gameRunning) { e.Cancel = true; HideToTray(); } };
            Window.Closed += (s, e) => Shutdown();

            DrawStars();
            PulseHalo();
            SetGameDir(Settings.LoadGameDir() ?? GameLocator.Find(), false);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            timer.Tick += (s, e) => RefreshStatus();
            // Tras Loaded: ahi ya existe el contexto de sincronizacion de WPF y los await vuelven al hilo de la ventana.
            Window.Loaded += (s, e) => { timer.Start(); RefreshStatus(); };
        }

        T Find<T>(string name) where T : class { return (T)Window.FindName(name); }

        static System.Drawing.Icon AppIcon()
        {
            return System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
        }

        // ------------------------------------------------------------------ escena
        void DrawStars()
        {
            var rnd = new Random(1601);
            for (int i = 0; i < 140; i++)
            {
                double size = rnd.NextDouble() < 0.12 ? 2.2 : 1.2 + rnd.NextDouble() * 0.6;
                var star = new Ellipse { Width = size, Height = size, Fill = Brushes.White, Opacity = 0.25 + rnd.NextDouble() * 0.6 };
                Canvas.SetLeft(star, rnd.NextDouble() * 978);
                Canvas.SetTop(star, Math.Pow(rnd.NextDouble(), 1.6) * 330);
                if (i % 5 == 0)
                {
                    var a = new DoubleAnimation(star.Opacity, 0.1, TimeSpan.FromSeconds(1.5 + rnd.NextDouble() * 3))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(rnd.NextDouble() * 4) };
                    star.BeginAnimation(UIElement.OpacityProperty, a);
                }
                stars.Children.Add(star);
            }
        }

        void PulseHalo()
        {
            var st = new ScaleTransform(1, 1);
            statusHalo.RenderTransform = st;
            var grow = new DoubleAnimation(1, 2.1, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            var fade = new DoubleAnimation(0.5, 0, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever };
            statusHalo.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // ------------------------------------------------------------------ estado del servidor
        async void RefreshStatus()
        {
            ServerStatus s;
            try { s = await StatusClient.FetchAsync(); } catch { return; }

            Color c; string text;
            if (s.LoginUp && s.WorldUp) { c = Green; text = "EN LÍNEA"; }
            else if (s.LoginUp) { c = Amber; text = "MUNDO NO DISPONIBLE"; }
            else if (s.FeedFresh && s.FeedLoginUp) { c = Amber; text = "NO LLEGO DESDE TU RED"; }
            else if (s.Maintenance) { c = Amber; text = "MANTENIMIENTO"; }
            else { c = Red; text = "FUERA DE LÍNEA"; }
            SetDot(statusDot, c); SetDot(statusHalo, c);
            statusText.Text = text;
            SetDot(loginDot, s.LoginUp ? Green : Red);
            SetDot(worldDot, s.WorldUp ? Green : Red);
            pingText.Text = s.LoginMs >= 0 ? s.LoginMs + " ms" : "";

            if (s.Players >= 0 && s.LoginUp)
                playersText.Text = s.Players == 1 ? "1 jugador conectado" : s.Players + " jugadores conectados";
            else if (!s.LoginUp && !s.WorldUp)
                playersText.Text = "El servidor no responde.";
            else
                playersText.Text = " ";

            messageText.Text = s.Message;
            messageText.Visibility = string.IsNullOrWhiteSpace(s.Message) ? Visibility.Collapsed : Visibility.Visible;

            if (s.FeedOk) { FillNews(s); FillLinks(s); }
            else if (newsList.Children.Count == 1 && newsList.Children[0] is TextBlock && ((TextBlock)newsList.Children[0]).Text == "Cargando…")
                ((TextBlock)newsList.Children[0]).Text = "No se pudieron cargar las novedades.";

            if (StatusClient.IsNewer(s.LatestLauncher, App.Version))
            {
                updateText.Text = "Hay una versión nueva del launcher (v" + s.LatestLauncher + ")";
                if (!string.IsNullOrEmpty(s.LauncherUrl)) updateUrl = s.LauncherUrl;
                updateBanner.Visibility = Visibility.Visible;
            }
        }

        static void SetDot(Shape e, Color c)
        {
            e.Fill = new SolidColorBrush(c);
        }

        void FillNews(ServerStatus s)
        {
            newsList.Children.Clear();
            if (s.News.Count == 0)
            {
                newsList.Children.Add(new TextBlock { Text = "Sin novedades.", Foreground = (Brush)Window.FindResource("Dim"), FontSize = 13 });
                return;
            }
            foreach (var n in s.News)
            {
                var item = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
                if (!string.IsNullOrEmpty(n.Date))
                    item.Children.Add(new TextBlock { Text = n.Date, FontSize = 11, Foreground = (Brush)Window.FindResource("Dim") });
                var title = new TextBlock { Text = n.Title ?? "", FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                if (!string.IsNullOrEmpty(n.Url))
                {
                    string url = n.Url;
                    title.Cursor = Cursors.Hand;
                    title.Foreground = (Brush)Window.FindResource("Gold");
                    title.MouseLeftButtonUp += (o, e) => OpenUrl(url);
                }
                item.Children.Add(title);
                if (!string.IsNullOrEmpty(n.Text))
                    item.Children.Add(new TextBlock { Text = n.Text, FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0xBB, 0xA5)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), LineHeight = 18 });
                newsList.Children.Add(item);
            }
        }

        void FillLinks(ServerStatus s)
        {
            linksPanel.Children.Clear();
            foreach (var l in s.Links)
            {
                string url = l.Url;
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                var b = new Button { Content = l.Label, Style = (Style)Window.FindResource("PillButton") };
                b.Click += (o, e) => OpenUrl(url);
                linksPanel.Children.Add(b);
            }
        }

        static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // ------------------------------------------------------------------ carpeta del juego
        void SetGameDir(string dir, bool save)
        {
            gameDir = dir;
            string version;
            string err = Patcher.CheckGameDir(dir, out version);
            folderText.Text = dir ?? "";
            folderText.ToolTip = dir;
            if (version != null) clientText.Text = "Cliente " + version + (err == null ? "  ✓" : "");
            else clientText.Text = "";
            if (err != null)
            {
                Step(err, MsgKind.Warn);
                folderButton.Content = dir == null ? "Elegir carpeta" : "Cambiar carpeta";
            }
            else
            {
                Step("Listo para jugar.", MsgKind.Info);
                folderButton.Content = "Cambiar carpeta";
                if (save) Settings.SaveGameDir(dir);
            }
        }

        void PickFolder()
        {
            using (var dlg = new WinForms.FolderBrowserDialog())
            {
                dlg.Description = "Elige la carpeta _classic_beta_ (la que tiene WowB.exe)";
                dlg.ShowNewFolderButton = false;
                if (gameDir != null && Directory.Exists(gameDir)) dlg.SelectedPath = gameDir;
                if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
                string dir = dlg.SelectedPath;
                // Si eligio la carpeta "World of Warcraft", se baja a _classic_beta_.
                string sub = Path.Combine(dir, "_classic_beta_");
                if (!File.Exists(Path.Combine(dir, Patcher.ExeName)) && File.Exists(Path.Combine(sub, Patcher.ExeName))) dir = sub;
                SetGameDir(dir, true);
            }
        }

        void OpenLog()
        {
            if (gameDir == null) return;
            string log = Path.Combine(gameDir, "Logs", "launcher.log");
            if (File.Exists(log)) try { Process.Start(new ProcessStartInfo(log) { UseShellExecute = true }); } catch { }
            else Step("Todavia no hay registro (se crea al jugar).", MsgKind.Dim);
        }

        // ------------------------------------------------------------------ jugar
        void Play()
        {
            string version;
            string err = Patcher.CheckGameDir(gameDir, out version);
            if (err != null) { Step(err, MsgKind.Error); if (!File.Exists(Path.Combine(gameDir ?? "", Patcher.ExeName))) PickFolder(); return; }
            Settings.SaveGameDir(gameDir);

            gameRunning = true;
            playButton.IsEnabled = false;
            playButton.Content = "EN JUEGO";
            folderButton.IsEnabled = false;

            patcher = new Patcher(gameDir);
            patcher.Message += (text, kind) => ui.BeginInvoke(new Action(() => Step(text, kind)));
            patcher.PhaseChanged += p => ui.BeginInvoke(new Action(() => OnPhase(p)));
            var t = new Thread(patcher.Run) { IsBackground = true, Name = "patcher" };
            t.Start();
        }

        void OnPhase(PatchPhase p)
        {
            switch (p)
            {
                case PatchPhase.WaitingLogin:
                    ShowTray();
                    Window.WindowState = WindowState.Minimized;
                    break;
                case PatchPhase.Ready:
                    System.Media.SystemSounds.Asterisk.Play();
                    if (tray != null)
                        tray.ShowBalloonTip(6000, "Classic Forever: listo",
                            "Si el primer intento de entrar al reino falló, vuelve a entrar sin cerrar el juego.", WinForms.ToolTipIcon.Info);
                    break;
                case PatchPhase.Closed:
                case PatchPhase.Failed:
                    gameRunning = false;
                    patcher = null;
                    playButton.IsEnabled = true;
                    playButton.Content = "JUGAR";
                    folderButton.IsEnabled = true;
                    HideTray();
                    RestoreWindow();
                    break;
            }
        }

        void Step(string text, MsgKind kind)
        {
            stepText.Text = text;
            Color c;
            switch (kind)
            {
                case MsgKind.Good: c = Green; break;
                case MsgKind.Warn: c = Amber; break;
                case MsgKind.Error: c = Red; break;
                case MsgKind.Dim: c = Color.FromRgb(0xB0, 0xA8, 0x94); break;
                default: c = Color.FromRgb(0xED, 0xE6, 0xD6); break;
            }
            stepText.Foreground = new SolidColorBrush(c);
            if (tray != null) tray.Text = Truncate("Classic Forever - " + text, 63);
        }

        static string Truncate(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        // ------------------------------------------------------------------ bandeja y cierre
        void ShowTray()
        {
            if (tray != null) return;
            tray = new WinForms.NotifyIcon { Icon = AppIcon(), Text = "Classic Forever", Visible = true };
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Abrir launcher", null, (s, e) => RestoreWindow());
            menu.Items.Add("Salir del launcher", null, (s, e) => ConfirmExit());
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => RestoreWindow();
        }

        void HideTray()
        {
            if (tray == null) return;
            tray.Visible = false;
            tray.Dispose();
            tray = null;
        }

        void HideToTray()
        {
            ShowTray();
            Window.Hide();
            tray.ShowBalloonTip(4000, "Classic Forever", "El launcher sigue aquí mientras juegas. Se cierra solo con el juego.", WinForms.ToolTipIcon.None);
        }

        void RestoreWindow()
        {
            Window.Show();
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            Window.Activate();
        }

        void OnCloseClicked()
        {
            if (gameRunning) HideToTray();
            else Window.Close();
        }

        void ConfirmExit()
        {
            if (gameRunning)
            {
                var r = MessageBox.Show("Si cierras el launcher mientras juegas y el juego se reconecta, no se volverá a poner la clave y no podrás entrar al reino.\n\n¿Salir de todas formas?",
                    "Classic Forever", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }
            Shutdown();
        }

        void Shutdown()
        {
            if (patcher != null) patcher.Stop();
            HideTray();
            app.Shutdown();
        }
    }

    static class Settings
    {
        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassicForeverLauncher", "gamedir.txt"); }
        }

        public static string LoadGameDir()
        {
            try
            {
                string d = File.ReadAllText(FilePath).Trim();
                return File.Exists(Path.Combine(d, Patcher.ExeName)) ? d : null;
            }
            catch { return null; }
        }

        public static void SaveGameDir(string dir)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)); File.WriteAllText(FilePath, dir); } catch { }
        }
    }

    static class GameLocator
    {
        // Busca _classic_beta_\WowB.exe: junto al launcher, en el registro de Blizzard y en las rutas habituales.
        public static string Find()
        {
            var candidates = new System.Collections.Generic.List<string>();
            string here = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            candidates.Add(here);
            candidates.Add(Path.Combine(here, "_classic_beta_"));
            candidates.Add(Path.Combine(Path.GetDirectoryName(here) ?? here, "_classic_beta_"));
            foreach (string key in new[] { @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft", @"SOFTWARE\Blizzard Entertainment\World of Warcraft" })
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key))
                    {
                        if (k == null) continue;
                        foreach (string v in new[] { "InstallPath", "GamePath" })
                        {
                            string p = k.GetValue(v) as string;
                            if (string.IsNullOrEmpty(p)) continue;
                            if (File.Exists(p)) p = Path.GetDirectoryName(p);
                            p = p.TrimEnd('\\');
                            string parent = Path.GetDirectoryName(p);
                            if (parent != null) candidates.Add(Path.Combine(parent, "_classic_beta_"));
                            candidates.Add(Path.Combine(p, "_classic_beta_"));
                        }
                    }
                }
                catch { }
            }
            foreach (var drive in DriveInfo.GetDrives().Where(d => { try { return d.DriveType == DriveType.Fixed && d.IsReady; } catch { return false; } }))
            {
                string r = drive.RootDirectory.FullName;
                foreach (string rel in new[] { "World of Warcraft", @"Program Files (x86)\World of Warcraft", @"Program Files\World of Warcraft",
                                               @"Games\World of Warcraft", @"Juegos\World of Warcraft", @"Battle.net\World of Warcraft",
                                               @"Blizzard\World of Warcraft", @"Program Files (x86)\Battle.net\World of Warcraft" })
                    candidates.Add(Path.Combine(r, rel, "_classic_beta_"));
            }
            foreach (string c in candidates)
            {
                try { if (File.Exists(Path.Combine(c, Patcher.ExeName))) return Path.GetFullPath(c); } catch { }
            }
            return null;
        }
    }
}
