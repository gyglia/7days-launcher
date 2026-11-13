using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

partial class Installer
{
    // Enumerate process identity without opening the Steam process or requesting SYNCHRONIZE access.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SteamProcessEntry {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool SteamProcessFirst(IntPtr snapshot, ref SteamProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool SteamProcessNext(IntPtr snapshot, ref SteamProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);

    static string SnapshotProcessName(int pid)
    {
        if (pid <= 0) return null;
        IntPtr snapshot = CreateToolhelp32Snapshot(2,0); // TH32CS_SNAPPROCESS: no modules or process memory.
        if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            var entry = new SteamProcessEntry { Size = (uint)Marshal.SizeOf(typeof(SteamProcessEntry)) };
            if (!SteamProcessFirst(snapshot,ref entry)) {
                int error = Marshal.GetLastWin32Error();
                if (error != 18) throw new Win32Exception(error);
                return null;
            }
            do { if (entry.ProcessId == (uint)pid) return entry.ExeFile; } while (SteamProcessNext(snapshot,ref entry));
            int last = Marshal.GetLastWin32Error();
            if (last != 18) throw new Win32Exception(last);
            return null;
        } finally { CloseHandle(snapshot); }
    }

    static bool SteamSessionReady(long user, int pid, Func<int,string> lookup)
    {
        if (user == 0 || pid <= 0) return false;
        try { return String.Equals(lookup(pid),"steam.exe",StringComparison.OrdinalIgnoreCase); }
        catch (Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    static int TestSteamDetection(string log)
    {
        try {
            using (var own = Process.GetCurrentProcess())
                Require(String.Equals(SnapshotProcessName(own.Id),Path.GetFileName(System.Windows.Forms.Application.ExecutablePath),StringComparison.OrdinalIgnoreCase),"Native snapshot identifies running process");
            Require(SnapshotProcessName(0) == null && SnapshotProcessName(Int32.MaxValue) == null,"Invalid and absent PID rejected");
            Require(SteamSessionReady(1,42,id => "STEAM.EXE"),"Steam identity accepted without process handle");
            Require(!SteamSessionReady(0,42,id => { throw new Exception("Should not query logged-out Steam"); }),"Logged-out Steam rejected");
            Require(!SteamSessionReady(1,0,id => "steam.exe"),"Missing registry PID rejected");
            Require(!SteamSessionReady(1,42,id => null),"Stale registry PID rejected");
            Require(!SteamSessionReady(1,42,id => "other.exe"),"Reused non-Steam PID rejected");
            Require(!SteamSessionReady(1,42,id => { throw new Win32Exception(5); }),"Access-denied enumeration does not crash launcher");
            File.WriteAllText(log,"PASS: real process snapshot, missing/stale/reused PID, signed-out account, case-insensitive Steam identity, access-denied regression. No Steam process handle required."); return 0;
        } catch(Exception error) { File.WriteAllText(log,error.ToString()); return 1; }
    }
}
