using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

partial class Installer
{
    const string AppReleaseRoot = "https://github.com/gyglia/7days-launcher/releases/";
    public class AppAsset { public string name; public string browser_download_url; public string digest; public long size; }
    public class AppRelease { public string tag_name { get; set; } public bool draft { get; set; } public bool prerelease { get; set; } public AppAsset[] assets { get; set; } }
    public class AppUpdate {
        public string Candidate; public string[] Targets; public string Hash; public string Version;
        public int ParentId; public long ParentStarted;
    }

    static AppAsset SelectAppAsset(AppRelease release, Version current)
    {
        Version next;
        if (release == null || release.draft || release.prerelease ||
            !Regex.IsMatch(release.tag_name ?? "", @"^v\d+\.\d+\.\d+$") ||
            !Version.TryParse(release.tag_name.Substring(1) + ".0", out next) || next <= current) return null;
        var assets = (release.assets ?? new AppAsset[0]).Where(a => a != null && a.name == "JAGO-Launcher.exe").ToArray();
        if (assets.Length != 1) throw new IOException("В релизе отсутствует файл лаунчера.");
        var asset = assets[0];
        if (asset.browser_download_url != AppReleaseRoot + "download/" + release.tag_name + "/JAGO-Launcher.exe" ||
            !Regex.IsMatch(asset.digest ?? "", @"^sha256:[a-fA-F0-9]{64}$") || asset.size <= 0 || asset.size > 1073741824)
            throw new IOException("Некорректное описание обновления лаунчера.");
        return asset;
    }

    static AppUpdate DownloadAppUpdate(string appDir, Action<int, string> report)
    {
        // Only our GUID staging folders and known files are eligible for cleanup.
        string updates = Path.Combine(appDir, "updates");
        if (Directory.Exists(updates)) foreach (string directory in Directory.GetDirectories(updates)) {
            if (!Regex.IsMatch(Path.GetFileName(directory), "^[a-f0-9]{32}$") ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            try {
                foreach (string name in new[] { "updater.exe", "new.exe", "plan.json" }) File.Delete(Path.Combine(directory, name));
                if (Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        string json;
        using (var response = GitHubRequest("https://api.github.com/repos/gyglia/7days-launcher/releases/latest", 8000).GetResponse())
        using (var reader = new StreamReader(response.GetResponseStream())) {
            char[] buffer = new char[524289]; int count = 0, read;
            while (count < buffer.Length && (read = reader.Read(buffer, count, buffer.Length - count)) > 0) count += read;
            if (count == buffer.Length) throw new IOException("Описание релиза слишком большое.");
            json = new string(buffer, 0, count);
        }
        var release = Json.Deserialize<AppRelease>(json);
        var asset = SelectAppAsset(release, Assembly.GetExecutingAssembly().GetName().Version);
        if (asset == null) return null;
        string stage = Path.Combine(appDir, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        string candidate = Path.Combine(stage, "new.exe");
        try {
            using (var response = GitHubRequest(asset.browser_download_url, 30000).GetResponse())
            using (var source = response.GetResponseStream())
            using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write)) {
                byte[] buffer = new byte[65536]; int read, previous = -1; long total = 0;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0) {
                    total += read; if (total > asset.size) throw new IOException("Неверный размер обновления.");
                    output.Write(buffer, 0, read);
                    int percent = (int)(total * 100 / asset.size);
                    if (percent != previous) { report(percent, "Обновление лаунчера"); previous = percent; }
                }
                if (total != asset.size) throw new IOException("Обновление загружено не полностью.");
            }
            var plan = new AppUpdate { Candidate = candidate, Hash = asset.digest.Substring(7), Version = release.tag_name.Substring(1) + ".0" };
            ValidateAppUpdate(plan);
            string current = System.Windows.Forms.Application.ExecutablePath;
            string stable = Path.Combine(appDir, "JAGO-Launcher.exe");
            plan.Targets = (File.Exists(stable) ? new[] { current, stable } : new[] { current }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            using (var parent = Process.GetCurrentProcess()) { plan.ParentId = parent.Id; plan.ParentStarted = parent.StartTime.ToUniversalTime().Ticks; }
            return plan;
        } catch { File.Delete(candidate); Directory.Delete(stage); throw; }
    }

    static void ValidateAppUpdate(AppUpdate plan)
    {
        using (var stream = File.OpenRead(plan.Candidate))
            if (!String.Equals(Sha256(stream), plan.Hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Контрольная сумма лаунчера не совпадает.");
        if (AssemblyName.GetAssemblyName(plan.Candidate).Version.ToString() != plan.Version)
            throw new IOException("Версия EXE не совпадает с релизом.");
    }

    static void ReplaceApp(AppUpdate plan, Action<string> restart)
    {
        ValidateAppUpdate(plan);
        var replaced = new List<Tuple<string, string>>();
        try {
            foreach (string target in plan.Targets) {
                string temp = target + ".update-" + Guid.NewGuid().ToString("N");
                string backup = target + ".previous-" + Guid.NewGuid().ToString("N");
                try {
                    File.Copy(plan.Candidate, temp);
                    File.Replace(temp, target, backup);
                    replaced.Add(Tuple.Create(target, backup));
                } finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            restart(plan.Targets[0]);
        } catch {
            foreach (var pair in Enumerable.Reverse(replaced)) File.Copy(pair.Item2, pair.Item1, true);
            throw;
        }
        foreach (var pair in replaced) { try { File.Delete(pair.Item2); } catch (IOException) {} }
    }

    static int ApplyAppUpdate(string planPath, bool test = false)
    {
        try {
            var plan = Json.Deserialize<AppUpdate>(File.ReadAllText(planPath));
            try {
                using (var parent = Process.GetProcessById(plan.ParentId)) {
                    if (parent.StartTime.ToUniversalTime().Ticks == plan.ParentStarted && !parent.WaitForExit(60000))
                        throw new IOException("Лаунчер не завершился для обновления.");
                }
            } catch (ArgumentException) { }
            // Process exit releases both its EXE and the single-instance mutex.
            ReplaceApp(plan, target => {
                string arguments = test ? "--ui-test \"" + Path.Combine(Path.GetDirectoryName(planPath), "restart-check.txt") + "\"" : "";
                using (var process = Process.Start(new ProcessStartInfo(target, arguments) { WorkingDirectory = Path.GetDirectoryName(target), UseShellExecute = false })) {
                    if (process == null) throw new IOException("Не удалось перезапустить лаунчер.");
                    if (test) {
                        if (!process.WaitForExit(30000)) { process.Kill(); process.WaitForExit(); throw new IOException("Test restart timed out"); }
                        if (process.ExitCode != 0) throw new IOException("Restarted test failed");
                    }
                }
            });
            return 0;
        } catch (Exception error) {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(planPath), "error.txt"), error.ToString());
            if (!test) System.Windows.Forms.MessageBox.Show("Не удалось обновить лаунчер. Прежняя версия сохранена. Запустите её снова.\n" + error.Message, "Обновление лаунчера");
            return 1;
        }
    }

    static int TestAppUpdate(string log)
    {
        string root = Path.Combine(Path.GetTempPath(), "launcher-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var current = Assembly.GetExecutingAssembly().GetName().Version;
            var release = new AppRelease { tag_name = "v99.0.0", assets = new[] { new AppAsset { name = "JAGO-Launcher.exe", size = 10, digest = "sha256:" + new string('a',64), browser_download_url = AppReleaseRoot + "download/v99.0.0/JAGO-Launcher.exe" } } };
            Require(SelectAppAsset(release, current) != null, "New version accepted");
            release.prerelease = true; Require(SelectAppAsset(release,current) == null, "Prerelease skipped"); release.prerelease = false;
            release.draft = true; Require(SelectAppAsset(release,current) == null, "Draft skipped"); release.draft = false;
            release.tag_name = "v" + current.ToString(3); Require(SelectAppAsset(release,current) == null, "Same version skipped");
            release.tag_name = "v1.0.0"; Require(SelectAppAsset(release,current) == null, "Older version skipped");
            release.tag_name = "v99.0.0"; release.assets[0].browser_download_url = "https://example.com/file.exe";
            bool rejected = false; try { SelectAppAsset(release,current); } catch (IOException) { rejected = true; } Require(rejected,"Foreign URL rejected");
            string candidate = Path.Combine(root,"new.exe"), first = Path.Combine(root,"first.exe"), second = Path.Combine(root,"second.exe");
            File.Copy(System.Windows.Forms.Application.ExecutablePath,candidate);
            string hash; using(var stream = File.OpenRead(candidate)) hash = Sha256(stream);
            var plan = new AppUpdate { Candidate = candidate, Hash = hash, Version = current.ToString(), Targets = new[]{first,second} };
            File.WriteAllText(first,"old first"); File.WriteAllText(second,"old second");
            rejected = false; try { ReplaceApp(plan, target => { throw new IOException("Simulated restart failure"); }); } catch(IOException) { rejected = true; }
            Require(rejected && File.ReadAllText(first) == "old first" && File.ReadAllText(second) == "old second", "Restart failure rolls back both copies");
            File.Delete(second); rejected = false;
            try { ReplaceApp(plan,target => {}); } catch(IOException) { rejected = true; }
            Require(rejected && File.ReadAllText(first) == "old first", "Second replacement failure rolls back first");
            File.WriteAllText(second,"old second"); plan.Hash = new string('0',64); rejected = false;
            try { ReplaceApp(plan,target => {}); } catch(IOException) { rejected = true; }
            Require(rejected && File.ReadAllText(first) == "old first", "Invalid hash leaves old EXE intact");
            plan.Hash = hash; plan.Version = "99.0.0.0"; rejected = false;
            try { ReplaceApp(plan,target => {}); } catch(IOException) { rejected = true; }
            Require(rejected && File.ReadAllText(first) == "old first", "Wrong binary version rejected"); plan.Version = current.ToString();
            plan.Hash = hash; bool restarted = false; ReplaceApp(plan,target => { restarted = target == first; });
            Require(restarted && new FileInfo(first).Length == new FileInfo(candidate).Length && new FileInfo(second).Length == new FileInfo(candidate).Length,"Both copies updated and restart requested");
            string helper = Path.Combine(root,"helper.exe"), planPath = Path.Combine(root,"plan.json");
            File.Copy(candidate,helper); plan.ParentId = Int32.MaxValue;
            File.WriteAllText(planPath,Json.Serialize(plan));
            using (var child = Process.Start(new ProcessStartInfo(helper,"--apply-self-update-test \"" + planPath + "\"") { UseShellExecute = false })) {
                if (!child.WaitForExit(60000)) { child.Kill(); child.WaitForExit(); throw new IOException("Updater test timed out"); }
                Require(child.ExitCode == 0,"Separate updater process succeeded");
            }
            Require(File.ReadAllText(Path.Combine(root,"restart-check.txt")).StartsWith("PASS:"),"Updated EXE really restarts and passes UI checks");
            File.WriteAllText(log,"PASS: version selection, draft/prerelease/older/same release exclusion, URL validation, checksum and binary version rejection, multi-copy replacement, rollback after replacement/restart failure, separate updater process, real EXE restart with UI verification."); return 0;
        } catch(Exception error) { File.WriteAllText(log,error.ToString()); return 1; }
        finally { Directory.Delete(root,true); }
    }
}
