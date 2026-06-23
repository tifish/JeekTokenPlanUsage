using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace JeekTokenPlanUsage;

/// Renders a tray icon as a colored rounded badge whose background color identifies
/// the provider + window, with a large white percentage number. When usage is high
/// the number turns amber as a warning.
public static class IconRenderer
{
    private const int Size = 48;
    private const int WarnThreshold = 80;

    private const float FrameThickness = 5f;

    private static readonly Color NumberBackground = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color White = Color.FromArgb(255, 255, 255);
    private static readonly Color WarnAmber = Color.FromArgb(255, 214, 10);
    private static readonly Color ErrorFrame = Color.FromArgb(120, 120, 120);
    private static readonly Color PauseBadgeBackground = Color.FromArgb(230, 0, 0, 0);
    private static readonly Color PauseBadgeFrame = Color.FromArgb(210, 255, 255, 255);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// Builds an icon whose colored frame identifies the provider/window; the interior
    /// is transparent and the number is drawn white (or red when usage is high) so it
    /// stays legible. The caller owns the returned Icon and must dispose it.
    public static Icon Render(
        Color frameColor,
        double? percent,
        bool isError,
        string placeholder = "--",
        bool isPaused = false)
    {
        string text;
        Color textColor;

        if (isError || percent is null)
        {
            text = placeholder;
            textColor = White;
            frameColor = ErrorFrame;
        }
        else
        {
            int value = (int)Math.Round(percent.Value);
            text = value >= 100 ? "100" : value.ToString();
            textColor = value >= WarnThreshold ? WarnAmber : White;
        }

        using var bmp = new Bitmap(Size, Size);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            DrawNumberBackground(g);
            DrawFrame(g, frameColor);
            DrawNumber(g, text, textColor);
            if (isPaused)
                DrawPauseBadge(g);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static void DrawFrame(Graphics g, Color color)
    {
        float inset = FrameThickness / 2f;
        using GraphicsPath path = RoundedRect(
            new RectangleF(inset, inset, Size - FrameThickness, Size - FrameThickness), Size * 0.22f);
        using var pen = new Pen(color, FrameThickness) { LineJoin = LineJoin.Round };
        g.DrawPath(pen, path);
    }

    private static void DrawNumberBackground(Graphics g)
    {
        float inset = FrameThickness;
        using GraphicsPath path = RoundedRect(
            new RectangleF(inset, inset, Size - inset * 2, Size - inset * 2), Size * 0.16f);
        using var brush = new SolidBrush(NumberBackground);
        g.FillPath(brush, path);
    }

    private static void DrawPauseBadge(Graphics g)
    {
        const float badgeSize = 18f;
        RectangleF badge = new(Size - badgeSize - 2f, 2f, badgeSize, badgeSize);
        using var badgeBrush = new SolidBrush(PauseBadgeBackground);
        using var badgePen = new Pen(PauseBadgeFrame, 1.4f);
        g.FillEllipse(badgeBrush, badge);
        g.DrawEllipse(badgePen, badge);

        const float barWidth = 3.3f;
        const float barHeight = 9.8f;
        const float gap = 3.2f;
        float x = badge.X + (badge.Width - barWidth * 2f - gap) / 2f;
        float y = badge.Y + (badge.Height - barHeight) / 2f;
        using var barBrush = new SolidBrush(White);
        using GraphicsPath left = RoundedRect(new RectangleF(x, y, barWidth, barHeight), 1.3f);
        using GraphicsPath right = RoundedRect(new RectangleF(x + barWidth + gap, y, barWidth, barHeight), 1.3f);
        g.FillPath(barBrush, left);
        g.FillPath(barBrush, right);
    }

    private const float GlyphEm = 64f;
    private static readonly RectangleF NumberArea = new(6, 6, Size - 12, Size - 12);

    // A single scale shared by all icons so digits have a consistent height regardless
    // of how many there are. Sized so a two-digit number fits the area.
    private static readonly float NumberScale = ComputeReferenceScale();

    // Draws the number as a scaled glyph path so the digits fill the icon area,
    // rather than via DrawString (whose line-height padding leaves the glyph small).
    private static void DrawNumber(Graphics g, string text, Color color)
    {
        using var family = new FontFamily("Segoe UI");
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)FontStyle.Bold, GlyphEm, PointF.Empty, StringFormat.GenericTypographic);

        RectangleF b = path.GetBounds();
        if (b.Width <= 0 || b.Height <= 0)
            return;

        // Use the shared scale; only shrink further if an unusually wide string overflows.
        float scale = NumberScale;
        if (b.Width * scale > NumberArea.Width)
            scale = NumberArea.Width / b.Width;

        using var m = new Matrix();
        m.Translate(
            NumberArea.X + (NumberArea.Width - b.Width * scale) / 2f,
            NumberArea.Y + (NumberArea.Height - b.Height * scale) / 2f);
        m.Scale(scale, scale);
        m.Translate(-b.X, -b.Y);
        path.Transform(m);

        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static float ComputeReferenceScale()
    {
        using var family = new FontFamily("Segoe UI");
        using var path = new GraphicsPath();
        path.AddString("88", family, (int)FontStyle.Bold, GlyphEm, PointF.Empty, StringFormat.GenericTypographic);
        RectangleF b = path.GetBounds();
        return Math.Min(NumberArea.Width / b.Width, NumberArea.Height / b.Height);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
