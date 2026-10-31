// Test window for the overlay helper's automated checks (run.ps1): animated content, never activated, no taskbar button,
// placed beyond the right edge of the rightmost screen with one pixel column left on it (a window wholly off screen is not
// redrawn, so it would send the capture one frame only).
// Usage: target.exe <hwnd-file> <width> <height> <resize-after-s> <resizes> <interval-ms> <life-s> [grow] [page [scroll]]
//   page: a video playing on a dark web page instead of the moving shapes (see PaintPage); scroll: that page scrolling
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class Target : Form
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    // The page is handed to Windows whole, as a layered window's picture: the usual painting only reaches the part of a window
    // that is on a screen, so the capture of this one would be black but for its one pixel column.
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc,
                                                                     ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    readonly int w, h, resizeAfterMs, resizes, intervalMs, lifeMs;
    public static int Sign = 1;   // -1: the resizes make the window larger than at the start ("grow")
    public static bool Page;      // "page"
    public static bool Scrolling; // "scroll" (with "page")
    Bitmap video;                 // the page's video: a picture wider and taller than the player, panned across it
    Bitmap frame;                 // the page as last drawn
    readonly DateTime start = DateTime.UtcNow;
    int done;
    DateTime nextResize;

    Target(int w, int h, int resizeAfterS, int resizes, int intervalMs, int lifeS)
    {
        this.w = w; this.h = h; resizeAfterMs = resizeAfterS * 1000; this.resizes = resizes; this.intervalMs = intervalMs; lifeMs = lifeS * 1000;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // One pixel column on the rightmost screen, the rest beyond it: Windows keeps drawing a window that is on a screen.
        Screen right = Screen.AllScreens[0];
        foreach (var s in Screen.AllScreens) if (s.Bounds.Right > right.Bounds.Right) right = s;
        Bounds = new Rectangle(right.Bounds.Right - 1, right.Bounds.Top + 100, w, h);
        Text = "DLSS 5 overlay test window\u2122 caf\u00E9";   // a title beyond ASCII (trademark sign, accent), as real ones have
        DoubleBuffered = true;
        var timer = new Timer { Interval = 15 };
        timer.Tick += (s, e) => Tick();
        timer.Start();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (Page) cp.ExStyle |= 0x80000;   // WS_EX_LAYERED
            return cp;
        }
    }

    void PresentPage()
    {
        var size = ClientSize;
        if (frame == null || frame.Size != size)
        {
            if (frame != null) frame.Dispose();
            frame = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        }
        using (var g = Graphics.FromImage(frame)) PaintPage(g, new Rectangle(Point.Empty, size), (DateTime.UtcNow - start).TotalSeconds);
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bitmap = frame.GetHbitmap(Color.Black);
        var old = SelectObject(mem, bitmap);
        var sz = new SIZE { cx = size.Width, cy = size.Height };
        var origin = new POINT();
        var blend = new BLENDFUNCTION { SourceConstantAlpha = 255 };
        UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref sz, mem, ref origin, 0, ref blend, 4);   // ULW_OPAQUE
        SelectObject(mem, old);
        DeleteObject(bitmap);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
    }

    void Tick()
    {
        var ms = (DateTime.UtcNow - start).TotalMilliseconds;
        if (ms > lifeMs) { Close(); return; }
        if (ms > resizeAfterMs && done < resizes && DateTime.UtcNow >= nextResize)
        {
            done++;
            nextResize = DateTime.UtcNow.AddMilliseconds(intervalMs);
            var shrink = done % 2 == 1 ? 16 * ((done + 1) / 2 % 8 + 1) : 0;
            Size = new Size(w - Sign * shrink, h - Sign * shrink);
        }
        if (Page) PresentPage();
        else Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = (DateTime.UtcNow - start).TotalSeconds;
        var g = e.Graphics;
        var r = ClientRectangle;
        using (var bg = new LinearGradientBrush(r, Color.FromArgb(30, 60, 120), Color.FromArgb(200, 120, 40), (float)(t * 40 % 360)))
            g.FillRectangle(bg, r);
        for (int i = 0; i < 12; i++)
        {
            var x = (float)((Math.Sin(t * 0.9 + i) * 0.4 + 0.5) * r.Width);
            var y = (float)((Math.Cos(t * 1.3 + i * 0.7) * 0.4 + 0.5) * r.Height);
            using (var b = new SolidBrush(Color.FromArgb(220, (i * 40) % 255, 255 - (i * 20) % 255, (i * 70) % 255)))
                g.FillEllipse(b, x - 40, y - 40, 80, 80);
        }
        using (var f = new Font("Segoe UI", 28))
            g.DrawString("DLSS 5 overlay test " + t.ToString("0.0"), f, Brushes.White, 40, 40);
    }

    // A dark web page with a video playing, laid out for 1920x1080: what the overlay is mostly used on. Only the video, the glow
    // around it (its colour drifts a little every frame, as a video site's ambient light does) and the tab's sound bars move; the
    // thin icons, flat buttons and small text on that glow, the bookmarks and the player's controls over the video stay still.
    // With "scroll" the page under the site's bar scrolls instead (at a speed that keeps changing, round and round) over a
    // background that stays still, and the glow keeps one colour: text and thin rules pass over still pixels.
    const int ContentHeight = 1700;

    void PaintPage(Graphics g, Rectangle r, double t)
    {
        if (video == null) video = MakeVideo(2400, 1300);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var k = Scrolling ? 0.5 : 0.5 + 0.5 * Math.Sin(t * 0.7);
        var glow = Color.FromArgb((int)(35 + 45 * k), (int)(70 - 15 * k), (int)(80 - 40 * k));
        using (var bg = new LinearGradientBrush(new Rectangle(0, 70, r.Width, r.Height), glow, Color.FromArgb(20, 20, 20), 70f))
            g.FillRectangle(bg, 0, 70, r.Width, r.Height - 70);
        using (var white = new Pen(Color.White, 2f))
        using (var grey = new SolidBrush(Color.FromArgb(170, 170, 170)))
        using (var small = new Font("Segoe UI", 10f))
        using (var text = new Font("Segoe UI", 11f))
        using (var bold = new Font("Segoe UI", 11f, FontStyle.Bold))
        using (var title = new Font("Segoe UI", 15f, FontStyle.Bold))
        using (var tiny = new Font("Segoe UI", 7.5f, FontStyle.Bold))
        {
            // The page under the site's bar; scrolled, it wraps round.
            g.SetClip(new Rectangle(0, 120, r.Width, r.Height - 120));
            var scrolled = (int)(t * 1200 + 300 * Math.Sin(t * 3));   // 300 to 2100 pixels a second, as a wheel eases in and out
            var top = 120 - (Scrolling ? (scrolled % ContentHeight + ContentHeight) % ContentHeight : 0);
            PaintContent(g, top, t, white, grey, small, text, bold, title, tiny);
            if (Scrolling) PaintContent(g, top + ContentHeight, t, white, grey, small, text, bold, title, tiny);
            g.ResetClip();
            // Browser: the tab strip with the sound bars of a playing tab, then the bookmarks bar.
            using (var b = new SolidBrush(Color.FromArgb(32, 33, 36))) g.FillRectangle(b, 0, 0, r.Width, 40);
            using (var b = new SolidBrush(Color.FromArgb(53, 54, 58))) g.FillRectangle(b, 0, 40, r.Width, 30);
            g.DrawString("A comparison of two versions - VideoSite", text, Brushes.White, 40, 10);
            for (int i = 0; i < 3; i++)
            {
                var bar = (float)(4 + 8 * (0.5 + 0.5 * Math.Sin(t * 9 + i * 2.1)));
                g.FillRectangle(Brushes.White, 420 + i * 5, 26 - bar, 3, bar);
            }
            var x = 20;
            foreach (var mark in new[] { "News - Today", "VideoSite", "Docs - Getting st...", "Weather - 7 day...", "Recipes - Quick...", "Maps - Directions..." })
            {
                g.FillEllipse(Brushes.SteelBlue, x, 48, 14, 14);
                g.DrawString(mark, small, Brushes.White, x + 18, 46);
                x += 30 + (int)g.MeasureString(mark, small).Width;
            }
            // The site's top bar, on the glow: thin icons, a logo, a search box, a flat button and a badge.
            for (int i = 0; i < 3; i++) g.DrawLine(white, 24, 86 + i * 7, 46, 86 + i * 7);
            using (var red = new SolidBrush(Color.FromArgb(255, 0, 51))) FillRound(g, red, 64, 81, 34, 24, 7);
            g.FillPolygon(Brushes.White, new[] { new PointF(76, 87), new PointF(76, 99), new PointF(87, 93) });
            g.DrawString("VideoSite", title, Brushes.White, 102, 79);
            using (var b = new SolidBrush(Color.FromArgb(18, 18, 18))) FillRound(g, b, 600, 76, 560, 34, 17);
            g.DrawString("Search", text, grey, 618, 82);
            using (var b = new SolidBrush(Color.FromArgb(63, 63, 63))) FillRound(g, b, 1560, 76, 110, 34, 17);
            g.DrawLine(white, 1578, 93, 1592, 93);
            g.DrawLine(white, 1585, 86, 1585, 100);
            g.DrawString("Create", bold, Brushes.White, 1600, 82);
            g.DrawArc(white, 1700, 80, 18, 20, 180, 180);
            g.DrawLine(white, 1700, 90, 1700, 100);
            g.DrawLine(white, 1718, 90, 1718, 100);
            g.DrawLine(white, 1697, 100, 1721, 100);
            using (var red = new SolidBrush(Color.FromArgb(204, 0, 0))) FillRound(g, red, 1708, 74, 24, 16, 8);
            g.DrawString("9+", tiny, Brushes.White, 1711, 75);
            g.FillEllipse(Brushes.DarkOrange, 1760, 78, 30, 30);
        }
    }

    // The page from its top: the player with its controls and the title under it, and beside it a list of videos and the
    // comments (text and 1-pixel rules), which only ever move with the page.
    void PaintContent(Graphics g, int top, double t, Pen white, Brush grey, Font small, Font text, Font bold, Font title, Font tiny)
    {
        // The player: the video pans across a picture larger than it; its controls stay where they are on it.
        var player = new Rectangle(40, top + 10, 1180, 664);
        var sx = (int)(t * 150) % (video.Width - player.Width);
        var sy = (int)(300 + 250 * Math.Sin(t * 0.5));
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.DrawImage(video, player, new Rectangle(sx, sy, player.Width, player.Height), GraphicsUnit.Pixel);
        var y = player.Bottom - 50;
        using (var b = new SolidBrush(Color.FromArgb(150, 150, 150))) g.FillRectangle(b, player.Left + 12, y, player.Width - 24, 3);
        using (var red = new SolidBrush(Color.FromArgb(255, 0, 51)))
        {
            g.FillRectangle(red, player.Left + 12, y, 430, 3);
            g.FillEllipse(red, player.Left + 436, y - 5, 13, 13);
        }
        g.FillRectangle(Brushes.White, player.Left + 22, y + 18, 5, 18);
        g.FillRectangle(Brushes.White, player.Left + 32, y + 18, 5, 18);
        g.FillPolygon(Brushes.White, new[] { new PointF(player.Left + 62, y + 22), new PointF(player.Left + 68, y + 22), new PointF(player.Left + 76, y + 16), new PointF(player.Left + 76, y + 38), new PointF(player.Left + 68, y + 32), new PointF(player.Left + 62, y + 32) });
        g.DrawString("2:17 / 15:12", text, Brushes.White, player.Left + 100, y + 16);
        g.DrawString("Character models  >", text, Brushes.White, player.Left + 260, y + 16);
        g.DrawRectangle(white, player.Right - 150, y + 17, 26, 18);
        g.DrawString("CC", tiny, Brushes.White, player.Right - 146, y + 19);
        g.DrawEllipse(white, player.Right - 108, y + 16, 20, 20);
        g.DrawEllipse(white, player.Right - 102, y + 22, 8, 8);
        g.DrawRectangle(white, player.Right - 70, y + 18, 24, 16);
        g.DrawString("A careful comparison: the remastered version against the original", title, Brushes.White, 40, player.Bottom + 14);
        // Beside the player: the list of videos, then the comments.
        for (int i = 0; i < 3; i++)
        {
            var row = top + 10 + i * 150;
            using (var b = new LinearGradientBrush(new Rectangle(1250, row, 250, 140), Color.FromArgb(60 + i * 50, 90, 160 - i * 40), Color.FromArgb(230, 200 - i * 60, 90), 30f + i * 40))
                g.FillRectangle(b, 1250, row, 250, 140);
            g.DrawString("New", small, Brushes.White, 1258, row + 6);
            g.DrawString(new[] { "A walk through a city at night", "A mountain timelapse", "A train ride in 4K" }[i], bold, Brushes.White, 1512, row + 4);
            g.DrawString("Channel " + (i + 1) + "  \u00B7  " + (i * 37 + 13) + "K  \u00B7  1d ago", small, grey, 1512, row + 30);
        }
        using (var rule = new Pen(Color.FromArgb(90, 90, 90), 1f))
            for (int i = 0; i < 14; i++)
            {
                var row = top + 480 + i * 86;
                g.FillEllipse(Brushes.SlateGray, 1250, row, 32, 32);
                g.DrawString("Viewer " + (i + 1), bold, Brushes.White, 1292, row - 2);
                g.DrawString(new[] { "The faces look far more natural in the new one.", "Look at the hair in the wind at 2:10.", "The original had more character, I think.", "Lighting on the armour is so much better now." }[i % 4], text, grey, 1292, row + 26);
                g.DrawLine(rule, 1250, row + 66, 1880, row + 66);
            }
    }

    static void FillRound(Graphics g, Brush b, float x, float y, float w, float h, float radius)
    {
        using (var p = new GraphicsPath())
        {
            p.AddArc(x, y, 2 * radius, 2 * radius, 180, 90);
            p.AddArc(x + w - 2 * radius, y, 2 * radius, 2 * radius, 270, 90);
            p.AddArc(x + w - 2 * radius, y + h - 2 * radius, 2 * radius, 2 * radius, 0, 90);
            p.AddArc(x, y + h - 2 * radius, 2 * radius, 2 * radius, 90, 90);
            p.CloseFigure();
            g.FillPath(b, p);
        }
    }

    // The video's picture: soft colour fields with detail on them (stones, leather, hair-like strokes), like a game scene.
    static Bitmap MakeVideo(int w, int h)
    {
        var bmp = new Bitmap(w, h);
        var rnd = new Random(5);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(70, 140, 200), Color.FromArgb(150, 110, 70), 60f))
                g.FillRectangle(bg, 0, 0, w, h);
            for (int i = 0; i < 260; i++)
            {
                var c = Color.FromArgb(rnd.Next(120, 230), rnd.Next(40, 230), rnd.Next(40, 200), rnd.Next(30, 180));
                var s = rnd.Next(30, 260);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, rnd.Next(-100, w), rnd.Next(-100, h), s, s * (0.5f + (float)rnd.NextDouble()));
            }
            for (int i = 0; i < 2500; i++)
            {
                var c = Color.FromArgb(rnd.Next(60, 200), rnd.Next(0, 255), rnd.Next(0, 255), rnd.Next(0, 255));
                using (var p = new Pen(c, 1f + (float)rnd.NextDouble() * 2f))
                {
                    var x0 = rnd.Next(0, w);
                    var y0 = rnd.Next(0, h);
                    g.DrawLine(p, x0, y0, x0 + rnd.Next(-40, 40), y0 + rnd.Next(-40, 40));
                }
            }
        }
        return bmp;
    }

    [STAThread]
    static void Main(string[] a)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));   // per-monitor v2: sizes are real pixels
        for (int i = 7; i < a.Length; i++)
        {
            if (a[i] == "grow") Sign = -1;
            if (a[i] == "page") Page = true;
            if (a[i] == "scroll") Scrolling = true;
        }
        var form = new Target(int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]), int.Parse(a[5]), int.Parse(a[6]));
        form.HandleCreated += (s, e) => File.WriteAllText(a[0], form.Handle.ToInt64().ToString());
        Application.Run(form);
    }
}
