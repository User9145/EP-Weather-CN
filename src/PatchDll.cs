// ep_patch_dll: re-apply the two EP weather-host patches to whatever official
// ep_weather_host.dll is present (survives ExplorerPatcher upgrades and works
// across EP versions whose weather URL string differs).
//   patch 1: weather web URL  ->  file:///C:/PROGRA~1/EXPLOR~1/EPWLOC~1/w.html
//   patch 2: WebView2 view height constant 600 -> 1200 (best effort)
// Usage: ep_patch_dll.exe <official.dll> <output.dll> [url]
//        ep_patch_dll.exe --check <dll> [expectedUrl]
//          exit 0 = patched with the expected URL
//          exit 2 = official DLL, patchable
//          exit 3 = damaged / unsupported
//          exit 4 = patched with a DIFFERENT (foreign) URL - restore it first
//        ep_patch_dll.exe --ver <dll>   (prints FileVersion)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

class EpPatchDll
{
    const string DefaultUrl = "file:///C:/PROGRA~1/EXPLOR~1/EPWLOC~1/w.html";

    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--check")
        {
            // exit 0 = patched with the expected URL
            // exit 2 = official DLL, patchable (Google navigation URL present)
            // exit 3 = unknown/damaged (neither; DLL needs to be restored)
            // exit 4 = patched with a foreign URL (an older/different install)
            string expect = args.Length > 2 ? args[2] : DefaultUrl;
            byte[] c = File.ReadAllBytes(args[1]);
            string cw = Encoding.Unicode.GetString(c);
            if (cw.IndexOf(expect, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Console.WriteLine("already patched with expected URL");
                return 0;
            }
            if (cw.IndexOf("file:///", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Console.Error.WriteLine("patched with a foreign URL: " + expect);
                return 4;
            }
            if (cw.IndexOf("https://www.google.com/search?", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Console.WriteLine("official, needs patch");
                return 2;
            }
            Console.Error.WriteLine("neither patched URL nor Google URL found - DLL damaged or unsupported");
            return 3;
        }
        if (args.Length == 2 && args[0] == "--ver")
        {
            Console.WriteLine(FileVersionInfo.GetVersionInfo(args[1]).FileVersion);
            return 0;
        }
        if (args.Length < 2) { Console.Error.WriteLine("usage: ep_patch_dll [--check <dll> [url]] [--ver <dll>] [<in> <out> [url]]"); return 2; }
        byte[] d = File.ReadAllBytes(args[0]);

        // ---- patch 1: weather URL (wide char), in place, null padded ----
        string newUrl = args.Length > 2 ? args[2] : DefaultUrl;
        byte[] nu = Encoding.Unicode.GetBytes(newUrl);

        int at = -1;
        byte[] needle = null;

        // 1) the exact URL our host machine's EP build ships
        string[] known = { "https://www.google.com/search?hl=%s&q=weather%s%s" };
        foreach (var k in known)
        {
            byte[] n = Encoding.Unicode.GetBytes(k);
            int a = Find(d, n, 0);
            if (a >= 0) { at = a; needle = n; break; }
        }

        // 2) generic: scan every wide-char http(s) URL in the image and pick
        //    one that looks like the weather provider (longest, preferring
        //    "...search..."). EP versions differ in the exact URL they ship.
        string[] kws = { "weather", "forecast", "google", "msn", "bing" };
        List<string> allUrls = FindWideUrls(d);
        var cands = new List<string>();
        foreach (var u0 in allUrls)
            foreach (var kw in kws)
                if (u0.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) { cands.Add(u0); break; }
        if (at < 0 && cands.Count > 0)
        {
            cands.Sort(delegate(string a, string b)
            {
                bool sa = a.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0;
                bool sb = b.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0;
                if (sa != sb) return sa ? -1 : 1;
                return b.Length.CompareTo(a.Length);
            });
            foreach (var u in cands)
            {
                if (u.Length < newUrl.Length) continue;
                byte[] n = Encoding.Unicode.GetBytes(u);
                int a = Find(d, n, 0);
                if (a < 0) continue;
                at = a; needle = n;
                Console.WriteLine("matched weather URL: " + u);
                break;
            }
        }

        if (at < 0)
        {
            Console.Error.WriteLine("weather URL not found (unsupported EP version?)");
            Console.Error.WriteLine("all http(s) URLs found in this DLL:");
            int shown = 0;
            foreach (var u in allUrls)
            {
                if (u.Length < 16) continue;
                Console.Error.WriteLine("  " + u);
                if (++shown >= 20) { Console.Error.WriteLine("  ..."); break; }
            }
            if (shown == 0) Console.Error.WriteLine("  (none)");
            return 3;
        }
        if (Find(d, needle, at + 1) >= 0) { Console.Error.WriteLine("weather URL not unique, refusing to patch"); return 3; }
        byte[] repl = new byte[needle.Length];
        Array.Copy(nu, repl, nu.Length); // rest stays 0 = null terminator + padding
        Array.Copy(repl, 0, d, at, repl.Length);

        // ---- patch 2: mov ecx,600 (B9 58 02 00 00) -> mov ecx,1200 ----
        // must be near the other ReboundBrowser immediates: 181, 152, 1333.
        // EP builds differ: strict neighbors first, then any UNIQUE site.
        int patched = 0;
        var sites = new List<int>();
        for (int i = 0; i + 5 <= d.Length; i++)
            if (d[i] == 0xB9 && d[i + 1] == 0x58 && d[i + 2] == 0x02 && d[i + 3] == 0 && d[i + 4] == 0)
                sites.Add(i);
        foreach (int i in sites)
        {
            int lo = Math.Max(0, i - 400), hi = Math.Min(d.Length, i + 400);
            bool near = FindRange(d, new byte[] { 0xB5, 0x00, 0x00, 0x00 }, lo, hi) >= 0 &&   // 181
                        FindRange(d, new byte[] { 0x98, 0x00, 0x00, 0x00 }, lo, hi) >= 0 &&   // 152
                        FindRange(d, new byte[] { 0x35, 0x05, 0x00, 0x00 }, lo, hi) >= 0;     // 1333
            if (near || sites.Count == 1)
            {
                d[i + 1] = 0xB0; d[i + 2] = 0x04; // 1200
                patched++;
            }
        }
        if (patched != 1)
            Console.Error.WriteLine("warning: view-height constant not found; continuing without it");

        File.WriteAllBytes(args[1], d);
        Console.WriteLine("patched ok");
        return 0;
    }

    // Enumerate distinct printable ASCII URLs stored as UTF-16LE.
    static List<string> FindWideUrls(byte[] d)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 8 <= d.Length; i++)
        {
            // "http" or "https" wide: h 0 t 0 t 0 p 0 [s 0]
            if (!(d[i] == 'h' && d[i + 1] == 0 && d[i + 2] == 't' && d[i + 3] == 0 &&
                  d[i + 4] == 't' && d[i + 5] == 0 && d[i + 6] == 'p' && d[i + 7] == 0))
                continue;
            var sb = new StringBuilder();
            int j = i;
            while (j + 1 < d.Length && d[j + 1] == 0 && d[j] >= 0x20 && d[j] <= 0x7E)
            {
                sb.Append((char)d[j]);
                j += 2;
            }
            string u = sb.ToString();
            if (u.Length >= 12 && seen.Add(u))
                urls.Add(u);
            i = Math.Max(i, j - 1);
        }
        return urls;
    }

    static int Find(byte[] hay, byte[] needle, int from)
    {
        for (int i = from; i + needle.Length <= hay.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    static int FindRange(byte[] hay, byte[] needle, int lo, int hi)
    {
        for (int i = lo; i + needle.Length <= hi; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
