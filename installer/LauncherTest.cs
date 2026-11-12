using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

partial class Installer
{
    static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    static int TestLauncher(string log)
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "JAGO-launcher-test-" + Guid.NewGuid().ToString("N"));
            string game = Path.Combine(root, "Steam Library кириллица", "7 Days to Die");
            string data = Path.Combine(root, "Data");
            string desktop = Path.Combine(root, "Desktop");
            string mods = Path.Combine(data, "Mods");
            string unrelated = Path.Combine(mods, "Unrelated");
            string russian = Path.Combine(mods, RussianMod);
            string original = Path.Combine(russian, "original.txt");
            Directory.CreateDirectory(game);
            Directory.CreateDirectory(desktop);
            Directory.CreateDirectory(unrelated);
            Directory.CreateDirectory(russian);
            File.WriteAllText(Path.Combine(game, "7DaysToDie.exe"), "test fixture");
            File.WriteAllText(original, "original mod");
            var bundled = ParseManifest(ResourceText("manifest.json"));
            string packageFiles = Path.Combine(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath),"packages");
            Require(!System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceNames().Any(n => n.EndsWith(".zip")),"Launcher contains no mod archives");
            Require(bundled.Packages.Sum(p => p.Folders.Length) == ModCount, "Baseline folder count");
            Action<int,string> progress = (percent, status) => {};
            Install(game, data, desktop, progress, stage => {
                foreach (var package in bundled.Packages) Extract(File.OpenRead(Path.Combine(packageFiles,package.Name)),stage);
            });
            Receipt legacy = ReadReceipt(data);
            Require(legacy.Packages == null, "Legacy receipt fixture");
            string executable = Process.GetCurrentProcess().MainModule.FileName;
            Func<ModPackage,byte[]> indexReader = p => File.ReadAllBytes(Path.Combine(packageFiles,p.Name + ".files.json"));
            Require(bundled.Packages.All(p => MatchesInstalledPackage(p,data,indexReader)),"Legacy files match all published indexes");
            var firstPackage = bundled.Packages[0];
            string firstFile = Path.Combine(mods,firstPackage.Folders[0],"ModInfo.xml");
            byte[] originalBytes = File.ReadAllBytes(firstFile), modifiedBytes = (byte[])originalBytes.Clone();
            modifiedBytes[0] ^= 1; File.WriteAllBytes(firstFile,modifiedBytes);
            Require(!MatchesInstalledPackage(firstPackage,data,indexReader),"Same-size content modification detected");
            File.WriteAllBytes(firstFile,originalBytes);
            Require(!MatchesInstalledPackage(firstPackage,data,p => new byte[0]),"Corrupt index rejected");
            var oldHash = firstPackage.FileIndexSha256; firstPackage.FileIndexSha256 = new string('0',64);
            Require(!MatchesInstalledPackage(firstPackage,data,indexReader),"Untrusted index hash rejected"); firstPackage.FileIndexSha256 = oldHash;
            ApplyRelease(bundled, bundled, game, data, desktop, executable, progress,
                p => { throw new Exception("Matching legacy package must not be downloaded: " + p.Name); },fileIndex:indexReader);
            Require(ReadReceipt(data).Baseline == legacy.Baseline,"Adoption retains original baseline");
            File.WriteAllText(StatePath(data),Json.Serialize(legacy));
            bool raced = false;
            try {
                ApplyRelease(bundled,bundled,game,data,desktop,executable,progress,p => { throw new Exception("Unexpected download"); },
                    beforeCommit:() => File.WriteAllBytes(firstFile,modifiedBytes),fileIndex:indexReader);
            } catch(IOException) { raced = true; }
            Require(raced && ReadReceipt(data).Packages == null,"Files changed during migration do not get a trusted receipt");
            File.WriteAllBytes(firstFile,originalBytes);

            // Restore only the fixture receipt to exercise a partially changed legacy install.
            File.WriteAllText(StatePath(data),Json.Serialize(legacy));
            string altered = Path.Combine(mods,bundled.Packages[0].Folders[0],"ModInfo.xml");
            string missing = Path.Combine(mods,bundled.Packages[1].Folders[0],"ModInfo.xml");
            string extra = Path.Combine(mods,bundled.Packages[2].Folders[0],"unexpected-file.txt");
            File.AppendAllText(altered," "); File.Delete(missing); File.WriteAllText(extra,"unexpected");
            Require(!MatchesInstalledPackage(bundled.Packages[0],data,indexReader),"Changed file detected");
            Require(!MatchesInstalledPackage(bundled.Packages[1],data,indexReader),"Missing file detected");
            Require(!MatchesInstalledPackage(bundled.Packages[2],data,indexReader),"Extra file detected");
            var requested = new System.Collections.Generic.List<string>();
            ApplyRelease(bundled,bundled,game,data,desktop,executable,progress,p => {
                requested.Add(p.Name); return Path.Combine(packageFiles,p.Name);
            },fileIndex:indexReader);
            Require(requested.Count == 3 && requested.OrderBy(n=>n).SequenceEqual(bundled.Packages.Take(3).Select(p=>p.Name).OrderBy(n=>n)),"Only three changed legacy packages downloaded");
            Require(!File.Exists(extra) && Directory.GetFiles(data,"unexpected-file.txt",SearchOption.AllDirectories).Length == 1,"Extra file retained in rollback backup, removed from active mod");
            Receipt baseline = ReadReceipt(data);
            Require(baseline.Baseline == legacy.Baseline, "Legacy backup retained");
            Require(IsCurrent(bundled, baseline, ChangedPackages(bundled, baseline, data)), "Bundled installation");
            Require(Directory.Exists(unrelated), "Unrelated mod retained");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic shortcut = shell.CreateShortcut(Path.Combine(desktop, "7 Days to Die - JAGO.lnk"));
            Require(String.Equals((string)shortcut.TargetPath, executable, StringComparison.OrdinalIgnoreCase), "Launcher shortcut");
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);

            var release = ParseManifest(ResourceText("manifest.json"));
            release.Version = "test-update";
            string archivePath = Path.Combine(root, "update.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry(RussianMod + "/ModInfo.xml").Open()))
                    writer.Write("<xml><Name value=\"JAGOUpdateTest\"/></xml>");
            }
            ModPackage changed = release.Packages.Single(p => p.Folders.Single() == RussianMod);
            using (var stream = File.OpenRead(archivePath))
            {
                changed.Sha256 = Sha256(stream);
                changed.Bytes = stream.Length;
            }
            string unchanged = Path.Combine(mods, release.Packages[0].Folders[0], "unrelated-to-update.txt");
            File.WriteAllText(unchanged, "keep during translation update");
            Require(ChangedPackages(release, baseline, data).Single().Name == changed.Name, "One changed package");
            string corrupt = Path.Combine(root, "corrupt.zip");
            File.WriteAllText(corrupt, "corrupt");
            bool rejected = false;
            try { ApplyRelease(release, bundled, game, data, desktop, executable, progress, p => corrupt); }
            catch (IOException error) { rejected = error.Message.Contains("Контрольная сумма"); }
            Require(rejected && ReadReceipt(data).Release == bundled.Version && File.Exists(unchanged), "Corrupt archive isolation");
            string offlineZip = Path.Combine(root,"offline.zip");
            using (var zip = ZipFile.Open(offlineZip,ZipArchiveMode.Create)) {
                using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write(Json.Serialize(release));
                foreach (var package in release.Packages) {
                    using (var input = File.OpenRead(package.Name == changed.Name ? archivePath : Path.Combine(packageFiles,package.Name)))
                    using (var output = zip.CreateEntry(package.Name,CompressionLevel.NoCompression).Open()) input.CopyTo(output);
                }
            }
            rejected = false;
            try { InstallOfflineBundle(offlineZip,bundled,game,data,desktop,executable,progress,() => { throw new IOException("Game started before commit"); }); }
            catch(IOException) { rejected = true; }
            Require(rejected && ReadReceipt(data).Release == bundled.Version && File.Exists(unchanged),"Offline pre-commit failure leaves installation intact");
            string incomplete = Path.Combine(root,"incomplete.zip");
            using(var zip = ZipFile.Open(incomplete,ZipArchiveMode.Create)) {
                using(var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write(Json.Serialize(release));
                using(var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open())) writer.Write("bad");
            }
            rejected = false;
            try { InstallOfflineBundle(incomplete,bundled,game,data,desktop,executable,progress); } catch(IOException) { rejected = true; }
            Require(rejected && ReadReceipt(data).Release == bundled.Version && !File.Exists(Path.Combine(root,"escape.txt")),"Incomplete/unsafe offline archive rejected");
            InstallOfflineBundle(offlineZip,bundled,game,data,desktop,executable,progress);
            Require(IsCurrent(release, ReadReceipt(data), ChangedPackages(release, ReadReceipt(data), data)), "Updated release");
            Require(File.Exists(unchanged), "Unchanged folder was left in place");

            release.Version = "test-remove-package";
            release.Packages = release.Packages.Where(p => p.Name != changed.Name).ToArray();
            ApplyRelease(release, bundled, game, data, desktop, executable, progress);
            Require(!Directory.Exists(russian), "Removed package deactivated");
            Uninstall(data, desktop, progress);
            Require(File.Exists(original) && Directory.Exists(unrelated) && ReadReceipt(data) == null, "Uninstall restores original files");
            Directory.Delete(root, true);
            File.WriteAllText(log, "PASS: legacy adoption with zero downloads, index validation, changed/missing/extra files detected, only changed legacy packages downloaded, original baseline and unrelated mods retained, extra files backed up, launcher shortcut, changed package only, corrupt archive isolation, offline ZIP install, failure isolation, package removal and original mod restoration.");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(log, error.ToString());
            return 1;
        }
    }
}
