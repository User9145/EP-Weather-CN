// EP "News and Interests" local panel v1.0.0 - generator mode. No resident server:
// runs, fetches weather (Open-Meteo) + news (RSS) + markets (qt.gtimg.cn),
// writes w.html next to itself, exits. ep_weather_host.dll is patched to open
// w.html via file:// URL. Reads EP's own registry settings (WeatherLocation,
// WeatherLanguage, WeatherTemperatureUnit) so the EP properties dialog stays in charge.
//
// ep_weather_local_config.txt: line 1 = fallback city; lines 2+ = extra feeds "名字|RSS地址"
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;
using Microsoft.Win32;

class EpWeatherLocalGen
{
    const string Version = "1.0";

    static readonly string ExeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ExplorerPatcher", "ep_weather_local.log");
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("s") + " " + msg + "\r\n"); } catch { }
    }

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; } catch { }
        try
        {
            if (mode == "--news")
            {
                // news-only refresh: rewrite just the fragment the page pulls
                // via /newshtml; w.html (weather) is left untouched.
                bool enN = RegStr("WeatherLanguage", "zh").StartsWith("en", StringComparison.OrdinalIgnoreCase);
                var np = BuildNews(enN);
                WriteAtomic("news.html", np[0] + "<!--SPLIT-->" + np[1]);
                Log("news.html written");
                return 0;
            }
            string frag;
            string html = Render(mode == "--weather", out frag);
            WriteAtomic("w.html", html);
            if (mode != "--weather" && frag != null) WriteAtomic("news.html", frag);
            Log("w.html written");
            return 0;
        }
        catch (Exception e)
        {
            // keep the last good w.html on failure (offline boot etc.)
            Log("generate failed: " + e.Message);
            return File.Exists(Path.Combine(ExeDir, "w.html")) ? 0 : 1;
        }
    }

    // atomic-ish replace: never leave a truncated file for WebView2 to read
    static void WriteAtomic(string name, string content)
    {
        string tmp = Path.Combine(ExeDir, name + ".tmp");
        string fin = Path.Combine(ExeDir, name);
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        if (File.Exists(fin)) File.Replace(tmp, fin, null);
        else File.Move(tmp, fin);
    }

    static string RegStr(string name, string def)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\ExplorerPatcher"))
            {
                if (k != null)
                {
                    var v = k.GetValue(name);
                    if (v is string && ((string)v).Trim().Length > 0) return ((string)v).Trim();
                }
            }
        }
        catch { }
        return def;
    }

    static int RegDword(string name, int def)
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

    static List<string> ConfigLines()
    {
        var lines = new List<string>();
        try
        {
            var p = Path.Combine(ExeDir, "ep_weather_local_config.txt");
            if (File.Exists(p))
                foreach (var l in File.ReadAllLines(p, Encoding.UTF8))
                    if (l.Trim().Length > 0) lines.Add(l.Trim());
        }
        catch { }
        return lines;
    }

    static string LoadDefaultCity()
    {
        var lines = ConfigLines();
        return lines.Count > 0 ? lines[0] : "北京";
    }

    // ---------- HTTP helpers ----------

    static string HttpGet(string url, Encoding enc, int timeoutMs)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ep-news-local/4.0";
        req.Timeout = timeoutMs;
        req.ReadWriteTimeout = timeoutMs;
        using (var resp = req.GetResponse())
        using (var rs = resp.GetResponseStream())
        using (var sr = new StreamReader(rs, enc))
            return sr.ReadToEnd();
    }

    static Dictionary<string, object> GetJson(string url)
    {
        return Json.Deserialize<Dictionary<string, object>>(HttpGet(url, Encoding.UTF8, 15000));
    }

    static object[] Arr(object o)
    {
        var al = o as System.Collections.ArrayList;
        return al == null ? (object[])o : al.ToArray();
    }

    // ---------- weather ----------

    static double[] Geocode(string name)
    {
        foreach (var lang in new[] { "zh", "en" })
        {
            try
            {
                var data = GetJson("https://geocoding-api.open-meteo.com/v1/search?name=" +
                    Uri.EscapeDataString(name) + "&count=1&language=" + lang + "&format=json");
                if (data.ContainsKey("results"))
                {
                    var arr = Arr(data["results"]);
                    if (arr.Length > 0)
                    {
                        var r = (Dictionary<string, object>)arr[0];
                        return new[] { Convert.ToDouble(r["latitude"]), Convert.ToDouble(r["longitude"]) };
                    }
                }
            }
            catch (Exception e) { Log("geocode " + lang + ": " + e.Message); }
        }
        return null;
    }

    static string Cond(int code, bool en)
    {
        switch (code)
        {
            case 0: case 1: return en ? "Clear" : "晴";
            case 2: return en ? "Partly cloudy" : "局部多云";
            case 3: return en ? "Cloudy" : "阴";
            case 45: case 48: return en ? "Fog" : "雾";
            case 51: case 53: case 55: return en ? "Drizzle" : "毛毛雨";
            case 56: case 57: return en ? "Freezing drizzle" : "冻毛毛雨";
            case 61: return en ? "Light rain" : "小雨";
            case 63: return en ? "Rain" : "中雨";
            case 65: return en ? "Heavy rain" : "大雨";
            case 66: case 67: return en ? "Freezing rain" : "冻雨";
            case 71: return en ? "Light snow" : "小雪";
            case 73: return en ? "Snow" : "中雪";
            case 75: return en ? "Heavy snow" : "大雪";
            case 77: return en ? "Snow grains" : "雪粒";
            case 80: return en ? "Light showers" : "小阵雨";
            case 81: return en ? "Showers" : "阵雨";
            case 82: return en ? "Heavy showers" : "强阵雨";
            case 85: return en ? "Snow showers" : "阵雪";
            case 86: return en ? "Heavy snow showers" : "大阵雪";
            case 95: return en ? "Thunderstorm" : "雷暴";
            case 96: case 99: return en ? "Thunderstorm with hail" : "雷暴伴冰雹";
            default: return en ? "Unknown" : "未知";
        }
    }

    static string Icon(int code, bool isDay)
    {
        string dn = isDay ? "_day" : "_night";
        switch (code)
        {
            case 0: case 1: return "sunny" + dn;
            case 2: return "partly_cloudy" + dn;
            case 3: return "cloudy";
            case 45: case 48: return "fog";
            case 51: case 53: case 55: case 56: case 57: case 61: case 80: return "rain_light";
            case 63: case 65: case 81: case 82: return "rain";
            case 66: case 67: return "rain_s_snow";
            case 71: case 73: case 77: case 85: return "snow";
            case 75: case 86: return "snow_heavy";
            case 95: case 96: case 99: return "thunderstorms";
            default: return "partly_cloudy" + dn;
        }
    }

    static double ToUnit(double c, string unit) { return unit == "f" ? c * 9.0 / 5.0 + 32.0 : c; }
    static double D(Dictionary<string, object> d, string k) { return Convert.ToDouble(d[k]); }
    static int I(Dictionary<string, object> d, string k) { return Convert.ToInt32(d[k]); }

    static string Esc(string s)
    {
        if (s == null) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    // ---------- news ----------

    class NewsItem
    {
        public string Title, Link, Source, Img;
        public DateTimeOffset When;
    }

    static readonly Regex ImgRe = new Regex("<img[^>]+src=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex TagRe = new Regex("<[^>]+>", RegexOptions.Compiled);

    static List<NewsItem> FetchFeed(string url, string source, int maxItems)
    {
        var items = new List<NewsItem>();
        try
        {
            string xmlText = HttpGet(url, Encoding.UTF8, 12000);
            var doc = new XmlDocument();
            doc.LoadXml(xmlText);
            var nodes = doc.SelectNodes("//item");
            if (nodes == null) return items;
            foreach (XmlNode n in nodes)
            {
                if (items.Count >= maxItems) break;
                var it = new NewsItem { Source = source, When = DateTimeOffset.Now };
                var t = n.SelectSingleNode("title"); if (t != null) it.Title = t.InnerText.Trim();
                var l = n.SelectSingleNode("link"); if (l != null) it.Link = l.InnerText.Trim();
                var d = n.SelectSingleNode("description");
                if (d != null)
                {
                    var m = ImgRe.Match(d.InnerText);
                    if (m.Success) it.Img = m.Groups[1].Value;
                }
                var p = n.SelectSingleNode("pubDate");
                if (p != null)
                {
                    DateTimeOffset dto;
                    if (DateTimeOffset.TryParseExact(p.InnerText.Trim(),
                            "ddd, dd MMM yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture,
                            DateTimeStyles.AllowWhiteSpaces, out dto) ||
                        DateTimeOffset.TryParse(p.InnerText.Trim(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AllowWhiteSpaces, out dto))
                        it.When = dto;
                }
                if (it.Title != null && it.Title.Length > 0 && it.Link != null && it.Link.Length > 0)
                    items.Add(it);
            }
        }
        catch (Exception e) { Log("feed " + source + ": " + e.Message); }
        return items;
    }

    static string Ago(DateTimeOffset when, bool en)
    {
        var span = DateTimeOffset.Now - when;
        if (span.TotalMinutes < 1) return en ? "just now" : "刚刚";
        if (span.TotalHours < 1) return (int)span.TotalMinutes + (en ? "m ago" : " 分钟前");
        if (span.TotalDays < 1) return (int)span.TotalHours + (en ? "h ago" : " 小时前");
        return (int)span.TotalDays + (en ? "d ago" : " 天前");
    }

    // ---------- markets ----------

    class Quote { public string Name, Code; public double Price, Change, Pct; }

    static List<Quote> FetchMarkets()
    {
        var list = new List<Quote>();
        try
        {
            string raw = HttpGet("https://qt.gtimg.cn/q=sh000001,sz399001,sz399006,hkHSI",
                Encoding.GetEncoding("GB18030"), 10000);
            foreach (Match m in Regex.Matches(raw, "v_(\\w+)=\"([^\"]*)\";"))
            {
                var p = m.Groups[2].Value.Split('~');
                if (p.Length < 33) continue;
                double price, chg, pct;
                if (!double.TryParse(p[3], NumberStyles.Any, CultureInfo.InvariantCulture, out price)) continue;
                double.TryParse(p[31], NumberStyles.Any, CultureInfo.InvariantCulture, out chg);
                double.TryParse(p[32], NumberStyles.Any, CultureInfo.InvariantCulture, out pct);
                list.Add(new Quote { Name = p[1], Code = p[2], Price = price, Change = chg, Pct = pct });
            }
        }
        catch (Exception e) { Log("markets: " + e.Message); }
        return list;
    }

    static string NewsLink(NewsItem it, string inner, string cls)
    {
        // opens in the host's default browser via the 127.0.0.1 command service;
        // never navigates the WebView (any navigation would trigger EP's error page).
        return "<a class=\"" + cls + "\" href=\"javascript:void(0)\" onclick=\"return ep_cmd('open?u=" +
            Uri.EscapeDataString(it.Link) + "')\">" + inner + "</a>";
    }

    // ---------- render ----------

    static string Render(bool skipNews, out string newsFrag)
    {
        // EP properties dialog drives these:
        string term = RegStr("WeatherLocation", "");
        string hl = RegStr("WeatherLanguage", "zh");
        string unit = RegDword("WeatherTemperatureUnit", 0) == 1 ? "f" : "c";
        bool en = hl.StartsWith("en", StringComparison.OrdinalIgnoreCase);

        string city = term.Length > 0 ? term : LoadDefaultCity();
        var geo = Geocode(city);
        if (geo == null && city != LoadDefaultCity()) { geo = Geocode(LoadDefaultCity()); city = LoadDefaultCity(); }
        if (geo == null) { geo = new double[] { 39.9075, 116.3972 }; city = LoadDefaultCity(); }

        var wx = GetJson(
            "https://api.open-meteo.com/v1/forecast?latitude=" + geo[0] + "&longitude=" + geo[1] +
            "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m,is_day" +
            "&hourly=temperature_2m,weather_code,is_day" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
            "&timezone=auto&forecast_days=7");

        var cur = (Dictionary<string, object>)wx["current"];
        int code = I(cur, "weather_code");
        bool isDay = I(cur, "is_day") == 1;
        string cond = Cond(code, en);
        string icon = Icon(code, isDay);
        double tc = D(cur, "temperature_2m");
        int temp = (int)Math.Round(ToUnit(tc, unit));
        int other = (int)Math.Round(ToUnit(tc, unit == "c" ? "f" : "c"));
        int feels = (int)Math.Round(ToUnit(D(cur, "apparent_temperature"), unit));
        string hum = cur.ContainsKey("relative_humidity_2m") ? I(cur, "relative_humidity_2m").ToString() : "--";
        string wind = cur.ContainsKey("wind_speed_10m") ? D(cur, "wind_speed_10m").ToString("0.#") : "--";
        string unitSym = unit == "c" ? "°C" : "°F";
        string otherSym = unit == "c" ? "°F" : "°C";
        string otherUnit = unit == "c" ? "f" : "c";
        int dataC = (int)Math.Round(tc);
        int dataF = (int)Math.Round(tc * 9.0 / 5.0 + 32.0);

        // compact 7-day strip for the weather card
        var daily = (Dictionary<string, object>)wx["daily"];
        var dTime = Arr(daily["time"]);
        var dCode = Arr(daily["weather_code"]);
        var dMax = Arr(daily["temperature_2m_max"]);
        var dMin = Arr(daily["temperature_2m_min"]);
        string[] wdZh = { "一", "二", "三", "四", "五", "六", "日" };
        string[] wdEn = { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" };
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(7, dTime.Length); i++)
        {
            var dt = DateTime.Parse((string)dTime[i]);
            int dow = (int)dt.DayOfWeek == 0 ? 6 : (int)dt.DayOfWeek - 1;
            string label = i == 0 ? (en ? "Now" : "今") : (en ? wdEn : wdZh)[dow];
            sb.Append("<div class=\"wd\"><div class=\"wdl\">" + label + "</div><img src=\"icons/" +
                Icon(Convert.ToInt32(dCode[i]), true) + ".png\" alt=\"\"><div class=\"wdv\">" +
                (int)Math.Round(ToUnit(Convert.ToDouble(dMax[i]), unit)) + "°</div><div class=\"wdn\">" +
                (int)Math.Round(ToUnit(Convert.ToDouble(dMin[i]), unit)) + "°</div></div>");
        }
        string days = sb.ToString();

        // hourly strip (hidden behind 更多)
        var hourly = (Dictionary<string, object>)wx["hourly"];
        var hTime = Arr(hourly["time"]);
        var hCode = Arr(hourly["weather_code"]);
        var hTemp = Arr(hourly["temperature_2m"]);
        var hDay = hourly.ContainsKey("is_day") ? Arr(hourly["is_day"]) : null;
        string nowH = ((string)cur["time"]).Substring(0, 13);
        int idx = 0;
        for (int i = 0; i < hTime.Length; i++)
            if (((string)hTime[i]).Substring(0, 13) == nowH) { idx = i; break; }

        // taskbar + panel headline show THIS HOUR's weather (per user request),
        // not the instantaneous "current" summary: temp, condition and icon
        // (incl. day/night) come from the hourly grid entry for the current hour.
        int hc0 = Convert.ToInt32(hCode[idx]);
        bool hd0 = hDay == null || Convert.ToInt32(hDay[idx]) == 1;
        code = hc0; cond = Cond(hc0, en); icon = Icon(hc0, hd0);
        tc = Convert.ToDouble(hTemp[idx]);
        temp = (int)Math.Round(ToUnit(tc, unit));
        other = (int)Math.Round(ToUnit(tc, unit == "c" ? "f" : "c"));
        dataC = (int)Math.Round(tc);
        dataF = (int)Math.Round(tc * 9.0 / 5.0 + 32.0);
        sb = new StringBuilder();
        for (int i = idx; i < Math.Min(idx + 8, hTime.Length); i++)
        {
            bool hd = hDay == null || Convert.ToInt32(hDay[i]) == 1;
            sb.Append("<div class=\"wd\"><div class=\"wdl\">" + ((string)hTime[i]).Substring(11, 5) +
                "</div><img src=\"icons/" + Icon(Convert.ToInt32(hCode[i]), hd) + ".png\" alt=\"\"><div class=\"wdv\">" +
                (int)Math.Round(ToUnit(Convert.ToDouble(hTemp[i]), unit)) + "°</div></div>");
        }
        string hours = sb.ToString();

        // ----- news + markets (live-refresh fragment) -----
        string newsL = "", newsR = ""; newsFrag = null;
        if (!skipNews)
        {
            var np = BuildNews(en);
            newsL = np[0]; newsR = np[1];
            newsFrag = newsL + "<!--SPLIT-->" + newsR;
        }

        return Template
            .Replace("{{ICON}}", icon).Replace("{{COND}}", Esc(cond))
            .Replace("{{TEMP}}", temp.ToString()).Replace("{{TEMP_OTHER}}", other.ToString())
            .Replace("{{DATA_C}}", dataC.ToString()).Replace("{{DATA_F}}", dataF.ToString())
            .Replace("{{UNIT}}", unitSym).Replace("{{OTHER_UNIT}}", otherUnit).Replace("{{OTHER_SYM}}", otherSym)
            .Replace("{{LOC}}", Esc(city))
            .Replace("{{FEELS_LABEL}}", en ? "Feels like" : "体感")
            .Replace("{{FEELS}}", feels + unitSym)
            .Replace("{{HUM_LABEL}}", en ? "Humidity" : "湿度").Replace("{{HUM}}", hum)
            .Replace("{{WIND_LABEL}}", en ? "Wind" : "风速").Replace("{{WIND}}", wind)
            .Replace("{{HOURS}}", hours).Replace("{{DAYS}}", days)
            .Replace("{{NEWS_L}}", newsL)
            .Replace("{{NEWS_R}}", newsR)
            .Replace("{{NEWS_SEC}}", RegDword("WeatherContentUpdateMode", 1200).ToString())
            .Replace("{{MORE}}", en ? "More" : "更多")
            .Replace("{{REFRESH_TITLE}}", en ? "Refresh" : "刷新")
            .Replace("{{BUSY_LABEL}}", en ? "Refreshing" : "刷新中")
            .Replace("{{SETLOC_BTN}}", en ? "Save" : "保存")
            .Replace("{{SETLOC_HINT}}", en ? "Weather location; applies after a few seconds" : "天气地区，保存后数秒内生效")
            .Replace("{{FREQ_LABEL}}", "")
            .Replace("{{FREQ_ROW}}", "")
            .Replace("{{FREQ_OK}}", "")
            .Replace("{{VERSION}}", Version);
    }

    // Builds the live-refresh fragment: left column (top stories + image/text
    // cards) and right column (markets card + image cards). The page shell is
    // static; these two columns are hot-swapped by the page's own JS timer via
    // the /newshtml endpoint, so "update frequency" governs NEWS while the
    // weather follows its own fixed hourly grid.
    static string[] BuildNews(bool en)
    {
        var sb = new StringBuilder();
        foreach (var q in FetchMarkets())
        {
            string cls = q.Change >= 0 ? "up" : "down";
            string sign = q.Change >= 0 ? "+" : "";
            sb.Append("<div class=\"mrow\"><div class=\"ml\"><div class=\"mcode\">" + Esc(q.Code) +
                "</div><div class=\"mname\">" + Esc(q.Name) + "</div></div><div class=\"mr\"><div class=\"mprice\">" +
                q.Price.ToString("N2", CultureInfo.InvariantCulture) + "</div><div class=\"mpct " + cls + "\">" +
                sign + q.Pct.ToString("0.00") + "%</div></div></div>");
        }
        string markets = sb.ToString();

        var feeds = new List<string[]> {
            new[] { en ? "China News" : "中新网", "https://www.chinanews.com.cn/rss/scroll-news.xml" },
            new[] { en ? "ITHome" : "IT之家", "https://www.ithome.com/rss/" }
        };
        var extra = ConfigLines();
        for (int i = 1; i < extra.Count; i++)
        {
            var parts = extra[i].Split('|');
            if (parts.Length == 2) feeds.Add(new[] { parts[0].Trim(), parts[1].Trim() });
        }
        var all = new List<NewsItem>();
        foreach (var f in feeds) all.AddRange(FetchFeed(f[1], f[0], 14));
        all.Sort((a, b2) => b2.When.CompareTo(a.When));

        var topStories = new StringBuilder();
        int tsCount = 0, imgIdx = 0;
        var cards = new List<string[]>(); // [html, "img"|"txt"] in time order
        foreach (var it in all)
        {
            if (it.Img == null && tsCount < 4)
            {
                tsCount++;
                topStories.Append(NewsLink(it,
                    "<div class=\"tmeta\">" + Esc(it.Source) + " ・ " + Ago(it.When, en) + "</div>" +
                    "<div class=\"ttitle\">" + Esc(it.Title) + "</div>", "titem"));
            }
            else if (it.Img != null && imgIdx < 10)
            {
                imgIdx++;
                // a broken image hides only itself, never the whole card -
                // otherwise the column heights collapse and leave a gap
                cards.Add(new[] { NewsLink(it,
                    "<img class=\"icimg\" src=\"" + Esc(it.Img) + "\" loading=\"lazy\" onerror=\"this.style.display='none'\">" +
                    "<div class=\"icover\"><div class=\"icmeta\">" + Esc(it.Source) + " ・ " + Ago(it.When, en) + "</div>" +
                    "<div class=\"ictitle\">" + Esc(it.Title) + "</div></div>", "icard"), "img" });
            }
            else
            {
                cards.Add(new[] { NewsLink(it,
                    "<div class=\"nb\"><div class=\"nt\">" + Esc(it.Title) + "</div>" +
                    "<div class=\"nm\">" + Esc(it.Source) + " ・ " + Ago(it.When, en) + "</div></div>", "news noimg dcard"), "txt" });
            }
        }
        // greedy height balance: left starts with the stories card (~260px),
        // right with weather (~340px) + markets (~220px); each card goes to
        // whichever column is currently shorter so neither ends in a void
        var leftCards = new StringBuilder();
        var rightCards = new StringBuilder();
        double lh = 260, rh = 560;
        foreach (var c in cards)
        {
            double h = c[1] == "img" ? 194 : 88;
            if (lh <= rh) { leftCards.Append(c[0]); lh += h; }
            else { rightCards.Append(c[0]); rh += h; }
        }

        string left = "<div class=\"dcard\"><div class=\"dhead\">" + (en ? "Top stories" : "热门故事") +
            " <span class=\"chev\">&gt;</span></div>" + topStories + "</div>" + leftCards;
        string right = "<div class=\"dcard\"><div class=\"dhead\">" + (en ? "Markets" : "市场") + "</div>" +
            markets + "</div>" + rightCards;
        return new[] { left, right };
    }

    // Win10 "News and Interests" replica: dark acrylic panel, two-column card grid,
    // compact blue weather card (EP hooks inside), markets card rows, story cards,
    // image cards, top-right refresh + menu (version). EP flyout window height follows
    // .ULSxyf offsetHeight x 353/367; WebView2 raster is patched to 1200px.
    const string Template = @"<!DOCTYPE html>
<html dir=""ltr"" lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<style>
* { margin:0; padding:0; box-sizing:border-box;
    font-family:""Segoe UI"",""Microsoft YaHei"",sans-serif; }
html,body { background:#424243; overflow:hidden; }
.ULSxyf { position:absolute; left:181px; top:152px; width:673px; height:690px; overflow:hidden; }
.panel { width:673px; height:664px; border-radius:0; color:#fff; overflow:hidden;
         background:transparent; border:none; position:relative;
         box-shadow:inset 0 0 0 1px rgba(255,255,255,0.08);
         display:flex; flex-direction:column; transform:translateY(0);
         transition:transform .3s cubic-bezier(.17,.84,.44,1); }
/* film-grain noise + glass inner highlight (pure texture, no layout change) */
.panel::after { content:""; position:absolute; left:0; top:0; right:0; bottom:0;
         pointer-events:none; opacity:0.04; mix-blend-mode:overlay;
         background-image:url(""data:image/svg+xml;base64,PHN2ZyB4bWxucz0naHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmcnIHdpZHRoPScxNDAnIGhlaWdodD0nMTQwJz48ZmlsdGVyIGlkPSduJz48ZmVUdXJidWxlbmNlIHR5cGU9J2ZyYWN0YWxOb2lzZScgYmFzZUZyZXF1ZW5jeT0nMC44NScgbnVtT2N0YXZlcz0nMicgc3RpdGNoVGlsZXM9J3N0aXRjaCcvPjwvZmlsdGVyPjxyZWN0IHdpZHRoPScxNDAnIGhlaWdodD0nMTQwJyBmaWx0ZXI9J3VybCgjbiknLz48L3N2Zz4=""); }
.panel.cls { transform:translateY(105%); }
.panel.closing { transition:transform .24s cubic-bezier(.55,.06,.68,.19); }
.topbar { display:flex; justify-content:flex-end; align-items:center; gap:4px;
          padding:12px 12px 8px; flex:none; }
.tb { display:inline-flex; align-items:center; justify-content:center; width:34px; height:34px;
      color:#ddd; text-decoration:none; font-size:17px; border-radius:4px; cursor:pointer; }
.tb:hover { background:rgba(255,255,255,.12); }
.tb.busy { width:auto; padding:0 8px; font-size:11px; color:#4ea3f1; pointer-events:none; }
details.menu { position:relative; }
details.menu summary { list-style:none; display:inline-flex; align-items:center;
      justify-content:center; width:34px; height:34px; color:#ddd; font-size:17px;
      border-radius:4px; cursor:pointer; }
details.menu summary::-webkit-details-marker { display:none; }
details.menu summary:hover { background:rgba(255,255,255,.12); }
details.menu[open] summary { background:rgba(255,255,255,.12); }
.mpop { position:absolute; right:0; top:34px; width:220px; background:rgba(43,43,46,.94);
        border:1px solid #454545; border-radius:6px; padding:12px 14px; font-size:12px;
        line-height:1.7; color:#ddd; z-index:60; box-shadow:0 4px 14px rgba(0,0,0,.5); }
.mpop .mv { font-weight:600; color:#fff; }
.cols { display:flex; gap:12px; padding:6px 14px 14px; overflow-y:auto; overflow-x:hidden;
        scrollbar-width:thin; flex:1; align-items:flex-start; }
.col { display:flex; flex-direction:column; gap:12px; width:316px; flex:none; }
.dcard { background:#2e2e33; border-radius:8px; padding:12px 14px; }
.dhead { font-size:14px; font-weight:600; margin-bottom:6px; display:flex;
         justify-content:space-between; }
.dhead .chev { color:#999; font-weight:400; }
/* weather card */
.wcard { background:linear-gradient(150deg,rgba(59,131,224,.88) 0%,rgba(43,104,196,.88) 60%,rgba(36,86,171,.88) 100%);
         border-radius:8px; padding:12px 14px 10px; }
.wloc { font-size:13px; font-weight:600; display:flex; justify-content:space-between; }
.wcond { font-weight:400; }
.wmain { display:flex; align-items:center; margin-top:4px; }
.wmain img.wicon { width:56px; height:56px; margin-right:8px; }
.wtemp { font-size:44px; font-weight:300; line-height:1; display:flex; align-items:flex-start; }
.wob-unit { display:flex; flex-direction:column; margin-left:4px; margin-top:2px;
            font-size:12px; line-height:1.3; }
.wob-unit span.wob_t { font-weight:600; }
.wob-unit a.wob_t { color:#fff; opacity:.65; text-decoration:none; cursor:pointer; }
.wmeta { font-size:11px; opacity:.85; margin-top:6px; }
.wdays { display:flex; justify-content:space-between; margin-top:8px;
         border-top:1px solid rgba(255,255,255,.25); padding-top:6px; }
.wd { display:flex; flex-direction:column; align-items:center; }
.wd img { width:24px; height:24px; margin:3px 0; }
.wdl { font-size:11px; opacity:.8; } .wdv { font-size:11px; }
.wdn { font-size:10px; opacity:.6; }
.whours { display:flex; justify-content:space-between; margin-top:6px;
          border-top:1px solid rgba(255,255,255,.25); padding-top:6px; }
details.wmore { margin:8px auto 0; text-align:center; }
details.wmore summary { display:inline-block; list-style:none; background:rgba(255,255,255,.18);
         color:#fff; font-size:12px; padding:4px 22px; border-radius:12px; cursor:pointer; }
details.wmore summary::-webkit-details-marker { display:none; }
details.wmore summary:hover { background:rgba(255,255,255,.28); }
/* markets */
.mrow { display:flex; justify-content:space-between; align-items:center;
        padding:6px 0; border-bottom:1px solid #3d3d3d; }
.mrow:last-child { border-bottom:none; }
.mcode { font-size:13px; font-weight:600; }
.mname { font-size:11px; color:#999; margin-top:1px; }
.mr { text-align:right; }
.mprice { font-size:13px; font-weight:600; }
.mpct { font-size:11px; margin-top:1px; }
.up { color:#e7484d; } .down { color:#2ea043; }
/* top stories */
.titem { display:block; text-decoration:none; color:#fff; padding:7px 0;
         border-bottom:1px solid #3d3d3d; }
.titem:last-child { border-bottom:none; }
.tmeta { font-size:11px; color:#4ea3f1; margin-bottom:3px; }
.ttitle { font-size:13px; font-weight:600; line-height:1.4; display:-webkit-box;
          -webkit-line-clamp:2; -webkit-box-orient:vertical; overflow:hidden; }
.titem:hover .ttitle { text-decoration:underline; }
/* image cards */
.icard { display:block; position:relative; border-radius:8px; overflow:hidden;
         text-decoration:none; color:#fff; background:#2e2e33; }
.icimg { width:316px; height:170px; object-fit:cover; display:block; }
.icover { position:absolute; left:0; right:0; bottom:0; padding:26px 12px 10px;
          background:linear-gradient(transparent,rgba(0,0,0,.85)); }
.icmeta { font-size:11px; color:#8ec6ff; margin-bottom:3px; }
.ictitle { font-size:13px; font-weight:600; line-height:1.35; display:-webkit-box;
           -webkit-line-clamp:2; -webkit-box-orient:vertical; overflow:hidden; }
/* leftover text news */
.news.noimg { display:block; text-decoration:none; color:#fff; }
.nt { font-size:13px; font-weight:600; line-height:1.4; }
.nm { font-size:11px; color:#999; margin-top:5px; }
/* dark scrollbar */
.cols { scrollbar-color:#4d4d4d #262626; }
.cols::-webkit-scrollbar { width:9px; }
.cols::-webkit-scrollbar-thumb { background:#4d4d4d; border-radius:5px;
        border:2px solid #262626; }
.cols::-webkit-scrollbar-track { background:transparent; }
/* location setting in menu */
.lset { display:flex; gap:6px; margin:8px 0 4px; }
.lset input { flex:1; min-width:0; background:#1f1f1f; border:1px solid #4a4a4a;
        border-radius:4px; color:#eee; font-size:12px; padding:5px 8px; outline:none; }
.lset input:focus { border-color:#4ea3f1; }
.lset button { background:#2b68c4; border:none; color:#fff; font-size:12px;
        padding:5px 12px; border-radius:4px; cursor:pointer; }
.lset button:hover { background:#3b83e0; }
.lhint { font-size:11px; color:#888; margin-bottom:4px; }
.freq { display:grid; grid-template-columns:repeat(4,1fr); gap:6px; margin:0 0 6px; }
.fq { text-align:center; background:rgba(255,255,255,.08); border-radius:4px;
      color:#ddd; text-decoration:none; padding:4px 0; font-size:12px; cursor:pointer;
      border:1px solid transparent; }
.fq:hover { background:rgba(255,255,255,.18); }
.fq.sel { border-color:#4ea3f1; }
.fq.on { background:#2b68c4; color:#fff; }
.fqok { display:block; width:auto; margin:0 0 4px auto; background:#2b68c4; border:none;
        color:#fff; font-size:12px; padding:5px 12px; border-radius:4px; cursor:pointer; }
.fqok:hover { background:#3b83e0; }
</style>
<script>
// In-place unit switch: never navigates (a navigation would destroy the JS
// context before ExplorerPatcher's follow-up script runs -> error page).
// wob_ttm always keeps the other unit's value for EP's 'x' read path.
function ep_unit(u) {
  var tm = document.getElementById('wob_tm');
  tm.innerText = tm.getAttribute('data-' + u);
  var span = document.querySelector('.wob-unit span.wob_t');
  var a = document.querySelector('.wob-unit a.wob_t');
  if (u === 'f') { span.innerText = '°F'; a.innerText = '°C';
                   a.onclick = function(){ return ep_unit('c'); }; }
  else           { span.innerText = '°C'; a.innerText = '°F';
                   a.onclick = function(){ return ep_unit('f'); }; }
  return false;
}
// Fire-and-forget command channel to the local ep_cmd_server (127.0.0.1:24381).
// Fetching http:// from a file:// page works here because EP launches WebView2
// with --disable-web-security. No navigation ever happens in the flyout.
function ep_cmd(c) {
  try { fetch('http://127.0.0.1:24381/' + c).catch(function(){}); } catch(e) {}
  return false;
}
function ep_setloc() {
  var v = document.getElementById('locin').value.trim();
  if (!v) return false;
  return ep_cmd('setloc?loc=' + encodeURIComponent(v));
}
// refresh: the button flips to a busy label until EP reloads the page (the
// reload itself restores the icon); a 12 s safety net restores it otherwise
function ep_refresh() {
  var b = document.getElementById('rbtn');
  if (b.getAttribute('data-busy') === '1') return false;
  b.setAttribute('data-busy', '1');
  b.className = 'tb busy';
  b.innerHTML = '{{BUSY_LABEL}}';
  setTimeout(ep_news, 2500);
  setTimeout(function() {
    try { b.className = 'tb'; b.innerHTML = '&#8635;'; b.removeAttribute('data-busy'); } catch (e) {}
  }, 12000);
  return ep_cmd('refresh');
}
// ---- visibility sync: the host window cannot be moved from outside (EP
// force-docks it), so the slide animation is done here with CSS transforms.
// The local service tells us over /vis + /viswait when the window shows/hides.
var epOpen = true; // matches the initial HTML (panel starts in the open state)
function ep_collapse(instant) {
  var p = document.getElementById('stage');
  if (!epOpen) return;
  epOpen = false;
  if (instant) {
    p.style.transition = 'none';
    p.classList.remove('closing');
    p.classList.add('cls');
    void p.offsetHeight;
    p.style.transition = '';
  } else {
    p.classList.add('closing');
    p.classList.add('cls');
  }
  // closing the panel always folds the ... menu back up
  var m = document.querySelector('details.menu');
  if (m) m.open = false;
}
function ep_expand(animate) {
  var p = document.getElementById('stage');
  if (epOpen) return;
  epOpen = true;
  if (!animate) {
    p.style.transition = 'none';
    p.classList.remove('cls', 'closing');
    void p.offsetHeight;
    p.style.transition = '';
  } else {
    p.classList.remove('cls', 'closing');
  }
}
// EP's open sequence hides and re-shows the flyout window several times in
// quick succession (dock recomputation), so a single 'hidden' reading is not
// trustworthy: if we folded the panel away on the last transient hide, the
// reopened window shows an empty gray sheet until some click re-expands it.
// Debounce: only fold away when the service confirms hidden again after 600ms.
var ep_hideTimer = 0;
function ep_applyState(t, initial) {
  var parts = t.split(';');
  var vis = parts[0] === '1';
  var age = parseInt(parts[1] || '999999', 10);
  if (vis) {
    clearTimeout(ep_hideTimer);
    if (initial && age < 800) {
      // fresh navigation right as EP shows the flyout: replay the slide-up
      ep_collapse(true);
      setTimeout(function() { ep_expand(true); }, 30);
    } else ep_expand(!initial);
  } else if (initial) {
    ep_collapse(true); // initial sync while hidden: no animation, no risk
  } else {
    clearTimeout(ep_hideTimer);
    ep_hideTimer = setTimeout(function() {
      fetch('http://127.0.0.1:24381/vis').then(function(r) { return r.text(); })
        .then(function(t2) {
          if (t2.split(';')[0] !== '1') ep_collapse(true);
        }).catch(function() {});
    }, 600);
  }
}
function ep_viswait() {
  fetch('http://127.0.0.1:24381/viswait').then(function(r) { return r.text(); }).then(function(t) {
    ep_applyState(t, false);
    ep_viswait();
  }).catch(function() { setTimeout(ep_viswait, 2000); });
}
fetch('http://127.0.0.1:24381/vis').then(function(r) { return r.text(); }).then(function(t) {
  ep_applyState(t, true);
}).catch(function() {});
ep_viswait();
// click anywhere outside the ... menu closes it
document.addEventListener('click', function(e) {
  var m = document.querySelector('details.menu');
  if (m && m.open && !m.contains(e.target)) m.open = false;
});
// news hot-swap: pull the fragment from the local command service and replace
// both columns in place - no navigation, no EP reload. Refresh timing is
// server-driven: the service long-polls us via /newswait the moment it has
// regenerated news.html, so the page keeps no interval timer of its own.
function ep_news() {
  try {
    fetch('http://127.0.0.1:24381/newshtml').then(function(r){ return r.text(); }).then(function(t){
      var p = t.split('<!--SPLIT-->');
      if (p.length === 2) {
        document.getElementById('newsL').innerHTML = p[0];
        document.getElementById('newsR').innerHTML = p[1];
      }
    }).catch(function(){});
  } catch(e) {}
}
function ep_news_wait() {
  try {
    fetch('http://127.0.0.1:24381/newswait').then(function(r){ return r.text(); }).then(function(){
      ep_news();
      ep_news_wait();
    }).catch(function(){ setTimeout(ep_news_wait, 5000); });
  } catch(e) { setTimeout(ep_news_wait, 5000); }
}
ep_news_wait();
// the flyout keeps its DOM while hidden - close the menu every time the
// panel regains focus (i.e. every time it is reopened)
window.addEventListener('focus', function() {
  var m = document.querySelector('details.menu');
  if (m) m.open = false;
  // fallback: if the local service is down the /vis channel never answers -
  // a focused flyout is by definition visible, so expand on our own
  ep_expand(true);
});
document.addEventListener('visibilitychange', function() {
  if (!document.hidden) ep_expand(true);
});
</script>
</head>
<body>
<div class=""ULSxyf"">
  <div class=""KFFQ0c""></div>
  <!-- inert protocol hooks: only read by the EP weather-host scrape script -->
  <span id=""wob_dts"" style=""display:none""></span>
  <span class=""YfftMc"" style=""display:none""><a href=""#""></a></span>
  <div class=""panel"" id=""stage"">
    <div class=""topbar"">
      <a class=""tb"" id=""rbtn"" href=""javascript:void(0)"" onclick=""return ep_refresh()"" title=""{{REFRESH_TITLE}}"">&#8635;</a>
      <details class=""menu""><summary>&#8943;</summary>
        <div class=""mpop""><span class=""mv"">资讯和兴趣 (本地版)</span> <span style=""color:#888"">v{{VERSION}}</span>
          <div class=""lset""><input id=""locin"" type=""text"" placeholder=""{{LOC}}"" value=""{{LOC}}""><button onclick=""ep_setloc();return false"">{{SETLOC_BTN}}</button></div>
          <div class=""lhint"">{{SETLOC_HINT}}</div>
          <div class=""lhint"" style=""margin-top:6px"">{{FREQ_LABEL}}</div>
          <div class=""freq"" style=""display:none"">{{FREQ_ROW}}</div>
          <button class=""fqok"" style=""display:none"" onclick=""return false"">{{FREQ_OK}}</button>
          数据: Open-Meteo ・ 腾讯行情 ・ RSS
        </div>
      </details>
    </div>
    <div class=""cols"">
      <div class=""col"" id=""newsL"">{{NEWS_L}}</div>
      <div class=""col"">
        <div class=""wcard"">
          <div class=""wloc""><span id=""wob_loc"">{{LOC}}</span><span class=""wcond"">{{COND}}</span></div>
          <div class=""wmain"">
            <img class=""YQ4gaf zr758c wicon"" id=""wob_tci"" src=""icons/{{ICON}}.png"" alt=""{{COND}}"" width=""56"" height=""56"">
            <div class=""wtemp""><span id=""wob_tm"" data-c=""{{DATA_C}}"" data-f=""{{DATA_F}}"">{{TEMP}}</span><span id=""wob_ttm"" style=""display:none"">{{TEMP_OTHER}}</span></div>
            <div class=""wob-unit""><span class=""wob_t"">{{UNIT}}</span><a class=""wob_t"" href=""javascript:void(0)"" onclick=""ep_unit('{{OTHER_UNIT}}');return false"">{{OTHER_SYM}}</a></div>
          </div>
          <div class=""wmeta"">{{FEELS_LABEL}} {{FEELS}} ・ {{HUM_LABEL}} {{HUM}}% ・ {{WIND_LABEL}} {{WIND}} km/h</div>
          <div class=""wdays"">{{DAYS}}</div>
          <details class=""wmore""><summary>{{MORE}}</summary><div class=""whours"">{{HOURS}}</div></details>
        </div>
        <div class=""col"" id=""newsR"" style=""width:auto;flex:none"">{{NEWS_R}}</div>
      </div>
    </div>
  </div>
</div>
</body>
</html>";
}
