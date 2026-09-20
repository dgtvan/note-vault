using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace NoteVault;

/// <summary>
/// Exactly two icon states, drawn at runtime so there are no .ico assets to ship.
/// Normal means nothing needs reviewing. Red means note-vault tried to do its job
/// and could not — nothing else ever colours it.
/// </summary>
public static class TrayIcons
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Normal { get; } = Build(Color.FromArgb(0x3B, 0x82, 0xF6), Color.FromArgb(0x1D, 0x4E, 0xD8));
    public static Icon Error { get; } = Build(Color.FromArgb(0xDC, 0x26, 0x26), Color.FromArgb(0x99, 0x1B, 0x1B));

    private static Icon Build(Color body, Color spine)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var rect = new Rectangle(3, 2, 26, 28);
            using (var path = RoundedRect(rect, 5))
            using (var brush = new SolidBrush(body))
                g.FillPath(brush, path);

            // Spine, so the glyph reads as a notebook rather than a plain square.
            using (var spineBrush = new SolidBrush(spine))
                g.FillRectangle(spineBrush, new Rectangle(3, 2, 7, 28));

            using var line = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
            g.FillRectangle(line, 13, 9, 13, 3);
            g.FillRectangle(line, 13, 15, 13, 3);
            g.FillRectangle(line, 13, 21, 9, 3);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
