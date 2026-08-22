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
    private const int FrameIntervalMilliseconds = 16;
    private const int RailHeight = 3;
    private const int MarkerWidth = 10;
    private const int MarkerHeight = 22;
    private static readonly string[] DefaultLabels = ["系统代理", "关闭代理", "TUN 模式"];

    private readonly System.Windows.Forms.Timer _animationTimer;
    private string[] _labels = (string[])DefaultLabels.Clone();
    private Size[] _labelSizes = new Size[PositionCount];
    private int _selectedIndex = (int)ProxyMode.Off;
    private int? _pendingSelectedIndex;
    private bool _dragging;
    private bool _notifyAfterAnimation;
    private float _renderedPosition = (float)ProxyMode.Off;
    private float _animationFrom;
    private float _animationTo = (float)ProxyMode.Off;
    private long _animationStartedAt;
    private int _lastInvalidatedThumbX = int.MinValue;
    private int _lastInvalidatedHighlight = -1;
    private bool _isInitialized;

    private readonly record struct MarkerPositions(int Left, int Center, int Right)
    {
        public int GetX(int index) => index switch
        {
            0 => Left,
            1 => Center,
            _ => Right,
        };
    }

    public ProxyModeSelector()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Opaque |
            ControlStyles.Selectable |
            ControlStyles.UserPaint,
            true);

        AccessibleRole = AccessibleRole.Slider;
        AccessibleName = "代理模式";
        AccessibleDescription = GetLabel(_selectedIndex);
        Size = new Size(260, 54);
        MinimumSize = new Size(0, 48);
        Margin = Padding.Empty;
        TabStop = true;
        Cursor = Cursors.Hand;

        _animationTimer = new System.Windows.Forms.Timer { Interval = FrameIntervalMilliseconds };
        _animationTimer.Tick += (_, _) => AdvanceAnimation();
        _isInitialized = true;
        RefreshLabelMetrics();
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
            RefreshLabelMetrics();
            AccessibleDescription = GetLabel(_selectedIndex);
            InvalidateVisual(force: true);
        }
    }

    /// <summary>True only while a mouse drag is active.</summary>
    [Browsable(false)]
    internal bool IsDragging => _dragging;

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

        var markers = GetMarkerPositions();
        var railLeft = markers.Left;
        var railRight = markers.Right;
        var railTop = Math.Max(8, (Height - 34) / 2);
        var highlightedIndex = GetNearestIndex(_renderedPosition);
        var animatedMarkerX = PositionToPixel(_renderedPosition, markers);

        var isEnabled = Enabled;
        using var idleBrush = new SolidBrush(isEnabled ? Color.FromArgb(207, 216, 232) : Color.FromArgb(223, 228, 237));
        using var selectedBrush = new SolidBrush(isEnabled ? UiPalette.Accent : Color.FromArgb(157, 171, 194));
        using var textBrush = new SolidBrush(ForeColor);
        using var mutedTextBrush = new SolidBrush(isEnabled ? UiPalette.MutedInk : Color.FromArgb(151, 161, 178));

        FillCapsule(graphics, idleBrush, new Rectangle(railLeft, railTop, railRight - railLeft, RailHeight));
        FillCapsule(graphics, selectedBrush, new Rectangle(railLeft, railTop, Math.Max(0, animatedMarkerX - railLeft), RailHeight));

        for (var index = 0; index < PositionCount; index++)
        {
            var markerX = markers.GetX(index);
            // The active thumb is painted once below.  Only omit the fixed
            // tick when it is physically on that tick; using the nearest
            // index here made ticks disappear halfway through a drag.
            if (Math.Abs(animatedMarkerX - markerX) > 1)
            {
                var marker = new Rectangle(
                    markerX - MarkerWidth / 2,
                    railTop - (MarkerHeight - RailHeight) / 2,
                    MarkerWidth,
                    MarkerHeight);

                FillCapsule(graphics, idleBrush, marker);
            }

            var label = _labels[index];
            var labelSize = _labelSizes[index];
            var labelX = Math.Clamp(markerX - labelSize.Width / 2, 0, Math.Max(0, Width - labelSize.Width));
            var labelY = railTop + MarkerHeight / 2 + 8;
            var labelBounds = new Rectangle(labelX, labelY, Math.Max(0, Width - labelX), Math.Max(labelSize.Height, Font.Height + 2));
            TextRenderer.DrawText(
                graphics,
                label,
                Font,
                labelBounds,
                index == highlightedIndex ? textBrush.Color : mutedTextBrush.Color,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        }

        var activeMarker = new Rectangle(
            animatedMarkerX - MarkerWidth / 2,
            railTop - (MarkerHeight - RailHeight) / 2,
            MarkerWidth,
            MarkerHeight);
        FillCapsule(graphics, selectedBrush, activeMarker);
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
        SetRenderedPosition(PixelToPosition(e.X), force: true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            // Update on the input message itself.  The visual path is small
            // and cached, so this keeps the thumb under the pointer instead
            // of introducing a timer-frame delay.
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
        InvalidateVisual(force: true);

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

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (!_isInitialized)
        {
            return;
        }

        RefreshLabelMetrics();
        InvalidateVisual(force: true);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_isInitialized)
        {
            return;
        }

        InvalidateVisual(force: true);
    }

    private void SetRenderedPosition(float position, bool force = false)
    {
        var normalized = Math.Clamp(position, 0F, PositionCount - 1);
        if (!force && Math.Abs(_renderedPosition - normalized) < 0.001F)
        {
            return;
        }

        _renderedPosition = normalized;
        InvalidateVisual(force, paintImmediately: _dragging);
    }

    private void InvalidateVisual(bool force = false, bool paintImmediately = false)
    {
        var markers = GetMarkerPositions();
        var thumbX = PositionToPixel(_renderedPosition, markers);
        var highlight = GetNearestIndex(_renderedPosition);
        if (!force && thumbX == _lastInvalidatedThumbX && highlight == _lastInvalidatedHighlight)
        {
            return;
        }

        _lastInvalidatedThumbX = thumbX;
        _lastInvalidatedHighlight = highlight;
        Invalidate();

        // Paint the thumb in the same input message while dragging.  A plain
        // Invalidate waits behind queued mouse messages on busy WinForms UIs,
        // which makes an otherwise light control visibly trail the cursor.
        if (paintImmediately && IsHandleCreated)
        {
            Update();
        }
    }

    private void RefreshLabelMetrics()
    {
        if (_labels is null || _labels.Length != PositionCount)
        {
            _labelSizes = new Size[PositionCount];
            return;
        }

        _labelSizes = _labels
            .Select(label => TextRenderer.MeasureText(label, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine))
            .ToArray();
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
        InvalidateVisual(force: true);
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

        InvalidateVisual();
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

        InvalidateVisual(force: true);
    }

    private void RaiseSelectionChanged()
    {
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void FillCapsule(Graphics graphics, Brush brush, Rectangle rectangle)
    {
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return;
        }

        if (rectangle.Width >= rectangle.Height)
        {
            var diameter = rectangle.Height;
            if (rectangle.Width <= diameter)
            {
                graphics.FillEllipse(brush, rectangle);
                return;
            }

            var radius = diameter / 2;
            graphics.FillRectangle(brush, rectangle.X + radius, rectangle.Y, rectangle.Width - diameter, rectangle.Height);
            graphics.FillEllipse(brush, rectangle.X, rectangle.Y, diameter, diameter);
            graphics.FillEllipse(brush, rectangle.Right - diameter, rectangle.Y, diameter, diameter);
            return;
        }

        var verticalDiameter = rectangle.Width;
        if (rectangle.Height <= verticalDiameter)
        {
            graphics.FillEllipse(brush, rectangle);
            return;
        }

        var verticalRadius = verticalDiameter / 2;
        graphics.FillRectangle(brush, rectangle.X, rectangle.Y + verticalRadius, rectangle.Width, rectangle.Height - verticalDiameter);
        graphics.FillEllipse(brush, rectangle.X, rectangle.Y, verticalDiameter, verticalDiameter);
        graphics.FillEllipse(brush, rectangle.X, rectangle.Bottom - verticalDiameter, verticalDiameter, verticalDiameter);
    }

    private MarkerPositions GetMarkerPositions()
    {
        var horizontalPadding = Math.Max(24, Width / 10);
        var usableWidth = Math.Max(1, Width - horizontalPadding * 2);
        return new MarkerPositions(
            horizontalPadding,
            horizontalPadding + usableWidth / 2,
            Width - horizontalPadding);
    }

    private float PixelToPosition(int horizontalPosition)
    {
        var markers = GetMarkerPositions();
        var railWidth = Math.Max(1, markers.Right - markers.Left);
        return Math.Clamp(
            (horizontalPosition - markers.Left) * (PositionCount - 1F) / railWidth,
            0F,
            PositionCount - 1F);
    }

    private static int PositionToPixel(float position, MarkerPositions markers)
    {
        var normalized = Math.Clamp(position / (PositionCount - 1F), 0F, 1F);
        return (int)Math.Round(markers.Left + (markers.Right - markers.Left) * normalized);
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
