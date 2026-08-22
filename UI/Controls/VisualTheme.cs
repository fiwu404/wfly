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
    public static bool IsDark { get; private set; }

    public static Color Canvas => IsDark ? Color.FromArgb(25, 29, 37) : Color.FromArgb(244, 247, 252);
    public static Color Card => IsDark ? Color.FromArgb(34, 40, 51) : Color.FromArgb(252, 253, 255);
    public static Color CardBorder => IsDark ? Color.FromArgb(67, 78, 96) : Color.FromArgb(211, 220, 235);
    public static Color Ink => IsDark ? Color.FromArgb(232, 237, 246) : Color.FromArgb(37, 48, 65);
    public static Color MutedInk => IsDark ? Color.FromArgb(162, 176, 196) : Color.FromArgb(100, 113, 132);
    public static Color Accent => IsDark ? Color.FromArgb(101, 147, 255) : Color.FromArgb(66, 112, 224);
    public static Color AccentSoft => IsDark ? Color.FromArgb(47, 62, 92) : Color.FromArgb(230, 238, 255);
    public static Color Hover => IsDark ? Color.FromArgb(42, 50, 65) : Color.FromArgb(239, 244, 253);
    public static Color CardTop => IsDark ? Color.FromArgb(250, 42, 49, 63) : Color.FromArgb(246, 255, 255, 255);
    public static Color CardBottom => IsDark ? Color.FromArgb(242, 31, 37, 48) : Color.FromArgb(226, 239, 245, 255);

    public static void SetDark(bool isDark) => IsDark = isDark;
}

/// <summary>
/// Keeps the native WinForms input controls visually aligned with the glass
/// cards without introducing per-control regions or composition layers.  The
/// latter are costly during interactive window resizing, so inputs use one
/// quiet, flat surface and the tables paint a lightweight shared frame.
/// </summary>
internal static class UiControlTheme
{
    private static Color GridLine => UiPalette.IsDark ? Color.FromArgb(70, 82, 101) : Color.FromArgb(207, 218, 234);
    private static Color AlternateRow => UiPalette.IsDark ? Color.FromArgb(39, 46, 59) : Color.FromArgb(248, 250, 254);
    private static Color HeaderSurface => UiPalette.IsDark ? Color.FromArgb(45, 55, 71) : Color.FromArgb(241, 247, 255);

    public static ComboBox CreateComboBox()
    {
        var comboBox = new ComboBox();
        ApplyComboBox(comboBox);
        return comboBox;
    }

    public static void ApplyComboBox(ComboBox comboBox)
    {
        ArgumentNullException.ThrowIfNull(comboBox);

        comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBox.FlatStyle = FlatStyle.Flat;
        comboBox.BackColor = UiPalette.Card;
        comboBox.ForeColor = UiPalette.Ink;
        comboBox.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
        comboBox.DrawMode = DrawMode.OwnerDrawFixed;
        comboBox.ItemHeight = Math.Max(26, TextRenderer.MeasureText("示例", comboBox.Font).Height + 8);
        comboBox.IntegralHeight = false;
        comboBox.DropDownHeight = comboBox.ItemHeight * 8;
        comboBox.DrawItem += DrawComboBoxItem;
    }

    public static void ApplyTextBox(TextBox textBox)
    {
        ArgumentNullException.ThrowIfNull(textBox);
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.BackColor = UiPalette.Card;
        textBox.ForeColor = UiPalette.Ink;
    }

    public static void ApplyNumericUpDown(NumericUpDown numericUpDown)
    {
        ArgumentNullException.ThrowIfNull(numericUpDown);
        numericUpDown.BorderStyle = BorderStyle.FixedSingle;
        numericUpDown.BackColor = UiPalette.Card;
        numericUpDown.ForeColor = UiPalette.Ink;
    }

    public static void ApplyListBox(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);
        listBox.BorderStyle = BorderStyle.FixedSingle;
        listBox.BackColor = UiPalette.Card;
        listBox.ForeColor = UiPalette.Ink;
    }

    public static void ApplyRichTextBox(RichTextBox richTextBox)
    {
        ArgumentNullException.ThrowIfNull(richTextBox);
        richTextBox.BorderStyle = BorderStyle.FixedSingle;
        richTextBox.BackColor = UiPalette.Card;
        richTextBox.ForeColor = UiPalette.Ink;
    }

    public static void ApplyDataGridView(DataGridView grid)
    {
        ArgumentNullException.ThrowIfNull(grid);

        grid.BackgroundColor = UiPalette.Card;
        grid.BorderStyle = BorderStyle.None;
        grid.RowHeadersVisible = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.GridColor = GridLine;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing;
        grid.ColumnHeadersHeight = 32;
        grid.RowTemplate.Height = 30;
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = UiPalette.Card,
            ForeColor = UiPalette.Ink,
            SelectionBackColor = UiPalette.AccentSoft,
            SelectionForeColor = UiPalette.Ink,
            Padding = new Padding(10, 2, 10, 2),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
        };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = AlternateRow,
            ForeColor = UiPalette.Ink,
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = HeaderSurface,
            ForeColor = UiPalette.Ink,
            SelectionBackColor = HeaderSurface,
            SelectionForeColor = UiPalette.Ink,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Padding = new Padding(10, 4, 10, 4),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            WrapMode = DataGridViewTriState.False,
        };
        grid.Paint += DrawDataGridFrame;
    }

    private static void DrawComboBoxItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox comboBox || e.Index < 0 || e.Index >= comboBox.Items.Count)
        {
            return;
        }

        var selected = (e.State & DrawItemState.Selected) != 0;
        var enabled = (e.State & DrawItemState.Disabled) == 0 && comboBox.Enabled;
        using var background = new SolidBrush(selected ? UiPalette.AccentSoft : UiPalette.Card);
        e.Graphics.FillRectangle(background, e.Bounds);
        var textBounds = Rectangle.Inflate(e.Bounds, -10, 0);
        TextRenderer.DrawText(
            e.Graphics,
            comboBox.GetItemText(comboBox.Items[e.Index]),
            comboBox.Font,
            textBounds,
            enabled ? UiPalette.Ink : SystemColors.GrayText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        if ((e.State & DrawItemState.Focus) != 0)
        {
            e.DrawFocusRectangle();
        }
    }

    private static void DrawDataGridFrame(object? sender, PaintEventArgs e)
    {
        if (sender is not DataGridView grid || grid.ClientSize.Width < 4 || grid.ClientSize.Height < 4)
        {
            return;
        }

        var bounds = grid.ClientRectangle;
        bounds.Inflate(-1, -1);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedGeometry.CreatePath(bounds, 8);
        using var border = new Pen(GridLine);
        e.Graphics.DrawPath(border, path);
    }
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

        using var path = RoundedGeometry.CreatePath(bounds, 14);
        using var background = new LinearGradientBrush(
            bounds,
            UiPalette.CardTop,
            UiPalette.CardBottom,
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

}

/// <summary>
/// A titleless version of the glass card for compact composite sections.
/// Unlike GroupBox it reserves no header band, so neighbouring pieces can be
/// combined into one intentionally thin surface.
/// </summary>
internal sealed class FrostedCardPanel : Panel
{
    public FrostedCardPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        Padding = new Padding(18, 14, 18, 14);
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
        var bounds = ClientRectangle;
        bounds.Inflate(-1, -1);
        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedGeometry.CreatePath(bounds, 18);
        using var background = new LinearGradientBrush(
            bounds,
            UiPalette.CardTop,
            UiPalette.CardBottom,
            LinearGradientMode.Vertical);
        using var border = new Pen(UiPalette.CardBorder, 1F);
        e.Graphics.FillPath(background, path);
        e.Graphics.DrawPath(border, path);
    }

}

/// <summary>
/// A compact self-painted button used across the shell.  WinForms' native
/// flat buttons are rectangular even when the surrounding cards are rounded;
/// this control keeps the interaction state while making its real hit area
/// and painted surface follow the same geometry.
/// </summary>
internal sealed class RoundedButton : Button
{
    private bool _hovered;
    private bool _pressed;

    public RoundedButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Opaque |
            ControlStyles.UserPaint |
            ControlStyles.CacheText,
            true);

        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        CornerRadius = 9;
        BorderColor = Color.Empty;
        HoverBackColor = Color.Empty;
        PressedBackColor = Color.Empty;
    }

    public int CornerRadius { get; set; }

    public int BorderThickness { get; set; }

    public Color BorderColor { get; set; }

    public Color HoverBackColor { get; set; }

    public Color PressedBackColor { get; set; }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = false;
            Invalidate();
        }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Always erase our entire double buffer before drawing a rounded
        // surface.  Button's native transparent-background route leaves the
        // previous frame in the four outer corners on some WinForms builds.
        e.Graphics.Clear(FindOpaqueAncestorColor());
        var bounds = ClientRectangle;
        bounds.Inflate(-1, -1);
        if (bounds.Width < 2 || bounds.Height < 2)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var surface = GetSurfaceColor();
        using var path = RoundedGeometry.CreatePath(bounds, CornerRadius);
        if (surface.A > 0)
        {
            using var fill = new SolidBrush(surface);
            e.Graphics.FillPath(fill, path);
        }

        if (BorderThickness > 0 && !BorderColor.IsEmpty && BorderColor.A > 0)
        {
            using var border = new Pen(BorderColor, BorderThickness);
            e.Graphics.DrawPath(border, path);
        }

        var textBounds = new Rectangle(
            Padding.Left,
            Padding.Top,
            Math.Max(0, Width - Padding.Horizontal),
            Math.Max(0, Height - Padding.Vertical));
        var textColor = Enabled ? ForeColor : SystemColors.GrayText;
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            textBounds,
            textColor,
            GetTextFormatFlags());

    }

    private Color GetSurfaceColor()
    {
        if (!Enabled)
        {
            return BackColor.A == 0 ? Color.Transparent : Color.FromArgb(128, BackColor);
        }

        if (_pressed && !PressedBackColor.IsEmpty)
        {
            return PressedBackColor;
        }

        if (_hovered && !HoverBackColor.IsEmpty)
        {
            return HoverBackColor;
        }

        return BackColor;
    }

    private TextFormatFlags GetTextFormatFlags()
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        flags |= TextAlign switch
        {
            ContentAlignment.TopLeft or ContentAlignment.MiddleLeft or ContentAlignment.BottomLeft => TextFormatFlags.Left,
            ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.HorizontalCenter,
        };
        flags |= TextAlign switch
        {
            ContentAlignment.TopLeft or ContentAlignment.TopCenter or ContentAlignment.TopRight => TextFormatFlags.Top,
            ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight => TextFormatFlags.Bottom,
            _ => TextFormatFlags.VerticalCenter,
        };
        return flags;
    }

    private Color FindOpaqueAncestorColor()
    {
        for (Control? current = Parent; current is not null; current = current.Parent)
        {
            if (current.BackColor.A == byte.MaxValue)
            {
                return current.BackColor;
            }
        }

        return UiPalette.Canvas;
    }

}

internal static class RoundedGeometry
{
    public static GraphicsPath CreatePath(Rectangle bounds, int radius)
    {
        var diameter = Math.Max(1, Math.Min(Math.Min(bounds.Width, bounds.Height), Math.Max(1, radius) * 2));
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
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwcpRound = 2;
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
            // Let Windows 11 own the top-level shadow and rounded frame.
            // This is DPI-aware and avoids the clipping artifacts caused by
            // assigning a manual Region to a native Form.
            var cornerPreference = DwmwcpRound;
            _ = DwmSetWindowAttribute(form.Handle, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));

            // Mica is the supported Windows 11 desktop backdrop.  The card
            // renderer remains the visual fallback if this attribute is not
            // present (for example on Windows 10).
            var backdrop = DwmsbtMainWindow;
            _ = DwmSetWindowAttribute(form.Handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

            var darkMode = UiPalette.IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
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
