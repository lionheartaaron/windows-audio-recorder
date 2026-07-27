using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WindowAudioRecorder;

/// <summary>Draws the window and tray icons at runtime so the app ships without icon assets.</summary>
internal static class AppIcons
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Idle { get; } = Render(Color.FromArgb(214, 62, 62), filled: false);

    public static Icon Recording { get; } = Render(Color.FromArgb(214, 62, 62), filled: true);

    private static Icon Render(Color color, bool filled)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var circle = new Rectangle(4, 4, 23, 23);
            if (filled)
            {
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, circle);
            }
            else
            {
                using var pen = new Pen(color, 3.5f);
                g.DrawEllipse(pen, circle);
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, new Rectangle(12, 12, 8, 8));
            }
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();   // Clone owns its data, so the HICON can go straight back.
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
