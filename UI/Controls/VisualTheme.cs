using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WFly.UI.Controls;

/// <summary>
/// Shared colours for the intentionally quiet, glass-like desktop surface.
/// Keeping the palette in one place makes the native WinForms controls feel
/// coherent without relying on third-party theme packages.
/// </summary>
internal static class UiPalette
{
    public static readonly Color Canvas = Color.FromArgb(244, 247, 252);
    public static readonly Color Card = Color.FromArgb(252, 253, 255);
    public static readonly Color CardBorder = Color.FromArgb(211, 220, 235);
    public static readonly Color Ink = Color.FromArgb(37, 48, 65);
    public static readonly Color MutedInk = Color.FromArgb(100, 113, 132);
    public static readonly Color Accent = Color.FromArgb(66, 112, 224);
    public static readonly Color AccentSoft = Color.FromArgb(230, 238, 255);
    public static readonly Color Hover = Color.FromArgb(239, 244, 253);
}

/// <summary>
/// Draws a soft, translucent-looking card while retaining normal GroupBox
/// layout behaviour for child controls.  It is deliberately lightweight: no
/// bitmap snapshots or heavyweight composition layers are required.
/// </summary>
internal sealed class FrostedGroupBox : GroupBox
{
    public FrostedGroupBox()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        ForeColor = UiPalette.Ink;
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        Padding = new Padding(18, 34, 18, 18);
        Margin = new Padding(0, 0, 12, 12);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var background = Parent?.BackColor ?? UiPalette.Canvas;
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = ClientRectangle;
        bounds.Inflate(-1, -1);
        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            return;
        }

        using var path = CreateRoundedPath(bounds, 14);
        using var background = new LinearGradientBrush(
            bounds,
            Color.FromArgb(246, 255, 255, 255),
            Color.FromArgb(226, 239, 245, 255),
            LinearGradientMode.Vertical);
        using var border = new Pen(UiPalette.CardBorder);
        graphics.FillPath(background, path);
        graphics.DrawPath(border, path);

        if (!string.IsNullOrWhiteSpace(Text))
        {
            var titleBounds = new Rectangle(18, 10, Math.Max(0, Width - 36), 22);
            TextRenderer.DrawText(
                graphics,
                Text,
                Font,
                titleBounds,
                ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2);
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// Uses the operating system backdrop when available and silently falls back
/// to the painted glass cards on older Windows versions.
/// </summary>
internal static class WindowBackdrop
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    public static void Apply(Form form)
    {
        if (!OperatingSystem.IsWindows() || !form.IsHandleCreated)
        {
            return;
        }

        try
        {
            // Mica is the supported Windows 11 desktop backdrop.  The card
            // renderer remains the visual fallback if this attribute is not
            // present (for example on Windows 10).
            var backdrop = DwmsbtMainWindow;
            _ = DwmSetWindowAttribute(form.Handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Older Windows: keep the lightweight painted fallback.
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows: keep the lightweight painted fallback.
        }
    }
}
