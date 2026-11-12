using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

partial class Installer : Form
{
    TextBox game = new TextBox { Left=20, Top=90, Width=470 };
    Button browse = new Button { Left=500, Top=88, Width=85, Text="Обзор…" };
    Button install = new Button { Left=20, Top=210, Width=565, Height=40, Text="Установить JAGO" };
    Button uninstall = new Button { Left=20, Top=260, Width=565, Height=40, Text="Удалить JAGO и восстановить файлы" };
    Label status = new Label { Left=20, Top=315, Width=565, Height=90, Text="Готов к установке." };
    ProgressBar progress = new ProgressBar { Left=20, Top=410, Width=565, Height=20 };
    bool busy;
#if LAUNCHER
    static readonly string[] Resources = ParseManifest(ResourceText("manifest.json")).Packages.Select(package=>package.Name).ToArray();
#else
    static readonly string[] Resources = { "JAGO.zip", "Gears.zip", "Quartz.zip", "Russian.zip", "ZZTong.zip", "GardenDecor.zip", "GardenDecorSeatLook.zip" };
#endif
    const string RussianMod = "ZZZZ-JAGO-Russian";
    const string PrefabMod = "ZZTong-Prefabs";
    const string GardenMod = "GardenDecor";
    const string SeatLookMod = "ZZZZZZ-LocalGardenDecorSeatLook";
    static readonly string[] AddedMods = { RussianMod, PrefabMod, GardenMod, SeatLookMod };
    const int ModCount = 34;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    [STAThread] static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--self-test") {
            try { SelfTest(args[1]); return 0; }
            catch(Exception e) { File.WriteAllText(args[1], e.ToString()); return 1; }
        }
        Application.EnableVisualStyles();
#if LAUNCHER
        return RunLauncher(args);
#else
        Application.Run(new Installer()); return 0;
#endif
    }
    Installer() {
        Text="JAGO — установка для 7 Days to Die";
        ClientSize=new System.Drawing.Size(605,450);
        FormBorderStyle=FormBorderStyle.FixedDialog; MaximizeBox=false;
        StartPosition=FormStartPosition.CenterScreen;
        Controls.Add(new Label {Left=20,Top=18,Width=565,Height=48,
            Text="JAGO 1.0.0, Gears, Quartz, русский перевод, ZZTong и Garden Decor.\nДля 7 Days to Die 3.4 (Windows / Steam)."});
        Controls.Add(new Label {Left=20,Top=68,Width=560,Text="Папка игры (найдена автоматически, если игра установлена через Steam):"});
        Controls.AddRange(new Control[]{game,browse,install,uninstall,status,progress});
        Controls.Add(new Label {Left=20,Top=130,Width=565,Height=65,
            Text="Установит 34 папки модов в %APPDATA%\\7DaysToDie\\Mods. Отключит EAC и создаст ярлык «7 Days to Die - JAGO». Заменяемые файлы сохранятся в резервной копии."});
        game.Text=FindGame();
        browse.Click += delegate { using(var d=new FolderBrowserDialog()) { d.Description="Выберите папку с 7DaysToDie.exe"; if(d.ShowDialog()==DialogResult.OK) game.Text=d.SelectedPath; } };
        FormClosing += delegate(object s, FormClosingEventArgs e) { if(busy) e.Cancel=true; };
        install.Click += delegate { StartInstall(); };
        uninstall.Click += delegate { StartUninstall(); };
    }
    void StartInstall() {
        if(!File.Exists(Path.Combine(game.Text,"7DaysToDie.exe"))) { MessageBox.Show("Не найден 7DaysToDie.exe. Укажите папку игры."); return; }
        if(Process.GetProcessesByName("7DaysToDie").Length>0 || Process.GetProcessesByName("7DaysToDie_EAC").Length>0) { MessageBox.Show("Закройте 7 Days to Die перед установкой."); return; }
        string gameDir=Path.GetFullPath(game.Text);
        busy=true; install.Enabled=uninstall.Enabled=browse.Enabled=game.Enabled=false;
        var worker=new BackgroundWorker {WorkerReportsProgress=true};
        worker.ProgressChanged += delegate(object s,ProgressChangedEventArgs e) { progress.Value=e.ProgressPercentage; status.Text=(string)e.UserState; };
        worker.DoWork += delegate(object s,DoWorkEventArgs e) {
            e.Result=Install(gameDir,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"7DaysToDie"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                (p,t)=>worker.ReportProgress(p,t));
        };
        worker.RunWorkerCompleted += delegate(object s,RunWorkerCompletedEventArgs e) {
            busy=false; uninstall.Enabled=game.Enabled=browse.Enabled=true;
            if(e.Error!=null) { install.Enabled=true; status.Text="Установка не завершена: "+e.Error.Message; MessageBox.Show(status.Text,"Ошибка установки"); }
            else { progress.Value=100; status.Text="Готово. Запускайте «7 Days to Die - JAGO» с рабочего стола при открытом Steam.\nПроверьте новый тестовый мир. Резервная копия: "+e.Result; install.Text="Установлено"; }
        };
        worker.RunWorkerAsync();
    }
    void StartUninstall() {
        if(Process.GetProcessesByName("7DaysToDie").Length>0 || Process.GetProcessesByName("7DaysToDie_EAC").Length>0) { MessageBox.Show("Закройте 7 Days to Die перед удалением."); return; }
        busy=true; install.Enabled=uninstall.Enabled=browse.Enabled=game.Enabled=false;
        var worker=new BackgroundWorker {WorkerReportsProgress=true};
        worker.ProgressChanged += delegate(object s,ProgressChangedEventArgs e) { progress.Value=e.ProgressPercentage; status.Text=(string)e.UserState; };
        worker.DoWork += delegate(object s,DoWorkEventArgs e) {
            e.Result=Uninstall(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"7DaysToDie"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),(p,t)=>worker.ReportProgress(p,t));
        };
        worker.RunWorkerCompleted += delegate(object s,RunWorkerCompletedEventArgs e) {
            busy=false; install.Enabled=uninstall.Enabled=browse.Enabled=game.Enabled=true; install.Text="Установить JAGO";
            if(e.Error!=null) { status.Text="Удаление не завершено: "+e.Error.Message; MessageBox.Show(status.Text,"Ошибка удаления"); }
            else { progress.Value=100; status.Text="JAGO удалён. Прежние файлы и настройки восстановлены.\nСнятый комплект сохранён здесь: "+e.Result; }
        };
        worker.RunWorkerAsync();
    }
    public class Receipt {
        public string Baseline;
        public string[] Names;
        public string[] ActiveNames;
        public Dictionary<string,string> Packages;
        public string Release;
    }
    static string StatePath(string dataDir) { return Path.Combine(dataDir,"JAGO-installation.json"); }
    static Receipt ReadReceipt(string dataDir) {
        string path=StatePath(dataDir);
        if(!File.Exists(path)) {
            // Version 1 wrote a success marker but did not yet write a receipt.
            if(!Directory.Exists(dataDir)) return null;
            var removedBaselines=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string removedDir in Directory.GetDirectories(dataDir,"JAGO-removed-*")) {
                string removedReceipt=Path.Combine(removedDir,"JAGO-installation.json");
                if(File.Exists(removedReceipt)) {
                    Receipt old=Json.Deserialize<Receipt>(File.ReadAllText(removedReceipt));
                    if(old!=null && old.Baseline!=null) removedBaselines.Add(old.Baseline);
                }
            }
            // Completed removal consumes the whole chain of earlier reinstalls.
            if(removedBaselines.Count>0) return null;
            string oldBackup=Directory.GetDirectories(dataDir,"JAGO-backup-*").OrderBy(p=>p,StringComparer.Ordinal).FirstOrDefault(p=>
                !removedBaselines.Contains(p) &&
                File.Exists(Path.Combine(p,"installation.txt")) &&
                File.ReadAllLines(Path.Combine(p,"installation.txt")).Any(line=>line.Equals("Mods: "+Path.Combine(dataDir,"Mods"),StringComparison.OrdinalIgnoreCase)));
            if(oldBackup==null) return null;
            string versionLine=File.ReadLines(Path.Combine(oldBackup,"installation.txt")).First();
            var legacyNames=PayloadNames().Where(n=>
                (n!=RussianMod || versionLine.Contains("Russian ")) &&
                (n!=PrefabMod || versionLine.Contains("ZZTong ")) &&
                ((n!=GardenMod && n!=SeatLookMod) || versionLine.Contains("Garden Decor "))).ToArray();
            File.WriteAllText(path,Json.Serialize(new Receipt {Baseline=oldBackup,Names=legacyNames}));
        }
        Receipt receipt=Json.Deserialize<Receipt>(File.ReadAllText(path));
        string full=receipt==null || receipt.Baseline==null ? "" : Path.GetFullPath(receipt.Baseline);
        var receiptNames=new HashSet<string>(receipt==null || receipt.Names==null ? new string[0] : receipt.Names,StringComparer.OrdinalIgnoreCase);
        bool knownNames=receipt!=null && receipt.Names!=null && receiptNames.Count==receipt.Names.Length &&
            receiptNames.All(SafeModName);
        if(knownNames && receipt.Packages==null) {
            var payloadNames=PayloadNames();
            knownNames=receiptNames.IsSubsetOf(payloadNames) &&
                receiptNames.IsSupersetOf(payloadNames.Where(n=>!AddedMods.Contains(n,StringComparer.OrdinalIgnoreCase)));
        }
        if(knownNames && receipt.Packages!=null)
            knownNames=receipt.ActiveNames!=null && receipt.ActiveNames.All(receiptNames.Contains);
        if(!knownNames ||
            !String.Equals(Path.GetDirectoryName(full),Path.GetFullPath(dataDir).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("JAGO-backup-",StringComparison.Ordinal) || !Directory.Exists(full))
            throw new IOException("Запись установки или исходная резервная копия повреждена. Файлы не изменены.");
        return receipt;
    }
    static bool SafeModName(string name) {
        return !String.IsNullOrEmpty(name) && name!="." && name!=".." &&
            name==name.Trim().TrimEnd('.') && name.IndexOfAny(Path.GetInvalidFileNameChars())<0 &&
            !Regex.IsMatch(name.Split('.')[0].TrimEnd(),@"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$",RegexOptions.IgnoreCase);
    }
    static string[] PayloadNames() {
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string resource in Resources)
            using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
                foreach(var e in zip.Entries) { string name=e.FullName.Split('/')[0]; if(e.FullName.Contains("/")) names.Add(name); }
        return names.OrderBy(n=>n,StringComparer.Ordinal).ToArray();
    }
    static void CopyTree(string source,string target) {
        if((File.GetAttributes(source)&FileAttributes.ReparsePoint)!=0) throw new IOException("Ссылки в папках модов не поддерживаются.");
        Directory.CreateDirectory(target);
        foreach(string file in Directory.GetFiles(source)) {
            if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) throw new IOException("Ссылки в папках модов не поддерживаются.");
            File.Copy(file,Path.Combine(target,Path.GetFileName(file)));
        }
        foreach(string dir in Directory.GetDirectories(source)) CopyTree(dir,Path.Combine(target,Path.GetFileName(dir)));
    }
    static string Uninstall(string dataDir,string desktop,Action<int,string> report) {
        Receipt receipt=ReadReceipt(dataDir);
        if(receipt==null) throw new IOException("Нет записи установки. Автоматическое удаление недоступно без резервной копии.");
        string mods=Path.Combine(dataDir,"Mods"),baseline=receipt.Baseline;
        string removed=Path.Combine(dataDir,"JAGO-removed-"+Guid.NewGuid().ToString("N"));
        string restore=Path.Combine(removed,"restore");
        Directory.CreateDirectory(restore); Directory.CreateDirectory(Path.Combine(removed,"Mods"));
        var moved=new List<string>(); var restored=new List<string>(); var movedFiles=new List<string>(); var restoredFiles=new List<string>();
        string[] targets={Path.Combine(dataDir,"launchersettings.json"),Path.Combine(desktop,"7 Days to Die - JAGO.lnk")};
        string[] backupNames={"launchersettings.json","desktop-shortcut.lnk"};
        try {
            report(10,"Подготовка исходных файлов…");
            foreach(string name in receipt.Names) {
                string old=Path.Combine(baseline,"Mods",name);
                if(Directory.Exists(old)) CopyTree(old,Path.Combine(restore,name));
            }
            report(45,"Снятие установленного комплекта…");
            foreach(string name in receipt.Names) {
                string target=Path.Combine(mods,name);
                if(Directory.Exists(target)) { Directory.Move(target,Path.Combine(removed,"Mods",name)); moved.Add(name); }
                if(Directory.Exists(Path.Combine(restore,name))) { Directory.Move(Path.Combine(restore,name),target); restored.Add(name); }
            }
            report(75,"Восстановление настроек и ярлыка…");
            for(int i=0;i<targets.Length;i++) {
                if(File.Exists(targets[i])) { File.Move(targets[i],Path.Combine(removed,backupNames[i])); movedFiles.Add(targets[i]); }
                string source=Path.Combine(baseline,backupNames[i]);
                if(File.Exists(source)) { File.Copy(source,targets[i]); restoredFiles.Add(targets[i]); }
            }
            File.Move(StatePath(dataDir),Path.Combine(removed,"JAGO-installation.json"));
            report(100,"Удалено и восстановлено"); return removed;
        } catch {
            // Preserve every removed/replaced file; rollback also uses moves, not deletion.
            foreach(string target in restoredFiles) File.Move(target,Path.Combine(removed,"rollback-"+Path.GetFileName(target)));
            for(int i=0;i<targets.Length;i++) if(movedFiles.Contains(targets[i])) File.Move(Path.Combine(removed,backupNames[i]),targets[i]);
            foreach(string name in restored) Directory.Move(Path.Combine(mods,name),Path.Combine(restore,name));
            foreach(string name in moved) Directory.Move(Path.Combine(removed,"Mods",name),Path.Combine(mods,name));
            throw;
        }
    }
    static IEnumerable<string> LibraryPaths(string text) {
        return Regex.Matches(text,"\"path\"\\s+\"([^\"]+)\"").Cast<Match>().Select(m=>m.Groups[1].Value.Replace("\\\\","\\"));
    }
    static string FindGame() {
        var roots=new List<string>();
        using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) { if(key!=null && key.GetValue("SteamPath")!=null) roots.Add(key.GetValue("SteamPath").ToString()); }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam"));
        foreach(string root in roots.ToArray()) {
            string vdf=Path.Combine(root,@"steamapps\libraryfolders.vdf");
            if(File.Exists(vdf)) roots.AddRange(LibraryPaths(File.ReadAllText(vdf)));
        }
        foreach(string root in roots.Distinct(StringComparer.OrdinalIgnoreCase)) {
            string manifest=Path.Combine(root,@"steamapps\appmanifest_251570.acf");
            if(!File.Exists(manifest)) continue;
            Match m=Regex.Match(File.ReadAllText(manifest),"\"installdir\"\\s+\"([^\"]+)\"");
            if(!m.Success) continue;
            string dir=Path.Combine(root,"steamapps","common",m.Groups[1].Value);
            if(File.Exists(Path.Combine(dir,"7DaysToDie.exe"))) return dir;
        }
        return "";
    }
    static void Extract(string resource,string destination) {
        using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            Extract(stream,destination);
    }
    static void Extract(Stream stream,string destination) {
        using(var zip=new ZipArchive(stream,ZipArchiveMode.Read)) {
            string prefix=Path.GetFullPath(destination)+Path.DirectorySeparatorChar;
            foreach(var entry in zip.Entries) {
                string[] parts=entry.FullName.TrimEnd('/','\\').Split('/','\\');
                if(!parts.All(SafeModName)) throw new IOException("Недопустимое имя файла в архиве: "+entry.FullName);
                string target=Path.GetFullPath(Path.Combine(destination,entry.FullName));
                if(!target.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new IOException("Недопустимый путь в архиве.");
                if(entry.Name.Length==0) Directory.CreateDirectory(target);
                else { Directory.CreateDirectory(Path.GetDirectoryName(target)); entry.ExtractToFile(target,false); }
            }
        }
    }
    static void SaveShortcut(string path,string gameDir,string dataDir,string launcherExe=null) {
        Type type=Type.GetTypeFromProgID("WScript.Shell");
        dynamic shell=Activator.CreateInstance(type);
        dynamic shortcut=shell.CreateShortcut(path);
        try {
            shortcut.TargetPath=launcherExe ?? Path.Combine(gameDir,"7DaysToDie.exe");
            shortcut.WorkingDirectory=launcherExe==null ? gameDir : Path.GetDirectoryName(launcherExe);
            shortcut.Arguments=launcherExe==null ? "-UserDataFolder=\""+dataDir+"\"" : "";
            shortcut.Description="7 Days to Die — JAGO";
            shortcut.Save();
        } finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
    }
    static string Install(string gameDir,string dataDir,string desktop,Action<int,string> report,
        Action<string> prepare=null, Receipt desired=null, string launcherExe=null) {
        Receipt previous=ReadReceipt(dataDir);
        Directory.CreateDirectory(dataDir);
        string mods=Path.Combine(dataDir,"Mods");
        string stage=Path.Combine(dataDir,"JAGO-stage-"+Guid.NewGuid().ToString("N"));
        string backup=Path.Combine(dataDir,"JAGO-backup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6));
        Directory.CreateDirectory(stage);
        var installed=new List<string>(); var replaced=new List<string>();
        var capturedBaseline=new List<string>();
        string launcher=Path.Combine(dataDir,"launchersettings.json");
        string shortcut=Path.Combine(desktop,"7 Days to Die - JAGO.lnk");
        bool hadLauncher=File.Exists(launcher),hadShortcut=File.Exists(shortcut), settingsTouched=false,shortcutTouched=false,stateTouched=false;
        string previousState=File.Exists(StatePath(dataDir)) ? File.ReadAllText(StatePath(dataDir)) : null;
        try {
            if(prepare==null) {
                report(5,"Распаковка JAGO…"); Extract(Resources[0],stage);
                report(30,"Распаковка дополнительных модов…");
                foreach(string resource in Resources.Skip(1)) Extract(resource,stage);
            } else prepare(stage);
            string[] folders=Directory.GetDirectories(stage);
            if((desired==null && folders.Length!=ModCount) || folders.Any(f=>!File.Exists(Path.Combine(f,"ModInfo.xml")))) throw new IOException("Неполный комплект модов.");
            // An older copy in the game directory would load alongside this copy.
            string gameMods=Path.Combine(gameDir,"Mods");
            var stagedNames=new HashSet<string>(folders.Select(Path.GetFileName),StringComparer.OrdinalIgnoreCase);
            var names=new HashSet<string>(desired==null ? stagedNames : desired.ActiveNames.AsEnumerable(),StringComparer.OrdinalIgnoreCase);
            string[] previouslyActive=previous==null ? new string[0] : previous.ActiveNames ?? previous.Names;
            if(!stagedNames.IsSubsetOf(names) ||
                names.Except(stagedNames,StringComparer.OrdinalIgnoreCase).Any(name=>!File.Exists(Path.Combine(mods,name,"ModInfo.xml"))))
                throw new IOException("Неполный комплект модов.");
            if(Directory.Exists(gameMods) && Directory.GetDirectories(gameMods).Any(f=>names.Contains(Path.GetFileName(f))))
                throw new IOException("В папке игры Mods уже есть моды из комплекта. Уберите старые копии и повторите установку.");
            Directory.CreateDirectory(backup);
            if(hadLauncher) File.Copy(launcher,Path.Combine(backup,"launchersettings.json"));
            if(hadShortcut) File.Copy(shortcut,Path.Combine(backup,"desktop-shortcut.lnk"));
            var settings=hadLauncher ? Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(launcher)) : new Dictionary<string,object>();
            if(settings==null) throw new IOException("Некорректный файл настроек лаунчера.");
            object configObj; Dictionary<string,object> config;
            if(settings.TryGetValue("DefaultRunConfig",out configObj)) { config=configObj as Dictionary<string,object>; if(config==null) throw new IOException("Некорректный DefaultRunConfig."); }
            else { config=new Dictionary<string,object>(); settings["DefaultRunConfig"]=config; }
            config["UseEAC"]=false;
            object extraObj; string extra=config.TryGetValue("AdditionalParameters",out extraObj) ? Convert.ToString(extraObj) : "";
            extra=Regex.Replace(extra ?? "","(?:^|\\s)-UserDataFolder=(?:\"[^\"]*\"|\\S+)","",RegexOptions.IgnoreCase).Trim();
            config["AdditionalParameters"]=(extra+" -UserDataFolder=\""+dataDir+"\"").Trim();
            Directory.CreateDirectory(mods); Directory.CreateDirectory(Path.Combine(backup,"Mods"));
            report(55,"Установка модов и русского перевода…");
            foreach(string name in previouslyActive.Where(n=>!names.Contains(n))) {
                string target=Path.Combine(mods,name);
                if(Directory.Exists(target)) { Directory.Move(target,Path.Combine(backup,"Mods",name)); replaced.Add(name); }
            }
            foreach(string folder in folders) {
                string name=Path.GetFileName(folder),target=Path.Combine(mods,name);
                if(Directory.Exists(target)) { Directory.Move(target,Path.Combine(backup,"Mods",name)); replaced.Add(name); }
                Directory.Move(folder,target); installed.Add(name);
            }
            report(80,"Настройка запуска без EAC…"); settingsTouched=true;
            File.WriteAllText(launcher,Json.Serialize(settings),new UTF8Encoding(false));
            report(90,"Создание ярлыка…"); shortcutTouched=true; SaveShortcut(shortcut,gameDir,dataDir,launcherExe);
            string description=desired==null ?
                "JAGO 1.0.0; Gears 8.0.0; Quartz 10.0.0; Russian 1.0.0; ZZTong 3.0.032; Garden Decor 0.1.1; SeatLook 1.1.0" :
                "JAGO launcher release "+desired.Release;
            // Preserve pre-existing copies of newly managed add-ons when upgrading older installers.
            if(previous!=null) foreach(string name in names.Except(previous.Names,StringComparer.OrdinalIgnoreCase)) {
                if(!replaced.Contains(name)) continue;
                string originalAddon=Path.Combine(previous.Baseline,"Mods",name);
                if(!Directory.Exists(originalAddon)) {
                    string originalStage=Path.Combine(stage,"original");
                    CopyTree(Path.Combine(backup,"Mods",name),originalStage);
                    Directory.Move(originalStage,originalAddon);
                    capturedBaseline.Add(originalAddon);
                }
            }
            var receipt=desired ?? new Receipt();
            receipt.Baseline=previous==null?backup:previous.Baseline;
            receipt.Names=names.Union(previous==null ? new string[0] : previous.Names,StringComparer.OrdinalIgnoreCase).ToArray();
            if(desired==null && previous!=null && previous.Packages!=null) {
                receipt.ActiveNames=names.ToArray();
                receipt.Packages=new Dictionary<string,string>();
            }
            stateTouched=true;
            File.WriteAllText(StatePath(dataDir),Json.Serialize(receipt));
            File.WriteAllText(Path.Combine(backup,"installation.txt"),description+"\r\nGame: "+gameDir+"\r\nMods: "+mods+"\r\nInstalled: "+DateTime.Now.ToString("s"));
            report(100,"Готово"); return backup;
        } catch {
            // Only paths created by this invocation can be removed during rollback.
            foreach(string name in installed) Directory.Delete(Path.Combine(mods,name),true);
            foreach(string name in replaced) Directory.Move(Path.Combine(backup,"Mods",name),Path.Combine(mods,name));
            foreach(string name in capturedBaseline) Directory.Move(name,Path.Combine(backup,"rollback-original-"+Path.GetFileName(name)));
            if(settingsTouched) { if(hadLauncher) File.Copy(Path.Combine(backup,"launchersettings.json"),launcher,true); else if(File.Exists(launcher)) File.Delete(launcher); }
            if(shortcutTouched) { if(hadShortcut) File.Copy(Path.Combine(backup,"desktop-shortcut.lnk"),shortcut,true); else if(File.Exists(shortcut)) File.Delete(shortcut); }
            if(stateTouched) { if(previousState!=null) File.WriteAllText(StatePath(dataDir),previousState); else if(File.Exists(StatePath(dataDir))) File.Delete(StatePath(dataDir)); }
            throw;
        } finally { if(Directory.Exists(stage)) Directory.Delete(stage,true); }
    }
    static void SelfTest(string log) {
        string root=Path.Combine(Path.GetTempPath(),"JAGO-installer-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string gameDir=Path.Combine(root,"Steam library кириллица","Game");
        string dataDir=Path.Combine(root,"user data"), desktop=Path.Combine(root,"Desktop");
        Directory.CreateDirectory(gameDir); Directory.CreateDirectory(desktop); Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(gameDir,"7DaysToDie.exe"),"test fixture, never executed");
        string launcher=Path.Combine(dataDir,"launchersettings.json");
        File.WriteAllText(launcher,"{\"CustomValue\":42,\"DefaultRunConfig\":{\"UseEAC\":true,\"Renderer\":\"dx11\",\"AdditionalParameters\":\"-foo -UserDataFolder=\\\"C:\\\\old data\\\"\"}}");
        Directory.CreateDirectory(Path.Combine(dataDir,"Mods","OtherMod"));
        File.WriteAllText(Path.Combine(dataDir,"Mods","OtherMod","keep.txt"),"keep");
        Directory.CreateDirectory(Path.Combine(dataDir,"Mods","0-Quartz"));
        File.WriteAllText(Path.Combine(dataDir,"Mods","0-Quartz","old-version.txt"),"original mod");
        File.WriteAllText(Path.Combine(desktop,"7 Days to Die - JAGO.lnk"),"original shortcut");
        string baselineSettings=File.ReadAllText(launcher);
        string firstBackup=Install(gameDir,dataDir,desktop,(p,t)=>{});
        if(Directory.GetDirectories(Path.Combine(dataDir,"Mods")).Length!=ModCount+1) throw new Exception("Mod count");
        var settings=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(launcher));
        var config=(Dictionary<string,object>)settings["DefaultRunConfig"];
        if((bool)config["UseEAC"] || !config["AdditionalParameters"].ToString().Contains("-foo") || !settings.ContainsKey("CustomValue")) throw new Exception("Settings preservation");
        if(config["AdditionalParameters"].ToString().Contains("old data")) throw new Exception("Old UDF not removed");
        string secondBackup=Install(gameDir,dataDir,desktop,(p,t)=>{});
        if(Directory.GetDirectories(Path.Combine(secondBackup,"Mods")).Length!=ModCount) throw new Exception("Backup count");
        if(!File.Exists(Path.Combine(desktop,"7 Days to Die - JAGO.lnk"))) throw new Exception("Shortcut missing");
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic link=shell.CreateShortcut(Path.Combine(desktop,"7 Days to Die - JAGO.lnk"));
        try {
            if(!String.Equals((string)link.TargetPath,Path.Combine(gameDir,"7DaysToDie.exe"),StringComparison.OrdinalIgnoreCase) || !((string)link.Arguments).Contains(dataDir)) throw new Exception("Shortcut target/arguments");
        } finally { Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
        foreach(string resource in Resources) {
            using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
            using(var sha=System.Security.Cryptography.SHA256.Create()) {
                foreach(var entry in zip.Entries) {
                    if(entry.Name.Length==0 || entry.FullName=="JAGO-README.txt") continue;
                    byte[] expected; using(var input=entry.Open()) expected=sha.ComputeHash(input);
                    byte[] actual; using(var input=File.OpenRead(Path.Combine(dataDir,"Mods",entry.FullName))) actual=sha.ComputeHash(input);
                    if(!expected.SequenceEqual(actual)) throw new Exception("Payload mismatch: "+entry.FullName);
                }
            }
        }
        if(File.ReadAllText(Path.Combine(dataDir,"Mods","OtherMod","keep.txt"))!="keep") throw new Exception("Other mod modified");
        string vdf="\"path\" \"D:\\\\SteamLibrary\"";
        if(LibraryPaths(vdf).Single()!=@"D:\SteamLibrary") throw new Exception("VDF parsing");
        // Invalid launcher input must fail without changing existing mods/settings.
        File.WriteAllText(launcher,"invalid-json");
        bool failed=false;
        try { Install(gameDir,dataDir,desktop,(p,t)=>{}); } catch { failed=true; }
        if(!failed || File.ReadAllText(launcher)!="invalid-json" || !File.Exists(Path.Combine(dataDir,"Mods","0-Quartz","Quartz.dll"))) throw new Exception("Failure isolation");
        // Force a failure after deployment to exercise the transaction rollback.
        string brokenDesktop=Path.Combine(root,"broken desktop");
        Directory.CreateDirectory(Path.Combine(brokenDesktop,"7 Days to Die - JAGO.lnk"));
        string originalSettings="{\"DefaultRunConfig\":{\"UseEAC\":true}}";
        File.WriteAllText(launcher,originalSettings);
        string marker=Path.Combine(dataDir,"Mods","0-Quartz","rollback-marker.txt");
        File.WriteAllText(marker,"original"); failed=false;
        try { Install(gameDir,dataDir,brokenDesktop,(p,t)=>{}); } catch { failed=true; }
        if(!failed || File.ReadAllText(launcher)!=originalSettings || !File.Exists(marker)) throw new Exception("Rollback failed");
        failed=false;
        try { Uninstall(dataDir,brokenDesktop,(p,t)=>{}); } catch { failed=true; }
        if(!failed || !File.Exists(marker) || !File.Exists(StatePath(dataDir)) || File.ReadAllText(launcher)!=originalSettings) throw new Exception("Uninstall rollback failed");
        string removed=Uninstall(dataDir,desktop,(p,t)=>{});
        if(File.Exists(StatePath(dataDir)) || File.ReadAllText(launcher)!=baselineSettings ||
            File.ReadAllText(Path.Combine(desktop,"7 Days to Die - JAGO.lnk"))!="original shortcut" ||
            File.ReadAllText(Path.Combine(dataDir,"Mods","0-Quartz","old-version.txt"))!="original mod" ||
            Directory.GetDirectories(Path.Combine(dataDir,"Mods")).Length!=2 ||
            !File.Exists(Path.Combine(removed,"Mods","0-Quartz","Quartz.dll"))) throw new Exception("Uninstall restoration failed");
        failed=false; try { Uninstall(dataDir,desktop,(p,t)=>{}); } catch { failed=true; }
        if(!failed) throw new Exception("Repeated uninstall should be blocked");
        string freshData=Path.Combine(root,"fresh data"),freshDesktop=Path.Combine(root,"fresh desktop");
        Directory.CreateDirectory(freshDesktop);
        Install(gameDir,freshData,freshDesktop,(p,t)=>{});
        File.Move(StatePath(freshData),Path.Combine(root,"v1-receipt-not-present.json"));
        if(ReadReceipt(freshData)==null) throw new Exception("Version 1 migration failed");
        Uninstall(freshData,freshDesktop,(p,t)=>{});
        if(File.Exists(Path.Combine(freshData,"launchersettings.json")) || File.Exists(Path.Combine(freshDesktop,"7 Days to Die - JAGO.lnk")) || Directory.GetDirectories(Path.Combine(freshData,"Mods")).Length!=0) throw new Exception("Fresh install uninstall failed");
        Install(gameDir,freshData,freshDesktop,(p,t)=>{});
        Uninstall(freshData,freshDesktop,(p,t)=>{});
        if(File.Exists(StatePath(freshData)) || Directory.GetDirectories(Path.Combine(freshData,"Mods")).Length!=0) throw new Exception("Install after uninstall failed");
        // Upgrade a previous 30-mod installation and remove both added packages correctly.
        Install(gameDir,freshData,freshDesktop,(p,t)=>{});
        Receipt legacy=ReadReceipt(freshData);
        legacy.Names=legacy.Names.Except(AddedMods).ToArray();
        foreach(string name in AddedMods) Directory.Move(Path.Combine(freshData,"Mods",name),Path.Combine(root,"legacy-30-"+name));
        File.WriteAllText(StatePath(freshData),Json.Serialize(legacy));
        Install(gameDir,freshData,freshDesktop,(p,t)=>{});
        if(ReadReceipt(freshData).Names.Length!=ModCount) throw new Exception("Legacy 30-mod upgrade receipt");
        Uninstall(freshData,freshDesktop,(p,t)=>{});
        if(Directory.GetDirectories(Path.Combine(freshData,"Mods")).Length!=0) throw new Exception("Add-ons not removed after legacy upgrade");
        // Upgrade the 31-folder translated installer; restore a manually installed ZZTong copy.
        Install(gameDir,freshData,freshDesktop,(p,t)=>{});
        Receipt translated=ReadReceipt(freshData);
        var absentFromTranslated=new[]{PrefabMod,GardenMod,SeatLookMod};
        translated.Names=translated.Names.Except(absentFromTranslated).ToArray();
        foreach(string name in absentFromTranslated) Directory.Move(Path.Combine(freshData,"Mods",name),Path.Combine(root,"legacy-31-"+name));
        string customPrefab=Path.Combine(freshData,"Mods",PrefabMod);
        Directory.CreateDirectory(customPrefab); File.WriteAllText(Path.Combine(customPrefab,"original.txt"),"original prefab package");
        File.WriteAllText(StatePath(freshData),Json.Serialize(translated));
        Install(gameDir,freshData,freshDesktop,(p,t)=>{});
        if(ReadReceipt(freshData).Names.Length!=ModCount) throw new Exception("Legacy 31-mod upgrade receipt");
        Uninstall(freshData,freshDesktop,(p,t)=>{});
        if(Directory.GetDirectories(Path.Combine(freshData,"Mods")).Length!=1 ||
            File.ReadAllText(Path.Combine(customPrefab,"original.txt"))!="original prefab package") throw new Exception("Pre-existing ZZTong not restored");
        string prefabData=Path.Combine(root,"previous 32-folder data");
        Install(gameDir,prefabData,freshDesktop,(p,t)=>{});
        var gardenAddons=new[]{GardenMod,SeatLookMod};
        Receipt prefabReceipt=ReadReceipt(prefabData);
        prefabReceipt.Names=prefabReceipt.Names.Except(gardenAddons).ToArray();
        if(prefabReceipt.Names.Length!=32) throw new Exception("32-folder fixture");
        foreach(string name in gardenAddons) {
            string folder=Path.Combine(prefabData,"Mods",name);
            Directory.Move(folder,Path.Combine(root,"legacy-32-"+name));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder,"original.txt"),name);
        }
        File.WriteAllText(StatePath(prefabData),Json.Serialize(prefabReceipt));
        Install(gameDir,prefabData,freshDesktop,(p,t)=>{});
        if(ReadReceipt(prefabData).Names.Length!=ModCount ||
            gardenAddons.Any(n=>!File.Exists(Path.Combine(prefabData,"Mods",n,"ModInfo.xml")) ||
                File.Exists(Path.Combine(prefabData,"Mods",n,"original.txt")))) throw new Exception("Legacy 32-mod upgrade");
        Uninstall(prefabData,freshDesktop,(p,t)=>{});
        if(Directory.GetDirectories(Path.Combine(prefabData,"Mods")).Length!=2 ||
            gardenAddons.Any(n=>File.ReadAllText(Path.Combine(prefabData,"Mods",n,"original.txt"))!=n)) throw new Exception("Pre-existing Garden Decor packages not restored");
        File.WriteAllText(log,"PASS: install/reinstall, all "+ModCount+" payload folder hashes, unrelated mods preserved, settings and shortcut, failure isolation, install rollback, uninstall rollback, original mods/settings/shortcut restored across reinstalls, fresh-install removal, repeated removal blocked, version 1 migration, install after uninstall, 30/31/32-folder upgrades, pre-existing ZZTong and Garden Decor packages restored after removal.\r\nTest directory: "+root+"\r\nDetected game: "+FindGame());
    }
}
