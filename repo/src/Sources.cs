using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace GoldDock
{
    public class Quote
    {
        public string Key = "";
        public string Name = "";
        public bool Alive;          // got a fresh number this round
        public bool Official;       // true = bank's own quote, false = derived
        public double Price;
        public double High;
        public double Low;
        public double Reg;          // 定期积存价 (ICBC) / 0
        public DateTime Stamp;      // quote time reported by source
        public string Note = "";    // source label / error text
    }

    public class Tick
    {
        public Quote Icbc = new Quote();
        public Quote Zs = new Quote();
        public double SgePrice;
        public double SgeHigh;
        public double SgeLow;
        public double SgeOpen;
        public bool SgeOk;
        public string SgeErr = "";

        // 上金所 Au99.99 日盘开盘（当日 09:00 后第一根分钟线的开盘价），涨跌基准
        public bool SgeDayOpenOk;
        public double SgeDayOpen;
        public string SgeDayOpenTime = "";
        public string SgeDayOpenErr = "";
        public DateTime FetchedAt = DateTime.MinValue;

        public bool AnyAlive { get { return Icbc.Alive || Zs.Alive; } }
    }

    public static class Sources
    {
        // --- ICBC 积存金 (公开网银行情接口，免登录，POST tranCode) ----------
        const string IcbcMain = "https://mybank.icbc.com.cn/servlet/AsynGetDataServlet";
        const string IcbcBackup = "https://cmbcnp.icbc.com.cn/servlet/AsynGetDataServlet";
        const string IcbcReferer = "https://mybank.icbc.com.cn/icbc/newperbank/perbank3/gold/goldaccrual_query_out.jsp";

        // --- 上海黄金交易所 Au99.99 (东方财富转发) --------------------------
        const string SgeUrl = "https://push2.eastmoney.com/api/qt/stock/get?secid=118.AU9999&fields=f43,f44,f45,f46,f57,f58,f60";
        static readonly string[] SgeHosts = new string[] { "push2.eastmoney.com", "82.push2.eastmoney.com", "push2delay.eastmoney.com" };

        static Sources()
        {
            try
            {
                ServicePointManager.SecurityProtocol =
                    (SecurityProtocolType)3072 | (SecurityProtocolType)768; // Tls12 | Tls11
            }
            catch { }
            try { ServicePointManager.Expect100Continue = false; } catch { }
            try { ServicePointManager.DefaultConnectionLimit = 8; } catch { }
        }

        public static string LastRaw = "";

        static string Once(Func<string> call, out Exception last)
        {
            last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { return call(); }
                catch (Exception ex) { last = ex; Thread.Sleep(220 + attempt * 260); }
            }
            return null;
        }

        public static string HttpGet(string url, string referer, int timeoutMs)
        {
            return Encoding.UTF8.GetString(HttpGetBytes(url, referer, timeoutMs));
        }

        public static byte[] HttpGetBytes(string url, string referer, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";
            req.Accept = "*/*";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.KeepAlive = false;
            if (referer != null) req.Referer = referer;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[8192];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                return ms.ToArray();
            }
        }

        public static string HttpPost(string url, string body, string referer, int timeoutMs)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";
            req.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";
            req.Accept = "application/json, text/javascript, */*; q=0.01";
            req.Headers["X-Requested-With"] = "XMLHttpRequest";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.KeepAlive = false;
            if (referer != null) req.Referer = referer;
            req.ContentLength = payload.Length;
            using (Stream rs = req.GetRequestStream()) rs.Write(payload, 0, payload.Length);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        static string IcbcCall(string endpoint)
        {
            return HttpPost(endpoint, "tranCode=A00622", IcbcReferer, 9000);
        }

        /// <summary>工行积存金 (JC001)。返回 null 表示失败，err 带原因。</summary>
        public static Quote FetchIcbc(out string err)
        {
            err = null;
            string text = null;
            Exception last = null;

            text = Once(delegate { return IcbcCall(IcbcMain); }, out last);
            if (text == null) text = Once(delegate { return IcbcCall(IcbcBackup); }, out last);

            if (text == null)
            {
                err = Short(last);
                return null;
            }
            LastRaw = text;
            try
            {
                Dictionary<string, object> root = Json.Obj(Json.Parse(text));
                List<object> rf = Json.Arr(Json.Get(root, "rf"));
                if (rf == null || rf.Count == 0)
                {
                    err = "接口无报价 (TranErrorCode=" + (Json.Str(root, "TranErrorCode") ?? "?") + ")";
                    return null;
                }
                Dictionary<string, object> row = Json.Obj(rf[0]);
                double price = Json.Dbl(row, "ActivePrice", double.NaN);
                if (double.IsNaN(price) || price <= 0)
                {
                    err = "报价为空";
                    return null;
                }
                Quote q = new Quote();
                q.Key = "icbc";
                q.Name = "工银积存金";
                q.Price = price;
                q.High = Json.Dbl(row, "HighPrice", 0);
                q.Low = Json.Dbl(row, "LowPrice", 0);
                q.Reg = Json.Dbl(row, "RegPrice", 0);
                q.Official = true;
                q.Alive = true;
                q.Stamp = ParseIcbcTime(Json.Str(root, "sysdate"));
                q.Note = "工行公开行情";
                return q;
            }
            catch (Exception ex)
            {
                err = "解析失败: " + ex.Message;
                return null;
            }
        }

        static DateTime ParseIcbcTime(string s)
        {
            DateTime t;
            string[] fmts = new string[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyyMMddHHmmss" };
            if (s != null && DateTime.TryParseExact(s.Trim(), fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                return t;
            return DateTime.Now;
        }

        /// <summary>上金所 Au99.99 现价/今开/最高/最低。新浪为主，东财为备。</summary>
        public static bool FetchSge(out double price, out double open, out double high, out double low, out string err)
        {
            price = open = high = low = 0;
            err = null;
            Exception last = null;

            // 主：新浪 hq_str_gds_AU9999 = 现价,?,买,卖,最高,最低,时间,昨收,今开,...
            string text = Once(delegate
            {
                byte[] raw = HttpGetBytes("https://hq.sinajs.cn/list=gds_AU9999", "https://finance.sina.com.cn", 8000);
                return Encoding.GetEncoding("GB2312").GetString(raw);
            }, out last);

            if (text != null)
            {
                int q1 = text.IndexOf('"');
                int q2 = text.LastIndexOf('"');
                if (q1 > 0 && q2 > q1 + 1)
                {
                    string[] f = text.Substring(q1 + 1, q2 - q1 - 1).Split(',');
                    if (f.Length >= 9)
                    {
                        price = ParseD(f[0]);
                        high = ParseD(f[4]);
                        low = ParseD(f[5]);
                        open = ParseD(f[8]);
                        if (price > 0) return true;
                    }
                }
                err = "新浪上金所数据格式异常";
            }
            else err = Short(last);

            // 备：东方财富
            for (int i = 0; i < SgeHosts.Length; i++)
            {
                string host = SgeHosts[i];
                string url = "https://" + host + "/api/qt/stock/get?secid=118.AU9999&fields=f43,f44,f45,f46,f57,f58,f60";
                string body = Once(delegate { return HttpGet(url, "https://quote.eastmoney.com/", 8000); }, out last);
                if (body == null) continue;
                try
                {
                    Dictionary<string, object> root = Json.Obj(Json.Parse(body));
                    Dictionary<string, object> data = Json.ObjAt(root, "data");
                    if (data == null) continue;
                    price = Json.Dbl(data, "f43", 0) / 100.0;
                    high = Json.Dbl(data, "f44", 0) / 100.0;
                    low = Json.Dbl(data, "f45", 0) / 100.0;
                    open = Json.Dbl(data, "f46", 0) / 100.0;
                    if (price > 0) return true;
                }
                catch (Exception ex) { err = "上金所解析失败: " + ex.Message; }
            }

            if (err == null) err = Short(last);
            return false;
        }

        /// <summary>上金所 Au99.99 当日日盘开盘价（09:00 之后第一根 1 分钟线的开盘价）。
        /// 东方财富 1 分钟 K 线；每天只需取一次。</summary>
        public static bool FetchSgeDayOpen(out double open, out string hhmm, out string err)
        {
            open = 0;
            hhmm = "";
            err = null;
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            const string url = "https://push2his.eastmoney.com/api/qt/stock/kline/get?secid=118.AU9999"
                + "&fields1=f1,f2,f3,f4,f5&fields2=f51,f52,f53,f54,f55,f56&klt=1&fqt=0&end=20500101&lmt=900";

            Exception last = null;
            string body = Once(delegate { return HttpGet(url, "https://quote.eastmoney.com/", 9000); }, out last);
            if (body == null) { err = Short(last); return false; }

            try
            {
                Dictionary<string, object> root = Json.Obj(Json.Parse(body));
                Dictionary<string, object> data = Json.ObjAt(root, "data");
                if (data == null) { err = "K线接口无数据"; return false; }
                List<object> ks = Json.Arr(Json.Get(data, "klines"));
                if (ks == null || ks.Count == 0) { err = "K线为空"; return false; }

                string prefix = today + " 09:";
                for (int i = 0; i < ks.Count; i++)
                {
                    string row = ks[i] as string;
                    if (row == null || row.Length < 18 || !row.StartsWith(prefix)) continue;
                    string[] f = row.Split(',');
                    if (f.Length < 2) continue;
                    double o = ParseD(f[1]);
                    if (o <= 0) continue;
                    open = o;
                    hhmm = f[0].Substring(f[0].Length - 5);
                    return true;
                }
                err = "今日尚无日盘开盘记录";
                return false;
            }
            catch (Exception ex)
            {
                err = "开盘价解析失败: " + ex.Message;
                return false;
            }
        }

        static double ParseD(string s)        {
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return 0;
        }

        /// <summary>浙商积存金：官方接口不可得时，按上金所 Au99.99 + 点差推算。</summary>
        public static Quote DeriveZs(double sgePrice, double sgeHigh, double sgeLow, double spread)
        {
            Quote q = new Quote();
            q.Key = "zs";
            q.Name = "浙商积存金";
            q.Price = Math.Round(sgePrice + spread, 2);
            q.High = sgeHigh > 0 ? Math.Round(sgeHigh + spread, 2) : 0;
            q.Low = sgeLow > 0 ? Math.Round(sgeLow + spread, 2) : 0;
            q.Official = false;
            q.Alive = true;
            q.Stamp = DateTime.Now;
            q.Note = "推算 · 上金所+点差";
            return q;
        }

        public static string Short(Exception ex)
        {
            if (ex == null) return "未知错误";
            WebException we = ex as WebException;
            if (we != null && we.Status == WebExceptionStatus.Timeout) return "网络超时";
            string m = ex.Message;
            if (m != null && m.Length > 60) m = m.Substring(0, 60) + "…";
            return m;
        }
    }
}
