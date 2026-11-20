using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GoldDock
{
    /// <summary>分层窗口（逐像素透明）+ 低级键盘钩子 + 颜色工具。</summary>
    static class Native
    {
        public const int WS_EX_LAYERED = 0x00080000;
        const int ULW_ALPHA = 2;
        const byte AC_SRC_ALPHA = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int CX, CY; public SIZE(int x, int y) { CX = x; CY = y; } }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        /// <summary>把一张 32bpp ARGB 位图贴到分层窗口的 (x,y)。</summary>
        public static bool PushBitmap(IntPtr hwnd, Bitmap bmp, int x, int y)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero, oldBmp = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                oldBmp = SelectObject(memDc, hBmp);
                SIZE size = new SIZE(bmp.Width, bmp.Height);
                POINT src = new POINT(0, 0);
                POINT dst = new POINT(x, y);
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = 0;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AC_SRC_ALPHA;
                return UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (hBmp != IntPtr.Zero) { SelectObject(memDc, oldBmp); DeleteObject(hBmp); }
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // ------------------------------------------------------------------
        // ESC 全局退出（只在本窗口是前台、或鼠标悬在卡片上时才生效）
        // ------------------------------------------------------------------
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int VK_ESCAPE = 0x1B;

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);

        static HookProc hookProc;
        static IntPtr hookId = IntPtr.Zero;

        public static bool InstallEscHook(Func<bool> shouldHandle, Action onEsc)
        {
            if (hookId != IntPtr.Zero) return true;
            hookProc = delegate(int nCode, IntPtr wParam, IntPtr lParam)
            {
                if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
                {
                    int vk = Marshal.ReadInt32(lParam);
                    if (vk == VK_ESCAPE && shouldHandle())
                    {
                        try { onEsc(); } catch { }
                        return (IntPtr)1;   // 吞掉这次 ESC
                    }
                }
                return CallNextHookEx(hookId, nCode, wParam, lParam);
            };
            hookId = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
            if (hookId == IntPtr.Zero) { hookProc = null; return false; }
            return true;
        }

        public static void RemoveEscHook()
        {
            if (hookId == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(hookId); } catch { }
            hookId = IntPtr.Zero;
            hookProc = null;
        }

        // ------------------------------------------------------------------
        // 颜色
        // ------------------------------------------------------------------
        public static Color Hex(string s, Color def)
        {
            try
            {
                if (string.IsNullOrEmpty(s)) return def;
                string t = s.Trim().TrimStart('#');
                if (t.Length == 6)
                {
                    int v = int.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    return Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
                }
            }
            catch { }
            return def;
        }

        public static string Hex(Color c)
        {
            return string.Format("{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        public static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int a)
        {
            if (a < 0) a = 0;
            if (a > 255) a = 255;
            return Color.FromArgb(a, c.R, c.G, c.B);
        }

        /// <summary>按 perceived luminance 判断这颜色算“亮”还是“暗”。</summary>
        public static bool IsLight(Color c)
        {
            return (c.R * 299 + c.G * 587 + c.B * 114) / 1000 > 140;
        }
    }
}
