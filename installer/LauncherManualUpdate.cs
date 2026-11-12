using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

partial class Installer
{
    static ReleaseManifest ReadOfflineBundle(string archivePath, string stage, Action<int,string> report)
    {
        using (var archive = ZipFile.OpenRead(archivePath)) {
            var manifests = archive.Entries.Where(e => e.FullName == "manifest.json").ToArray();
            if (manifests.Length != 1 || manifests[0].Length > 524288)
                throw new IOException("Выберите полный ZIP сборки с manifest.json в корне.");
            ReleaseManifest release;
            using (var reader = new StreamReader(manifests[0].Open())) release = ParseManifest(reader.ReadToEnd());
            if (archive.Entries.Count != release.Packages.Length + 1 || release.Packages.Sum(p => p.Bytes) > 4294967296L)
                throw new IOException("Неверный состав полного архива сборки.");
            long total = release.Packages.Sum(p => p.Bytes), done = 0;
            foreach (var package in release.Packages) {
                var entries = archive.Entries.Where(e => e.FullName == package.Name).ToArray();
                if (entries.Length != 1 || entries[0].Length != package.Bytes)
                    throw new IOException("Отсутствует или повреждён пакет " + package.Name);
                string target = Path.Combine(stage, package.Name);
                using (var input = entries[0].Open())
                using (var output = File.Create(target)) {
                    byte[] buffer = new byte[65536]; int count; long copied = 0;
                    while ((count = input.Read(buffer,0,buffer.Length)) > 0) {
                        copied += count; if (copied > package.Bytes) throw new IOException("Неверный размер пакета.");
                        output.Write(buffer,0,count);
                    }
                    if (copied != package.Bytes) throw new IOException("Неполный пакет.");
                }
                using (var input = File.OpenRead(target))
                    if (!String.Equals(Sha256(input),package.Sha256,StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Контрольная сумма не совпала: " + package.Name);
                done += package.Bytes; report((int)(done * 35 / total),"Проверка архива сборки");
            }
            return release;
        }
    }

    static ReleaseManifest InstallOfflineBundle(string archive, ReleaseManifest bundled, string game, string data, string desktop,
        string executable, Action<int,string> report, Action beforeCommit = null)
    {
        string stage = Path.Combine(Path.GetTempPath(),"launcher-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try {
            var release = ReadOfflineBundle(archive,stage,report);
            if (release.GameVersion != bundled.GameVersion) throw new IOException("Архив предназначен для другой версии игры: " + release.GameVersion);
            ApplyRelease(release,bundled,game,data,desktop,executable,(p,t) => report(35 + p * 65 / 100,t),
                package => Path.Combine(stage,package.Name),beforeCommit);
            return release;
        } finally { Directory.Delete(stage,true); }
    }

    sealed partial class LauncherWindow
    {
        string manualArchive;
        System.Windows.Controls.Button chooseArchive, applyArchive;
        System.Windows.Controls.TextBlock archiveName;

        void ChooseManualArchive()
        {
            if (busy || launching) return;
            using (var dialog = new System.Windows.Forms.OpenFileDialog { Title = "Выберите полный архив сборки", Filter = "Архив сборки (*.zip)|*.zip", CheckFileExists = true }) {
                if (dialog.ShowDialog(new NativeOwner(this)) != System.Windows.Forms.DialogResult.OK) return;
                manualArchive = dialog.FileName;
                archiveName.Text = Path.GetFileName(manualArchive); archiveName.ToolTip = manualArchive;
                RenderState();
            }
        }

        void InstallManualArchive()
        {
            if (busy || launching || String.IsNullOrEmpty(manualArchive)) return;
            if (previewMode) { Notify("Это демонстрация: моды не изменяются"); return; }
            if (!HasGame()) { Notify("Сначала выберите папку игры"); return; }
            if (GameRunning()) { Notify("Закройте игру перед обновлением"); return; }
            string selected = manualArchive;
            Work(report => {
                var release = InstallOfflineBundle(selected,bundled,gameDir,dataDir,desktop,InstallLauncherCopy(),report,
                    () => { if (GameRunning()) throw new IOException("Закройте игру перед обновлением."); });
                SaveText(CachedManifest,Json.Serialize(release)); SaveText(GamePathFile,gameDir);
                return release;
            }, result => {
                remote = (ReleaseManifest)result; installed = ReadReceipt(dataDir); offline = false;
                manualArchive = null; archiveName.Text = "Архив не выбран"; archiveName.ToolTip = null;
                RenderState(); Notify("Сборка обновлена из архива");
            },"Обновление из архива");
        }
    }
}
