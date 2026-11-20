using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GoldDock
{
    static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        /// <summary>未处理异常落盘，方便排查（写到配置文件旁边）。</summary>
        static void LogCrash(Exception ex)
        {
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(Config.FilePath), "error.log");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + ex + "\r\n\r\n");
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs te) { LogCrash(te.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs ue) { LogCrash(ue.ExceptionObject as Exception); };

            string snap = ArgValue(args, "--snapshot");
            string self = ArgValue(args, "--selftest");

            if (self != null) { SelfTest(self); return; }

            CardForm form = new CardForm();

            if (snap != null)
            {
                form.LoadDataOnce();
                if (ArgValue(args, "--demo") != null) form.SeedDemo();
                form.OpaqueRender = true;
                using (Bitmap bmp = form.RenderToBitmap())
                    bmp.Save(snap, ImageFormat.Png);
                form.Close();
                return;
            }

            Application.Run(form);
        }

        static string ArgValue(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) return args[i + 1];
                    return Path.Combine(Path.GetTempPath(), "golddock_" + name.TrimStart('-') + ".txt");
                }
            }
            return null;
        }

        static void SelfTest(string outPath)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            try
            {
                string err;
                Quote q = Sources.FetchIcbc(out err);
                sb.AppendLine("ICBC alive=" + (q != null));
                if (q != null)
                    sb.AppendLine(string.Format("ICBC price={0} high={1} low={2} reg={3} stamp={4} note={5}",
                        q.Price, q.High, q.Low, q.Reg, q.Stamp.ToString("yyyy-MM-dd HH:mm:ss"), q.Note));
                else
                    sb.AppendLine("ICBC err=" + err);

                double p, o, h, l; string se;
                bool ok = Sources.FetchSge(out p, out o, out h, out l, out se);
                sb.AppendLine("SGE alive=" + ok + (ok ? string.Format(" price={0} open={1} high={2} low={3}", p, o, h, l) : " err=" + se));
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION " + ex);
            }
            try { File.WriteAllText(outPath, sb.ToString(), System.Text.Encoding.UTF8); } catch { }
        }
    }

    public class CardForm : Form
    {
        float S = 1f;
        const float BaseW = 324f;
        const float BaseH = 208f;
        const float BaseMiniH = 124f;

        Config cfg = Config.Load();
        Tick tick = new Tick();
        readonly List<double> icbcHist = new List<double>();
        readonly List<double> zsHist = new List<double>();
        readonly List<double> spreadSamples = new List<double>();
        double effSpread = 4.50;   // 实际用于推算浙商价的上金所点差

        System.Windows.Forms.Timer timer;
        int countdown;
        volatile bool fetching;
        int flashTicks;
        DateTime lastAlert = DateTime.MinValue;
        DateTime lastOpenTry = DateTime.MinValue;
        string lastErr = "";

        NotifyIcon tray;
        ContextMenuStrip menu;
        ToolStripMenuItem miTop, miMini, miAuto, miAlert, miTrans;

        Point dragOffset;
        bool dragging;
        bool moved;

        Font fHead, fName, fPrice, fPriceMini, fChg, fSub, fTime, fFoot;

        // 分层窗口（逐像素透明）
        Bitmap layer;
        bool layerReady;
        bool pushing;
        bool inPaint;
        public bool OpaqueRender;      // 仅截图时用：强制不透明
        bool modalOpen;                // 有模态对话框时不响应 ESC 退出

        public CardForm()
        {
            using (Graphics g = CreateGraphics()) S = g.DpiX / 96f;
            Text = "GoldDock";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = cfg.TopMost;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Color.FromArgb(15, 18, 25);

            BuildFonts();
            ApplySize();
            ApplyRegion();

            int w = (int)(BaseW * S), h = (int)((cfg.Mini ? BaseMiniH : BaseH) * S);
            if (cfg.X < 0 || cfg.Y < 0)
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int x = wa.Right - w - (int)(24 * S);
                int y = wa.Top + (int)(80 * S);
                if (cfg.Mini) y = wa.Top + (int)(24 * S);
                Location = new Point(Math.Max(wa.Left, x), Math.Max(wa.Top, y));
            }
            else
            {
                // 防止上次的位置落到屏幕外（换显示器/改分辨率后）
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int x = Math.Min(Math.Max(cfg.X, wa.Left), wa.Right - w);
                int y = Math.Min(Math.Max(cfg.Y, wa.Top), wa.Bottom - h);
                Location = new Point(x, y);
            }

            BuildMenu();
            BuildTray();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate { OnSecond(); };
            timer.Start();

            countdown = 0;
        }

        void BuildFonts()
        {
            fHead = new Font("Microsoft YaHei UI", 10.5f * S, FontStyle.Regular, GraphicsUnit.Pixel);
            fName = new Font("Microsoft YaHei UI", 12.5f * S, FontStyle.Regular, GraphicsUnit.Pixel);
            fPrice = new Font("Consolas", 27f * S, FontStyle.Bold, GraphicsUnit.Pixel);
            fPriceMini = new Font("Consolas", 20f * S, FontStyle.Bold, GraphicsUnit.Pixel);
            fChg = new Font("Microsoft YaHei UI", 11.5f * S, FontStyle.Bold, GraphicsUnit.Pixel);
            fSub = new Font("Microsoft YaHei UI", 9.5f * S, FontStyle.Regular, GraphicsUnit.Pixel);
            fTime = new Font("Consolas", 10f * S, FontStyle.Regular, GraphicsUnit.Pixel);
            fFoot = new Font("Microsoft YaHei UI", 9f * S, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        void ApplySize()
        {
            float h = cfg.Mini ? BaseMiniH : BaseH;
            ClientSize = new Size((int)Math.Round(BaseW * S), (int)Math.Round(h * S));
            TopMost = cfg.TopMost;
            ApplyRegion();
        }

        void ApplyRegion()
        {
            // 逐像素透明已经能把圆角画出来，不再用 Region 裁（Region 会把圆角切成硬锯齿）
        }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            if (rad < 0.5f) { p.AddRectangle(r); return p; }
            float d = rad * 2f;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ------------------------------------------------------------------
        // 数据
        // ------------------------------------------------------------------
        void OnSecond()
        {
            if (flashTicks > 0) { flashTicks--; Repaint(); }

            if (fetching) return;
            if (countdown > 0) { countdown--; if (countdown == 0) StartFetch(); return; }
            StartFetch();
        }

        public void StartFetch()
        {
            if (fetching) return;
            fetching = true;
            Thread t = new Thread(delegate ()
            {
                Tick result = FetchOnce();
                try
                {
                    if (IsHandleCreated) BeginInvoke(new MethodInvoker(delegate { Apply(result); }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        Tick FetchOnce()
        {
            Tick t = new Tick();
            t.FetchedAt = DateTime.Now;

            string err;
            Quote q = null;
            try { q = Sources.FetchIcbc(out err); }
            catch (Exception ex) { err = Sources.Short(ex); q = null; }

            if (q != null) t.Icbc = q;
            else
            {
                t.Icbc.Key = "icbc";
                t.Icbc.Name = "工银积存金";
                t.Icbc.Alive = false;
                t.Icbc.Note = err;
            }

            double p = 0, o = 0, h = 0, l = 0; string se = "";
            bool ok = false;
            try { ok = Sources.FetchSge(out p, out o, out h, out l, out se); }
            catch (Exception ex) { se = Sources.Short(ex); }
            t.SgeOk = ok;
            t.SgePrice = ok ? p : 0;
            t.SgeHigh = ok ? h : 0;
            t.SgeLow = ok ? l : 0;
            t.SgeOpen = ok ? o : 0;
            t.SgeErr = ok ? "" : se;

            // 当日日盘开盘价（涨跌基准）：每天取一次，失败 10 分钟后重试
            string today = Today();
            bool needOpen = (cfg.SgeOpenDate != today || cfg.SgeOpenPrice <= 0);
            if (needOpen && (DateTime.Now - lastOpenTry).TotalSeconds > 600)
            {
                lastOpenTry = DateTime.Now;
                double oo; string ot; string oe;
                bool ook = Sources.FetchSgeDayOpen(out oo, out ot, out oe);
                t.SgeDayOpenOk = ook;
                t.SgeDayOpen = ook ? oo : 0;
                t.SgeDayOpenTime = ook ? ot : "";
                t.SgeDayOpenErr = ook ? "" : oe;
            }

            if (ok)
                t.Zs = Sources.DeriveZs(p, h, l, effSpread);
            else
            {
                t.Zs.Key = "zs";
                t.Zs.Name = "浙商积存金";
                t.Zs.Alive = false;
                t.Zs.Note = se;
            }
            return t;
        }

        public void LoadDataOnce()
        {
            Tick t = FetchOnce();
            ApplyDirect(t);
        }

        // 仅用于截图自检：合成一段当日走势，避免空图
        public void SeedDemo()
        {
            Random rnd = new Random(42);
            if (tick.Icbc.Alive)
            {
                double end = tick.Icbc.Price;
                double start = Math.Round(end * 1.0131, 2);
                icbcHist.Clear();
                double v = start;
                for (int i = 0; i < 48; i++)
                {
                    v += (end - start) / 47.0 + (rnd.NextDouble() - 0.5) * 0.55;
                    icbcHist.Add(i == 47 ? end : Math.Round(v, 2));
                }
                cfg.IcbcBasePrice = start;
                cfg.IcbcBaseTime = "09:12";
                cfg.IcbcBaseDate = tick.Icbc.Stamp.ToString("yyyy-MM-dd");
            }
            if (tick.Zs.Alive)
            {
                double end = tick.Zs.Price;
                double start = Math.Round(end * 1.0128, 2);
                zsHist.Clear();
                double v = start;
                for (int i = 0; i < 48; i++)
                {
                    v += (end - start) / 47.0 + (rnd.NextDouble() - 0.5) * 0.55;
                    zsHist.Add(i == 47 ? end : Math.Round(v, 2));
                }
                cfg.ZsBasePrice = start;
                cfg.ZsBaseTime = "09:12";
                cfg.ZsBaseDate = tick.Zs.Stamp.ToString("yyyy-MM-dd");
            }
        }

        void ApplyDirect(Tick t)
        {
            ApplyCore(t);
            Repaint();
        }

        void Apply(Tick t)
        {
            ApplyCore(t);
            Repaint();
        }

        void ApplyCore(Tick t)
        {
            fetching = false;
            tick = t;
            lastErr = "";

            if (t.SgeDayOpenOk && t.SgeDayOpen > 0)
            {
                cfg.SgeOpenDate = Today();
                cfg.SgeOpenPrice = t.SgeDayOpen;
                cfg.SgeOpenTime = t.SgeDayOpenTime;
            }

            if (t.Icbc.Alive)
            {
                Push(icbcHist, t.Icbc.Price);
                UpdateBase(ref cfg.IcbcBaseDate, ref cfg.IcbcBasePrice, ref cfg.IcbcBaseTime, t.Icbc);
            }
            else lastErr = "工行: " + (t.Icbc.Note ?? "取价失败");

            if (t.Icbc.Alive && t.SgeOk && t.SgePrice > 0)
            {
                double gap = t.Icbc.Price - t.SgePrice;
                if (gap > -20 && gap < 40) spreadSamples.Add(gap);
                while (spreadSamples.Count > 40) spreadSamples.RemoveAt(0);
            }
            effSpread = (cfg.Spread > 0) ? cfg.Spread
                : (spreadSamples.Count > 0 ? Math.Round(Median(spreadSamples), 2) : effSpread);

            if (t.SgeOk && t.Zs.Alive)
                t.Zs = Sources.DeriveZs(t.SgePrice, t.SgeHigh, t.SgeLow, effSpread);

            if (t.Zs.Alive)
            {
                Push(zsHist, t.Zs.Price);
                UpdateBase(ref cfg.ZsBaseDate, ref cfg.ZsBasePrice, ref cfg.ZsBaseTime, t.Zs);
            }
            else if (lastErr.Length == 0) lastErr = "上金所: " + (t.Zs.Note ?? "取价失败");

            cfg.Save();
            countdown = cfg.IntervalSec;

            if (cfg.AlertPct > 0)
            {
                double pct1 = Pct(t.Icbc);
                double pct2 = Pct(t.Zs);
                double worst = Math.Min(pct1, pct2);
                if (worst <= -cfg.AlertPct && (DateTime.Now - lastAlert).TotalSeconds > 60)
                {
                    lastAlert = DateTime.Now;
                    flashTicks = 8;
                    if (tray != null)
                        tray.ShowBalloonTip(4000, "积存金跌幅提醒",
                            string.Format("工银 {0:0.00}（{1:0.00}%）  浙商 {2:0.00}（{3:0.00}%）",
                                t.Icbc.Price, pct1, t.Zs.Price, pct2),
                            ToolTipIcon.Warning);
                }
            }
        }

        static double Median(List<double> xs)
        {
            double[] a = xs.ToArray();
            Array.Sort(a);
            int n = a.Length;
            return (n % 2 == 1) ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2.0;
        }

        static void Push(List<double> list, double v)
        {
            if (list.Count > 0 && Math.Abs(list[list.Count - 1] - v) < 0.0001) return;
            list.Add(v);
            if (list.Count > 60) list.RemoveAt(0);
        }

        void UpdateBase(ref string date, ref double price, ref string time, Quote q)
        {
            string d = q.Stamp.ToString("yyyy-MM-dd");
            if (date != d || price <= 0)
            {
                date = d;
                price = q.Price;
                time = q.Stamp.ToString("HH:mm");
            }
        }

        static string Today() { return DateTime.Now.ToString("yyyy-MM-dd"); }

        /// <summary>当日开盘价是否可用（否则退回“当天首次取价”基准）。</summary>
        bool HasDayOpen { get { return cfg.SgeOpenPrice > 0 && cfg.SgeOpenDate == Today(); } }

        /// <summary>涨跌基准：优先当日开盘价（上金所日盘开盘 + 点差，并夹逼在当日高/低之间）。</summary>
        double BaseOf(Quote q)
        {
            if (cfg.ManualBaseDate == Today())
            {
                double m = (q.Key == "icbc") ? cfg.IcbcBasePrice : cfg.ZsBasePrice;
                if (m > 0) return m;
            }
            if (HasDayOpen)
            {
                double o = cfg.SgeOpenPrice + effSpread;
                // 开盘价不可能高于当日最高、也不可能低于当日最低
                if (q.High > 0 && o > q.High) o = q.High;
                if (q.Low > 0 && o < q.Low) o = q.Low;
                if (o > 0) return o;
            }
            return (q.Key == "icbc") ? cfg.IcbcBasePrice : cfg.ZsBasePrice;
        }

        string BaseLabel()
        {
            if (cfg.ManualBaseDate != Today() && HasDayOpen)
                return "开盘 " + cfg.SgeOpenPrice.ToString("0.00");
            string bt = !string.IsNullOrEmpty(cfg.IcbcBaseTime) ? cfg.IcbcBaseTime : cfg.ZsBaseTime;
            return "基准 " + (string.IsNullOrEmpty(bt) ? "--:--" : bt);
        }

        double Pct(Quote q)
        {
            double b = BaseOf(q);
            if (b <= 0 || q.Price <= 0) return 0;
            return (q.Price - b) / b * 100.0;
        }

        // ------------------------------------------------------------------
        // 绘制
        // ------------------------------------------------------------------
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED;
                return cp;
            }
        }

        // ------------------------------------------------------------------
        // 颜色（全部来自配置，可随时改）
        // ------------------------------------------------------------------
        Color BgColor { get { return Native.Hex(cfg.BgColor, Color.FromArgb(20, 25, 36)); } }
        Color FgColor { get { return Native.Hex(cfg.FgColor, Color.FromArgb(230, 236, 245)); } }
        Color UpColor { get { return Native.Hex(cfg.UpColor, Color.FromArgb(240, 82, 90)); } }
        Color DownColor { get { return Native.Hex(cfg.DownColor, Color.FromArgb(38, 194, 124)); } }

        /// <summary>背景实际 alpha：0% 时仍留 1，保证整块区域能被鼠标抓到。</summary>
        int BgAlpha
        {
            get
            {
                if (OpaqueRender) return 255;
                int a = (int)Math.Round(255.0 * cfg.BgAlpha / 100.0);
                return a < 1 ? 1 : a;
            }
        }

        /// <summary>派生文字色；全透明背景下适当加浓，免得看不清。</summary>
        Color T(int alpha)
        {
            if (cfg.BgAlpha <= 25) alpha = Math.Min(235, (int)Math.Round(alpha * 1.8));
            return Native.Alpha(FgColor, alpha);
        }

        Color PriceColor(double pct)
        {
            if (pct > 0.0001) return UpColor;
            if (pct < -0.0001) return DownColor;
            return T(150);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (inPaint) return;
            inPaint = true;
            try { RenderLayer(); PushLayer(); }
            finally { inPaint = false; }
        }

        void Repaint()
        {
            if (!IsHandleCreated) return;

            RenderLayer();
            PushLayer();
        }

        static void PrepareG(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        /// <summary>字体是否建好了 —— 窗口在构造期就会触发 OnResize，必须挡在渲染之前。</summary>
        bool Ready
        {
            get
            {
                return fHead != null && fName != null && fPrice != null && fPriceMini != null
                    && fChg != null && fSub != null && fTime != null && fFoot != null;
            }
        }

        void RenderLayer()
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            if (!Ready || W <= 0 || H <= 0) return;
            if (layer == null || layer.Width != W || layer.Height != H)
            {
                if (layer != null) layer.Dispose();
                layer = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            }
            using (Graphics g = Graphics.FromImage(layer))
            {
                g.Clear(Color.Transparent);
                PrepareG(g);
                Render(g);
            }
            if (cfg.BgAlpha <= 40 && !OpaqueRender) AddTextShadow(layer);
            layerReady = true;
        }

        /// <summary>背景很透时给所有内容加一层 1px 深色阴影，保证在浅色桌面上也看得清。</summary>
        static void AddTextShadow(Bitmap bmp)
        {
            int W = bmp.Width, H = bmp.Height;
            using (Bitmap sil = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            {
                using (Graphics gs = Graphics.FromImage(sil))
                {
                    ColorMatrix cm = new ColorMatrix(new float[][]
                    {
                        new float[] { 0, 0, 0, 0, 0 },
                        new float[] { 0, 0, 0, 0, 0 },
                        new float[] { 0, 0, 0, 0, 0 },
                        new float[] { 0, 0, 0, 0.72f, 0 },
                        new float[] { 0, 0, 0, 0, 1 }
                    });
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(cm);
                        gs.DrawImage(bmp, new Rectangle(0, 0, W, H), 0, 0, W, H, GraphicsUnit.Pixel, ia);
                    }
                }
                using (Bitmap copy = (Bitmap)bmp.Clone())
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    // 四向描边，浅色/深色桌面上都能看清
                    g.DrawImageUnscaled(sil, -1, 0);
                    g.DrawImageUnscaled(sil, 1, 0);
                    g.DrawImageUnscaled(sil, 0, -1);
                    g.DrawImageUnscaled(sil, 0, 1);
                    g.DrawImageUnscaled(copy, 0, 0);
                }
            }
        }

        void PushLayer()
        {
            if (!layerReady || layer == null || !IsHandleCreated || pushing || !Ready) return;
            pushing = true;
            try { Native.PushBitmap(Handle, layer, Left, Top); }
            finally { pushing = false; }
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            PushLayer();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (IsHandleCreated) { RenderLayer(); PushLayer(); }
        }

        public Bitmap RenderToBitmap()
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                PrepareG(g);
                Render(g);
            }
            return b;
        }

        void Render(Graphics g)
        {
            if (!Ready) return;
            int W = ClientSize.Width, H = ClientSize.Height;
            int a = BgAlpha;
            using (GraphicsPath path = Round(new RectangleF(0, 0, W, H), 14f * S))
            {
                g.SetClip(path);
                RectangleF full = new RectangleF(0, 0, W, H);
                using (LinearGradientBrush br = new LinearGradientBrush(full,
                    Native.Alpha(Native.Mix(BgColor, Color.White, 0.09), a),
                    Native.Alpha(BgColor, a), 90f))
                    g.FillRectangle(br, full);
                g.ResetClip();
                Color bc = flashTicks > 0 ? Color.FromArgb(255, 176, 32) : Native.Alpha(FgColor, 30);
                Pen border = new Pen(bc, 1f * S);
                border.Alignment = PenAlignment.Inset;
                g.DrawPath(border, path);
                border.Dispose();
            }

            float px = 16f * S;
            float right = W - 16f * S;

            if (!cfg.Mini)
            {
                // 顶部
                Color dotColor = fetching ? Color.FromArgb(216, 172, 85)
                    : (tick.AnyAlive ? Color.FromArgb(46, 190, 120) : Color.FromArgb(255, 90, 90));
                float cy = 20f * S;
                using (SolidBrush b = new SolidBrush(dotColor))
                    g.FillEllipse(b, px - 2f * S, cy - 3.2f * S, 6.4f * S, 6.4f * S);
                using (SolidBrush b = new SolidBrush(T(165)))
                    g.DrawString("黄金积存金", fHead, b, px + 12f * S, 12.5f * S);

                string tstr = tick.FetchedAt == DateTime.MinValue ? "--:--:--" : tick.FetchedAt.ToString("HH:mm:ss");
                using (SolidBrush b = new SolidBrush(T(110)))
                {
                    SizeF sz = g.MeasureString(tstr, fTime);
                    g.DrawString(tstr, fTime, b, right - sz.Width, 13f * S);
                }
            }

            float b1 = cfg.Mini ? 14f * S : 34f * S;
            float b2 = cfg.Mini ? 66f * S : 112f * S;

            if (!cfg.Mini)
            {
                using (Pen p = new Pen(T(30), 1f * S))
                    g.DrawLine(p, px, 106f * S, right, 106f * S);
            }

            DrawBlock(g, b1, tick.Icbc, icbcHist, BaseOf(tick.Icbc), "");
            DrawBlock(g, b2, tick.Zs, zsHist, BaseOf(tick.Zs), "");

            if (!cfg.Mini)
            {
                string left = BaseLabel() + "  |  ";
                left += tick.SgeOk ? "上金所 " + tick.SgePrice.ToString("0.00") : "上金所未连接";
                Color lc = tick.SgeOk ? T(100) : Color.FromArgb(a, 220, 110, 110);
                using (SolidBrush b = new SolidBrush(lc))
                    g.DrawString(left, fFoot, b, px, H - 18f * S);
                using (SolidBrush b = new SolidBrush(T(100)))
                {
                    string r = cfg.IntervalSec + "s";
                    SizeF sz = g.MeasureString(r, fFoot);
                    g.DrawString(r, fFoot, b, right - sz.Width, H - 18f * S);
                }
            }
        }

        void DrawBlock(Graphics g, float y0, Quote q, List<double> hist, double basePrice, string baseTime)
        {
            float px = 16f * S;
            float right = ClientSize.Width - 16f * S;

            double pct = 0;
            if (basePrice > 0 && q.Price > 0) pct = (q.Price - basePrice) / basePrice * 100.0;

            Color main = q.Alive ? PriceColor(pct) : T(110);

            string arrow = pct > 0.0001 ? "▲" : (pct < -0.0001 ? "▼" : "•");
            string chg = "--";
            if (q.Alive && basePrice > 0)
                chg = string.Format("{0} {1:+0.00;-0.00;0.00}  {2:+0.00;-0.00;0.00}%",
                    arrow, q.Price - basePrice, pct);

            if (cfg.Mini)
            {
                using (SolidBrush b = new SolidBrush(T(160)))
                    g.DrawString(q.Name, fSub, b, px, y0 - 2f * S);

                string mps = q.Alive ? q.Price.ToString("0.00") : "--.--";
                using (SolidBrush b = new SolidBrush(main))
                    g.DrawString(mps, fPriceMini, b, px - 1f * S, y0 + 10f * S);

                using (SolidBrush b = new SolidBrush(main))
                {
                    SizeF sz = g.MeasureString(chg, fChg);
                    g.DrawString(chg, fChg, b, right - sz.Width, y0 + 14f * S);
                }
                return;
            }

            // 名称
            using (SolidBrush b = new SolidBrush(T(170)))
                g.DrawString(q.Name, fName, b, px, y0 - 4f * S);

            // 来源角标
            string badge = !q.Alive ? "离线" : (q.Official ? "实时" : "推算");
            Color bc = !q.Alive ? Color.FromArgb(210, 100, 100)
                : (q.Official ? Color.FromArgb(216, 172, 85) : T(120));
            using (SolidBrush b = new SolidBrush(bc))
            {
                SizeF sz = g.MeasureString(badge, fSub);
                g.DrawString(badge, fSub, b, right - sz.Width, y0 - 2f * S);
            }

            // 价格
            string ps = q.Alive ? q.Price.ToString("0.00") : "--.--";
            using (SolidBrush b = new SolidBrush(main))
                g.DrawString(ps, fPrice, b, px - 2f * S, y0 + 18f * S);

            // 涨跌
            using (SolidBrush b = new SolidBrush(main))
                g.DrawString(chg, fChg, b, px, y0 + 52f * S);

            // 高/低（官方报价）或 点差（推算）
            if (!cfg.Mini && q.Alive)
            {
                string hl = (q.Official && q.High > 0 && q.Low > 0)
                    ? string.Format("高 {0:0.00}  低 {1:0.00}", q.High, q.Low)
                    : string.Format("点差 {0:0.00}{1}", effSpread, cfg.Spread > 0 ? "" : " 自动");
                using (SolidBrush b = new SolidBrush(T(105)))
                {
                    SizeF sz = g.MeasureString(hl, fSub);
                    g.DrawString(hl, fSub, b, right - sz.Width, y0 + 53f * S);
                }
            }

            // 迷你走势
            if (!cfg.Mini && hist.Count > 1)
            {
                float w = 92f * S, h = 33f * S;
                RectangleF r = new RectangleF(right - w, y0 + 17f * S, w, h);
                DrawSpark(g, r, hist, main);
            }
        }

        void DrawSpark(Graphics g, RectangleF r, List<double> data, Color color)
        {
            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < data.Count; i++)
            {
                if (data[i] < min) min = data[i];
                if (data[i] > max) max = data[i];
            }
            if (max - min < 0.005) { max = min + 0.01; }
            // 波动太小时不要把噪声放大成"山峰"：给 y 轴一个最小跨度
            double floor = Math.Max(0.5, min * 0.0007);
            if (max - min < floor)
            {
                double mid = (max + min) / 2.0;
                min = mid - floor / 2.0;
                max = mid + floor / 2.0;
            }
            float pad = 2f * S;
            float usableH = r.Height - pad * 2f;

            PointF[] pts = new PointF[data.Count];
            for (int i = 0; i < data.Count; i++)
            {
                float x = r.X + (data.Count == 1 ? r.Width : (r.Width * i / (float)(data.Count - 1)));
                float y = r.Y + pad + (float)((max - data[i]) / (max - min)) * usableH;
                pts[i] = new PointF(x, y);
            }

            using (GraphicsPath area = new GraphicsPath())
            {
                area.AddLines(pts);
                area.AddLine(pts[pts.Length - 1].X, r.Bottom, r.X, r.Bottom);
                area.CloseFigure();
                using (LinearGradientBrush br = new LinearGradientBrush(
                    new RectangleF(r.X, r.Y, r.Width, r.Height),
                    Color.FromArgb(46, color), Color.FromArgb(0, color), 90f))
                    g.FillPath(br, area);
            }

            using (Pen p = new Pen(Color.FromArgb(200, color), 1.4f * S))
            {
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, pts);
            }

            using (SolidBrush b = new SolidBrush(color))
            {
                PointF last = pts[pts.Length - 1];
                g.FillEllipse(b, last.X - 1.8f * S, last.Y - 1.8f * S, 3.6f * S, 3.6f * S);
            }
        }

        // ------------------------------------------------------------------
        // 交互
        // ------------------------------------------------------------------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                try { Activate(); } catch { }
                dragging = true;
                moved = false;
                dragOffset = e.Location;
                try { Capture = true; } catch { }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            Point p = PointToScreen(e.Location);
            Point np = new Point(p.X - dragOffset.X, p.Y - dragOffset.Y);
            if (np != Location)
            {
                Location = np;
                PushLayer();
            }
            moved = true;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            try { Capture = false; } catch { }
            if (dragging && moved)
            {
                cfg.X = Location.X;
                cfg.Y = Location.Y;
                cfg.Save();
            }
            dragging = false;
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            ToggleMini();
        }

        void ToggleMini()
        {
            Point loc = Location;
            cfg.Mini = !cfg.Mini;
            ApplySize();
            Location = loc;
            ApplyRegion();
            cfg.Save();
            if (miMini != null) miMini.Checked = cfg.Mini;
            Repaint();
        }

        // ------------------------------------------------------------------
        // 菜单 / 托盘
        // ------------------------------------------------------------------
        void BuildMenu()
        {
            menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

            miTop = new ToolStripMenuItem("窗口置顶", null, delegate { ToggleTop(); });
            miTop.Checked = cfg.TopMost;
            menu.Items.Add(miTop);

            miMini = new ToolStripMenuItem("紧凑模式", null, delegate { ToggleMini(); });
            miMini.Checked = cfg.Mini;
            menu.Items.Add(miMini);

            ToolStripMenuItem miInterval = new ToolStripMenuItem("刷新间隔");
            int[] secs = new int[] { 3, 5, 10, 15, 30, 60 };
            for (int i = 0; i < secs.Length; i++)
            {
                int s = secs[i];
                ToolStripMenuItem it = new ToolStripMenuItem(s + " 秒", null, delegate
                {
                    cfg.IntervalSec = s;
                    countdown = 1;
                    cfg.Save();
                    Repaint();
                });
                it.Checked = (cfg.IntervalSec == s);
                miInterval.DropDownItems.Add(it);
            }
            menu.Items.Add(miInterval);

            miAlert = new ToolStripMenuItem("跌幅提醒…", null, delegate { EditAlert(); });
            menu.Items.Add(miAlert);

            ToolStripMenuItem miSpread = new ToolStripMenuItem("浙商点差…", null, delegate { EditSpread(); });
            menu.Items.Add(miSpread);

            menu.Items.Add(new ToolStripMenuItem("外观与颜色…", null, delegate { EditAppearance(); }));

            miTrans = new ToolStripMenuItem("背景全透明", null, delegate { ToggleTransparent(); });
            miTrans.Checked = cfg.BgAlpha <= 0;
            menu.Items.Add(miTrans);

            menu.Items.Add(new ToolStripSeparator());

            miAuto = new ToolStripMenuItem("开机自动启动", null, delegate { ToggleAuto(); });
            miAuto.Checked = IsAutoStart();
            menu.Items.Add(miAuto);

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(new ToolStripMenuItem("立即刷新", null, delegate { countdown = 1; StartFetch(); }));
            menu.Items.Add(new ToolStripMenuItem("以当前价重置今日基准", null, delegate { ResetBase(); }));
            menu.Items.Add(new ToolStripMenuItem("改用当日开盘价基准", null, delegate { UseDayOpen(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出（或按 ESC）", null, delegate { ExitApp(); }));

            ContextMenuStrip = menu;
        }

        void ToggleTop()
        {
            cfg.TopMost = !cfg.TopMost;
            TopMost = cfg.TopMost;
            miTop.Checked = cfg.TopMost;
            cfg.Save();
        }

        void EditSpread()
        {
            string v = Prompt("浙商积存金点差", "浙商价 = 上金所 Au99.99 + 点差（元/克）；填 0 = 自动跟随工行", cfg.Spread.ToString("0.00"));
            if (v == null) return;
            double d;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            {
                cfg.Spread = d;
                cfg.Save();
                countdown = 1;
            }
        }

        void EditAlert()
        {
            string v = Prompt("跌幅提醒阈值", "当日跌幅超过该百分比时闪烁并弹提示（0 = 关闭）", cfg.AlertPct.ToString("0.0"));
            if (v == null) return;
            double d;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            {
                cfg.AlertPct = Math.Max(0, d);
                cfg.Save();
            }
        }

        // ------------------------------------------------------------------
        // 外观与颜色
        // ------------------------------------------------------------------
        static void StyleSwatch(Button b, string cap, Color c)
        {
            if (b == null) return;
            b.BackColor = c;
            b.ForeColor = Native.IsLight(c) ? Color.Black : Color.White;
            b.Text = cap + "  " + Native.Hex(c);
        }

        void EditAppearance()
        {
            Color bg = BgColor, fg = FgColor, up = UpColor, dn = DownColor;
            int alpha = cfg.BgAlpha;

            using (Form dlg = new Form())
            {
                dlg.Text = "外观与颜色";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterScreen;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.TopMost = true;
                dlg.ClientSize = new Size(392, 300);
                dlg.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

                Label l0 = new Label();
                l0.Text = "预设";
                l0.Location = new Point(14, 14);
                l0.Size = new Size(52, 20);
                dlg.Controls.Add(l0);

                Button pDark = new Button();
                pDark.Text = "深色";
                pDark.Location = new Point(66, 10);
                pDark.Size = new Size(72, 28);
                dlg.Controls.Add(pDark);

                Button pLight = new Button();
                pLight.Text = "浅色";
                pLight.Location = new Point(146, 10);
                pLight.Size = new Size(72, 28);
                dlg.Controls.Add(pLight);

                Button pGold = new Button();
                pGold.Text = "金色";
                pGold.Location = new Point(226, 10);
                pGold.Size = new Size(72, 28);
                dlg.Controls.Add(pGold);

                Button pTrans = new Button();
                pTrans.Text = "全透明";
                pTrans.Location = new Point(306, 10);
                pTrans.Size = new Size(72, 28);
                dlg.Controls.Add(pTrans);

                Button sbBg = MakeSwatch(dlg, 66, 50, "背景色", delegate(Color c) { bg = c; });
                Button sbFg = MakeSwatch(dlg, 214, 50, "文字色", delegate(Color c) { fg = c; });
                Button sbUp = MakeSwatch(dlg, 66, 88, "上涨色", delegate(Color c) { up = c; });
                Button sbDn = MakeSwatch(dlg, 214, 88, "下跌色", delegate(Color c) { dn = c; });

                Label l1 = new Label();
                l1.Text = "背景不透明度";
                l1.Location = new Point(14, 132);
                l1.Size = new Size(84, 20);
                dlg.Controls.Add(l1);

                TrackBar tb = new TrackBar();
                tb.Minimum = 0;
                tb.Maximum = 100;
                tb.TickFrequency = 10;
                tb.SmallChange = 1;
                tb.LargeChange = 10;
                tb.Location = new Point(96, 126);
                tb.Size = new Size(212, 42);
                tb.Value = Math.Max(0, Math.Min(100, alpha));
                dlg.Controls.Add(tb);

                Label lv = new Label();
                lv.Location = new Point(312, 136);
                lv.Size = new Size(72, 20);
                lv.Text = tb.Value + "%";
                dlg.Controls.Add(lv);

                Label l2 = new Label();
                l2.Location = new Point(14, 176);
                l2.Size = new Size(368, 34);
                l2.ForeColor = Color.FromArgb(110, 120, 135);
                l2.Text = "0% = 完全透明，只留数字浮在桌面上（整块区域仍可用鼠标拖动）。";
                dlg.Controls.Add(l2);

                Action refresh = null;
                refresh = delegate
                {
                    StyleSwatch(sbBg, "背景色", bg);
                    StyleSwatch(sbFg, "文字色", fg);
                    StyleSwatch(sbUp, "上涨色", up);
                    StyleSwatch(sbDn, "下跌色", dn);
                    tb.Value = Math.Max(0, Math.Min(100, alpha));
                    lv.Text = alpha + "%";
                };

                pDark.Click += delegate { bg = Color.FromArgb(20, 25, 36); fg = Color.FromArgb(230, 236, 245); up = Color.FromArgb(240, 82, 90); dn = Color.FromArgb(38, 194, 124); alpha = 92; refresh(); };
                pLight.Click += delegate { bg = Color.FromArgb(244, 246, 250); fg = Color.FromArgb(27, 34, 48); up = Color.FromArgb(217, 58, 69); dn = Color.FromArgb(18, 160, 95); alpha = 96; refresh(); };
                pGold.Click += delegate { bg = Color.FromArgb(42, 33, 24); fg = Color.FromArgb(242, 227, 198); up = Color.FromArgb(232, 180, 90); dn = Color.FromArgb(111, 207, 155); alpha = 94; refresh(); };
                pTrans.Click += delegate { alpha = 0; refresh(); };

                tb.ValueChanged += delegate { alpha = tb.Value; lv.Text = alpha + "%"; };

                Button def = new Button();
                def.Text = "恢复默认";
                def.Location = new Point(14, 252);
                def.Size = new Size(88, 30);
                def.Click += delegate
                {
                    bg = Color.FromArgb(20, 25, 36); fg = Color.FromArgb(230, 236, 245);
                    up = Color.FromArgb(240, 82, 90); dn = Color.FromArgb(38, 194, 124);
                    alpha = 92; refresh();
                };
                dlg.Controls.Add(def);

                Button ok = new Button();
                ok.Text = "确定";
                ok.DialogResult = DialogResult.OK;
                ok.Location = new Point(220, 252);
                ok.Size = new Size(74, 30);
                dlg.Controls.Add(ok);

                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Location = new Point(302, 252);
                cancel.Size = new Size(74, 30);
                dlg.Controls.Add(cancel);

                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                refresh();

                modalOpen = true;
                DialogResult r;
                try { r = dlg.ShowDialog(this); }
                finally { modalOpen = false; }

                if (r == DialogResult.OK)
                {
                    cfg.BgColor = Native.Hex(bg);
                    cfg.FgColor = Native.Hex(fg);
                    cfg.UpColor = Native.Hex(up);
                    cfg.DownColor = Native.Hex(dn);
                    cfg.BgAlpha = Math.Max(0, Math.Min(100, alpha));
                    lastAlpha = cfg.BgAlpha > 0 ? cfg.BgAlpha : lastAlpha;
                    if (miTrans != null) miTrans.Checked = cfg.BgAlpha <= 0;
                    cfg.Save();
                    Repaint();
                }
            }
        }

        static Button MakeSwatch(Form host, int x, int y, string caption, Action<Color> set)
        {
            Button b = new Button();
            b.Location = new Point(x, y);
            b.Size = new Size(140, 30);
            b.FlatStyle = FlatStyle.Flat;
            b.Text = caption;
            host.Controls.Add(b);
            b.Click += delegate
            {
                using (ColorDialog cd = new ColorDialog())
                {
                    cd.FullOpen = true;
                    cd.Color = b.BackColor;
                    if (cd.ShowDialog(host) == DialogResult.OK)
                    {
                        set(cd.Color);
                        StyleSwatch(b, caption, cd.Color);
                    }
                }
            };
            return b;
        }

        void ResetBase()        {
            cfg.ManualBaseDate = Today();
            if (tick.Icbc.Alive)
            {
                cfg.IcbcBaseDate = tick.Icbc.Stamp.ToString("yyyy-MM-dd");
                cfg.IcbcBasePrice = tick.Icbc.Price;
                cfg.IcbcBaseTime = tick.Icbc.Stamp.ToString("HH:mm");
            }
            if (tick.Zs.Alive)
            {
                cfg.ZsBaseDate = tick.Zs.Stamp.ToString("yyyy-MM-dd");
                cfg.ZsBasePrice = tick.Zs.Price;
                cfg.ZsBaseTime = tick.Zs.Stamp.ToString("HH:mm");
            }
            cfg.Save();
            Repaint();
        }

        void UseDayOpen()
        {
            cfg.ManualBaseDate = "";
            cfg.Save();
            Repaint();
        }

        void ToggleTransparent()
        {
            if (cfg.BgAlpha > 0)
            {
                lastAlpha = cfg.BgAlpha;
                cfg.BgAlpha = 0;
            }
            else
            {
                cfg.BgAlpha = lastAlpha > 0 ? lastAlpha : 92;
            }
            if (miTrans != null) miTrans.Checked = cfg.BgAlpha <= 0;
            cfg.Save();
            Repaint();
        }

        int lastAlpha = 92;

        public void ExitApp()
        {
            try { Close(); }
            catch { try { Application.Exit(); } catch { } }
        }

        void ToggleAuto()
        {
            bool on = !IsAutoStart();
            SetAutoStart(on);
            miAuto.Checked = on;
        }

        static bool IsAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (k == null) return false;
                    return k.GetValue("GoldDock") != null;
                }
            }
            catch { return false; }
        }

        static void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("GoldDock", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("GoldDock", false);
                }
            }
            catch { }
        }

        void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            tray.Text = "黄金积存金";
            tray.Visible = true;
            tray.DoubleClick += delegate { Visible = !Visible; if (Visible) Activate(); };
            tray.ContextMenuStrip = menu;
        }

        static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(216, 172, 85)))
                        g.FillEllipse(b, 3, 3, 26, 26);
                    using (Pen p = new Pen(Color.FromArgb(120, 90, 30), 2f))
                        g.DrawEllipse(p, 3, 3, 26, 26);
                    using (Font f = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(40, 30, 10)))
                    {
                        SizeF sz = g.MeasureString("金", f);
                        g.DrawString("金", f, b, (32 - sz.Width) / 2f, (32 - sz.Height) / 2f);
                    }
                }
                IntPtr h = bmp.GetHicon();
                return Icon.FromHandle(h);
            }
        }

        string Prompt(string title, string label, string value)
        {
            using (Form dlg = new Form())            {
                dlg.Text = title;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.TopMost = true;              // 卡片本身置顶，对话框必须一起置顶才点得到
                dlg.ClientSize = new Size(340, 132);
                dlg.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

                Label lb = new Label();
                lb.Text = label;
                lb.Location = new Point(14, 14);
                lb.Size = new Size(312, 34);
                dlg.Controls.Add(lb);

                TextBox tb = new TextBox();
                tb.Text = value;
                tb.Location = new Point(14, 52);
                tb.Size = new Size(312, 24);
                dlg.Controls.Add(tb);

                Button ok = new Button();
                ok.Text = "确定";
                ok.DialogResult = DialogResult.OK;
                ok.Location = new Point(166, 92);
                ok.Size = new Size(74, 26);
                dlg.Controls.Add(ok);

                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Location = new Point(252, 92);
                cancel.Size = new Size(74, 26);
                dlg.Controls.Add(cancel);

                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                modalOpen = true;
                DialogResult r;
                try { r = dlg.ShowDialog(this); }
                finally { modalOpen = false; }
                return r == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Repaint();
            try
            {
                Native.InstallEscHook(
                    delegate { return ShouldEscExit(); },
                    delegate
                    {
                        try { BeginInvoke(new MethodInvoker(delegate { ExitApp(); })); }
                        catch { }
                    });
            }
            catch { }
        }

        /// <summary>ESC 只在“卡片是前台窗口”或“鼠标停在卡片上”时才算退出，避免抢全系统的 ESC。</summary>
        bool ShouldEscExit()
        {
            if (modalOpen) return false;
            // 右键菜单打开时，ESC 应该关菜单，而不是退出程序
            try { if (menu != null && menu.Visible) return false; } catch { }
            try { if (Native.GetForegroundWindow() == Handle) return true; } catch { }
            try { return Bounds.Contains(Cursor.Position); } catch { return false; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && !modalOpen)
            {
                ExitApp();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            cfg.X = Location.X;
            cfg.Y = Location.Y;
            cfg.Save();
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
            try { Native.RemoveEscHook(); } catch { }
            if (layer != null) { layer.Dispose(); layer = null; }
            fHead.Dispose(); fName.Dispose(); fPrice.Dispose(); fPriceMini.Dispose();
            fChg.Dispose(); fSub.Dispose(); fTime.Dispose(); fFoot.Dispose();
        }
    }
}
