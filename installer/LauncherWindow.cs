using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows;
using WF = System.Windows.Forms;

partial class Installer
{
    static int RunLauncher(string[] args)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        if (args.Length == 2 && args[0] == "--launcher-test") return TestLauncher(args[1]);
        if (args.Length == 2 && args[0] == "--release-test") {
            try {
                string text = FetchManifest();
                ReleaseManifest release = ParseManifest(text);
                if (release.Version == ParseManifest(ResourceText("manifest.json")).Version)
                    Require(text == ResourceText("manifest.json"), "Published and bundled manifests differ");
                File.WriteAllText(args[1], "PASS: GitHub manifest " + release.Version + ", " + release.Packages.Length + " packages.");
                return 0;
            } catch (Exception error) { File.WriteAllText(args[1], error.ToString()); return 1; }
        }
        if (args.Length == 2 && (args[0].StartsWith("--preview") || args[0] == "--ui-test" || args[0] == "--demo-export")) {
            try {
                using (var window = new LauncherWindow(true)) {
                    if (args[0] == "--ui-test") return window.TestInterface(args[1]);
                    if (args[0] == "--demo-export") { window.ExportAnimation(args[1]); return 0; }
                    window.SetPreviewState(args[0] == "--preview" ? "ready" : args[0].Substring(10));
                    window.SavePreview(args[1]);
                }
                return 0;
            } catch (Exception error) { File.WriteAllText(args[1] + ".error.txt", error.ToString()); return 1; }
        }
        bool demo = args.Length == 1 && args[0] == "--demo";
        bool first;
        using (var instance = new Mutex(true, @"Local\JAGO-Sever2026-Launcher" + (demo ? "-Demo" : ""), out first)) {
            if (!first) { WF.MessageBox.Show("Лаунчер уже открыт.", "7 Days to Die"); return 0; }
            var application = new System.Windows.Application();
            using (var window = new LauncherWindow(demo)) return application.Run(window);
        }
    }

    sealed partial class LauncherWindow : Window, IDisposable
    {
        readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie");
        readonly string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        readonly string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JAGO");
        readonly ReleaseManifest bundled;
        readonly bool previewMode;
        const string JoinServer = "185.189.255.19:27100";
        ReleaseManifest remote;
        Receipt installed;
        ModPackage[] previewChanges;
        string gameDir;
        string lastError;
        bool previewGameValid = true;
        bool offline, busy, launching, disposed, ready;

        string GamePathFile { get { return Path.Combine(appDir, "game-path.txt"); } }
        string CachedManifest { get { return Path.Combine(appDir, "manifest.json"); } }
        string StableLauncher { get { return Path.Combine(appDir, "JAGO-Launcher.exe"); } }
        static bool ValidGame(string path) { return !String.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "7DaysToDie.exe")); }
        bool HasGame() { return previewMode ? previewGameValid : ValidGame(gameDir); }
        ModPackage[] ViewChanges() { return previewMode ? previewChanges : ChangedPackages(remote, installed, dataDir); }

        string SavedGame()
        {
            try {
                if (File.Exists(GamePathFile)) { string saved = File.ReadAllText(GamePathFile).Trim(); if (ValidGame(saved)) return saved; }
                return FindGame();
            } catch (IOException) { return ""; } catch (UnauthorizedAccessException) { return ""; }
        }

        static void SaveText(string path, string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N");
            try { File.WriteAllText(temp, value); if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        void Browse()
        {
            using (var dialog = new WF.FolderBrowserDialog { Description = "Выберите папку с 7DaysToDie.exe", SelectedPath = gameDir, ShowNewFolderButton = false }) {
                if (dialog.ShowDialog(new NativeOwner(this)) != WF.DialogResult.OK) return;
                if (!ValidGame(dialog.SelectedPath)) { Notify("В этой папке нет 7DaysToDie.exe."); return; }
                gameDir = dialog.SelectedPath;
                if (!previewMode) try { SaveText(GamePathFile, gameDir); } catch (Exception error) { SetError(error); }
                previewGameValid = true; RenderState();
                if (!previewMode) TryAutomaticUpdate();
            }
        }

        void RenderState()
        {
            folder.Text = String.IsNullOrEmpty(gameDir) ? "Папка игры не выбрана" : gameDir;
            folder.ToolTip = folder.Text;
            version.Text = "Установлено: " + (installed == null ? "—" : String.IsNullOrEmpty(installed.Release) ? "прежняя сборка" : installed.Release) +
                "\nДоступно: " + (remote == null ? "проверяем…" : remote.Version) + "   ·   Игра " + (remote ?? bundled).GameVersion;
            remove.IsEnabled = !busy && !launching && installed != null;
            browse.IsEnabled = retry.IsEnabled = !busy && !launching;
            update.IsEnabled = !busy && !launching;
            ready = remote != null && installed != null && IsCurrent(remote, installed, ViewChanges());
            play.IsEnabled = !busy && !launching && ready && HasGame();
            play.ToolTip = null;
            update.ToolTip = installed == null ? "Установить сборку" : ready ? "Проверить обновления" : "Обновить сборку";
            if (busy || launching) return;
            progress.Visibility = Visibility.Collapsed;
            SetAtmosphere("idle", false);
            if (remote == null) status.Text = "Проверяем обновления…";
            else if (!HasGame()) status.Text = "Выберите папку игры в настройках";
            else if (ready) status.Text = offline ? "Офлайн · доступна сохранённая сборка" : "";
            else {
                long bytes = ViewChanges().Where(p => !IsBundled(p, bundled)).Sum(p => p.Bytes);
                status.Text = installed == null ? "Подготовка установки сборки" : "Доступно обновление";
                update.ToolTip = "Модулей: " + ViewChanges().Length + (bytes > 0 ? " · Загрузка " + FormatSize(bytes) : " · Файлы доступны локально");
                if (offline && bytes > 0) { update.IsEnabled = false; status.Text = "Нет сети · повторите проверку в настройках"; }
            }
        }

        void Notify(string message) { status.Text = message; status.ToolTip = message; }
        void SetError(Exception error)
        {
            lastError = error.ToString();
            Notify("Не удалось завершить операцию · подробности в настройках");
            SetAtmosphere("error", false);
        }

        void Work(Func<Action<int, string>, object> action, Action<object> completed, string title)
        {
            lastError = null; HideSettings(); busy = true; RenderState(); status.Text = "";
            progress.Value = 0; progress.IsIndeterminate = true; progress.Visibility = Visibility.Visible; SetAtmosphere("updating", false);
            var worker = new BackgroundWorker { WorkerReportsProgress = true };
            worker.ProgressChanged += delegate(object sender, ProgressChangedEventArgs e) {
                if (disposed) return;
                progress.IsIndeterminate = false; progress.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage));
                status.Text = ""; status.ToolTip = (string)e.UserState;
            };
            worker.DoWork += delegate(object sender, DoWorkEventArgs e) { e.Result = action((percent, text) => worker.ReportProgress(percent, text)); };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e) {
                worker.Dispose(); busy = false;
                if (disposed) return;
                progress.Visibility = Visibility.Collapsed;
                if (e.Error == null) completed(e.Result);
                else { RenderState(); SetError(e.Error); }
            };
            worker.RunWorkerAsync();
        }

        void CheckRelease()
        {
            if (busy || launching) return;
            if (previewMode) { StartDemoUpdate(); return; }
            remote = null;
            Work(report => {
                Receipt local = ReadReceipt(dataDir);
                ReleaseManifest release; string text;
                try { text = FetchManifest(); release = ParseManifest(text); }
                catch (WebException) {
                    release = File.Exists(CachedManifest) ? ParseManifest(File.ReadAllText(CachedManifest)) : bundled;
                    return Tuple.Create(release, local, true);
                }
                SaveText(CachedManifest, text); return Tuple.Create(release, local, false);
            }, result => {
                var state = (Tuple<ReleaseManifest, Receipt, bool>)result;
                remote = state.Item1; installed = state.Item2; offline = state.Item3; RenderState();
                TryAutomaticUpdate();
            }, "Проверяем обновления");
        }

        string InstallLauncherCopy()
        {
            string current = WF.Application.ExecutablePath;
            if (String.Equals(current, StableLauncher, StringComparison.OrdinalIgnoreCase)) return current;
            Directory.CreateDirectory(appDir);
            string temp = StableLauncher + "." + Guid.NewGuid().ToString("N");
            try { File.Copy(current, temp); if (File.Exists(StableLauncher)) File.Replace(temp, StableLauncher, null); else File.Move(temp, StableLauncher); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return StableLauncher;
        }

        void TryAutomaticUpdate()
        {
            if (previewMode || disposed || busy || launching || remote == null || ready || !HasGame()) return;
            if (offline && ViewChanges().Any(p => !IsBundled(p, bundled))) return;
            UpdateMods();
        }

        void UpdateMods()
        {
            if (busy || launching) return;
            if (previewMode) { StartDemoUpdate(); return; }
            if (!HasGame()) { ShowSettings(); Browse(); return; }
            if (remote == null || ready) { CheckRelease(); return; }
            if (GameRunning()) { Notify("Закройте игру перед обновлением"); return; }
            Work(report => {
                report(4, "Подготовка лаунчера…"); string executable = InstallLauncherCopy(); SaveText(GamePathFile, gameDir);
                ApplyRelease(remote, bundled, gameDir, dataDir, desktop, executable, report, beforeCommit: () => {
                    if (GameRunning()) throw new IOException("Закройте игру перед обновлением.");
                });
                return ReadReceipt(dataDir);
            }, result => { installed = (Receipt)result; RenderState(); }, installed == null ? "Устанавливаем сборку" : "Обновляем сборку");
        }

        void Play()
        {
            if (busy || launching || !ready || !HasGame()) return;
            if (!previewMode && GameRunning()) { Notify("Игра уже запущена"); return; }
            HideSettings(); launching = true; RenderState(); status.Text = "";
            SetAtmosphere("launch", false);
            Delay(2800, delegate {
                if (disposed) return;
                if (previewMode) { launching = false; RenderState(); return; }
                EnsureSteam(delegate {
                try {
                    if (GameRunning()) throw new IOException("Игра уже запущена.");
                    string logs = Path.Combine(appDir, "logs"); Directory.CreateDirectory(logs);
                    string log = Path.Combine(logs, "join-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".log");
                    var game = Process.Start(new ProcessStartInfo(Path.Combine(gameDir, "7DaysToDie.exe"),
                        "-UserDataFolder=\"" + dataDir + "\" -logfile \"" + log + "\"") { WorkingDirectory = gameDir, UseShellExecute = false });
                    if (game == null) throw new IOException("Не удалось запустить игру.");
                    WaitAndJoin(game, log);
                } catch (Exception error) { launching = false; RenderState(); SetError(error); }
                });
            });
        }

        static bool SteamSignedIn()
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess")) {
                if (key == null || Convert.ToInt64(key.GetValue("ActiveUser", 0)) == 0) return false;
                int pid = Convert.ToInt32(key.GetValue("pid", 0));
                if (pid <= 0) return false;
                try {
                    using (var process = Process.GetProcessById(pid))
                        return !process.HasExited && String.Equals(process.ProcessName, "steam", StringComparison.OrdinalIgnoreCase);
                } catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; }
            }
        }

        void EnsureSteam(Action launchGame)
        {
            try {
                if (SteamSignedIn()) { launchGame(); return; }
                using (var steam = Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true })) {}
                status.Text = "Ожидаем входа в Steam…";
                var elapsed = Stopwatch.StartNew();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timers.Add(timer);
                Action stop = delegate { timer.Stop(); timers.Remove(timer); };
                timer.Tick += delegate {
                    try {
                        if (disposed) { stop(); return; }
                        if (SteamSignedIn()) { stop(); status.Text = ""; launchGame(); return; }
                        if (elapsed.Elapsed.TotalMinutes >= 5)
                            throw new IOException("Войдите в Steam и снова нажмите 7 Days.");
                    } catch (Exception error) { stop(); launching = false; RenderState(); SetError(error); }
                };
                Closed += delegate { stop(); };
                timer.Start();
            } catch (Exception error) { launching = false; RenderState(); SetError(error); }
        }

        void WaitAndJoin(Process game, string log)
        {
            var elapsed = Stopwatch.StartNew();
            double steamReadyAt = -1, requestAt = -1;
            long readPosition = 0;
            string recent = "";
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timers.Add(timer);
            bool stopped = false;
            Action stop = delegate { if (stopped) return; stopped = true; timer.Stop(); timers.Remove(timer); game.Dispose(); };
            timer.Tick += delegate {
                try {
                    if (disposed) { stop(); return; }
                    if (game.HasExited) throw new IOException("Игра закрылась до подключения к серверу.");
                    if (File.Exists(log)) {
                        using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                            stream.Position = Math.Min(readPosition, stream.Length);
                            using (var reader = new StreamReader(stream)) {
                                recent += reader.ReadToEnd();
                                readPosition = stream.Position;
                            }
                        }
                        if (recent.Length > 131072) recent = recent.Substring(recent.Length - 131072);
                    }
                    if (steamReadyAt < 0 && recent.Contains("[Steamworks.NET] Login ok.")) steamReadyAt = elapsed.Elapsed.TotalSeconds;
                    if (requestAt < 0 && steamReadyAt >= 0 && elapsed.Elapsed.TotalSeconds - steamReadyAt >= 10) {
                        using (var steam = Process.Start(new ProcessStartInfo("steam://connect/" + JoinServer) { UseShellExecute = true })) {}
                        requestAt = elapsed.Elapsed.TotalSeconds;
                    }
                    if (requestAt >= 0 && recent.Contains("Connecting to server " + JoinServer)) {
                        stop(); launching = false; Close(); return;
                    }
                    if (elapsed.Elapsed.TotalSeconds > 180 || (requestAt >= 0 && elapsed.Elapsed.TotalSeconds - requestAt > 30))
                        throw new IOException("Не удалось передать автоподключение. Игра оставлена открытой; подключитесь к " + JoinServer + " вручную.");
                } catch (Exception error) { stop(); launching = false; RenderState(); SetError(error); }
            };
            Closed += delegate { stop(); };
            timer.Start();
        }

        void RemoveMods()
        {
            if (busy || launching || installed == null) return;
            if (previewMode) { Notify("Это демонстрация: файлы игры не изменяются"); return; }
            if (GameRunning()) { Notify("Закройте игру перед удалением"); return; }
            if (System.Windows.MessageBox.Show(this, "Удалить сборку и восстановить моды и настройки до первой установки?", "Удаление сборки",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Work(report => Uninstall(dataDir, desktop, report), result => { installed = null; RenderState(); Notify("Прежние моды и настройки восстановлены"); }, "Удаляем сборку");
        }

        sealed class NativeOwner : WF.IWin32Window
        {
            readonly IntPtr handle;
            public NativeOwner(Window window) { handle = new System.Windows.Interop.WindowInteropHelper(window).Handle; }
            public IntPtr Handle { get { return handle; } }
        }
    }
}


