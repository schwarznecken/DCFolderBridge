using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;

internal static class Program
{
    internal const string EventName = "Local\\DCFolderBridge.Stop.v1";
    [STAThread] static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--stop")
        {
            try { using (var e = EventWaitHandle.OpenExisting(EventName)) e.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            return;
        }
        if (args.Length > 0 && args[0] == "--self-test")
        {
            string report;
            try { Tests.Run(); report = "PASS: path filtering, argument quoting, transition tracking, close policy"; }
            catch (Exception ex) { report = "FAIL: " + ex; Environment.ExitCode = 1; }
            if (args.Length > 1) File.WriteAllText(args[1], report); else Console.WriteLine(report);
            return;
        }
        bool created;
        using (var mutex = new Mutex(true, "Local\\DCFolderBridge.Instance.v1", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { using (var app = new Bridge()) Application.Run(app); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "DC Folder Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}

internal sealed class Config
{
    public string Executable;
    public bool CloseExplorer = true;
    public bool DryRun;
    public Config(string root)
    {
        string file = Path.Combine(root, "settings.ini");
        if (File.Exists(file)) foreach (string raw in File.ReadAllLines(file))
        {
            string line = raw.Trim();
            if (line.StartsWith("#") || line.StartsWith(";")) continue;
            int i = line.IndexOf('='); if (i < 1) continue;
            string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
            if (k.Equals("DoubleCommander", StringComparison.OrdinalIgnoreCase)) Executable = v;
            if (k.Equals("CloseExplorer", StringComparison.OrdinalIgnoreCase)) CloseExplorer = bool.Parse(v);
            if (k.Equals("DryRun", StringComparison.OrdinalIgnoreCase)) DryRun = bool.Parse(v);
        }
        if (String.IsNullOrWhiteSpace(Executable)) Executable = "doublecmd.exe";
        Executable = Environment.ExpandEnvironmentVariables(Executable);
        if (!Path.IsPathRooted(Executable)) Executable = Path.GetFullPath(Path.Combine(root, Executable));
        if (!File.Exists(Executable)) throw new FileNotFoundException("settings.ini の DoubleCommander に doublecmd.exe の場所を設定してください。", Executable);
    }
}

internal static class Policy
{
    public static bool IsFolderPath(string p)
    {
        if (String.IsNullOrEmpty(p) || p.IndexOfAny(new char[] { '"', '\r', '\n', '\0' }) >= 0) return false;
        if (p.StartsWith("\\\\?\\") || p.StartsWith("\\\\.\\")) return false;
        if (p.Length >= 3 && Char.IsLetter(p[0]) && p[1] == ':' && p[2] == '\\') return true;
        if (!p.StartsWith("\\\\")) return false;
        string[] parts = p.Substring(2).Split('\\');
        return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0;
    }
    // Windows argv quoting: double trailing backslashes before the closing quote.
    public static string Quote(string s)
    {
        StringBuilder b = new StringBuilder("\""); int slashes = 0;
        foreach (char c in s)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') b.Append('\\', slashes * 2 + 1); else b.Append('\\', slashes);
            b.Append(c); slashes = 0;
        }
        b.Append('\\', slashes * 2); return b.Append('"').ToString();
    }
    public static bool MayClose(bool enabled, bool existing, int sameHandle, int tabs, bool dcForeground)
    { return enabled && !existing && sameHandle == 1 && tabs == 1 && dcForeground; }
}

internal sealed class Seen
{
    public string Path, Delivered;
    public int Stable;
    public bool Existing;
    public Seen(string path, bool existing) { Path = path; Existing = existing; Delivered = existing ? path : null; Stable = 1; }
    public bool Observe(string path)
    {
        if (!String.Equals(Path, path, StringComparison.OrdinalIgnoreCase)) { Path = path; Stable = 1; }
        else Stable++;
        return Stable >= 3 && !String.Equals(Delivered, path, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class ShellItem
{
    public long Hwnd;
    public string Path;
    public DateTime CloseAfter, CloseDeadline;
    public bool ActivationAttempted;
}

internal sealed class Bridge : ApplicationContext
{
    readonly Config config;
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer;
    readonly EventWaitHandle stop;
    readonly Dictionary<long, Seen> seen = new Dictionary<long, Seen>();
    readonly HashSet<long> baseline = new HashSet<long>();
    readonly List<ShellItem> pending = new List<ShellItem>();
    readonly string root = AppDomain.CurrentDomain.BaseDirectory;
    bool paused, initial = true;
    int failures;
    string lastStatus = "起動しました";
    public Bridge()
    {
        config = new Config(root);
        stop = new EventWaitHandle(false, EventResetMode.AutoReset, Program.EventName);
        var menu = new ContextMenuStrip();
        var pause = new ToolStripMenuItem("一時停止") { CheckOnClick = true };
        pause.CheckedChanged += delegate { paused = pause.Checked; initial = true; seen.Clear(); baseline.Clear(); pending.Clear(); tray.Text = paused ? "DC Folder Bridge — 一時停止" : "DC Folder Bridge — 動作中"; Log(paused ? "Paused" : "Resumed"); };
        menu.Items.Add(pause);
        menu.Items.Add("DCを開く", null, delegate { try { Launch(null); } catch (Exception ex) { ShowError(ex); } });
        menu.Items.Add("状態", null, delegate { MessageBox.Show(lastStatus + "\n\nDC: " + config.Executable + "\nCloseExplorer: " + config.CloseExplorer + "\nDryRun: " + config.DryRun, "DC Folder Bridge"); });
        menu.Items.Add("設定ファイルを開く（変更後は再起動）", null, delegate { Process.Start("notepad.exe", Policy.Quote(Path.Combine(root, "settings.ini"))); });
        menu.Items.Add("終了", null, delegate { ExitThread(); });
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "DC Folder Bridge — 動作中", ContextMenuStrip = menu, Visible = true };
        timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += Tick;
        Log("Started; close=" + config.CloseExplorer + "; dryRun=" + config.DryRun);
        timer.Start();
    }
    static void Release(object o) { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
    List<ShellItem> Snapshot()
    {
        var items = new List<ShellItem>(); object shell = null, windows = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            windows = ((dynamic)shell).Windows(); int count = ((dynamic)windows).Count;
            for (int i = 0; i < count; i++)
            {
                object w = null, doc = null, folder = null, self = null;
                try
                {
                    w = ((dynamic)windows).Item(i);
                    if (w == null || !String.Equals(Path.GetFileName((string)((dynamic)w).FullName), "explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    long hwnd = Convert.ToInt64(((dynamic)w).HWND);
                    // Include busy windows in the snapshot so they are never mistaken for new windows.
                    string path = null;
                    if (!((dynamic)w).Busy && ((dynamic)w).Visible)
                    {
                        doc = ((dynamic)w).Document; folder = ((dynamic)doc).Folder; self = ((dynamic)folder).Self;
                        if ((bool)((dynamic)self).IsFileSystem) path = (string)((dynamic)self).Path;
                    }
                    items.Add(new ShellItem { Hwnd = hwnd, Path = path });
                }
                catch (COMException) { }
                catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                finally { Release(self); Release(folder); Release(doc); Release(w); }
            }
        }
        finally { Release(windows); Release(shell); }
        return items;
    }
    void Tick(object sender, EventArgs e)
    {
        if (stop.WaitOne(0)) { ExitThread(); return; }
        if (paused) return;
        try
        {
            var items = Snapshot(); failures = 0;
            var counts = new Dictionary<long, int>();
            foreach (var w in items) { if (!counts.ContainsKey(w.Hwnd)) counts[w.Hwnd] = 0; counts[w.Hwnd]++; }
            foreach (var request in pending.ToArray())
            {
                ShellItem current = items.Find(delegate(ShellItem x) { return x.Hwnd == request.Hwnd && String.Equals(x.Path, request.Path, StringComparison.OrdinalIgnoreCase); });
                if (current == null || DateTime.UtcNow > request.CloseDeadline || counts[request.Hwnd] != 1) { pending.Remove(request); continue; }
                if (DateTime.UtcNow >= request.CloseAfter && !request.ActivationAttempted)
                { request.ActivationAttempted = true; Native.ActivateDc(config.Executable); continue; }
                if (DateTime.UtcNow >= request.CloseAfter && Policy.MayClose(config.CloseExplorer, false, counts[request.Hwnd], Native.TabCount(new IntPtr(request.Hwnd)), Native.IsDcForeground(config.Executable)))
                { CloseSameWindow(request); pending.Remove(request); }
            }
            foreach (long key in new List<long>(seen.Keys)) if (!counts.ContainsKey(key)) { seen.Remove(key); baseline.Remove(key); }
            foreach (var w in items)
            {
                if (initial) baseline.Add(w.Hwnd);
                if (counts[w.Hwnd] != 1) { baseline.Add(w.Hwnd); seen.Remove(w.Hwnd); continue; }
                if (!Policy.IsFolderPath(w.Path)) { Seen busyState; if (seen.TryGetValue(w.Hwnd, out busyState)) busyState.Stable = 0; continue; }
                Seen state;
                if (!seen.TryGetValue(w.Hwnd, out state)) { seen[w.Hwnd] = new Seen(w.Path, baseline.Contains(w.Hwnd)); continue; }
                if (!state.Observe(w.Path)) continue;
                // Deliver each stable navigation only once, including on failures. A new navigation can retry.
                state.Delivered = w.Path;
                if (config.DryRun) { Log("DryRun: eligible folder detected (path omitted)"); continue; }
                try
                {
                    Launch(w.Path);
                    Log("Folder dispatched to DC (path omitted)");
                    if (config.CloseExplorer && !state.Existing)
                    {
                        pending.Add(new ShellItem { Hwnd = w.Hwnd, Path = w.Path, CloseAfter = DateTime.UtcNow.AddSeconds(1), CloseDeadline = DateTime.UtcNow.AddSeconds(5) });
                    }
                }
                catch (Exception ex) { Log("Dispatch failed: " + ex.GetType().Name); tray.ShowBalloonTip(4000, "DC Folder Bridge", "DCへの転送に失敗しました。Explorerは残します。", ToolTipIcon.Warning); }
            }
            initial = false;
        }
        catch (Exception ex)
        {
            if (++failures == 1) Log("Monitor error: " + ex.GetType().Name);
            if (failures == 10) { paused = true; tray.Text = "DC Folder Bridge — 監視エラー"; tray.ShowBalloonTip(5000, "DC Folder Bridge", "Explorerの監視に失敗しました。ツールを再起動してください。", ToolTipIcon.Warning); }
        }
    }
    void Launch(string path)
    {
        var start = new ProcessStartInfo(config.Executable, "-C" + (path == null ? "" : " -P L -T " + Policy.Quote(path))) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(config.Executable) };
        using (var process = Process.Start(start)) { if (process == null) throw new InvalidOperationException("起動できませんでした。"); }
        // Never block the tray/UI awaiting DC. If it is not already foreground, keep Explorer open.
    }
    void CloseSameWindow(ShellItem expected)
    {
        object shell = null, windows = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")); windows = ((dynamic)shell).Windows();
            int count = ((dynamic)windows).Count;
            for (int i = 0; i < count; i++)
            {
                object w = null, doc = null, folder = null, self = null;
                try
                {
                    w = ((dynamic)windows).Item(i);
                    if (w == null || Convert.ToInt64(((dynamic)w).HWND) != expected.Hwnd) continue;
                    doc = ((dynamic)w).Document; folder = ((dynamic)doc).Folder; self = ((dynamic)folder).Self;
                    if (!((dynamic)w).Busy && String.Equals((string)((dynamic)self).Path, expected.Path, StringComparison.OrdinalIgnoreCase)
                        && Native.TabCount(new IntPtr(expected.Hwnd)) == 1 && Native.IsDcForeground(config.Executable))
                    { ((dynamic)w).Quit(); Log("Closed transferred single-tab Explorer window"); }
                    return;
                }
                finally { Release(self); Release(folder); Release(doc); Release(w); }
            }
        }
        catch (Exception ex) { Log("Explorer retained: " + ex.GetType().Name); }
        finally { Release(windows); Release(shell); }
    }
    void ShowError(Exception ex) { MessageBox.Show(ex.Message, "DC Folder Bridge"); }
    void Log(string message)
    {
        lastStatus = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message;
        try { string p = Path.Combine(root, "bridge.log"); if (File.Exists(p) && new FileInfo(p).Length > 262144) File.Move(p, p + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".old"); File.AppendAllText(p, lastStatus + Environment.NewLine); } catch { }
    }
    protected override void ExitThreadCore() { timer.Stop(); Log("Stopped"); tray.Visible = false; base.ExitThreadCore(); }
    protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); tray.Dispose(); stop.Dispose(); } base.Dispose(disposing); }
}

internal static class Native
{
    delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    internal static int TabCount(IntPtr hwnd)
    {
        int n = 0;
        EnumChildWindows(hwnd, delegate(IntPtr child, IntPtr unused) { var b = new StringBuilder(256); GetClassName(child, b, b.Capacity); if (b.ToString() == "ShellTabWindowClass") n++; return true; }, IntPtr.Zero);
        return n;
    }
    internal static bool IsDcForeground(string exe)
    {
        try { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); using (var p = Process.GetProcessById((int)pid)) return String.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase); } catch { return false; }
    }
    internal static void ActivateDc(string exe)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))) using (p)
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero || !String.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsIconic(p.MainWindowHandle)) ShowWindowAsync(p.MainWindowHandle, 9);
                SetForegroundWindow(p.MainWindowHandle); return;
            }
            catch { }
        }
    }
}

internal static class Tests
{
    static void Check(bool ok) { if (!ok) throw new Exception("Assertion failed"); }
    internal static void Run()
    {
        Check(Policy.IsFolderPath(@"C:\")); Check(Policy.IsFolderPath(@"C:\日本語 & space")); Check(Policy.IsFolderPath(@"\\server\share\folder"));
        foreach (string bad in new [] { "", "shell:Downloads", "::{GUID}", "C:relative", @"\\server", @"\\?\C:\", "C:\\bad\"x", "C:\\x\ny" }) Check(!Policy.IsFolderPath(bad));
        Check(Policy.Quote(@"C:\") == "\"C:\\\\\""); Check(Policy.Quote("a b") == "\"a b\"");
        var old = new Seen(@"C:\A", true); Check(!old.Observe(@"C:\A")); Check(!old.Observe(@"C:\A"));
        Check(!old.Observe(@"C:\B")); Check(!old.Observe(@"C:\B")); Check(old.Observe(@"C:\B"));
        old.Delivered = @"C:\B"; Check(!old.Observe(@"C:\B"));
        var fresh = new Seen(@"C:\A", false); Check(!fresh.Observe(@"C:\A")); Check(fresh.Observe(@"C:\A"));
        Check(Policy.MayClose(true, false, 1, 1, true));
        Check(!Policy.MayClose(true, true, 1, 1, true)); Check(!Policy.MayClose(true, false, 1, 2, true));
        Check(!Policy.MayClose(true, false, 2, 1, true)); Check(!Policy.MayClose(true, false, 1, 0, true));
        Check(!Policy.MayClose(true, false, 1, 1, false)); Check(!Policy.MayClose(false, false, 1, 1, true));
    }
}
