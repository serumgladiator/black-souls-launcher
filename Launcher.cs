using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BS
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new ShellForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Black Souls", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
    }

    sealed class GameSearch
    {
        readonly object gate = new object();
        int best1;
        int best2;
        volatile bool cancel;
        int lastStatusTick;

        public bool IsCancelled { get { return cancel; } }

        public void Cancel() { cancel = true; }

        public void Reset() { cancel = false; }

        public void Pin(int part)
        {
            lock (gate)
            {
                if (part == 1) best1 = 100000;
                else best2 = 100000;
            }
        }

        public void Forget(int part)
        {
            lock (gate)
            {
                if (part == 1) best1 = 0;
                else best2 = 0;
            }
        }

        public delegate void Hit(int part, string exe, int score);
        public delegate void Note(string text);

        public int ClassifyFile(string exe)
        {
            int score;
            return Judge(exe, out score);
        }

        public void Fast(Hit hit, Note note)
        {
            string here = AppDomain.CurrentDomain.BaseDirectory;
            ScanTree(here, 2, hit);
            string parent = Path.GetDirectoryName(here.TrimEnd('\\', '/'));
            ScanTree(parent, 2, hit);
            if (cancel) return;

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ScanTree(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), 2, hit);
            ScanTree(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), 1, hit);
            ScanTree(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), 2, hit);
            ScanTree(Path.Combine(profile, "Downloads"), 2, hit);
            ScanTree(Path.Combine(profile, "Games"), 3, hit);
            ScanTree(Path.Combine(profile, "Saved Games"), 2, hit);
            if (cancel) return;

            ScanSteam(hit);
            ScanUninstall(hit);
            ScanGog(hit);
            if (cancel) return;

            ScanLinks(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), true, false, hit);
            ScanLinks(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), false, true, hit);
            ScanLinks(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), false, true, hit);
            if (cancel) return;

            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    if (cancel) return;
                    if (!drives[i].IsReady) continue;
                    if (drives[i].DriveType != DriveType.Fixed && drives[i].DriveType != DriveType.Removable) continue;
                    string root = drives[i].RootDirectory.FullName;
                    ProbeDir(root, hit);
                    string[] subs;
                    try { subs = Directory.GetDirectories(root); }
                    catch { continue; }
                    for (int s = 0; s < subs.Length; s++)
                    {
                        if (cancel) return;
                        string name = Path.GetFileName(subs[s]);
                        if (SkipDir(name)) continue;
                        if (NameHint(name) || GameRoot(name))
                        {
                            if (note != null) note(name);
                            ScanTree(subs[s], 3, hit);
                        }
                    }
                }
            }
            catch { }
        }

        public void Deep(Hit hit, Note note)
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { return; }
            for (int i = 0; i < drives.Length; i++)
            {
                if (cancel) return;
                try
                {
                    if (!drives[i].IsReady) continue;
                    if (drives[i].DriveType != DriveType.Fixed && drives[i].DriveType != DriveType.Removable) continue;
                    if (note != null) note("Смотрим " + drives[i].Name.TrimEnd('\\'));
                    Walk(drives[i].RootDirectory.FullName, 0, 7, hit, note);
                }
                catch { }
            }
        }

        void Walk(string dir, int depth, int max, Hit hit, Note note)
        {
            if (cancel || depth > max) return;
            string name = Path.GetFileName(dir);
            if (depth > 0 && SkipDir(name)) return;
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return;
            }
            catch { return; }

            if (note != null && depth >= 1)
            {
                int now = Environment.TickCount;
                if (unchecked(now - lastStatusTick) > 450)
                {
                    lastStatusTick = now;
                    note(dir);
                }
            }
            ProbeDir(dir, hit);
            if (depth == max) return;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { return; }
            for (int i = 0; i < subs.Length; i++)
            {
                if (cancel) return;
                Walk(subs[i], depth + 1, max, hit, note);
            }
        }

        void ScanTree(string dir, int depthLeft, Hit hit)
        {
            if (cancel || string.IsNullOrEmpty(dir) || depthLeft < 0) return;
            if (!Directory.Exists(dir)) return;
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return;
            }
            catch { return; }
            ProbeDir(dir, hit);
            if (depthLeft == 0) return;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { return; }
            for (int i = 0; i < subs.Length; i++)
            {
                if (cancel) return;
                if (SkipDir(Path.GetFileName(subs[i]))) continue;
                ScanTree(subs[i], depthLeft - 1, hit);
            }
        }

        void ProbeDir(string dir, Hit hit)
        {
            string game = Path.Combine(dir, "Game.exe");
            if (File.Exists(game)) Consider(game, hit);
            if (!NameHint(Path.GetFileName(dir))) return;
            try
            {
                foreach (string exe in Directory.EnumerateFiles(dir, "*.exe"))
                {
                    if (cancel) return;
                    if (exe.EndsWith("\\Game.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (exe.EndsWith("/Game.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    Consider(exe, hit);
                }
            }
            catch { }
        }

        void Consider(string exe, Hit hit)
        {
            if (cancel || string.IsNullOrEmpty(exe)) return;
            int score;
            int part = Judge(exe, out score);
            if (part == 0 || score <= 0) return;
            bool changed = false;
            lock (gate)
            {
                if (part == 1 && score > best1) { best1 = score; changed = true; }
                else if (part == 2 && score > best2) { best2 = score; changed = true; }
            }
            if (changed && hit != null) hit(part, exe, score);
        }

        int Judge(string exe, out int score)
        {
            score = 0;
            string file = Path.GetFileName(exe);
            if (string.IsNullOrEmpty(file)) return 0;
            string lower = file.ToLowerInvariant();
            if (lower.IndexOf("unins") >= 0 || lower.IndexOf("setup") >= 0 || lower.IndexOf("crash") >= 0) return 0;

            string dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir)) return 0;
            string folder = Path.GetFileName(dir);
            string title = null;
            string ini = Path.Combine(dir, "Game.ini");
            if (File.Exists(ini)) title = ReadTitle(ini);

            int part = 0;
            if (IsBlack(title)) part = IsTwo(title) ? 2 : 1;
            else
            {
                string names = (folder ?? "") + "\n" + Path.GetFileNameWithoutExtension(file);
                if (!IsBlack(names)) return 0;
                string stem = Path.GetFileNameWithoutExtension(file);
                bool gameExe = stem.Equals("Game", StringComparison.OrdinalIgnoreCase);
                if (!gameExe && !IsBlack(stem)) return 0;
                part = IsTwo(names) ? 2 : 1;
            }

            score = 10;
            if (file.Equals("Game.exe", StringComparison.OrdinalIgnoreCase)) score += 40;
            if (IsBlack(title)) score += 40;
            if (NameHint(folder)) score += 15;
            if (NameHint(Path.GetFileNameWithoutExtension(file))) score += 10;
            return part;
        }

        static string ReadTitle(string ini)
        {
            try
            {
                byte[] data = File.ReadAllBytes(ini);
                if (data.Length == 0 || data.Length > 400000) return null;
                string text;
                if (data.Length > 2 && data[0] == 0xFF && data[1] == 0xFE)
                    text = Encoding.Unicode.GetString(data);
                else if (data.Length > 2 && data[0] == 0xFE && data[1] == 0xFF)
                    text = Encoding.BigEndianUnicode.GetString(data);
                else
                    text = Encoding.UTF8.GetString(data);
                string[] lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim().Trim('\r').Trim('\uFEFF');
                    if (line.StartsWith("Title", StringComparison.OrdinalIgnoreCase))
                    {
                        int eq = line.IndexOf('=');
                        if (eq >= 0) return line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.ToLowerInvariant().Replace('２', '2').Replace('Ⅱ', '2').Replace('ⅱ', '2');
        }

        static string Compact(string s)
        {
            s = Norm(s);
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
            }
            return sb.ToString();
        }

        static bool IsBlack(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.IndexOf("ブラックソウル") >= 0) return true;
            return Compact(s).Contains("blacksoul");
        }

        static bool IsTwo(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string n = Norm(s);
            if (n.IndexOf("ブラックソウル2") >= 0) return true;
            if (n.IndexOf("ブラックソウルii") >= 0) return true;
            string c = Compact(s);
            return c.Contains("blacksouls2") || c.Contains("blacksoulsii")
                || c.Contains("blacksoul2") || c.Contains("blacksoulii");
        }

        static bool NameHint(string name)
        {
            return IsBlack(name);
        }

        static bool GameRoot(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n == "games" || n == "game" || n == "steamlibrary" || n == "steam" || n == "gog games"
                || n == "gog" || n == "rpg" || n == "rpgs" || n == "vn" || n == "visual novels";
        }

        static bool SkipDir(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name[0] == '$' || name[0] == '.') return true;
            string n = name.ToLowerInvariant();
            switch (n)
            {
                case "windows":
                case "winsxs":
                case "appdata":
                case "application data":
                case "programdata":
                case "perflogs":
                case "recovery":
                case "system volume information":
                case "node_modules":
                case "packages":
                case "windowsapps":
                case "temp":
                case "tmp":
                case "cache":
                case "nuget":
                case "containers":
                case "intel":
                case "nvidia":
                case "amd":
                    return true;
            }
            return false;
        }

        void ScanSteam(Hit hit)
        {
            var libs = new System.Collections.Generic.List<string>();
            AddSteamRoot(libs, ReadReg(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"));
            AddSteamRoot(libs, ReadReg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"));
            AddSteamRoot(libs, ReadReg(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"));
            for (int i = 0; i < libs.Count; i++)
            {
                string vdf = Path.Combine(libs[i], "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                try
                {
                    string text = File.ReadAllText(vdf);
                    int from = 0;
                    while (from < text.Length)
                    {
                        int p = text.IndexOf("\"path\"", from, StringComparison.OrdinalIgnoreCase);
                        if (p < 0) break;
                        int q1 = text.IndexOf('"', p + 6);
                        if (q1 < 0) break;
                        int q2 = text.IndexOf('"', q1 + 1);
                        if (q2 < 0) break;
                        string path = text.Substring(q1 + 1, q2 - q1 - 1).Replace("\\\\", "\\");
                        AddSteamRoot(libs, path);
                        from = q2 + 1;
                    }
                }
                catch { }
            }
            for (int i = 0; i < libs.Count; i++)
            {
                if (cancel) return;
                ScanTree(Path.Combine(libs[i], "steamapps", "common"), 2, hit);
            }
        }

        static void AddSteamRoot(System.Collections.Generic.List<string> libs, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            path = path.Replace('/', '\\').Trim().Trim('"');
            if (!Directory.Exists(path)) return;
            for (int i = 0; i < libs.Count; i++)
                if (string.Equals(libs[i], path, StringComparison.OrdinalIgnoreCase)) return;
            libs.Add(path);
        }

        static string ReadReg(RegistryHive hive, RegistryView view, string sub, string value)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey key = root.OpenSubKey(sub))
                {
                    if (key == null) return null;
                    return key.GetValue(value) as string;
                }
            }
            catch { return null; }
        }

        void ScanUninstall(Hit hit)
        {
            ScanUninstallHive(RegistryHive.LocalMachine, RegistryView.Registry64, hit);
            ScanUninstallHive(RegistryHive.LocalMachine, RegistryView.Registry32, hit);
            ScanUninstallHive(RegistryHive.CurrentUser, RegistryView.Default, hit);
        }

        void ScanUninstallHive(RegistryHive hive, RegistryView view, Hit hit)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (key == null) return;
                    string[] names = key.GetSubKeyNames();
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (cancel) return;
                        using (RegistryKey sub = key.OpenSubKey(names[i]))
                        {
                            if (sub == null) continue;
                            string display = sub.GetValue("DisplayName") as string;
                            string loc = sub.GetValue("InstallLocation") as string;
                            string icon = CleanExe(sub.GetValue("DisplayIcon") as string);
                            if (!string.IsNullOrEmpty(icon) && File.Exists(icon) && (IsBlack(display) || IsBlack(icon) || IsBlack(loc)))
                                Consider(icon, hit);
                            if (!string.IsNullOrEmpty(loc) && (IsBlack(display) || IsBlack(loc)))
                                ScanTree(loc, 1, hit);
                        }
                    }
                }
            }
            catch { }
        }

        void ScanGog(Hit hit)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\GOG.com\Games"))
                {
                    if (key == null) return;
                    string[] names = key.GetSubKeyNames();
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (cancel) return;
                        using (RegistryKey sub = key.OpenSubKey(names[i]))
                        {
                            if (sub == null) continue;
                            string path = sub.GetValue("path") as string;
                            string exe = CleanExe(sub.GetValue("exe") as string);
                            if (string.IsNullOrEmpty(exe)) exe = CleanExe(sub.GetValue("launchCommand") as string);
                            if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) Consider(exe, hit);
                            if (!string.IsNullOrEmpty(path) && IsBlack(path)) ScanTree(path, 1, hit);
                        }
                    }
                }
            }
            catch { }
        }

        static string CleanExe(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim().Trim('"');
            int comma = s.LastIndexOf(',');
            if (comma > 2 && s.Length - comma <= 3)
            {
                bool num = true;
                for (int k = comma + 1; k < s.Length; k++)
                    if (s[k] < '0' || s[k] > '9') num = false;
                if (num) s = s.Substring(0, comma).Trim().Trim('"');
            }
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        void ScanLinks(string dir, bool all, bool recursive, Hit hit)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            string[] links;
            try { links = Directory.GetFiles(dir, "*.lnk", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly); }
            catch { return; }
            for (int i = 0; i < links.Length; i++)
            {
                if (cancel) return;
                string name = Path.GetFileNameWithoutExtension(links[i]);
                if (!all && !IsBlack(name)) continue;
                string target = ResolveLink(links[i]);
                if (string.IsNullOrEmpty(target)) continue;
                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!all || IsBlack(name) || IsBlack(target)) Consider(target, hit);
            }
        }

        public static string ResolveLink(string path)
        {
            try
            {
                object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                object sc = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                object target = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null);
                return target as string;
            }
            catch { return null; }
        }
    }
}
