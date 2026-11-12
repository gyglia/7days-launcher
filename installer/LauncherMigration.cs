using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

partial class Installer
{
    public class PackageFileIndex { public string PackageSha256 { get; set; } public PackageFile[] Files { get; set; } }
    public class PackageFile { public string Path { get; set; } public long Bytes { get; set; } public string Sha256 { get; set; } }

    static byte[] DownloadFileIndex(ModPackage package)
    {
        if (package.FileIndexBytes <= 0 || package.FileIndexBytes > 8388608 ||
            !Regex.IsMatch(package.FileIndexSha256 ?? "",@"^[a-fA-F0-9]{64}$") ||
            !Regex.IsMatch(package.FileIndexUrl ?? "","^" + Regex.Escape(ReleaseRoot) + @"download/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+\.files\.json$"))
            return null;
        using (var response = GitHubRequest(package.FileIndexUrl,8000).GetResponse())
        using (var source = response.GetResponseStream())
        using (var buffer = new MemoryStream()) {
            byte[] chunk = new byte[65536]; int count;
            while ((count = source.Read(chunk,0,chunk.Length)) > 0) {
                if (buffer.Length + count > package.FileIndexBytes) throw new IOException("Неверный размер индекса файлов.");
                buffer.Write(chunk,0,count);
            }
            return buffer.ToArray();
        }
    }

    static bool MatchesInstalledPackage(ModPackage package,string dataDir,Func<ModPackage,byte[]> readIndex = null)
    {
        try {
            byte[] bytes = readIndex == null ? DownloadFileIndex(package) : readIndex(package);
            if (bytes == null || bytes.LongLength != package.FileIndexBytes || bytes.Length > 8388608) return false;
            using (var stream = new MemoryStream(bytes)) if (!String.Equals(Sha256(stream),package.FileIndexSha256,StringComparison.OrdinalIgnoreCase)) return false;
            var index = new JavaScriptSerializer { MaxJsonLength = 8388608 }.Deserialize<PackageFileIndex>(Encoding.UTF8.GetString(bytes));
            if (index == null || !String.Equals(index.PackageSha256,package.Sha256,StringComparison.OrdinalIgnoreCase) ||
                index.Files == null || index.Files.Length == 0 || index.Files.Length > 100000) return false;
            var expected = new Dictionary<string,PackageFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in index.Files) {
                if (file == null || file.Path == null || file.Path.Contains("\\") ||
                    file.Path.Split('/').Length < 2 || !file.Path.Split('/').All(SafeModName) ||
                    !package.Folders.Contains(file.Path.Split('/')[0],StringComparer.OrdinalIgnoreCase) ||
                    file.Bytes < 0 || !Regex.IsMatch(file.Sha256 ?? "",@"^[a-fA-F0-9]{64}$") || expected.ContainsKey(file.Path)) return false;
                expected.Add(file.Path,file);
            }
            if (package.Folders.Any(f => !expected.ContainsKey(f + "/ModInfo.xml"))) return false;
            string mods = System.IO.Path.Combine(dataDir,"Mods");
            if (!Directory.Exists(mods) || (File.GetAttributes(mods) & FileAttributes.ReparsePoint) != 0) return false;
            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in package.Folders) {
                string root = System.IO.Path.Combine(mods,folder);
                if (!Directory.Exists(root)) return false;
                var pending = new Stack<string>(); pending.Push(root);
                while (pending.Count > 0) {
                    string directory = pending.Pop();
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
                    foreach (string child in Directory.GetDirectories(directory)) pending.Push(child);
                    foreach (string path in Directory.GetFiles(directory)) {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
                        string relative = path.Substring(mods.Length + 1).Replace('\\','/'); PackageFile file;
                        if (!actual.Add(relative) || !expected.TryGetValue(relative,out file)) return false;
                        using (var stream = File.OpenRead(path))
                            if (stream.Length != file.Bytes || !String.Equals(Sha256(stream),file.Sha256,StringComparison.OrdinalIgnoreCase)) return false;
                    }
                }
            }
            return actual.SetEquals(expected.Keys);
        } catch (IOException) { return false; }
          catch (UnauthorizedAccessException) { return false; }
          catch (System.Net.WebException) { return false; }
          catch (ArgumentException) { return false; }
          catch (InvalidOperationException) { return false; }
    }
}
