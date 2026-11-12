using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

partial class Installer
{
    const string ReleaseRoot = "https://github.com/gyglia/jago-releases/releases/";
    const string ManifestUrl = ReleaseRoot + "latest/download/manifest.json";

    public class ReleaseManifest
    {
        public int Schema { get; set; }
        public string Version { get; set; }
        public string GameVersion { get; set; }
        public string Notes { get; set; }
        public ModPackage[] Packages { get; set; }
    }

    public class ModPackage
    {
        public string Name { get; set; }
        public string Sha256 { get; set; }
        public long Bytes { get; set; }
        public string[] Folders { get; set; }
        public string Url { get; set; }
    }

    static Stream OpenResource(string name)
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
    }

    static string ResourceText(string name)
    {
        using (var reader = new StreamReader(OpenResource(name))) return reader.ReadToEnd();
    }

    static ReleaseManifest ParseManifest(string text)
    {
        var manifest = new JavaScriptSerializer().Deserialize<ReleaseManifest>(text);
        if (manifest == null || manifest.Schema != 1 ||
            !Regex.IsMatch(manifest.Version ?? "", @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$") ||
            !Regex.IsMatch(manifest.GameVersion ?? "", @"^\d+(\.\d+){1,3}$") ||
            manifest.Packages == null || manifest.Packages.Length == 0 || manifest.Packages.Length > 200)
            throw new IOException("Некорректный манифест JAGO.");

        var packageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModPackage package in manifest.Packages)
        {
            if (package == null || !Regex.IsMatch(package.Name ?? "", @"^[A-Za-z0-9][A-Za-z0-9._-]{0,79}\.zip$") ||
                !packageNames.Add(package.Name) ||
                !Regex.IsMatch(package.Sha256 ?? "", @"^[A-Fa-f0-9]{64}$") ||
                package.Bytes <= 0 || package.Bytes > 2147483648L ||
                package.Folders == null || package.Folders.Length == 0 ||
                !package.Folders.All(name => SafeModName(name) && folderNames.Add(name)))
                throw new IOException("Некорректное описание пакета JAGO.");

            if (!String.IsNullOrEmpty(package.Url) &&
                !Regex.IsMatch(package.Url, "^" + Regex.Escape(ReleaseRoot) + @"download/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+\.zip$"))
                throw new IOException("Неизвестный адрес пакета " + package.Name + ".");
        }
        return manifest;
    }

    static HttpWebRequest GitHubRequest(string url, int timeout)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.UserAgent = "JAGO-Launcher/1.0";
        request.Timeout = timeout;
        request.ReadWriteTimeout = timeout;
        request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
        return request;
    }

    static string FetchManifest()
    {
        using (var response = GitHubRequest(ManifestUrl, 8000).GetResponse())
        using (var stream = response.GetResponseStream())
        using (var memory = new MemoryStream())
        {
            byte[] buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (memory.Length + read > 524288) throw new IOException("Манифест слишком большой.");
                memory.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(memory.ToArray()).TrimStart('\uFEFF');
        }
    }

    static ModPackage[] ChangedPackages(ReleaseManifest release, Receipt current, string dataDir)
    {
        return release.Packages.Where(package =>
        {
            string hash;
            return current == null || current.Packages == null ||
                !current.Packages.TryGetValue(package.Name, out hash) ||
                !String.Equals(hash, package.Sha256, StringComparison.OrdinalIgnoreCase) ||
                package.Folders.Any(folder => !File.Exists(Path.Combine(dataDir, "Mods", folder, "ModInfo.xml")));
        }).ToArray();
    }

    static bool IsCurrent(ReleaseManifest release, Receipt current, ModPackage[] changes)
    {
        return current != null && current.Release == release.Version && changes.Length == 0 &&
            new HashSet<string>(current.ActiveNames ?? current.Names, StringComparer.OrdinalIgnoreCase)
                .SetEquals(release.Packages.SelectMany(package => package.Folders));
    }

    static string Sha256(Stream stream)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }

    static bool IsBundled(ModPackage package, ReleaseManifest bundled)
    {
        return bundled.Packages.Any(candidate => candidate.Name == package.Name &&
            candidate.Bytes == package.Bytes && String.Equals(candidate.Sha256, package.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    static void DownloadPackage(ModPackage package, string file, Action<long> report)
    {
        if (String.IsNullOrEmpty(package.Url)) throw new IOException("В релизе нет ссылки на " + package.Name + ".");
        using (var response = GitHubRequest(package.Url, 25000).GetResponse())
        using (var source = response.GetResponseStream())
        using (var target = File.Create(file))
        {
            byte[] buffer = new byte[65536];
            int read;
            long bytes = 0;
            var clock = Stopwatch.StartNew();
            while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
            {
                bytes += read;
                if (bytes > package.Bytes) throw new IOException("Размер пакета не совпал: " + package.Name);
                target.Write(buffer, 0, read);
                if (clock.ElapsedMilliseconds >= 150) { report(bytes); clock.Restart(); }
            }
            if (bytes != package.Bytes) throw new IOException("Пакет скачался не полностью: " + package.Name);
            report(bytes);
        }
    }

    static void StagePackage(Stream source, ModPackage package, string target)
    {
        using (source)
        {
            if (source.Length != package.Bytes || !String.Equals(Sha256(source), package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Контрольная сумма не совпала: " + package.Name);
            source.Position = 0;
            Extract(source, target);
        }
        if (!new HashSet<string>(Directory.GetDirectories(target).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase)
                .SetEquals(package.Folders) ||
            package.Folders.Any(name => !File.Exists(Path.Combine(target, name, "ModInfo.xml"))))
            throw new IOException("Содержимое пакета не совпало с манифестом: " + package.Name);
        // Archives may also ship a top-level readme. Only mod directories go into Mods.
        foreach (string file in Directory.GetFiles(target)) File.Delete(file);
    }

    static bool GameRunning()
    {
        foreach (string name in new[] { "7DaysToDie", "7DaysToDie_EAC" })
        {
            Process[] processes = Process.GetProcessesByName(name);
            foreach (Process process in processes) process.Dispose();
            if (processes.Length > 0) return true;
        }
        return false;
    }

    static string ApplyRelease(ReleaseManifest release, ReleaseManifest bundled, string gameDir, string dataDir,
        string desktop, string launcherExe, Action<int,string> report, Func<ModPackage,string> localArchive = null,
        Action beforeCommit = null)
    {
        var changed = ChangedPackages(release, ReadReceipt(dataDir), dataDir);
        var desired = new Receipt
        {
            Release = release.Version,
            ActiveNames = release.Packages.SelectMany(package => package.Folders).ToArray(),
            Packages = release.Packages.ToDictionary(package => package.Name, package => package.Sha256, StringComparer.OrdinalIgnoreCase)
        };
        long total = changed.Where(package => !IsBundled(package, bundled)).Sum(package => package.Bytes);
        long downloaded = 0;
        return Install(gameDir, dataDir, desktop, report, stage =>
        {
            foreach (ModPackage package in changed)
            {
                string scratch = Path.Combine(stage, ".p");
                Directory.CreateDirectory(scratch);
                string archive = scratch + ".zip";
                if (IsBundled(package, bundled))
                {
                    report(10, "Распаковка " + package.Name + "…");
                    StagePackage(OpenResource(package.Name), package, scratch);
                }
                else
                {
                    report(10 + (int)(40 * downloaded / total), "Загрузка " + package.Name + "…");
                    if (localArchive == null)
                        DownloadPackage(package, archive, bytes => report(10 + (int)(40 * (downloaded + bytes) / total),
                            "Загрузка " + package.Name + " · " + FormatSize(downloaded + bytes) + " / " + FormatSize(total)));
                    else File.Copy(localArchive(package), archive);
                    downloaded += package.Bytes;
                    report(10 + (int)(40 * downloaded / total), "Проверка " + package.Name + "…");
                    StagePackage(File.OpenRead(archive), package, scratch);
                    File.Delete(archive);
                }
                foreach (string name in package.Folders)
                    Directory.Move(Path.Combine(scratch, name), Path.Combine(stage, name));
                Directory.Delete(scratch);
            }
            if (beforeCommit != null) beforeCommit();
        }, desired, launcherExe);
    }

    static string FormatSize(long size)
    {
        return size >= 1048576 ? (size / 1048576.0).ToString("0.#") + " МБ" : Math.Max(1, (size + 1023) / 1024) + " КБ";
    }
}
