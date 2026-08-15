using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace WFly.UI.Controls;

internal sealed record TrafficChartSample(
    DateTimeOffset Timestamp,
    long ProxyUploadBytesPerSecond,
    long ProxyDownloadBytesPerSecond,
    long DirectUploadBytesPerSecond,
    long DirectDownloadBytesPerSecond);

/// <summary>
/// Keeps a bounded in-memory traffic window and renders four smooth, hoverable curves.
/// </summary>
internal sealed class TrafficChartControl : Control
{
    private const int MaximumSamples = 120;
    private readonly List<TrafficChartSample> _samples = [];
    private readonly ToolTip _toolTip = new();
    private int _hoveredSample = -1;

    public TrafficChartControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        BackColor = Color.White;
        MinimumSize = new Size(320, 190);
    }

    [Browsable(false)]
    public IReadOnlyList<TrafficChartSample> Samples => _samples;

    public void Append(TrafficChartSample sample)
    {
        _samples.Add(sample);
        if (_samples.Count > MaximumSamples)
        {
            _samples.RemoveRange(0, _samples.Count - MaximumSamples);
        }

        Invalidate();
    }

    public void Clear()
    {
        _samples.Clear();
        _hoveredSample = -1;
        _toolTip.Hide(this);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(BackColor);

        var bounds = new Rectangle(44, 18, Math.Max(1, Width - 62), Math.Max(1, Height - 54));
        DrawGrid(graphics, bounds);
        if (_samples.Count == 0)
        {
            TextRenderer.DrawText(
                graphics,
                "等待流量数据（不会伪造曲线）",
                Font,
                bounds,
                Color.FromArgb(118, 128, 145),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            DrawLegend(graphics, bounds.Bottom + 12);
            return;
        }

        var maximum = Math.Max(1L, _samples
            .SelectMany(sample => new[]
            {
                sample.ProxyUploadBytesPerSecond,
                sample.ProxyDownloadBytesPerSecond,
                sample.DirectUploadBytesPerSecond,
                sample.DirectDownloadBytesPerSecond,
            })
            .Max());

        DrawYAxis(graphics, bounds, maximum);
        DrawSeries(graphics, bounds, maximum, sample => sample.ProxyUploadBytesPerSecond, Color.FromArgb(238, 105, 89));
        DrawSeries(graphics, bounds, maximum, sample => sample.ProxyDownloadBytesPerSecond, Color.FromArgb(54, 127, 245));
        DrawSeries(graphics, bounds, maximum, sample => sample.DirectUploadBytesPerSecond, Color.FromArgb(151, 103, 219));
        DrawSeries(graphics, bounds, maximum, sample => sample.DirectDownloadBytesPerSecond, Color.FromArgb(31, 160, 111));
        DrawLegend(graphics, bounds.Bottom + 12);

        if (_hoveredSample >= 0 && _hoveredSample < _samples.Count)
        {
            var x = GetX(bounds, _hoveredSample, _samples.Count);
            using var marker = new Pen(Color.FromArgb(88, 94, 110), 1F) { DashStyle = DashStyle.Dot };
            graphics.DrawLine(marker, x, bounds.Top, x, bounds.Bottom);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_samples.Count == 0)
        {
            return;
        }

        var bounds = new Rectangle(44, 18, Math.Max(1, Width - 62), Math.Max(1, Height - 54));
        var relative = Math.Clamp(e.X - bounds.Left, 0, bounds.Width);
        var index = _samples.Count == 1
            ? 0
            : (int)Math.Round(relative / (double)bounds.Width * (_samples.Count - 1));
        index = Math.Clamp(index, 0, _samples.Count - 1);
        if (_hoveredSample != index)
        {
            _hoveredSample = index;
            Invalidate();
        }

        var sample = _samples[index];
        _toolTip.Show(
            $"{sample.Timestamp.LocalDateTime:HH:mm:ss}\n代理 ↑ {FormatRate(sample.ProxyUploadBytesPerSecond)}  ↓ {FormatRate(sample.ProxyDownloadBytesPerSecond)}\n直连 ↑ {FormatRate(sample.DirectUploadBytesPerSecond)}  ↓ {FormatRate(sample.DirectDownloadBytesPerSecond)}",
            this,
            e.Location.X + 14,
            e.Location.Y + 14,
            1_800);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredSample = -1;
        _toolTip.Hide(this);
        Invalidate();
    }

    private void DrawGrid(Graphics graphics, Rectangle bounds)
    {
        using var gridPen = new Pen(Color.FromArgb(229, 234, 241));
        using var borderPen = new Pen(Color.FromArgb(202, 211, 223));
        for (var row = 0; row <= 4; row++)
        {
            var y = bounds.Top + bounds.Height * row / 4;
            graphics.DrawLine(gridPen, bounds.Left, y, bounds.Right, y);
        }

        graphics.DrawRectangle(borderPen, bounds);
    }

    private void DrawYAxis(Graphics graphics, Rectangle bounds, long maximum)
    {
        for (var row = 0; row <= 4; row++)
        {
            var value = maximum * (4 - row) / 4;
            var label = FormatRate(value);
            var size = TextRenderer.MeasureText(label, Font);
            TextRenderer.DrawText(
                graphics,
                label,
                Font,
                new Point(Math.Max(0, bounds.Left - size.Width - 6), bounds.Top + bounds.Height * row / 4 - size.Height / 2),
                Color.FromArgb(105, 115, 130),
                TextFormatFlags.NoPadding);
        }
    }

    private void DrawSeries(
        Graphics graphics,
        Rectangle bounds,
        long maximum,
        Func<TrafficChartSample, long> selector,
        Color color)
    {
        if (_samples.Count == 0)
        {
            return;
        }

        var points = _samples
            .Select((sample, index) => new PointF(
                GetX(bounds, index, _samples.Count),
                bounds.Bottom - (float)(Math.Clamp(selector(sample), 0, maximum) / (double)maximum * bounds.Height)))
            .ToArray();
        using var pen = new Pen(color, 2F) { LineJoin = LineJoin.Round };
        if (points.Length == 1)
        {
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, points[0].X - 2, points[0].Y - 2, 4, 4);
        }
        else if (points.Length == 2)
        {
            graphics.DrawLines(pen, points);
        }
        else
        {
            graphics.DrawCurve(pen, points, 0.42F);
        }
    }

    private void DrawLegend(Graphics graphics, int y)
    {
        var legend = new[]
        {
            ("代理上传", Color.FromArgb(238, 105, 89)),
            ("代理下载", Color.FromArgb(54, 127, 245)),
            ("直连上传", Color.FromArgb(151, 103, 219)),
            ("直连下载", Color.FromArgb(31, 160, 111)),
        };
        var x = 44;
        foreach (var (text, color) in legend)
        {
            using var brush = new SolidBrush(color);
            graphics.FillRectangle(brush, x, y + 3, 9, 9);
            TextRenderer.DrawText(graphics, text, Font, new Point(x + 14, y), ForeColor, TextFormatFlags.NoPadding);
            x += TextRenderer.MeasureText(text, Font).Width + 34;
        }
    }

    private static int GetX(Rectangle bounds, int index, int count) =>
        count <= 1 ? bounds.Left : bounds.Left + bounds.Width * index / (count - 1);

    private static string FormatRate(long bytesPerSecond)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        var value = Math.Max(0, bytesPerSecond);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}
