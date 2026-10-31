// ep_cmd_server: tiny localhost command channel for the EP "News and Interests"
// panel. The panel's WebView2 cannot open external links without breaking the
// flyout, so the page instead does fire-and-forget fetch() calls here:
//   GET /open?u=<urlencoded>   -> open in the host's default browser
//   GET /setloc?loc=<city>     -> save WeatherLocation, regenerate, reload panel
//   GET /refresh               -> regenerate w.html, reload panel
// Raw TcpListener (no HttpListener) so no admin/urlacl is needed.
// Compiled as winexe: no console window. Idles at zero CPU.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

class EpCmdServer
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);
    [DllImport("user32.dll")]
    static extern IntPtr PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("ole32.dll")]
    static extern int CoInitialize(IntPtr pvReserved);
    [DllImport("ole32.dll")]
    static extern int CoCreateInstance([In] ref Guid rclsid, IntPtr pUnkOuter,
        uint dwClsContext, [In] ref Guid riid, out IntPtr ppv);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);
    delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")]
    static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    // The page is fully opaque, so no DWM backdrop material is involved. EP
    // still applies its own Mica to the flyout on every (re)dock, which caused
    // pure-black panels historically - KillBackdrop keeps DWMSBT_NONE pinned as
    // cheap insurance (the opaque page would hide it anyway).
    static void KillBackdrop(IntPtr h)
    {
        int none = 1; // DWMSBT_NONE
        DwmSetWindowAttribute(h, 38, ref none, 4); // DWMWA_SYSTEMBACKDROP_TYPE
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    const int GWL_STYLE = -16;
    const long WS_CAPTION = 0xC00000;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1;

    const uint EP_WEATHER_WM_FETCH_DATA = 0x0400 + 10; // WM_USER + 10, from EP ep_weather.h
    const string EPW_CLASS = "ExplorerPatcher_Weather_{A6EA9C2D-4982-4827-9204-0AC532959F6D}";
    const int Port = 24381;

    // EP weather host COM ids (from EP ep_weather.h). Holding a reference to an
    // instance keeps epw_OutstandingObjects > 0, so DllCanUnloadNow always says
    // S_FALSE and the dllhost surrogate hosting the panel is never unloaded by
    // COM's idle timer (that unload was what killed the taskbar button).
    static readonly Guid CLSID_EPWeather = new Guid("A6EA9C2D-4982-4827-9204-0AC532959F6D");
    static readonly Guid IID_IEPWeather = new Guid("CDBF3734-F847-4F1B-B953-A605434DC1E7");
    const uint CLSCTX_LOCAL_SERVER = 0x4;

    static IntPtr s_pin = IntPtr.Zero;          // held COM reference, never released
    static IntPtr s_hwnd = IntPtr.Zero;         // panel window the pin belongs to
    static DateTime s_lastWeather = DateTime.Now, s_lastNews = DateTime.Now;
    // visibility state shared with the page via /vis and /viswait
    static bool s_vis = false;
    static int s_visGen = 0;
    // bumped whenever news.html is rewritten; the page's /newswait long-poll
    // wakes on it, so the page needs no interval timer of its own
    static int s_newsGen = 0;
    static DateTime s_visAt = DateTime.MinValue;
    static DateTime s_chromeAt = DateTime.MinValue;
    static DateTime s_lastTrim = DateTime.MinValue;

    static readonly string ExeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ExplorerPatcher", "ep_cmd_server.log");

    static void Log(string m)
    {
        try
        {
            // cap the log so months of uptime cannot grow it unbounded:
            // past 256 KB, keep only the most recent ~128 KB
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 256 * 1024)
            {
                string tail;
                using (var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(-128 * 1024, SeekOrigin.End);
                    tail = new StreamReader(fs).ReadToEnd();
                }
                File.WriteAllText(LogPath, tail);
            }
            File.AppendAllText(LogPath, DateTime.Now.ToString("s") + " " + m + "\r\n");
        }
        catch { }
    }

    static int Main()
    {
        // single instance
        bool created;
        var mtx = new Mutex(true, "Local\\EPNewsCmdServer", out created);
        if (!created) return 0;

        var listener = new TcpListener(IPAddress.Loopback, Port);
        try { listener.Start(); }
        catch (Exception e) { Log("bind failed: " + e.Message); return 1; }
        Log("listening on 127.0.0.1:" + Port);

        // keepalive: pin the EP weather COM server so its dllhost surrogate is
        // never idle-unloaded; log if the panel window ever disappears.
        var keep = new Thread(KeepaliveLoop);
        keep.IsBackground = true;
        keep.Start();
        // flyout fx: snap flush to taskbar + Win10 slide-up animation on show
        var fx = new Thread(FlyoutFxLoop);
        fx.IsBackground = true;
        fx.Start();

        while (true)
        {
            try
            {
                var client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(Handle, client);
            }
            catch { Thread.Sleep(1000); }
        }
    }

    static void Handle(object state)
    {
        var client = (TcpClient)state;
        try
        {
            client.ReceiveTimeout = 3000;
            var ns = client.GetStream();
            var buf = new byte[4096];
            int n = ns.Read(buf, 0, buf.Length);
            if (n <= 0) return;
            string req = Encoding.ASCII.GetString(buf, 0, n);
            // first line: GET /open?u=... HTTP/1.1
            int sp1 = req.IndexOf(' ');
            int sp2 = req.IndexOf(' ', sp1 + 1);
            if (sp1 < 0 || sp2 < 0) return;
            string path = req.Substring(sp1 + 1, sp2 - sp1 - 1);

            byte[] resp;
            string raw = Uri.UnescapeDataString(path);
            if (raw.StartsWith("/newswait"))
            {
                // long-poll: block until news.html is regenerated (max 15s),
                // then wake the page so it hot-swaps the fragment in place
                int gen0 = s_newsGen;
                var deadline = DateTime.Now.AddSeconds(15);
                while (s_newsGen == gen0 && DateTime.Now < deadline) Thread.Sleep(30);
                byte[] nbody = Encoding.ASCII.GetBytes(s_newsGen.ToString());
                var nhead = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nContent-Type: text/plain\r\nContent-Length: " +
                    nbody.Length + "\r\nConnection: close\r\n\r\n");
                ns.Write(nhead, 0, nhead.Length);
                ns.Write(nbody, 0, nbody.Length);
                return;
            }
            if (raw.StartsWith("/newshtml"))
            {
                // live news fragment pulled by the page after /newswait wakes it
                byte[] body;
                try { body = File.ReadAllBytes(Path.Combine(ExeDir, "news.html")); }
                catch { body = Encoding.UTF8.GetBytes(""); }
                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " +
                    body.Length + "\r\nConnection: close\r\n\r\n");
                ns.Write(head, 0, head.Length);
                ns.Write(body, 0, body.Length);
                return;
            }
            if (raw.StartsWith("/viswait"))
            {
                // long-poll: block until the panel's visibility changes (max 15s)
                int gen0 = s_visGen;
                var deadline = DateTime.Now.AddSeconds(15);
                while (s_visGen == gen0 && DateTime.Now < deadline) Thread.Sleep(30);
                byte[] body = VisState();
                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nContent-Type: text/plain\r\nContent-Length: " +
                    body.Length + "\r\nConnection: close\r\n\r\n");
                ns.Write(head, 0, head.Length);
                ns.Write(body, 0, body.Length);
                return;
            }
            if (raw.StartsWith("/vis"))
            {
                byte[] body = VisState();
                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nContent-Type: text/plain\r\nContent-Length: " +
                    body.Length + "\r\nConnection: close\r\n\r\n");
                ns.Write(head, 0, head.Length);
                ns.Write(body, 0, body.Length);
                return;
            }
            resp = Encoding.ASCII.GetBytes(
                "HTTP/1.1 204 No Content\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            ns.Write(resp, 0, resp.Length);

            // handle the command after replying
            Log("cmd: " + raw);
            if (raw.StartsWith("/open?u="))
            {
                string u = raw.Substring(8);
                if (u.StartsWith("http://") || u.StartsWith("https://"))
                {
                    OpenInNewBrowserWindow(u);
                    Log("opened " + u);
                }
            }
            else if (raw.StartsWith("/setint?sec="))
            {
                int sec;
                if (int.TryParse(raw.Substring(12), out sec) && sec >= 60 && sec <= 86400)
                {
                    // this setting now means: NEWS refresh interval. The weather
                    // follows its own fixed hourly grid (see KeepaliveLoop).
                    using (var k = Registry.CurrentUser.CreateSubKey(@"Software\ExplorerPatcher"))
                        if (k != null) k.SetValue("WeatherContentUpdateMode", sec, RegistryValueKind.DWord);
                    s_lastNews = DateTime.Now; s_lastWeather = DateTime.Now;
                    Log("setint news " + sec + "s");
                    RunGen("");      // full regen bakes the new interval into the page
                    NotifyPanel();
                }
            }
            else if (raw.StartsWith("/setloc?loc="))
            {
                string loc = raw.Substring(12).Trim();
                if (loc.Length > 0)
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(@"Software\ExplorerPatcher"))
                        if (k != null) k.SetValue("WeatherLocation", loc, RegistryValueKind.String);
                    Log("setloc " + loc);
                    RunGen("");
                    NotifyPanel();
                }
            }
            else if (raw.StartsWith("/refresh"))
            {
                RunGen("");
                NotifyPanel();
            }
        }
        catch { }
        finally { try { client.Close(); } catch { } }
    }

    static void RunGen(string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(ExeDir, "ep_weather_local.exe"),
                Arguments = args,
                WorkingDirectory = ExeDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (p != null) p.WaitForExit(30000);
            Log("regen done" + (args.Length > 0 ? " " + args : ""));
            // --weather rewrites only w.html; every other regen also rewrites
            // news.html, so wake the page's /newswait long-poll
            if (args != "--weather") Interlocked.Increment(ref s_newsGen);
        }
        catch (Exception e) { Log("regen: " + e.Message); }
    }

    static int RegInt(string name, int def)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\ExplorerPatcher"))
            {
                if (k != null)
                {
                    var v = k.GetValue(name);
                    if (v is int) return (int)v;
                }
            }
        }
        catch { }
        return def;
    }

    static void NotifyPanel()
    {
        var h = FindWindowW(EPW_CLASS, null);
        Log("notify hwnd=" + h.ToString("X"));
        if (h != IntPtr.Zero) PostMessageW(h, EP_WEATHER_WM_FETCH_DATA, IntPtr.Zero, IntPtr.Zero);
    }

    // Runs forever on a background thread. MTA is fine for holding a reference.
    static void KeepaliveLoop()
    {
        CoInitialize(IntPtr.Zero);
        bool armed = false; // only log death after we have seen the panel once
        while (true)
        {
            try
            {
                var h = FindWindowW(EPW_CLASS, null);
                if (h != s_hwnd)
                {
                    // explorer restart (or host respawn) created a NEW window:
                    // the old pin is a dead proxy and the new window has its
                    // caption bar again - reset everything.
                    if (s_pin != IntPtr.Zero) { try { Marshal.Release(s_pin); } catch { } }
                    s_pin = IntPtr.Zero;
                    s_hwnd = h;
                    if (h != IntPtr.Zero) Log("new panel window " + h.ToString("X") + ", re-pinning");
                }
                if (s_pin == IntPtr.Zero) PinHost();
                bool alive = h != IntPtr.Zero;
                if (alive) armed = true;
                else if (armed) Log("watchdog: panel window is gone (pin should prevent this)");

                // --- content scheduler ---
                // weather (taskbar temp/icon + weather card): fixed hourly grid
                if ((DateTime.Now - s_lastWeather).TotalMinutes >= 60)
                {
                    s_lastWeather = DateTime.Now;
                    RunGen("--weather");
                    NotifyPanel(); // forces EP to re-read -> taskbar updates now
                }
                // news: the user-facing "update frequency" setting (the page's
                // own JS timer pulls the fresh fragment via /newshtml)
                int nsec = RegInt("WeatherContentUpdateMode", 1200);
                if ((DateTime.Now - s_lastNews).TotalSeconds >= nsec)
                {
                    s_lastNews = DateTime.Now;
                    RunGen("--news");
                }
                // memory: this process is idle ~99.9% of the time, so hand
                // trimmed pages back to the OS every 5 min (purely advisory;
                // .NET reclaims instantly if it ever needs them back)
                if ((DateTime.Now - s_lastTrim).TotalMinutes >= 5)
                {
                    s_lastTrim = DateTime.Now;
                    SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle,
                        new IntPtr(-1), new IntPtr(-1));
                }
            }
            catch (Exception e) { Log("keepalive: " + e.Message); }
            Thread.Sleep(60000);
        }
    }

    // Win10 "News and Interests" feel. EP force-docks the flyout window itself:
    // cross-process SetWindowPos moves return TRUE but never move the window
    // (verified with MoveTest), so the slide animation lives INSIDE the page as
    // a CSS transform. This loop only:
    //   - tracks visibility for the page's /vis + /viswait endpoints
    //   - self-heals the window chrome (caption strip, EP's Mica off)
    static void FlyoutFxLoop()
    {
        while (true)
        {
            try
            {
                var h = FindWindowW(EPW_CLASS, null);
                bool vis = h != IntPtr.Zero && IsWindowVisible(h);
                if (vis != s_vis)
                {
                    s_vis = vis;
                    s_visAt = DateTime.Now;
                    Interlocked.Increment(ref s_visGen);
                    if (vis) FixChrome(h);
                    Log("vis=" + (vis ? "1" : "0"));
                }
                // chrome self-heal once a second (survives explorer restarts,
                // which recreate the window with its caption bar back)
                if (h != IntPtr.Zero && (DateTime.Now - s_chromeAt).TotalMilliseconds > 1000)
                {
                    s_chromeAt = DateTime.Now;
                    FixChrome(h);
                }
            }
            catch { }
            Thread.Sleep(25);
        }
    }

    static byte[] VisState()
    {
        int age = s_visAt == DateTime.MinValue ? 999999
            : (int)(DateTime.Now - s_visAt).TotalMilliseconds;
        return Encoding.ASCII.GetBytes((s_vis ? "1;" : "0;") + age);
    }

    // The panel is fully opaque (solid page background), so no DWM backdrop
    // material is involved at all. EP's own Mica is still disabled per window
    // (KillBackdrop) purely as insurance against the historical pure-black bug.

    // self-healing chrome fix, safe to call on a timer: EP recreates the flyout
    // window (with a caption bar) after every explorer restart, so a one-shot
    // "already stripped" flag goes stale and the bar comes back. Instead, check
    // the live style every time and strip whenever a caption is present.
    // Stripping alone is NOT enough: EP sized/positioned the window with the
    // caption INCLUDED (AdjustWindowRect), so after the strip the outer window
    // is one caption-bar taller than the client area needs to be, and the
    // uncovered band shows the window class's BLACK_BRUSH = black frame on
    // every side. Fix: after stripping, shrink the OUTER window back down so
    // outer == original client size, pinning the bottom-right corner (the edges
    // EP's RecomputeWeatherFlyoutLocation docks to). EP re-docks on every show
    // anyway, so even a bad transient here self-corrects on the next click.
    static void FixChrome(IntPtr h)
    {
        try
        {
            KillBackdrop(h); // EP re-applies Mica on (re)creation/dock
            long style = GetWindowLongPtrW(h, GWL_STYLE).ToInt64();
            if ((style & WS_CAPTION) != 0)
            {
                // measure BEFORE stripping: client size is what the layout
                // was actually designed for
                RECT wr, cli0;
                GetWindowRect(h, out wr);
                GetClientRect(h, out cli0);
                int cw = cli0.Right - cli0.Left, ch = cli0.Bottom - cli0.Top;

                SetWindowLongPtrW(h, GWL_STYLE, new IntPtr(style & ~WS_CAPTION));
                int sq = DWMWCP_DONOTROUND;
                DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref sq, 4);
                // first pass: recalc NC with the caption gone
                SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                // second pass: shrink the outer window so the client goes back
                // to its intended cw x ch; no NC area left, so requested size
                // == client size. Pin right+bottom edges (taskbar dock side).
                if (cw > 0 && ch > 0)
                    SetWindowPos(h, IntPtr.Zero, wr.Right - cw, wr.Bottom - ch, cw, ch,
                        SWP_NOZORDER | SWP_NOACTIVATE);
                Log("caption stripped, window shrunk to " + cw + "x" + ch);
            }
            // only stretch while hidden: resizing the WebView2 child of a
            // VISIBLE window can stall its compositor (panel rendered blank
            // white until recreated). While hidden it is safe and covers the
            // band left by the removed caption before the next show.
            if (!IsWindowVisible(h)) FixWebViewStretch(h);
        }
        catch (Exception e) { Log("chrome fix: " + e.Message); }
    }

    // stretch the WebView2 child windows (Chrome_WidgetWin_* and the render
    // widget host) over the whole client area; no-ops when already covering it
    static void FixWebViewStretch(IntPtr h)
    {
        RECT cli; GetClientRect(h, out cli);
        int cw = cli.Right - cli.Left, ch = cli.Bottom - cli.Top;
        if (cw <= 0 || ch <= 0) return;
        EnumChildWindows(h, delegate(IntPtr c, IntPtr l)
        {
            var sb = new StringBuilder(64);
            GetClassNameW(c, sb, 64);
            string cn = sb.ToString();
            if (cn.StartsWith("Chrome_WidgetWin") || cn == "Chrome_RenderWidgetHostHWND")
            {
                RECT cr; GetClientRect(c, out cr);
                if (cr.Right - cr.Left != cw || cr.Bottom - cr.Top != ch)
                {
                    MoveWindow(c, 0, 0, cw, ch, true);
                    Log("webview child stretched to " + cw + "x" + ch);
                }
            }
            return true;
        }, IntPtr.Zero);
    }

    // open the URL in a NEW window of the user's default browser
    static void OpenInNewBrowserWindow(string url)
    {
        try
        {
            string progId = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice",
                "ProgId", null) as string;
            string cmd = null;
            if (!string.IsNullOrEmpty(progId))
                cmd = Registry.GetValue(
                    @"HKEY_CLASSES_ROOT\" + progId + @"\shell\open\command", null, null) as string;
            if (!string.IsNullOrEmpty(cmd))
            {
                string exe;
                if (cmd.StartsWith("\""))
                {
                    int e = cmd.IndexOf('"', 1);
                    exe = e > 1 ? cmd.Substring(1, e - 1) : null;
                }
                else
                {
                    int e = cmd.IndexOf(' ');
                    exe = e > 0 ? cmd.Substring(0, e) : cmd;
                }
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                {
                    string name = Path.GetFileName(exe).ToLowerInvariant();
                    string flag = name.Contains("firefox") ? "-new-window" : "--new-window";
                    Process.Start(exe, flag + " \"" + url + "\"");
                    return;
                }
            }
        }
        catch (Exception e2) { Log("newwindow: " + e2.Message); }
        try { Process.Start(url); } catch { } // fallback: default handler
    }

    static void PinHost()
    {
        try
        {
            var clsid = CLSID_EPWeather; var iid = IID_IEPWeather;
            IntPtr p;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out p);
            if (hr == 0 && p != IntPtr.Zero)
            {
                s_pin = p; // held for process lifetime on purpose
                Log("pinned EP weather host (ref held, surrogate stays loaded)");
            }
            else Log("pin hr=0x" + hr.ToString("X8"));
        }
        catch (Exception e) { Log("pin: " + e.Message); }
    }
}
