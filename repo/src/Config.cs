using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GoldDock
{
    public class Config
    {
        public int X = -1;
        public int Y = -1;
        public int IntervalSec = 5;
        public double Spread = 0;       // 0 = 自动跟随工行点差；>0 = 手动固定点差
        public bool TopMost = true;
        public bool Mini = false;
        public double AlertPct = 1.0;   // 跌幅超过该百分比时高亮提醒，0 = 关闭

        // 外观
        public int BgAlpha = 92;            // 背景不透明度 0..100，0 = 完全透明
        public string BgColor = "141924";   // 背景主色
        public string FgColor = "E6ECF5";   // 文字主色
        public string UpColor = "F0525A";   // 涨
        public string DownColor = "26C27C"; // 跌

        // 当日基准（开盘价）缓存
        public string IcbcBaseDate = "";        public double IcbcBasePrice = 0;
        public string IcbcBaseTime = "";
        public string ZsBaseDate = "";
        public double ZsBasePrice = 0;
        public string ZsBaseTime = "";

        // 上金所当日日盘开盘价（涨跌基准），每天取一次
        public string SgeOpenDate = "";
        public double SgeOpenPrice = 0;
        public string SgeOpenTime = "";
        // 用户手动“以当前价重置基准”的日期；等于今天时优先用下面的手工基准
        public string ManualBaseDate = "";

        readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Dir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoldDock");
                try { Directory.CreateDirectory(d); }
                catch (Exception ex) { LastError = "mkdir: " + ex.GetType().Name + " " + ex.Message; }
                return d;
            }
        }

        public static string LastError = "";

        static string _path;

        /// <summary>优先用 exe 同目录（绿色便携），不可写时退到 %APPDATA%，再退到 %TEMP%。</summary>
        public static string FilePath
        {
            get
            {
                if (_path == null) _path = ResolvePath();
                return _path;
            }
        }

        static string ResolvePath()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            if (CanWrite(dir)) return Path.Combine(dir, "GoldDock.ini");

            string ad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoldDock");
            try
            {
                Directory.CreateDirectory(ad);
                if (CanWrite(ad)) return Path.Combine(ad, "config.ini");
            }
            catch { }

            string td = Path.Combine(Path.GetTempPath(), "GoldDock");
            try
            {
                Directory.CreateDirectory(td);
                return Path.Combine(td, "config.ini");
            }
            catch { }

            return Path.Combine(dir, "GoldDock.ini");
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".gd_probe");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        c.map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch { }
            c.X = c.GetInt("x", c.X);
            c.Y = c.GetInt("y", c.Y);
            c.IntervalSec = Math.Max(2, Math.Min(120, c.GetInt("interval", c.IntervalSec)));
            c.Spread = c.GetDouble("spread", c.Spread);
            c.TopMost = c.GetInt("topmost", 1) == 1;
            c.Mini = c.GetInt("mini", 0) == 1;
            c.AlertPct = c.GetDouble("alert", c.AlertPct);
            c.BgAlpha = Math.Max(0, Math.Min(100, c.GetInt("bg.alpha", c.BgAlpha)));
            c.BgColor = c.GetStr("bg.color", c.BgColor);
            c.FgColor = c.GetStr("fg.color", c.FgColor);
            c.UpColor = c.GetStr("up.color", c.UpColor);
            c.DownColor = c.GetStr("down.color", c.DownColor);
            c.IcbcBaseDate = c.GetStr("icbc.date", "");
            c.IcbcBasePrice = c.GetDouble("icbc.price", 0);
            c.IcbcBaseTime = c.GetStr("icbc.time", "");
            c.ZsBaseDate = c.GetStr("zs.date", "");
            c.ZsBasePrice = c.GetDouble("zs.price", 0);
            c.ZsBaseTime = c.GetStr("zs.time", "");
            c.SgeOpenDate = c.GetStr("sge.open.date", "");
            c.SgeOpenPrice = c.GetDouble("sge.open.price", 0);
            c.SgeOpenTime = c.GetStr("sge.open.time", "");
            c.ManualBaseDate = c.GetStr("base.manual.date", "");
            return c;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# GoldDock 配置");
                sb.AppendLine("x=" + X.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("y=" + Y.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("interval=" + IntervalSec.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("spread=" + Spread.ToString("0.##", CultureInfo.InvariantCulture));
                sb.AppendLine("topmost=" + (TopMost ? "1" : "0"));
                sb.AppendLine("mini=" + (Mini ? "1" : "0"));
                sb.AppendLine("alert=" + AlertPct.ToString("0.##", CultureInfo.InvariantCulture));
                sb.AppendLine("bg.alpha=" + BgAlpha.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("bg.color=" + BgColor);
                sb.AppendLine("fg.color=" + FgColor);
                sb.AppendLine("up.color=" + UpColor);
                sb.AppendLine("down.color=" + DownColor);
                sb.AppendLine("icbc.date=" + IcbcBaseDate);
                sb.AppendLine("icbc.price=" + IcbcBasePrice.ToString("0.00", CultureInfo.InvariantCulture));
                sb.AppendLine("icbc.time=" + IcbcBaseTime);
                sb.AppendLine("zs.date=" + ZsBaseDate);
                sb.AppendLine("zs.price=" + ZsBasePrice.ToString("0.00", CultureInfo.InvariantCulture));
                sb.AppendLine("zs.time=" + ZsBaseTime);
                sb.AppendLine("sge.open.date=" + SgeOpenDate);
                sb.AppendLine("sge.open.price=" + SgeOpenPrice.ToString("0.00", CultureInfo.InvariantCulture));
                sb.AppendLine("sge.open.time=" + SgeOpenTime);
                sb.AppendLine("base.manual.date=" + ManualBaseDate);
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { LastError = LastError + " | save: " + ex.GetType().Name + " " + ex.Message; }
        }

        string GetStr(string k, string def)
        {
            string v;
            return map.TryGetValue(k, out v) ? v : def;
        }

        int GetInt(string k, int def)
        {
            int d;
            return int.TryParse(GetStr(k, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out d) ? d : def;
        }

        double GetDouble(string k, double def)
        {
            double d;
            return double.TryParse(GetStr(k, null), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : def;
        }
    }
}
