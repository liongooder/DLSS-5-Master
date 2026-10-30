// Test window for the overlay helper's automated checks (run.ps1): animated content, never activated, no taskbar button,
// placed beyond the right edge of the rightmost screen with one pixel column left on it (a window wholly off screen is not
// redrawn, so it would send the capture one frame only).
// Usage: target.exe <hwnd-file> <width> <height> <resize-after-s> <resizes> <interval-ms> <life-s> [grow]
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class Target : Form
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    readonly int w, h, resizeAfterMs, resizes, intervalMs, lifeMs;
    public static int Sign = 1;   // -1: the resizes make the window larger than at the start ("grow")
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
        Text = "DLSS 5 overlay test window";
        DoubleBuffered = true;
        var timer = new Timer { Interval = 15 };
        timer.Tick += (s, e) => Tick();
        timer.Start();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

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
        Invalidate();
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

    [STAThread]
    static void Main(string[] a)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));   // per-monitor v2: sizes are real pixels
        if (a.Length > 7 && a[7] == "grow") Sign = -1;
        var form = new Target(int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]), int.Parse(a[5]), int.Parse(a[6]));
        form.HandleCreated += (s, e) => File.WriteAllText(a[0], form.Handle.ToInt64().ToString());
        Application.Run(form);
    }
}
