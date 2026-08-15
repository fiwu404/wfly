using System.ComponentModel;
using System.Drawing.Drawing2D;
using WFly.Models;

namespace WFly.UI.Controls;

/// <summary>
/// A three-position, mouse and keyboard accessible traffic mode selector.
/// It intentionally draws the rail and markers instead of relying on text glyphs.
/// </summary>
internal sealed class ProxyModeSelector : Control
{
    private static readonly string[] Labels = ["系统代理", "关闭代理", "TUN 模式"];
    private ProxyMode _mode = ProxyMode.Off;
    private bool _dragging;

    public ProxyModeSelector()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.UserPaint,
            true);
        AccessibleRole = AccessibleRole.Slider;
        AccessibleName = "代理模式";
        Size = new Size(340, 82);
        MinimumSize = new Size(240, 72);
        TabStop = true;
    }

    [DefaultValue(ProxyMode.Off)]
    public ProxyMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            AccessibleDescription = GetLabel(value);
            Invalidate();
            ModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ModeChanged;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(BackColor);

        var points = GetMarkerPoints();
        var railLeft = points[0].X;
        var railRight = points[^1].X;
        const int railHeight = 10;
        const int markerWidth = 10;
        const int markerHeight = 28;
        var railTop = Math.Max(12, (Height - 44) / 2);
        var selectedIndex = (int)_mode;

        var isEnabled = Enabled;
        using var idleBrush = new SolidBrush(isEnabled ? Color.FromArgb(207, 216, 232) : Color.FromArgb(223, 228, 237));
        using var selectedBrush = new SolidBrush(isEnabled ? UiPalette.Accent : Color.FromArgb(157, 171, 194));
        using var selectedOutline = new Pen(isEnabled ? Color.FromArgb(45, 88, 184) : Color.FromArgb(135, 148, 169), 2F);
        using var textBrush = new SolidBrush(ForeColor);
        using var mutedTextBrush = new SolidBrush(isEnabled ? UiPalette.MutedInk : Color.FromArgb(151, 161, 178));

        graphics.FillRoundedRectangle(idleBrush, railLeft, railTop, railRight - railLeft, railHeight, 5);
        graphics.FillRoundedRectangle(selectedBrush, railLeft, railTop, points[selectedIndex].X - railLeft, railHeight, 5);

        for (var index = 0; index < points.Length; index++)
        {
            var marker = new Rectangle(
                points[index].X - markerWidth / 2,
                railTop - (markerHeight - railHeight) / 2,
                markerWidth,
                markerHeight);

            graphics.FillRectangle(index == selectedIndex ? selectedBrush : idleBrush, marker);
            if (index == selectedIndex)
            {
                graphics.DrawRectangle(selectedOutline, marker);
            }

            var labelSize = TextRenderer.MeasureText(Labels[index], Font);
            var labelX = Math.Clamp(points[index].X - labelSize.Width / 2, 0, Math.Max(0, Width - labelSize.Width));
            var labelY = railTop + markerHeight / 2 + 12;
            TextRenderer.DrawText(
                graphics,
                Labels[index],
                Font,
                new Point(labelX, labelY),
                index == selectedIndex ? ForeColor : mutedTextBrush.Color,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        if (Focused)
        {
            var focusRectangle = ClientRectangle;
            focusRectangle.Inflate(-2, -2);
            ControlPaint.DrawFocusRectangle(graphics, focusRectangle, ForeColor, BackColor);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        Focus();
        _dragging = true;
        Capture = true;
        SelectClosestMode(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging && e.Button == MouseButtons.Left)
        {
            SelectClosestMode(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = false;
            Capture = false;
            SelectClosestMode(e.X);
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture)
        {
            _dragging = false;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var next = _mode;
        switch (e.KeyCode)
        {
            case Keys.Left:
                next = (ProxyMode)Math.Max((int)ProxyMode.SystemProxy, (int)_mode - 1);
                break;
            case Keys.Right:
                next = (ProxyMode)Math.Min((int)ProxyMode.Tun, (int)_mode + 1);
                break;
            case Keys.Home:
                next = ProxyMode.SystemProxy;
                break;
            case Keys.End:
                next = ProxyMode.Tun;
                break;
            default:
                return;
        }

        Mode = next;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private Point[] GetMarkerPoints()
    {
        var horizontalPadding = Math.Max(24, Width / 10);
        var usableWidth = Math.Max(1, Width - horizontalPadding * 2);
        var centerY = Math.Max(24, Height / 2 - 9);
        return
        [
            new Point(horizontalPadding, centerY),
            new Point(horizontalPadding + usableWidth / 2, centerY),
            new Point(Width - horizontalPadding, centerY),
        ];
    }

    private void SelectClosestMode(int horizontalPosition)
    {
        var points = GetMarkerPoints();
        var closest = Enumerable.Range(0, points.Length)
            .OrderBy(index => Math.Abs(points[index].X - horizontalPosition))
            .First();
        Mode = (ProxyMode)closest;
    }

    private static string GetLabel(ProxyMode mode) => Labels[(int)mode];
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, int x, int y, int width, int height, int radius)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var diameter = Math.Min(Math.Min(width, height), radius * 2);
        using var path = new GraphicsPath();
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
