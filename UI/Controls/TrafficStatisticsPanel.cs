using System.ComponentModel;

namespace WFly.UI.Controls;

/// <summary>
/// A full-width home-page traffic summary. It deliberately receives already
/// classified counters rather than trying to infer proxy/direct traffic in the
/// UI, because an unavailable controller must not be displayed as zero traffic.
/// </summary>
[DesignerCategory("Code")]
internal sealed class TrafficStatisticsPanel : Panel
{
    private static readonly Color ProxyColor = Color.FromArgb(54, 127, 245);
    private static readonly Color DirectColor = Color.FromArgb(31, 160, 111);
    private readonly Label[] _proxyValues = new Label[5];
    private readonly Label[] _directValues = new Label[5];
    private readonly Label _metricHeader;
    private readonly Label _proxyHeader;
    private readonly Label _directHeader;
    private bool _compact;

    public TrafficStatisticsPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        BackColor = UiPalette.Card;
        ForeColor = UiPalette.Ink;
        Padding = new Padding(14, 12, 14, 14);
        Margin = Padding.Empty;
        MinimumSize = new Size(360, 180);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var title = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "流量统计",
            ForeColor = UiPalette.Ink,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 6,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiPalette.CardBorder,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36F));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        for (var row = 1; row <= 5; row++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));
        }

        _metricHeader = CreateHeaderLabel("流量指标", UiPalette.MutedInk, ContentAlignment.MiddleLeft);
        _proxyHeader = CreateHeaderLabel("节点（代理）", ProxyColor, ContentAlignment.MiddleRight);
        _directHeader = CreateHeaderLabel("直连", DirectColor, ContentAlignment.MiddleRight);
        grid.Controls.Add(_metricHeader, 0, 0);
        grid.Controls.Add(_proxyHeader, 1, 0);
        grid.Controls.Add(_directHeader, 2, 0);

        string[] metricNames = ["上传速度", "下载速度", "活动连接", "累计上传", "累计下载"];
        for (var index = 0; index < metricNames.Length; index++)
        {
            var row = index + 1;
            grid.Controls.Add(CreateMetricLabel(metricNames[index]), 0, row);

            var proxy = CreateValueLabel(ProxyColor);
            _proxyValues[index] = proxy;
            grid.Controls.Add(proxy, 1, row);

            var direct = CreateValueLabel(DirectColor);
            _directValues[index] = direct;
            grid.Controls.Add(direct, 2, row);
        }

        root.Controls.Add(title, 0, 0);
        root.Controls.Add(grid, 0, 1);
        Controls.Add(root);
        RenderEmpty();
    }

    /// <summary>
    /// The last value rendered by this panel. A <see langword="null"/> value
    /// means no sampler/controller data has been supplied yet.
    /// </summary>
    [Browsable(false)]
    public TrafficStatisticsSnapshot? LastSnapshot { get; private set; }

    /// <summary>
    /// Refreshes all ten proxy/direct values. It is safe to call from a worker
    /// continuation; values are marshalled to the control's UI thread when it
    /// has a WinForms handle.
    /// </summary>
    public void SetSnapshot(TrafficStatisticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action<TrafficStatisticsSnapshot>(SetSnapshot), snapshot);
            }
            catch (InvalidOperationException)
            {
                // The form can close between the worker completing and the
                // queued UI update. Nothing needs to be restored here.
            }

            return;
        }

        LastSnapshot = snapshot.Normalize();
        Render(LastSnapshot);
    }

    /// <summary>Returns the display to an unavailable state without changing any sampler state.</summary>
    public void ClearSnapshot()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(ClearSnapshot));
            }
            catch (InvalidOperationException)
            {
                // See SetSnapshot.
            }

            return;
        }

        LastSnapshot = null;
        RenderEmpty();
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        // MinimumSize may raise Resize while this control's constructor is
        // still assigning its header labels. Defer responsive text updates
        // until the visual tree has been constructed.
        if (_metricHeader is null || _proxyHeader is null)
        {
            return;
        }

        var compact = ClientSize.Width < 540;
        if (_compact == compact)
        {
            return;
        }

        _compact = compact;
        _metricHeader.Text = compact ? "指标" : "流量指标";
        _proxyHeader.Text = compact ? "代理" : "节点（代理）";
    }

    private static Label CreateHeaderLabel(string text, Color color, ContentAlignment alignment) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        TextAlign = alignment,
        ForeColor = color,
        BackColor = UiPalette.AccentSoft,
        Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold),
        Padding = new Padding(8, 0, 8, 0),
        Margin = new Padding(1),
    };

    private static Label CreateMetricLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = UiPalette.MutedInk,
        BackColor = UiPalette.Card,
        Padding = new Padding(8, 0, 6, 0),
        Margin = new Padding(1),
    };

    private static Label CreateValueLabel(Color color) => new()
    {
        Text = "—",
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleRight,
        ForeColor = color,
        BackColor = UiPalette.Card,
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        Padding = new Padding(6, 0, 8, 0),
        Margin = new Padding(1),
    };

    private void Render(TrafficStatisticsSnapshot snapshot)
    {
        _proxyValues[0].Text = FormatRate(snapshot.ProxyUploadBytesPerSecond);
        _proxyValues[1].Text = FormatRate(snapshot.ProxyDownloadBytesPerSecond);
        _proxyValues[2].Text = FormatConnectionCount(snapshot.ProxyActiveConnections);
        _proxyValues[3].Text = FormatBytes(snapshot.ProxyUploadTotalBytes);
        _proxyValues[4].Text = FormatBytes(snapshot.ProxyDownloadTotalBytes);

        _directValues[0].Text = FormatRate(snapshot.DirectUploadBytesPerSecond);
        _directValues[1].Text = FormatRate(snapshot.DirectDownloadBytesPerSecond);
        _directValues[2].Text = FormatConnectionCount(snapshot.DirectActiveConnections);
        _directValues[3].Text = FormatBytes(snapshot.DirectUploadTotalBytes);
        _directValues[4].Text = FormatBytes(snapshot.DirectDownloadTotalBytes);
    }

    private void RenderEmpty()
    {
        foreach (var label in _proxyValues.Concat(_directValues))
        {
            label.Text = "—";
        }
    }

    private static string FormatRate(long? bytesPerSecond)
    {
        if (bytesPerSecond is null)
        {
            return "—";
        }

        double value = Math.Max(0L, bytesPerSecond.Value);
        string[] units = ["B/s", "KB/s", "MB/s"];
        var unit = 0;
        while (value >= 1024D && unit < units.Length - 1)
        {
            value /= 1024D;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null)
        {
            return "—";
        }

        double value = Math.Max(0L, bytes.Value);
        string[] units = ["B", "KB", "MB", "GB"];
        var unit = 0;
        while (value >= 1024D && unit < units.Length - 1)
        {
            value /= 1024D;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private static string FormatConnectionCount(int? count) => count is null
        ? "—"
        : $"{Math.Max(0, count.Value)}";
}

/// <summary>
/// UI-ready traffic counters. Nullable fields mean the data source is not
/// currently able to classify that value; the panel renders those as an em dash
/// rather than inventing a zero.
/// </summary>
internal sealed record TrafficStatisticsSnapshot(
    DateTimeOffset CapturedAt,
    long? ProxyUploadBytesPerSecond,
    long? ProxyDownloadBytesPerSecond,
    int? ProxyActiveConnections,
    long? ProxyUploadTotalBytes,
    long? ProxyDownloadTotalBytes,
    long? DirectUploadBytesPerSecond,
    long? DirectDownloadBytesPerSecond,
    int? DirectActiveConnections,
    long? DirectUploadTotalBytes,
    long? DirectDownloadTotalBytes)
{
    internal TrafficStatisticsSnapshot Normalize() => this with
    {
        CapturedAt = CapturedAt == default ? DateTimeOffset.UtcNow : CapturedAt,
        ProxyUploadBytesPerSecond = NormalizeBytes(ProxyUploadBytesPerSecond),
        ProxyDownloadBytesPerSecond = NormalizeBytes(ProxyDownloadBytesPerSecond),
        ProxyActiveConnections = NormalizeConnections(ProxyActiveConnections),
        ProxyUploadTotalBytes = NormalizeBytes(ProxyUploadTotalBytes),
        ProxyDownloadTotalBytes = NormalizeBytes(ProxyDownloadTotalBytes),
        DirectUploadBytesPerSecond = NormalizeBytes(DirectUploadBytesPerSecond),
        DirectDownloadBytesPerSecond = NormalizeBytes(DirectDownloadBytesPerSecond),
        DirectActiveConnections = NormalizeConnections(DirectActiveConnections),
        DirectUploadTotalBytes = NormalizeBytes(DirectUploadTotalBytes),
        DirectDownloadTotalBytes = NormalizeBytes(DirectDownloadTotalBytes),
    };

    private static long? NormalizeBytes(long? value) => value is null ? null : Math.Max(0L, value.Value);

    private static int? NormalizeConnections(int? value) => value is null ? null : Math.Max(0, value.Value);
}
