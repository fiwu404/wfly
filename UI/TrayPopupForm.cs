using WFly.Models;
using WFly.UI.Controls;
using System.Runtime.InteropServices;

namespace WFly.UI;

/// <summary>
/// Custom-drawn tray menu. It deliberately avoids the platform ToolStrip menu
/// so the tray interactions use the same glass cards and rounded controls as
/// the main shell.
/// </summary>
internal sealed class TrayPopupForm : Form
{
    private readonly System.Windows.Forms.Timer _dismissTimer = new() { Interval = 80 };
    private bool _outsideClickArmed;
    private bool _activationObserved;

    public TrayPopupForm(
        AppSettings settings,
        IReadOnlyList<NodeGroup> groups,
        IReadOnlyList<ProxyNode> nodes,
        Action<ProxyNode> selectNode,
        Action<ProxyMode> selectProxyMode,
        Action<ProxyRoutingMode> selectRoutingMode,
        Action showWindow,
        Action exit)
    {
        ArgumentNullException.ThrowIfNull(settings);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(244, 420);
        MinimumSize = Size;
        MaximumSize = Size;
        BackColor = UiPalette.Canvas;
        Padding = new Padding(1);
        Font = new Font("Microsoft YaHei UI", 9F);
        Deactivate += (_, _) => Close();
        Shown += OnPopupShown;
        _dismissTimer.Tick += CheckDismiss;

        var card = new FrostedCardPanel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(12, 10, 12, 10),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = Color.Transparent,
            Padding = Padding.Empty,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(CreateTitle("WFly"), 0, 0);
        layout.Controls.Add(CreateModeRow(
            [(ProxyMode.SystemProxy, "系统"), (ProxyMode.Off, "关闭"), (ProxyMode.Tun, "TUN")],
            mode => { selectProxyMode(mode); Close(); },
            settings.ProxyMode), 0, 1);
        layout.Controls.Add(CreateModeRow(
            [(ProxyRoutingMode.Rules, "规则"), (ProxyRoutingMode.Global, "全局"), (ProxyRoutingMode.Direct, "直连")],
            mode => { selectRoutingMode(mode); Close(); },
            settings.RoutingMode), 0, 2);

        var nodeList = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 7, 0, 7),
            Margin = Padding.Empty,
        };
        var enabledNodes = nodes.Where(node => node.IsEnabled).ToArray();
        if (enabledNodes.Length == 0)
        {
            nodeList.Controls.Add(CreateMutedLabel("暂无启用节点"));
        }
        else
        {
            foreach (var group in groups)
            {
                var groupNodes = enabledNodes.Where(node => string.Equals(node.GroupId, group.Id, StringComparison.Ordinal)).ToArray();
                if (groupNodes.Length == 0)
                {
                    continue;
                }

                nodeList.Controls.Add(CreateMutedLabel(group.Name));
                foreach (var node in groupNodes)
                {
                    var button = new RoundedButton
                    {
                        Text = node.Name,
                        AutoSize = false,
                        Size = new Size(210, 30),
                        TextAlign = ContentAlignment.MiddleLeft,
                        Padding = new Padding(10, 0, 10, 0),
                        BackColor = string.Equals(node.Id, settings.SelectedNodeId, StringComparison.Ordinal) ? UiPalette.AccentSoft : UiPalette.Card,
                        ForeColor = string.Equals(node.Id, settings.SelectedNodeId, StringComparison.Ordinal) ? UiPalette.Accent : UiPalette.Ink,
                        HoverBackColor = UiPalette.Hover,
                        PressedBackColor = UiPalette.AccentSoft,
                        BorderColor = UiPalette.CardBorder,
                        CornerRadius = 9,
                        Margin = new Padding(0, 2, 0, 2),
                    };
                    button.Click += (_, _) => { selectNode(node); Close(); };
                    nodeList.Controls.Add(button);
                }
            }
        }

        layout.Controls.Add(nodeList, 0, 3);
        var showButton = CreateActionButton("显示主窗口", true);
        showButton.Click += (_, _) => { showWindow(); Close(); };
        layout.Controls.Add(showButton, 0, 4);
        var exitButton = CreateActionButton("退出 WFly", false);
        exitButton.Click += (_, _) => { exit(); Close(); };
        layout.Controls.Add(exitButton, 0, 5);
        card.Controls.Add(layout);
        Controls.Add(card);
    }

    protected override void OnSizeChanged(EventArgs eventArgs)
    {
        base.OnSizeChanged(eventArgs);
        if (ClientSize.Width <= 1 || ClientSize.Height <= 1)
        {
            return;
        }

        using var path = RoundedGeometry.CreatePath(new Rectangle(Point.Empty, ClientSize), 16);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dismissTimer.Stop();
            _dismissTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnPopupShown(object? sender, EventArgs eventArgs)
    {
        Activate();
        NativeMethods.SetForegroundWindow(Handle);
        _activationObserved = NativeMethods.GetForegroundWindow() == Handle || ContainsFocus;
        _dismissTimer.Start();
    }

    private void CheckDismiss(object? sender, EventArgs eventArgs)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        var pressed = MouseButtons != MouseButtons.None;
        if (!_outsideClickArmed)
        {
            // The right mouse button used to open the popup may still be held.
            // Arm dismissal only after that opening click has been released.
            _outsideClickArmed = !pressed;
        }
        else if (pressed && !Bounds.Contains(Cursor.Position))
        {
            Close();
            return;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == Handle || ContainsFocus)
        {
            _activationObserved = true;
        }
        else if (_activationObserved)
        {
            Close();
        }
    }

    private Label CreateTitle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(Font.FontFamily, 14F, FontStyle.Bold),
        ForeColor = UiPalette.Ink,
        Margin = new Padding(2, 0, 0, 8),
    };

    private FlowLayoutPanel CreateModeRow<T>(IReadOnlyList<(T Value, string Text)> options, Action<T> select, T selected)
        where T : struct, Enum
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6),
        };
        foreach (var (value, text) in options)
        {
            var isSelected = EqualityComparer<T>.Default.Equals(value, selected);
            var button = new RoundedButton
            {
                Text = text,
                AutoSize = false,
                Size = new Size(68, 30),
                Margin = new Padding(0, 0, 4, 0),
                BackColor = isSelected ? UiPalette.Accent : UiPalette.Hover,
                ForeColor = isSelected ? Color.White : UiPalette.Ink,
                HoverBackColor = UiPalette.AccentSoft,
                PressedBackColor = UiPalette.AccentSoft,
                BorderColor = UiPalette.CardBorder,
                CornerRadius = 9,
            };
            button.Click += (_, _) => select(value);
            row.Controls.Add(button);
        }

        return row;
    }

    private RoundedButton CreateActionButton(string text, bool primary) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Top,
        Height = 34,
        Margin = new Padding(0, 3, 0, 0),
        BackColor = primary ? UiPalette.Accent : UiPalette.Hover,
        ForeColor = primary ? Color.White : UiPalette.Ink,
        HoverBackColor = primary ? Color.FromArgb(53, 94, 192) : UiPalette.AccentSoft,
        PressedBackColor = UiPalette.AccentSoft,
        BorderColor = UiPalette.CardBorder,
        CornerRadius = 8,
    };

    private Label CreateMutedLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Width = 210,
        Height = 24,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = UiPalette.MutedInk,
        Margin = new Padding(3, 0, 0, 0),
    };

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(nint windowHandle);
    }
}
