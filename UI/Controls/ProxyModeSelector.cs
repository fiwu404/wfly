using System.ComponentModel;
using System.Drawing.Drawing2D;
using WFly.Models;

namespace WFly.UI.Controls;

/// <summary>
/// A reusable three-position slider. Mouse input is only committed after the
/// thumb is released: while dragging, the thumb follows the pointer without
/// changing <see cref="SelectedIndex"/> or raising selection events.
/// </summary>
internal sealed class ProxyModeSelector : Control
{
    private const int PositionCount = 3;
    private static readonly string[] DefaultLabels = ["系统代理", "关闭代理", "TUN 模式"];

    private readonly System.Windows.Forms.Timer _animationTimer;
    private string[] _labels = (string[])DefaultLabels.Clone();
    private int _selectedIndex = (int)ProxyMode.Off;
    private int? _pendingSelectedIndex;
    private bool _dragging;
    private bool _notifyAfterAnimation;
    private float _renderedPosition = (float)ProxyMode.Off;
    private float _animationFrom;
    private float _animationTo = (float)ProxyMode.Off;
    private long _animationStartedAt;

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
        AccessibleDescription = GetLabel(_selectedIndex);
        Size = new Size(260, 54);
        MinimumSize = new Size(168, 48);
        TabStop = true;
        Cursor = Cursors.Hand;

        _animationTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _animationTimer.Tick += (_, _) => AdvanceAnimation();
    }

    /// <summary>
    /// The labels for the left, middle, and right positions. Assign exactly
    /// three strings when reusing the control for another three-way setting.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string[] Labels
    {
        get => (string[])_labels.Clone();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length != PositionCount)
            {
                throw new ArgumentException("三档滑轨必须提供三个标签。", nameof(value));
            }

            if (value.Any(label => label is null))
            {
                throw new ArgumentException("三档滑轨的标签不能为 null。", nameof(value));
            }

            _labels = (string[])value.Clone();
            AccessibleDescription = GetLabel(_selectedIndex);
            Invalidate();
        }
    }

    /// <summary>
    /// Gets or sets the committed zero-based position. Setting this property
    /// from code commits immediately and preserves the short visual animation.
    /// </summary>
    [DefaultValue((int)ProxyMode.Off)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SetSelectedIndexFromCode(value);
    }

    /// <summary>
    /// Compatibility view for the existing proxy-mode use case.
    /// </summary>
    [DefaultValue(ProxyMode.Off)]
    public ProxyMode Mode
    {
        get => (ProxyMode)_selectedIndex;
        set => SelectedIndex = (int)value;
    }

    /// <summary>
    /// Raised only when a committed position changes. For mouse input this is
    /// after release and the snap-back animation, never during a drag.
    /// </summary>
    public event EventHandler? SelectedIndexChanged;

    /// <summary>
    /// Compatibility event for the existing proxy-mode use case.
    /// </summary>
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
        const int railHeight = 6;
        const int markerWidth = 10;
        const int markerHeight = 22;
        var railTop = Math.Max(8, (Height - 34) / 2);
        var highlightedIndex = GetNearestIndex(_renderedPosition);
        var animatedMarkerX = PositionToPixel(_renderedPosition, points);

        var isEnabled = Enabled;
        using var idleBrush = new SolidBrush(isEnabled ? Color.FromArgb(207, 216, 232) : Color.FromArgb(223, 228, 237));
        using var selectedBrush = new SolidBrush(isEnabled ? UiPalette.Accent : Color.FromArgb(157, 171, 194));
        using var selectedOutline = new Pen(isEnabled ? Color.FromArgb(45, 88, 184) : Color.FromArgb(135, 148, 169), 2F);
        using var textBrush = new SolidBrush(ForeColor);
        using var mutedTextBrush = new SolidBrush(isEnabled ? UiPalette.MutedInk : Color.FromArgb(151, 161, 178));

        graphics.FillRoundedRectangle(idleBrush, railLeft, railTop, railRight - railLeft, railHeight, 5);
        graphics.FillRoundedRectangle(selectedBrush, railLeft, railTop, Math.Max(0, animatedMarkerX - railLeft), railHeight, 5);

        for (var index = 0; index < points.Length; index++)
        {
            var marker = new Rectangle(
                points[index].X - markerWidth / 2,
                railTop - (markerHeight - railHeight) / 2,
                markerWidth,
                markerHeight);

            graphics.FillRoundedRectangle(idleBrush, marker.X, marker.Y, marker.Width, marker.Height, 3);

            var label = _labels[index];
            var labelSize = TextRenderer.MeasureText(label, Font);
            var labelX = Math.Clamp(points[index].X - labelSize.Width / 2, 0, Math.Max(0, Width - labelSize.Width));
            var labelY = railTop + markerHeight / 2 + 8;
            TextRenderer.DrawText(
                graphics,
                label,
                Font,
                new Point(labelX, labelY),
                index == highlightedIndex ? textBrush.Color : mutedTextBrush.Color,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        var activeMarker = new Rectangle(
            animatedMarkerX - markerWidth / 2,
            railTop - (markerHeight - railHeight) / 2,
            markerWidth,
            markerHeight);
        graphics.FillRoundedRectangle(selectedBrush, activeMarker.X, activeMarker.Y, activeMarker.Width, activeMarker.Height, 3);
        graphics.DrawRoundedRectangle(selectedOutline, activeMarker, 3);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled)
        {
            return;
        }

        Focus();
        CancelAnimation(discardPendingSelection: true);
        _dragging = true;
        Capture = true;
        SetRenderedPosition(PixelToPosition(e.X));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            SetRenderedPosition(PixelToPosition(e.X));
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_dragging)
        {
            return;
        }

        SetRenderedPosition(PixelToPosition(e.X));
        _dragging = false;
        Capture = false;

        var targetIndex = GetNearestIndex(_renderedPosition);
        if (targetIndex == _selectedIndex)
        {
            BeginAnimation(targetIndex, notifyAfterAnimation: false);
            return;
        }

        // Do not change the public selection yet. The host only receives the
        // change after the thumb has settled at its nearest fixed position.
        _pendingSelectedIndex = targetIndex;
        BeginAnimation(targetIndex, notifyAfterAnimation: true);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture || !_dragging)
        {
            return;
        }

        _dragging = false;
        _pendingSelectedIndex = null;
        BeginAnimation(_selectedIndex, notifyAfterAnimation: false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var next = _selectedIndex;
        switch (e.KeyCode)
        {
            case Keys.Left:
                next = Math.Max(0, _selectedIndex - 1);
                break;
            case Keys.Right:
                next = Math.Min(PositionCount - 1, _selectedIndex + 1);
                break;
            case Keys.Home:
                next = 0;
                break;
            case Keys.End:
                next = PositionCount - 1;
                break;
            default:
                return;
        }

        SelectedIndex = next;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled && _dragging)
        {
            _dragging = false;
            Capture = false;
            _pendingSelectedIndex = null;
            BeginAnimation(_selectedIndex, notifyAfterAnimation: false);
        }
    }

    private void SetSelectedIndexFromCode(int value)
    {
        ValidateIndex(value);
        CancelAnimation(discardPendingSelection: true);

        if (_dragging)
        {
            _dragging = false;
            Capture = false;
        }

        var changed = _selectedIndex != value;
        _selectedIndex = value;
        AccessibleDescription = GetLabel(_selectedIndex);
        BeginAnimation(_selectedIndex, notifyAfterAnimation: false);

        if (changed)
        {
            RaiseSelectionChanged();
        }
    }

    private void SetRenderedPosition(float position)
    {
        _renderedPosition = Math.Clamp(position, 0F, PositionCount - 1);
        Invalidate();
    }

    private void BeginAnimation(float targetPosition, bool notifyAfterAnimation)
    {
        _animationTimer.Stop();
        _animationFrom = _renderedPosition;
        _animationTo = Math.Clamp(targetPosition, 0F, PositionCount - 1);
        _animationStartedAt = Environment.TickCount64;
        _notifyAfterAnimation = notifyAfterAnimation;

        if (Math.Abs(_animationTo - _animationFrom) < 0.001F)
        {
            _renderedPosition = _animationTo;
            CompleteAnimation();
            return;
        }

        _animationTimer.Start();
        Invalidate();
    }

    private void CancelAnimation(bool discardPendingSelection)
    {
        _animationTimer.Stop();
        _notifyAfterAnimation = false;
        if (discardPendingSelection)
        {
            _pendingSelectedIndex = null;
        }
    }

    private void AdvanceAnimation()
    {
        const float durationMilliseconds = 170F;
        var elapsed = Math.Clamp(
            (float)(Environment.TickCount64 - _animationStartedAt) / durationMilliseconds,
            0F,
            1F);
        var eased = 1F - MathF.Pow(1F - elapsed, 3F);
        _renderedPosition = _animationFrom + (_animationTo - _animationFrom) * eased;

        if (elapsed >= 1F)
        {
            _renderedPosition = _animationTo;
            _animationTimer.Stop();
            CompleteAnimation();
            return;
        }

        Invalidate();
    }

    private void CompleteAnimation()
    {
        var shouldNotify = _notifyAfterAnimation;
        _notifyAfterAnimation = false;

        if (shouldNotify && _pendingSelectedIndex is { } pendingIndex)
        {
            _pendingSelectedIndex = null;
            if (_selectedIndex != pendingIndex)
            {
                _selectedIndex = pendingIndex;
                AccessibleDescription = GetLabel(_selectedIndex);
                RaiseSelectionChanged();
            }
        }
        else
        {
            _pendingSelectedIndex = null;
        }

        Invalidate();
    }

    private void RaiseSelectionChanged()
    {
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private Point[] GetMarkerPoints()
    {
        var horizontalPadding = Math.Max(24, Width / 10);
        var usableWidth = Math.Max(1, Width - horizontalPadding * 2);
        var centerY = Math.Max(20, Height / 2 - 7);
        return
        [
            new Point(horizontalPadding, centerY),
            new Point(horizontalPadding + usableWidth / 2, centerY),
            new Point(Width - horizontalPadding, centerY),
        ];
    }

    private float PixelToPosition(int horizontalPosition)
    {
        var points = GetMarkerPoints();
        var railWidth = Math.Max(1, points[^1].X - points[0].X);
        return Math.Clamp(
            (horizontalPosition - points[0].X) * (PositionCount - 1F) / railWidth,
            0F,
            PositionCount - 1F);
    }

    private static int PositionToPixel(float position, Point[] points)
    {
        var normalized = Math.Clamp(position / (PositionCount - 1F), 0F, 1F);
        return (int)Math.Round(points[0].X + (points[^1].X - points[0].X) * normalized);
    }

    private static int GetNearestIndex(float position)
        => Math.Clamp((int)MathF.Round(position, MidpointRounding.AwayFromZero), 0, PositionCount - 1);

    private static void ValidateIndex(int value)
    {
        if (value < 0 || value >= PositionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "三档滑轨索引必须介于 0 和 2 之间。");
        }
    }

    private string GetLabel(int index)
    {
        ValidateIndex(index);
        return _labels[index];
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, int x, int y, int width, int height, int radius)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        using var path = CreateRoundedRectanglePath(new Rectangle(x, y, width, height), radius);
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle rectangle, int radius)
    {
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return;
        }

        using var path = CreateRoundedRectanglePath(rectangle, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle rectangle, int radius)
    {
        var diameter = Math.Min(Math.Min(rectangle.Width, rectangle.Height), radius * 2);
        var path = new GraphicsPath();
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
