using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WFly.Models;
using WFly.Services;
using WFly.UI.Controls;

namespace WFly.UI;

/// <summary>
/// The Windows desktop shell.  Each page is intentionally a real WinForms
/// control instead of a faux navigation view: data remains in the portable
/// data directory and long-running network actions stay user initiated.
/// </summary>
internal sealed partial class DashboardForm : Form
{
    private static readonly string[] NavigationItems =
    [
        "首页", "节点", "节点组", "连接", "规则", "日志", "测试", "设置",
    ];

    private readonly AppPaths _paths;
    private readonly InstalledCoreStore _installedCoreStore;
    private readonly SettingsStore _settingsStore;
    private readonly NodeGroupStore _nodeGroupStore;
    private readonly ProxyNodeStore _proxyNodeStore;
    private readonly RuleSetStore _ruleSetStore;
    private readonly CoreCatalogService _catalogService;
    private readonly CoreInstaller _installer;
    private readonly GeoFileService _geoFileService;
    private readonly SubscriptionProfileService _subscriptionProfileService;
    private readonly ProfileGenerationService _profileGenerationService;
    private readonly CoreProcessService _processService;
    private readonly InMemoryLogStore _logStore;
    private readonly NetworkDiagnosticsService _networkDiagnosticsService;
    private readonly SiteLatencyTestService _siteLatencyTestService;
    private readonly NodeSpeedTestService _nodeSpeedTestService;
    private readonly ClashApiClient _clashApiClient;
    private readonly WindowsSystemProxyService _systemProxyService;
    private readonly bool _resumeTunAfterElevation;
    // The busy guard in RefreshTrafficAsync prevents controller calls overlapping.
    private readonly System.Windows.Forms.Timer _trafficTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _subscriptionTimer = new() { Interval = 5 * 60 * 1_000 };
    private readonly Dictionary<string, Control> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _navigationButtons = new(StringComparer.Ordinal);
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill, BackColor = UiPalette.Canvas };
    private readonly Label _statusLabel = new();
    private readonly NotifyIcon _trayIcon = new();
    private TrayPopupForm? _trayPopup;

    private AppSettings _settings = new();
    private IReadOnlyList<NodeGroup> _groups = [];
    private IReadOnlyList<ProxyNode> _currentNodes = [];
    private IReadOnlyList<ProxyNode> _allNodes = [];
    private IReadOnlyList<RuleSet> _ruleSets = [];
    private string _currentPage = "首页";
    private CancellationTokenSource? _operationCancellation;
    private bool _operationBusy;
    private bool _isLoading;
    private bool _closeCleanupInProgress;
    private bool _allowCloseAfterCleanup;
    private bool _exitRequestedFromTray;
    private bool _trayHintShown;
    private bool _trafficTickBusy;
    private bool _isInteractiveResize;
    private bool _restartTrafficTimerAfterResize;
    private bool _proxyModeTransitionBusy;
    private bool _routingModeTransitionBusy;
    private bool _coreDownloadProgressActive;
    private bool _isCompactWindow;
    private bool _isSynchronizingCompactHome;
    private Size _regularMinimumSize;
    private Size _regularClientSize;
    private string? _runningCoreId;
    private int? _runningMixedProxyPort;
    private readonly Dictionary<string, ConnectionTrafficCounter> _previousTrafficConnections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _coreDownloadStates = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _previousTrafficCounterAt;
    private long _proxyUploadTotal;
    private long _proxyDownloadTotal;
    private long _directUploadTotal;
    private long _directDownloadTotal;
    private HashSet<string> _knownConnectionIds = new(StringComparer.Ordinal);

    // 首页
    private Label? _homeNodeLabel;
    private Label? _homeCoreLabel;
    private Label? _homeRunningLabel;
    private Label? _homeIpLabel;
    private Label? _homeIpTypeLabel;
    private Label? _homeGoogleLatencyLabel;
    private Label? _homeConnectionCountLabel;
    private ProxyModeSelector? _proxyModeSelector;
    private ProxyModeSelector? _routingModeSelector;
    private TrafficChartControl? _trafficChart;
    private TrafficStatisticsPanel? _trafficStatistics;
    private Control? _mainShell;
    private Control? _compactShell;
    private RoundedButton? _themeToggleButton;
    private RoundedButton? _compactToggleButton;
    private ComboBox? _compactNodeSelector;
    private Label? _compactNodeLabel;
    private Label? _compactCoreLabel;
    private Label? _compactStatusLabel;
    private Label? _compactIpLabel;
    private Label? _compactIpTypeLabel;
    private Label? _compactGoogleLatencyLabel;
    private ProxyModeSelector? _compactProxyModeSelector;
    private ProxyModeSelector? _compactRoutingModeSelector;

    // 节点组 / 节点（由 partial 文件建立）
    private DataGridView? _groupGrid;
    private ComboBox? _nodeGroupSelector;
    private DataGridView? _nodeGrid;
    private ContextMenuStrip? _nodeTestMenu;

    // 规则
    private ListBox? _ruleSetList;
    private DataGridView? _ruleGrid;
    private RichTextBox? _ruleJsonBox;
    private RuleSet? _activeRuleSet;

    // 日志、测试、连接、设置
    private RichTextBox? _logBox;
    private DataGridView? _testGrid;
    private CheckBox? _testThroughProxyCheckBox;
    private DataGridView? _connectionGrid;
    private ComboBox? _settingsCoreSelector;
    private DataGridView? _coreGrid;
    private readonly Dictionary<string, int> _coreDownloadPercentages = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object GeoFilesGridRowTag = new();
    private bool _geoFilesDownloadActive;
    private int _geoFilesDownloadPercent;
    private NumericUpDown? _mixedPortInput;
    private TextBox? _tunNameInput;
    private CheckBox? _geoFilesSmartRoutingCheckBox;


    public DashboardForm(
        AppPaths paths,
        InstalledCoreStore installedCoreStore,
        SettingsStore settingsStore,
        NodeGroupStore nodeGroupStore,
        ProxyNodeStore proxyNodeStore,
        RuleSetStore ruleSetStore,
        CoreCatalogService catalogService,
        CoreInstaller installer,
        GeoFileService geoFileService,
        SubscriptionProfileService subscriptionProfileService,
        ProfileGenerationService profileGenerationService,
        CoreProcessService processService,
        InMemoryLogStore logStore,
        NetworkDiagnosticsService networkDiagnosticsService,
        SiteLatencyTestService siteLatencyTestService,
        NodeSpeedTestService nodeSpeedTestService,
        ClashApiClient clashApiClient,
        WindowsSystemProxyService systemProxyService,
        bool resumeTunAfterElevation)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _installedCoreStore = installedCoreStore ?? throw new ArgumentNullException(nameof(installedCoreStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _nodeGroupStore = nodeGroupStore ?? throw new ArgumentNullException(nameof(nodeGroupStore));
        _proxyNodeStore = proxyNodeStore ?? throw new ArgumentNullException(nameof(proxyNodeStore));
        _ruleSetStore = ruleSetStore ?? throw new ArgumentNullException(nameof(ruleSetStore));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _geoFileService = geoFileService ?? throw new ArgumentNullException(nameof(geoFileService));
        _subscriptionProfileService = subscriptionProfileService ?? throw new ArgumentNullException(nameof(subscriptionProfileService));
        _profileGenerationService = profileGenerationService ?? throw new ArgumentNullException(nameof(profileGenerationService));
        _processService = processService ?? throw new ArgumentNullException(nameof(processService));
        _logStore = logStore ?? throw new ArgumentNullException(nameof(logStore));
        _networkDiagnosticsService = networkDiagnosticsService ?? throw new ArgumentNullException(nameof(networkDiagnosticsService));
        _siteLatencyTestService = siteLatencyTestService ?? throw new ArgumentNullException(nameof(siteLatencyTestService));
        _nodeSpeedTestService = nodeSpeedTestService ?? throw new ArgumentNullException(nameof(nodeSpeedTestService));
        _clashApiClient = clashApiClient ?? throw new ArgumentNullException(nameof(clashApiClient));
        _systemProxyService = systemProxyService ?? throw new ArgumentNullException(nameof(systemProxyService));
        _resumeTunAfterElevation = resumeTunAfterElevation;

        InitializeComponent();
        InitializeTrayIcon();
        _processService.LogReceived += OnCoreLogReceived;
        _processService.RunningStateChanged += OnCoreRunningStateChanged;
        _logStore.EntryAdded += OnRuntimeLogAdded;
        _trafficTimer.Tick += async (_, _) => await RefreshTrafficAsync();
        _subscriptionTimer.Tick += async (_, _) => await RefreshDueSubscriptionsAsync();
        Shown += async (_, _) => await LoadStateAsync();
        FormClosing += OnFormClosing;
        ResizeBegin += OnInteractiveResizeBegin;
        ResizeEnd += OnInteractiveResizeEnd;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _operationCancellation?.Dispose();
            _trafficTimer.Dispose();
            _subscriptionTimer.Dispose();
            _processService.LogReceived -= OnCoreLogReceived;
            _processService.RunningStateChanged -= OnCoreRunningStateChanged;
            _logStore.EntryAdded -= OnRuntimeLogAdded;
            Resize -= OnWindowResize;
            ResizeBegin -= OnInteractiveResizeBegin;
            ResizeEnd -= OnInteractiveResizeEnd;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _nodeTestMenu?.Dispose();
            _trayPopup?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        Text = $"WFly {ProductInfo.Version}";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        // The dashboard deliberately has no document scroll bar.  Keep a
        // compact but real minimum height so its four live sections never
        // collapse into one another on a user resize.
        MinimumSize = new Size(1060, 832);
        ClientSize = new Size(1280, 852);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = UiPalette.Canvas;
        HandleCreated += (_, _) => WindowBackdrop.Apply(this);

        // The navigation stays on the left, separated by one drawn line.
        // SplitContainer keeps the divider draggable so users can choose the
        // content/navigation ratio that works for their screen.
        var root = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.None,
            IsSplitterFixed = false,
            SplitterWidth = 4,
            BackColor = UiPalette.Canvas,
        };
        var splitInitialized = false;
        root.SizeChanged += (_, _) =>
        {
            if (splitInitialized)
            {
                return;
            }

            // The home header contains two independent three-position
            // controls.  Preserve enough width for both rather than allowing
            // one control to expand across its sibling's table cell.
            const int contentMinimum = 840;
            const int navigationMinimum = 148;
            if (root.ClientSize.Width < contentMinimum + navigationMinimum + root.SplitterWidth)
            {
                return;
            }

            // Initialise after Dock has given SplitContainer a real width;
            // setting panel minimums or SplitterDistance on its default
            // design-time width would throw before the form can be shown.
            root.Panel1MinSize = navigationMinimum;
            root.Panel2MinSize = contentMinimum;
            var maximum = root.ClientSize.Width - contentMinimum - root.SplitterWidth;
            root.SplitterDistance = Math.Clamp(188, navigationMinimum, maximum);
            splitInitialized = true;
        };
        root.Panel1.BackColor = UiPalette.Canvas;
        root.Panel2.BackColor = UiPalette.Canvas;
        root.Panel1.Paint += (_, args) =>
        {
            using var divider = new Pen(UiPalette.CardBorder);
            var x = Math.Max(0, root.Panel1.ClientSize.Width - 1);
            args.Graphics.DrawLine(divider, x, 0, x, root.Panel1.ClientSize.Height);
        };

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiPalette.Canvas,
            Padding = new Padding(28, 6, 22, 14),
        };
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.Controls.Add(_pageHost, 0, 0);
        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = Color.FromArgb(97, 108, 124);
        _statusLabel.Margin = new Padding(0, 10, 0, 0);
        _statusLabel.Text = "正在读取本地数据…";
        main.Controls.Add(_statusLabel, 0, 1);

        var navigation = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiPalette.Canvas,
            Padding = new Padding(16, 8, 16, 18),
        };
        var navigationLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = NavigationItems.Length + 4,
            BackColor = UiPalette.Canvas,
        };
        navigationLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var brand = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(10, 0, 0, 14),
        };
        var brandIcon = new PictureBox
        {
            Image = AppIconFactory.Image,
            Size = new Size(28, 28),
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, 8, 0),
        };
        var brandText = new Label
        {
            AutoSize = true,
            Text = "WFly",
            ForeColor = UiPalette.Ink,
            Font = new Font(Font.FontFamily, 19F, FontStyle.Bold),
            Margin = new Padding(0, 1, 0, 0),
        };
        brand.Controls.Add(brandIcon);
        brand.Controls.Add(brandText);
        navigationLayout.Controls.Add(brand, 0, 0);

        for (var index = 0; index < NavigationItems.Length; index++)
        {
            var page = NavigationItems[index];
            navigationLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var button = new RoundedButton
            {
                Text = page,
                Dock = DockStyle.Top,
                Height = 44,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 0, 0),
                ForeColor = UiPalette.MutedInk,
                BackColor = UiPalette.Canvas,
                HoverBackColor = UiPalette.Hover,
                PressedBackColor = UiPalette.AccentSoft,
                CornerRadius = 10,
                TabStop = true,
                Margin = new Padding(0, 3, 0, 3),
                Font = new Font(Font.FontFamily, 10F, FontStyle.Regular),
            };
            button.Click += (_, _) => ShowPage(page);
            navigationLayout.Controls.Add(button, 0, index + 1);
            _navigationButtons.Add(page, button);
        }

        navigationLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        navigation.Controls.Add(navigationLayout);

        root.Panel1.Controls.Add(navigation);
        root.Panel2.Controls.Add(main);
        // The custom title bar is drawn above the shell. Reserve its height
        // explicitly so the navigation brand and the first dashboard card can
        // never slide underneath it at a different DPI.
        var shell = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiPalette.Canvas,
            Padding = new Padding(0, 32, 0, 0),
        };
        shell.Controls.Add(root);
        Controls.Add(shell);
        _mainShell = shell;
        _regularMinimumSize = MinimumSize;
        _regularClientSize = ClientSize;
        _compactShell = BuildCompactDashboard();
        _compactShell.Visible = false;
        Controls.Add(_compactShell);
        CreateWindowQuickActions();
        ShowPage("首页");
    }

    private Control BuildCompactDashboard()
    {
        var root = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiPalette.Canvas,
            Padding = new Padding(14, 42, 14, 14),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = UiPalette.Canvas,
            Padding = Padding.Empty,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var informationCard = new FrostedCardPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 11, 14, 10), Margin = new Padding(0, 0, 0, 8) };
        var informationLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = Color.Transparent };
        informationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        informationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _compactNodeLabel = AddValueRow(informationLayout, "节点", 0);
        _compactCoreLabel = AddValueRow(informationLayout, "内核", 1);
        _compactStatusLabel = AddValueRow(informationLayout, "状态", 2);
        informationCard.Controls.Add(informationLayout);
        layout.Controls.Add(informationCard, 0, 0);

        var nodeCard = new FrostedCardPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 8) };
        var nodeLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
        nodeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        nodeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        nodeLayout.Controls.Add(CreateSettingsLabel("节点选择"), 0, 0);
        _compactNodeSelector = UiControlTheme.CreateComboBox();
        _compactNodeSelector.Dock = DockStyle.Fill;
        _compactNodeSelector.DisplayMember = nameof(ProxyNode.Name);
        _compactNodeSelector.SelectedIndexChanged += async (_, _) => await SelectCompactNodeAsync();
        nodeLayout.Controls.Add(_compactNodeSelector, 1, 0);
        nodeCard.Controls.Add(nodeLayout);
        layout.Controls.Add(nodeCard, 0, 1);

        var proxyCard = CreateCompactSelectorCard("代理开关", ["系统代理", "关闭代理", "TUN 模式"], out _compactProxyModeSelector);
        _compactProxyModeSelector.ModeChanged += (_, _) => SelectProxyModeFromCompact();
        layout.Controls.Add(proxyCard, 0, 2);

        var routingCard = CreateCompactSelectorCard("代理规则", ["规则", "全局", "直连"], out _compactRoutingModeSelector);
        _compactRoutingModeSelector.SelectedIndexChanged += (_, _) => SelectRoutingModeFromCompact();
        layout.Controls.Add(routingCard, 0, 3);

        var egressCard = new FrostedCardPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 10), Margin = Padding.Empty };
        var egressLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent };
        egressLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 33F));
        egressLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 33F));
        egressLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 34F));
        egressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        egressLayout.Controls.Add(CreateInlineValue("出口 IP", out _compactIpLabel), 0, 0);
        egressLayout.Controls.Add(CreateInlineValue("IP 类型", out _compactIpTypeLabel), 0, 1);
        egressLayout.Controls.Add(CreateInlineValue("Google", out _compactGoogleLatencyLabel), 0, 2);
        var checkButton = CreateSecondaryButton("检测出口");
        checkButton.AutoSize = false;
        checkButton.Size = new Size(100, 30);
        checkButton.Anchor = AnchorStyles.Left;
        checkButton.Click += async (_, _) => await CheckEgressAsync();
        egressLayout.Controls.Add(checkButton, 0, 3);
        egressCard.Controls.Add(egressLayout);
        layout.Controls.Add(egressCard, 0, 4);

        root.Controls.Add(layout);
        return root;
    }

    private FrostedCardPanel CreateCompactSelectorCard(string title, string[] labels, out ProxyModeSelector selector)
    {
        var card = new FrostedCardPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 8), Margin = new Padding(0, 0, 0, 8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var heading = new Label { Text = title, AutoSize = true, ForeColor = UiPalette.Ink, Font = new Font(Font.FontFamily, 9F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) };
        selector = new ProxyModeSelector { Dock = DockStyle.Fill, BackColor = UiPalette.Card, ForeColor = UiPalette.Ink, Labels = labels, MinimumSize = new Size(0, 45) };
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(selector, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private void CreateWindowQuickActions()
    {
        var titleBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 32,
            BackColor = UiPalette.Canvas,
            Padding = new Padding(10, 0, 8, 0),
        };
        titleBar.MouseDown += BeginWindowDrag;
        var titleIdentity = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Left,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        titleIdentity.MouseDown += BeginWindowDrag;
        var titleIcon = new PictureBox
        {
            Image = AppIconFactory.Image,
            Size = new Size(19, 19),
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 6, 6, 0),
        };
        titleIcon.MouseDown += BeginWindowDrag;
        var title = new Label
        {
            Text = $"WFly {ProductInfo.Version}",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiPalette.Ink,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Regular),
            Padding = new Padding(0, 8, 0, 0),
        };
        title.MouseDown += BeginWindowDrag;
        titleIdentity.Controls.Add(titleIcon);
        titleIdentity.Controls.Add(title);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 202,
            BackColor = Color.Transparent,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _themeToggleButton = CreateWindowActionButton("夜", "切换日夜主题");
        _themeToggleButton.Click += (_, _) => ToggleTheme();
        _compactToggleButton = CreateWindowActionButton("小", "切换小型化窗口");
        _compactToggleButton.Click += (_, _) => ToggleCompactWindow();
        var minimize = CreateWindowActionButton("—", "最小化窗口");
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        var maximize = CreateWindowActionButton("□", "最大化或还原窗口");
        maximize.Click += (_, _) => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        var close = CreateWindowActionButton("×", "关闭 WFly");
        close.HoverBackColor = Color.FromArgb(194, 63, 66);
        close.Click += (_, _) => Close();
        actions.Controls.Add(_themeToggleButton);
        actions.Controls.Add(_compactToggleButton);
        actions.Controls.Add(minimize);
        actions.Controls.Add(maximize);
        actions.Controls.Add(close);
        titleBar.Controls.Add(titleIdentity);
        titleBar.Controls.Add(actions);
        Controls.Add(titleBar);
        titleBar.BringToFront();
    }

    private RoundedButton CreateWindowActionButton(string text, string tooltip) => new()
    {
        Text = text,
        AccessibleName = tooltip,
        AutoSize = false,
        Size = new Size(38, 28),
        Margin = new Padding(2, 2, 0, 2),
        BackColor = UiPalette.Hover,
        ForeColor = UiPalette.Ink,
        HoverBackColor = UiPalette.AccentSoft,
        PressedBackColor = UiPalette.AccentSoft,
        BorderColor = UiPalette.CardBorder,
        CornerRadius = 8,
        Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
    };

    private void BeginWindowDrag(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized)
        {
            return;
        }

        _ = ReleaseCapture();
        _ = SendMessage(Handle, WmNcLButtonDown, HtCaption, IntPtr.Zero);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // A borderless form does not receive the normal thick-frame style.
            // Keep it so the HT* values below become real native resize gestures.
            parameters.Style |= WsThickFrame | WsMaximizeBox;
            return parameters;
        }
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg != WmNcHitTest || WindowState == FormWindowState.Maximized)
        {
            return;
        }

        var screenPoint = new Point(
            (short)(message.LParam.ToInt64() & 0xFFFF),
            (short)((message.LParam.ToInt64() >> 16) & 0xFFFF));
        var point = PointToClient(screenPoint);
        const int resizeGrip = 7;
        var left = point.X <= resizeGrip;
        var right = point.X >= ClientSize.Width - resizeGrip;
        var top = point.Y <= resizeGrip;
        var bottom = point.Y >= ClientSize.Height - resizeGrip;
        message.Result = left && top ? (IntPtr)HtTopLeft :
            right && top ? (IntPtr)HtTopRight :
            left && bottom ? (IntPtr)HtBottomLeft :
            right && bottom ? (IntPtr)HtBottomRight :
            left ? (IntPtr)HtLeft :
            right ? (IntPtr)HtRight :
            top ? (IntPtr)HtTop :
            bottom ? (IntPtr)HtBottom :
            message.Result;
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, int wParam, IntPtr lParam);

    private const int WmNcHitTest = 0x0084;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int WsThickFrame = 0x00040000;
    private const int WsMaximizeBox = 0x00010000;

    private void ToggleTheme()
    {
        UiPalette.SetDark(!UiPalette.IsDark);
        _themeToggleButton!.Text = UiPalette.IsDark ? "日" : "夜";
        WindowBackdrop.Apply(this);
        SuspendLayout();
        try
        {
            ApplyTheme(this);
            ShowPage(_currentPage);
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }

        PerformLayout();
        Invalidate(invalidateChildren: true);
        _trafficChart?.Invalidate();
        _trafficStatistics?.Invalidate(invalidateChildren: true);
    }

    private void ApplyTheme(Control control)
    {
        switch (control)
        {
            case TrafficStatisticsPanel statistics:
                statistics.ApplyTheme();
                return;
            case TrafficChartControl chart:
                chart.ApplyTheme();
                return;
            case FrostedGroupBox group:
                group.ForeColor = UiPalette.Ink;
                group.Invalidate();
                break;
            case FrostedCardPanel card:
                card.Invalidate();
                break;
            case Form or Panel or TableLayoutPanel or FlowLayoutPanel:
                control.BackColor = UiPalette.Canvas;
                break;
            case ProxyModeSelector selector:
                selector.BackColor = UiPalette.Card;
                selector.ForeColor = UiPalette.Ink;
                break;
            case Label label:
                label.ForeColor = ReferenceEquals(label, _statusLabel) ? UiPalette.MutedInk : UiPalette.Ink;
                label.BackColor = Color.Transparent;
                break;
            case CheckBox checkBox:
                checkBox.ForeColor = UiPalette.Ink;
                checkBox.BackColor = UiPalette.Canvas;
                break;
            case RoundedButton button:
                var primary = button.ForeColor.ToArgb() == Color.White.ToArgb();
                button.BackColor = primary ? UiPalette.Accent : UiPalette.Hover;
                button.ForeColor = primary ? Color.White : UiPalette.Ink;
                button.HoverBackColor = primary ? Color.FromArgb(53, 94, 192) : UiPalette.AccentSoft;
                button.PressedBackColor = UiPalette.AccentSoft;
                button.BorderColor = UiPalette.CardBorder;
                break;
            case DataGridView grid:
                UiControlTheme.ApplyDataGridView(grid);
                break;
            case ComboBox comboBox:
                UiControlTheme.ApplyComboBox(comboBox);
                break;
            case TextBox textBox:
                UiControlTheme.ApplyTextBox(textBox);
                break;
            case NumericUpDown numeric:
                UiControlTheme.ApplyNumericUpDown(numeric);
                break;
            case RichTextBox richText:
                UiControlTheme.ApplyRichTextBox(richText);
                break;
            case ListBox listBox:
                UiControlTheme.ApplyListBox(listBox);
                break;
        }

        foreach (Control child in control.Controls)
        {
            ApplyTheme(child);
        }
    }

    private void ToggleCompactWindow()
    {
        if (_mainShell is null || _compactShell is null)
        {
            return;
        }

        _isCompactWindow = !_isCompactWindow;
        if (_isCompactWindow)
        {
            _regularClientSize = ClientSize;
            _regularMinimumSize = MinimumSize;
            _mainShell.Visible = false;
            _compactShell.Visible = true;
            MinimumSize = new Size(320, 480);
            MaximumSize = Size.Empty;
            Size = new Size(370, 560);
            _compactToggleButton!.Text = "全";
        }
        else
        {
            _compactShell.Visible = false;
            _mainShell.Visible = true;
            MaximumSize = Size.Empty;
            MinimumSize = _regularMinimumSize;
            ClientSize = _regularClientSize;
            _compactToggleButton!.Text = "小";
        }

        RefreshHomePage();
    }

    private async Task SelectCompactNodeAsync()
    {
        if (_isLoading || _isSynchronizingCompactHome || _compactNodeSelector?.SelectedItem is not ProxyNode node)
        {
            return;
        }

        await SelectNodeFromTrayAsync(node);
    }

    private void SelectProxyModeFromCompact()
    {
        if (_isLoading || _isSynchronizingCompactHome || _compactProxyModeSelector is null || _proxyModeSelector is null)
        {
            return;
        }

        _proxyModeSelector.SelectedIndex = _compactProxyModeSelector.SelectedIndex;
    }

    private void SelectRoutingModeFromCompact()
    {
        if (_isLoading || _isSynchronizingCompactHome || _compactRoutingModeSelector is null || _routingModeSelector is null)
        {
            return;
        }

        _routingModeSelector.SelectedIndex = _compactRoutingModeSelector.SelectedIndex;
    }

    private void InitializeTrayIcon()
    {
        Icon = AppIconFactory.Instance;
        _trayIcon.Icon = AppIconFactory.Instance;
        _trayIcon.Text = $"WFly {ProductInfo.Version}";
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        _trayIcon.MouseUp += (_, args) =>
        {
            if (args.Button == MouseButtons.Right)
            {
                ShowTrayPopup();
            }
        };
        _trayIcon.Visible = true;
        Resize += OnWindowResize;
    }

    private void ShowTrayPopup()
    {
        if (IsDisposed || _trayPopup is { IsDisposed: false })
        {
            return;
        }

        var popup = new TrayPopupForm(
            _settings,
            _groups,
            _allNodes,
            node => _ = SelectNodeFromTrayAsync(node),
            SelectProxyModeFromTray,
            SelectRoutingModeFromTray,
            RestoreFromTray,
            RequestExitFromTray);
        popup.FormClosed += (_, _) => _trayPopup = null;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var location = new Point(
            Math.Clamp(Cursor.Position.X - popup.Width + 12, area.Left, area.Right - popup.Width),
            Math.Clamp(Cursor.Position.Y - popup.Height - 8, area.Top, area.Bottom - popup.Height));
        popup.Location = location;
        _trayPopup = popup;
        popup.Show();
    }

    private async Task SelectNodeFromTrayAsync(ProxyNode node)
    {
        if (IsDisposed ||
            _operationBusy ||
            _proxyModeTransitionBusy ||
            _routingModeTransitionBusy ||
            !node.IsEnabled ||
            string.Equals(node.Id, _settings.SelectedNodeId, StringComparison.Ordinal))
        {
            return;
        }

        var isRunning = _processService.IsRunning;
        var saved = await RunOperationAsync("正在选择节点…", async cancellationToken =>
        {
            _settings.SelectedNodeGroupId = node.GroupId;
            _settings.SelectedNodeId = node.Id;
            await _settingsStore.SaveAsync(_settings, cancellationToken);
            await RefreshCurrentNodesAsync();
            PostLog(
                "NODE",
                isRunning
                    ? $"已选择节点“{node.Name}”；当前内核继续运行，重启代理后将使用此节点。"
                    : $"已选择节点“{node.Name}”。");
        });

        if (saved && !IsDisposed)
        {
            SetStatus(isRunning
                ? $"已选择 {node.Name}；重启代理后生效。"
                : $"已选择 {node.Name}。 ");
            RefreshHomePage();
            RefreshNodesPage();
        }
    }

    private void SelectRoutingModeFromTray(ProxyRoutingMode mode)
    {
        if (IsDisposed ||
            _operationBusy ||
            _routingModeTransitionBusy ||
            _isLoading ||
            _settings.RoutingMode == mode ||
            _routingModeSelector is null)
        {
            return;
        }

        // Reuse the home selector's existing save/restart/rollback path.
        _routingModeSelector.SelectedIndex = (int)mode;
    }

    private void SelectProxyModeFromTray(ProxyMode mode)
    {
        if (IsDisposed ||
            _operationBusy ||
            _proxyModeTransitionBusy ||
            _isLoading ||
            _settings.ProxyMode == mode ||
            _proxyModeSelector is null)
        {
            return;
        }

        _proxyModeSelector.SelectedIndex = (int)mode;
    }

    private void OnWindowResize(object? sender, EventArgs eventArgs)
    {
        if (WindowState == FormWindowState.Minimized && !_allowCloseAfterCleanup)
        {
            HideToTray(showHint: true);
        }
    }

    private void OnInteractiveResizeBegin(object? sender, EventArgs eventArgs)
    {
        if (_isInteractiveResize || IsDisposed)
        {
            return;
        }

        _isInteractiveResize = true;
        _restartTrafficTimerAfterResize = _trafficTimer.Enabled;
        _trafficTimer.Stop();

        // Keep the native border responsive: the current page holds its last
        // stable layout while the user drags, then performs one complete
        // layout and graph repaint after the mouse is released.
        _pageHost.SuspendLayout();
        if (_trafficChart is not null)
        {
            _trafficChart.SuspendHeavyRendering = true;
        }
    }

    private void OnInteractiveResizeEnd(object? sender, EventArgs eventArgs)
    {
        if (!_isInteractiveResize)
        {
            return;
        }

        _isInteractiveResize = false;
        _restartTrafficTimerAfterResize &= !IsDisposed;
        if (!IsDisposed)
        {
            _pageHost.ResumeLayout(performLayout: true);
            if (_trafficChart is not null)
            {
                _trafficChart.SuspendHeavyRendering = false;
            }

            _pageHost.Invalidate(invalidateChildren: true);
            if (_restartTrafficTimerAfterResize)
            {
                _trafficTimer.Start();
                _ = RefreshTrafficAsync();
            }
        }

        _restartTrafficTimerAfterResize = false;
    }

    private void HideToTray(bool showHint)
    {
        if (IsDisposed || _allowCloseAfterCleanup)
        {
            return;
        }

        Hide();
        if (showHint && !_trayHintShown)
        {
            _trayHintShown = true;
            _trayIcon.ShowBalloonTip(
                2_500,
                "WFly 正在后台运行",
                "双击托盘图标可恢复窗口；右键菜单可退出。",
                ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        if (IsDisposed)
        {
            return;
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Show();
        Activate();
        BringToFront();
    }

    private void RequestExitFromTray()
    {
        if (IsDisposed)
        {
            return;
        }

        _exitRequestedFromTray = true;
        RestoreFromTray();
        Close();
    }

    private void ShowPage(string page)
    {
        if (!_navigationButtons.ContainsKey(page))
        {
            return;
        }

        _currentPage = page;
        foreach (var (name, button) in _navigationButtons)
        {
            var selected = string.Equals(name, page, StringComparison.Ordinal);
            button.BackColor = selected ? UiPalette.AccentSoft : UiPalette.Canvas;
            button.ForeColor = selected ? UiPalette.Accent : UiPalette.MutedInk;
            button.Font = new Font(Font.FontFamily, 10F, selected ? FontStyle.Bold : FontStyle.Regular);
        }

        if (!_pages.TryGetValue(page, out var pageControl))
        {
            pageControl = page switch
            {
                "首页" => BuildHomePage(),
                "节点" => BuildNodesPage(),
                "节点组" => BuildNodeGroupsPage(),
                "连接" => BuildConnectionsPage(),
                "规则" => BuildRulesPage(),
                "日志" => BuildLogsPage(),
                "测试" => BuildTestsPage(),
                "设置" => BuildSettingsPage(),
                _ => throw new InvalidOperationException($"Unknown page '{page}'."),
            };
            pageControl.Dock = DockStyle.Fill;
            _pages.Add(page, pageControl);
            _pageHost.Controls.Add(pageControl);
        }

        foreach (Control control in _pageHost.Controls)
        {
            control.Visible = ReferenceEquals(control, pageControl);
        }

        pageControl.BringToFront();
        RefreshVisiblePage();
        if (string.Equals(page, "首页", StringComparison.Ordinal) && !_isInteractiveResize)
        {
            _ = RefreshTrafficAsync();
        }
    }

    private Control BuildHomePage()
    {
        // The dashboard is intentionally a single responsive canvas instead
        // of a scrolling document. Four rows share the available client area;
        // the form minimum size keeps the compact chart and statistics table
        // readable on smaller displays.
        var root = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = false,
            BackColor = UiPalette.Canvas,
            Padding = new Padding(2, 2, 10, 6),
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = UiPalette.Canvas,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 142F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 138F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 47F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 53F));

        var topCard = new FrostedCardPanel
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 10),
        };
        var topLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32F));
        topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1F));
        topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68F));
        topLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var overview = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 18, 0),
            Padding = Padding.Empty,
        };
        overview.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        overview.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var overviewTitle = new Label
        {
            AutoSize = true,
            Text = "节点信息",
            ForeColor = UiPalette.Ink,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4),
        };
        var overviewLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(10, 4, 10, 4),
            Margin = Padding.Empty,
        };
        overviewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        overviewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _homeNodeLabel = AddValueRow(overviewLayout, "节点", 0);
        _homeCoreLabel = AddValueRow(overviewLayout, "内核", 1);
        _homeRunningLabel = AddValueRow(overviewLayout, "状态", 2);
        _homeConnectionCountLabel = AddValueRow(overviewLayout, "连接", 3);
        overview.Controls.Add(overviewTitle, 0, 0);
        overview.Controls.Add(overviewLayout, 0, 1);

        var divider = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiPalette.CardBorder,
            Margin = new Padding(0, 10, 0, 10),
        };
        var modeGroup = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 3,
            RowCount = 2,
            Margin = new Padding(8, 0, 0, 0),
            Padding = Padding.Empty,
            BackColor = UiPalette.Card,
        };
        modeGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        modeGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14F));
        modeGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        modeGroup.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        modeGroup.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var modeTitle = new Label
        {
            AutoSize = true,
            Text = "代理开关",
            ForeColor = UiPalette.Ink,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        };
        var modeLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 1,
            Padding = new Padding(2, 0, 2, 0),
            Margin = Padding.Empty,
            BackColor = UiPalette.Card,
        };
        modeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        modeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _proxyModeSelector = new ProxyModeSelector
        {
            Dock = DockStyle.Fill,
            BackColor = UiPalette.Card,
            ForeColor = UiPalette.Ink,
            Margin = Padding.Empty,
            Labels = ["系统代理", "关闭代理", "TUN 模式"],
        };
        _proxyModeSelector.ModeChanged += async (_, _) => await HandleProxyModeChangedAsync();
        modeLayout.Controls.Add(_proxyModeSelector, 0, 0);
        var routingTitle = new Label
        {
            AutoSize = true,
            Text = "代理规则",
            ForeColor = UiPalette.Ink,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        };
        var routingLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 1,
            Padding = new Padding(2, 0, 2, 0),
            Margin = Padding.Empty,
            BackColor = UiPalette.Card,
        };
        routingLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        routingLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _routingModeSelector = new ProxyModeSelector
        {
            Dock = DockStyle.Fill,
            BackColor = UiPalette.Card,
            ForeColor = UiPalette.Ink,
            Margin = Padding.Empty,
            Labels = ["规则", "全局", "直连"],
        };
        _routingModeSelector.SelectedIndexChanged += async (_, _) => await HandleRoutingModeChangedAsync();
        routingLayout.Controls.Add(_routingModeSelector, 0, 0);
        modeGroup.Controls.Add(modeTitle, 0, 0);
        modeGroup.Controls.Add(routingTitle, 2, 0);
        modeGroup.Controls.Add(modeLayout, 0, 1);
        modeGroup.Controls.Add(routingLayout, 2, 1);
        topLayout.Controls.Add(overview, 0, 0);
        topLayout.Controls.Add(divider, 1, 0);
        topLayout.Controls.Add(modeGroup, 2, 0);
        topCard.Controls.Add(topLayout);
        layout.Controls.Add(topCard, 0, 0);

        var egress = CreateGroup("IP 出口检测与真实延迟");
        egress.AutoSize = false;
        egress.Dock = DockStyle.Fill;
        egress.Margin = new Padding(0, 0, 0, 10);
        var egressLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Padding = new Padding(10, 2, 10, 4),
            Margin = Padding.Empty,
        };
        egressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28F));
        egressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44F));
        egressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28F));
        egressLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        egressLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        egressLayout.Controls.Add(CreateInlineValue("出口 IP", out _homeIpLabel), 0, 0);
        egressLayout.Controls.Add(CreateInlineValue("IP 类型", out _homeIpTypeLabel), 1, 0);
        egressLayout.SetColumnSpan(_homeIpTypeLabel.Parent!, 2);
        egressLayout.Controls.Add(CreateInlineValue("Google", out _homeGoogleLatencyLabel), 0, 1);
        egressLayout.SetColumnSpan(_homeGoogleLatencyLabel.Parent!, 2);
        var checkEgressButton = CreateSecondaryButton("检测出口");
        checkEgressButton.AutoSize = false;
        checkEgressButton.Size = new Size(120, 36);
        checkEgressButton.Anchor = AnchorStyles.None;
        checkEgressButton.Click += async (_, _) => await CheckEgressAsync();
        egressLayout.Controls.Add(checkEgressButton, 2, 1);
        egress.Controls.Add(egressLayout);
        layout.Controls.Add(egress, 0, 1);

        var trafficGroup = CreateGroup("实时流量");
        trafficGroup.AutoSize = false;
        trafficGroup.Dock = DockStyle.Fill;
        trafficGroup.Margin = Padding.Empty;
        _trafficChart = new TrafficChartControl { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = UiPalette.Card };
        trafficGroup.Controls.Add(_trafficChart);
        layout.Controls.Add(trafficGroup, 0, 2);

        var statisticsCard = new FrostedCardPanel
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        _trafficStatistics = new TrafficStatisticsPanel { Dock = DockStyle.Fill };
        statisticsCard.Controls.Add(_trafficStatistics);
        layout.Controls.Add(statisticsCard, 0, 3);

        root.Controls.Add(layout);
        return root;
    }

    private Control BuildConnectionsPage()
    {
        var root = CreateScrollablePage();
        var content = CreateVerticalPageLayout();
        var header = CreatePageHeader("运行中的连接");
        AddPageRow(content, header);
        var refresh = CreateSecondaryButton("刷新连接");
        refresh.Margin = new Padding(0, 0, 0, 10);
        refresh.Click += async (_, _) => await RefreshConnectionsAsync();
        AddPageRow(content, refresh);
        _connectionGrid = CreateGrid();
        _connectionGrid.Columns.Add("Host", "主机");
        _connectionGrid.Columns.Add("Port", "端口");
        _connectionGrid.Columns.Add("Network", "网络");
        _connectionGrid.Columns.Add("Type", "类型");
        _connectionGrid.Columns.Add("Rule", "规则");
        _connectionGrid.Columns.Add("Chains", "链路");
        _connectionGrid.Columns.Add("Upload", "上传");
        _connectionGrid.Columns.Add("Download", "下载");
        _connectionGrid.Columns.Add("Started", "开始时间");
        _connectionGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing;
        _connectionGrid.ColumnHeadersHeight = 32;
        _connectionGrid.RowTemplate.Height = 30;
        _connectionGrid.Height = 510;
        AddPageRow(content, _connectionGrid, 510);
        root.Controls.Add(content);
        return root;
    }

    private Control BuildLogsPage()
    {
        var root = CreateScrollablePage();
        var content = CreateVerticalPageLayout();
        AddPageRow(content, CreatePageHeader("日志"));
        var actions = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        var exportButton = CreateSecondaryButton("导出日志");
        exportButton.Click += async (_, _) => await ExportLogsAsync();
        var clearButton = CreateSecondaryButton("清空内存日志");
        clearButton.Click += (_, _) =>
        {
            _logStore.Clear();
            if (_logBox is not null)
            {
                _logBox.Clear();
            }
        };
        actions.Controls.Add(exportButton);
        actions.Controls.Add(clearButton);
        AddPageRow(content, actions);
        _logBox = new RichTextBox
        {
            Dock = DockStyle.Top,
            Height = 520,
            ReadOnly = true,
            WordWrap = false,
            DetectUrls = false,
            BackColor = Color.White,
            Font = new Font("Cascadia Mono", 9F),
            BorderStyle = BorderStyle.FixedSingle,
        };
        UiControlTheme.ApplyRichTextBox(_logBox);
        AddPageRow(content, _logBox, 520);
        root.Controls.Add(content);
        RenderLogs();
        return root;
    }

    private Control BuildTestsPage()
    {
        var root = CreateScrollablePage();
        var content = CreateVerticalPageLayout();
        AddPageRow(content, CreatePageHeader("测试"));
        var controls = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        _testThroughProxyCheckBox = new CheckBox
        {
            Text = "通过本地代理",
            Checked = TryGetActiveMixedProxyPort(out _),
            AutoSize = true,
            Padding = new Padding(0, 6, 8, 0),
        };
        var runButton = CreatePrimaryButton("开始延迟测试");
        runButton.Click += async (_, _) => await RunSiteLatencyTestsAsync(_testThroughProxyCheckBox.Checked);
        controls.Controls.Add(_testThroughProxyCheckBox);
        controls.Controls.Add(runButton);
        AddPageRow(content, controls);
        _testGrid = CreateGrid();
        _testGrid.Columns.Add("Name", "站点");
        _testGrid.Columns.Add("Host", "主机");
        _testGrid.Columns.Add("Status", "状态");
        _testGrid.Columns.Add(new DataGridViewLinkColumn
        {
            Name = "Latency",
            HeaderText = "延迟",
            TrackVisitedState = false,
            LinkColor = UiPalette.Accent,
            ActiveLinkColor = Color.FromArgb(43, 83, 178),
            VisitedLinkColor = UiPalette.Accent,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _testGrid.Columns.Add("Unlock", "解锁情况");
        foreach (var target in SiteLatencyTestService.DefaultTargets)
        {
            var rowIndex = _testGrid.Rows.Add(target.Name, target.Uri.Host, "等待测试", "点击测试", "—");
            _testGrid.Rows[rowIndex].Tag = target;
            _testGrid.Rows[rowIndex].Cells["Latency"].ToolTipText = "点击可单独测试该站点";
        }

        _testGrid.CellContentClick += async (_, eventArgs) => await TestGridCellContentClickAsync(eventArgs);
        _testGrid.Height = 410;
        AddPageRow(content, _testGrid, 410);
        root.Controls.Add(content);
        RefreshTestsPage();
        return root;
    }

    private Control BuildSettingsPage()
    {
        var root = CreateScrollablePage();
        var content = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54F));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46F));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = CreatePageHeader("设置");
        content.Controls.Add(header, 0, 0);
        content.SetColumnSpan(header, 2);

        var general = CreateGroup("本地运行设置");
        // Keep the form card at its content height. A forced fill made the
        // empty lower area look like an editable panel.
        general.AutoSize = true;
        general.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        general.Dock = DockStyle.Top;
        general.Margin = new Padding(0, 0, 8, 0);
        var generalLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 5,
            Padding = new Padding(4),
            Margin = Padding.Empty,
        };
        generalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        generalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var row = 0; row < 5; row++)
        {
            generalLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        generalLayout.Controls.Add(CreateSettingsLabel("默认内核"), 0, 0);
        _settingsCoreSelector = UiControlTheme.CreateComboBox();
        _settingsCoreSelector.Dock = DockStyle.Fill;
        _settingsCoreSelector.MinimumSize = new Size(260, 30);
        _settingsCoreSelector.DisplayMember = nameof(CoreDefinition.DisplayName);
        _settingsCoreSelector.ValueMember = nameof(CoreDefinition.Id);
        _settingsCoreSelector.DataSource = CoreRegistry.All.ToArray();
        generalLayout.Controls.Add(_settingsCoreSelector, 1, 0);
        generalLayout.Controls.Add(CreateSettingsLabel("本地混合端口"), 0, 1);
        _mixedPortInput = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = 160, Anchor = AnchorStyles.Left };
        UiControlTheme.ApplyNumericUpDown(_mixedPortInput);
        generalLayout.Controls.Add(_mixedPortInput, 1, 1);
        generalLayout.Controls.Add(CreateSettingsLabel("TUN 接口名"), 0, 2);
        _tunNameInput = new TextBox
        {
            Width = 160,
            MinimumSize = new Size(160, 30),
            MaximumSize = new Size(160, 30),
            Anchor = AnchorStyles.Left,
        };
        UiControlTheme.ApplyTextBox(_tunNameInput);
        generalLayout.Controls.Add(_tunNameInput, 1, 2);
        var saveSettings = CreatePrimaryButton("保存设置");
        saveSettings.Anchor = AnchorStyles.Left;
        saveSettings.Margin = new Padding(0, 10, 0, 0);
        saveSettings.Click += async (_, _) => await SaveGeneralSettingsAsync();
        generalLayout.Controls.Add(saveSettings, 1, 3);

        generalLayout.Controls.Add(CreateSettingsLabel("GeoFiles"), 0, 4);
        var geoFilesControls = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _geoFilesSmartRoutingCheckBox = new CheckBox
        {
            Text = "智能分流（中国大陆直连）",
            AutoSize = true,
            Padding = new Padding(0, 6, 4, 6),
            ForeColor = UiPalette.Ink,
        };
        _geoFilesSmartRoutingCheckBox.CheckedChanged += async (_, _) => await SaveGeoFilesSettingsAsync();
        geoFilesControls.Controls.Add(_geoFilesSmartRoutingCheckBox);
        generalLayout.Controls.Add(geoFilesControls, 1, 4);
        general.Controls.Add(generalLayout);

        var cores = CreateGroup("内核与 GeoFiles 下载更新");
        cores.Dock = DockStyle.Top;
        cores.Margin = new Padding(8, 0, 0, 0);
        var coreLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(4),
            Margin = Padding.Empty,
        };
        coreLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        // Header plus four rows (three cores and GeoFiles) needs no spare
        // white viewport below it.
        coreLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 154F));
        coreLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _coreGrid = CreateGrid();
        _coreGrid.Dock = DockStyle.Fill;
        _coreGrid.Columns.Add("Name", "内核");
        _coreGrid.Columns.Add("State", "状态");
        _coreGrid.Columns.Add("Installed", "已安装版本");
        _coreGrid.CellPainting += CoreGridCellPainting;
        _coreGrid.CellContentClick += async (_, args) => await DownloadCoreFromGridAsync(args);
        _coreGrid.CellMouseMove += (_, args) => UpdateCoreGridCursor(args);
        coreLayout.Controls.Add(_coreGrid, 0, 0);
        var coreActions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 10, 0, 0),
            Padding = Padding.Empty,
        };
        var downloadAllCores = CreatePrimaryButton("下载所有内核");
        downloadAllCores.AutoSize = false;
        downloadAllCores.Size = new Size(150, 40);
        downloadAllCores.Margin = Padding.Empty;
        downloadAllCores.Click += async (_, _) => await DownloadAllCoresAsync();
        coreActions.Controls.Add(downloadAllCores);
        coreLayout.Controls.Add(coreActions, 0, 1);
        cores.Controls.Add(coreLayout);
        content.Controls.Add(general, 0, 1);
        content.Controls.Add(cores, 1, 1);

        root.Controls.Add(content);

        return root;
    }

    private async Task LoadStateAsync()
    {
        var resumeTun = false;
        _isLoading = true;
        try
        {
            _settings = await _settingsStore.LoadAsync();
            var settingsChanged = NormalizeNativeConfigSettings();
            resumeTun = _resumeTunAfterElevation &&
                        _settings.ProxyMode == ProxyMode.Tun &&
                        SingBoxTunConfigBuilder.IsAdministrator();
            // A core is not persisted across GUI launches.  Keep the three
            // position control honest: without a running core the middle
            // position is the only active-safe state, except for the one
            // trusted runas restart requested immediately after choosing TUN.
            if (!_processService.IsRunning && _settings.ProxyMode != ProxyMode.Off && !resumeTun)
            {
                _settings.ProxyMode = ProxyMode.Off;
                settingsChanged = true;
            }

            if (settingsChanged)
            {
                await _settingsStore.SaveAsync(_settings);
            }

            // A prior crash or forced termination can leave Windows pointing at
            // WFly's loopback listener even though this new process has not
            // started a core. Restore only when the saved lease still belongs
            // to us; WindowsSystemProxyService refuses to overwrite any later
            // user or third-party change.
            if (_settings.SystemProxyLease is not null && !_processService.IsRunning)
            {
                try
                {
                    await RestoreSystemProxyAsync();
                    PostLog("SYS", "已检查并处理上次运行遗留的系统代理租约。");
                }
                catch (Exception exception)
                {
                    PostLog("SYS", $"无法自动恢复上次的系统代理：{exception.Message}");
                }
            }

            await RefreshGroupsAsync();
            _ruleSets = await _ruleSetStore.GetAllAsync();
            await RefreshCurrentNodesAsync();
            _trafficTimer.Start();
            _subscriptionTimer.Start();
            SetStatus("就绪");
            PostLog("SYS", "WFly 已就绪；运行数据位于 data 目录。 ");
        }
        catch (Exception exception)
        {
            ShowError("无法读取本地数据", exception);
        }
        finally
        {
            _isLoading = false;
            RefreshVisiblePage();
        }

        if (resumeTun && !IsDisposed)
        {
            await ResumeTunAfterElevationAsync();
        }
    }

    private async Task RefreshGroupsAsync()
    {
        _groups = await _nodeGroupStore.GetAllAsync();
        if (!string.IsNullOrWhiteSpace(_settings.SelectedNodeGroupId) &&
            !_groups.Any(group => string.Equals(group.Id, _settings.SelectedNodeGroupId, StringComparison.Ordinal)))
        {
            _settings.SelectedNodeGroupId = null;
            _settings.SelectedNodeId = null;
            await _settingsStore.SaveAsync(_settings);
        }

        if (string.IsNullOrWhiteSpace(_settings.SelectedNodeGroupId) && _groups.Count > 0)
        {
            _settings.SelectedNodeGroupId = _groups[0].Id;
            await _settingsStore.SaveAsync(_settings);
        }
    }

    private async Task RefreshCurrentNodesAsync()
    {
        _allNodes = await _proxyNodeStore.GetAllAsync();
        _currentNodes = string.IsNullOrWhiteSpace(_settings.SelectedNodeGroupId)
            ? []
            : _allNodes
                .Where(node => string.Equals(node.GroupId, _settings.SelectedNodeGroupId, StringComparison.Ordinal))
                .ToArray();
        if (!string.IsNullOrWhiteSpace(_settings.SelectedNodeId) &&
            !_currentNodes.Any(node => string.Equals(node.Id, _settings.SelectedNodeId, StringComparison.Ordinal)))
        {
            _settings.SelectedNodeId = null;
            await _settingsStore.SaveAsync(_settings);
        }

        if (string.IsNullOrWhiteSpace(_settings.SelectedNodeId) && _currentNodes.Count > 0)
        {
            _settings.SelectedNodeId = _currentNodes[0].Id;
            await _settingsStore.SaveAsync(_settings);
        }
    }

    private void RefreshVisiblePage()
    {
        switch (_currentPage)
        {
            case "首页":
                RefreshHomePage();
                break;
            case "节点":
                RefreshNodesPage();
                break;
            case "节点组":
                RefreshNodeGroupsPage();
                break;
            case "连接":
                _ = RefreshConnectionsAsync();
                break;
            case "规则":
                RefreshRulesPage();
                break;
            case "日志":
                RenderLogs();
                break;
            case "测试":
                RefreshTestsPage();
                break;
            case "设置":
                _ = RefreshSettingsPageAsync();
                break;
        }
    }

    private void RefreshHomePage()
    {
        if (_homeNodeLabel is null)
        {
            return;
        }

        var selectedNode = SelectedNode;
        var selectedGroup = SelectedGroup;
        _homeNodeLabel.Text = selectedNode is null
            ? (selectedGroup is null ? "请先创建节点组" : $"{selectedGroup.Name}（尚无节点）")
            : $"{selectedNode.Name} · {selectedGroup?.Name}";
        _homeCoreLabel!.Text = selectedNode is null ? "—" : CoreRegistry.GetById(selectedNode.CoreId)?.DisplayName ?? selectedNode.CoreId;
        _homeRunningLabel!.Text = _processService.IsRunning ? "运行中" : "已停止";
        _homeConnectionCountLabel!.Text = "等待控制器数据";
        if (_proxyModeSelector is not null && _proxyModeSelector.Mode != _settings.ProxyMode)
        {
            _proxyModeSelector.Mode = _settings.ProxyMode;
        }

        if (_proxyModeSelector is not null)
        {
            _proxyModeSelector.Enabled = !_operationBusy && !_proxyModeTransitionBusy && !_routingModeTransitionBusy;
        }

        if (_routingModeSelector is not null)
        {
            if (_routingModeSelector.SelectedIndex != (int)_settings.RoutingMode)
            {
                _routingModeSelector.SelectedIndex = (int)_settings.RoutingMode;
            }

            _routingModeSelector.Enabled = !_operationBusy && !_proxyModeTransitionBusy && !_routingModeTransitionBusy;
        }

        RefreshCompactHome(selectedNode, selectedGroup);
    }

    private void RefreshCompactHome(ProxyNode? selectedNode, NodeGroup? selectedGroup)
    {
        if (_compactNodeLabel is null)
        {
            return;
        }

        _compactNodeLabel.Text = selectedNode is null
            ? (selectedGroup is null ? "请先创建节点组" : $"{selectedGroup.Name}（尚无节点）")
            : selectedNode.Name;
        _compactCoreLabel!.Text = selectedNode is null ? "—" : CoreRegistry.GetById(selectedNode.CoreId)?.DisplayName ?? selectedNode.CoreId;
        _compactStatusLabel!.Text = _processService.IsRunning ? "运行中" : "已停止";

        if (_compactNodeSelector is not null)
        {
            var enabledNodes = _allNodes.Where(node => node.IsEnabled).ToArray();
            var needsItems = _compactNodeSelector.Items.Count != enabledNodes.Length ||
                             _compactNodeSelector.Items.Cast<ProxyNode>().Select(node => node.Id)
                                 .SequenceEqual(enabledNodes.Select(node => node.Id), StringComparer.Ordinal) == false;
            _isSynchronizingCompactHome = true;
            try
            {
                if (needsItems)
                {
                    _compactNodeSelector.Items.Clear();
                    _compactNodeSelector.Items.AddRange(enabledNodes);
                }

                var selectedIndex = Array.FindIndex(enabledNodes, node => string.Equals(node.Id, _settings.SelectedNodeId, StringComparison.Ordinal));
                _compactNodeSelector.SelectedIndex = selectedIndex;
            }
            finally
            {
                _isSynchronizingCompactHome = false;
            }
        }

        if (_compactProxyModeSelector is not null)
        {
            _compactProxyModeSelector.Mode = _settings.ProxyMode;
            _compactProxyModeSelector.Enabled = !_operationBusy && !_proxyModeTransitionBusy && !_routingModeTransitionBusy;
        }

        if (_compactRoutingModeSelector is not null)
        {
            _compactRoutingModeSelector.SelectedIndex = (int)_settings.RoutingMode;
            _compactRoutingModeSelector.Enabled = !_operationBusy && !_proxyModeTransitionBusy && !_routingModeTransitionBusy;
        }
    }

    private async Task HandleRoutingModeChangedAsync()
    {
        if (_isLoading ||
            _routingModeTransitionBusy ||
            _routingModeSelector is null)
        {
            return;
        }

        var selectedMode = (ProxyRoutingMode)_routingModeSelector.SelectedIndex;
        if (selectedMode == _settings.RoutingMode)
        {
            return;
        }

        var previousMode = _settings.RoutingMode;
        _routingModeTransitionBusy = true;
        try
        {
            if (_processService.IsRunning &&
                !string.Equals(_runningCoreId, "sing-box", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("代理规则滑轨当前只会重生成 sing-box 配置。请停止 Mihomo 或 Xray-core 后切换规则。");
            }

            _settings.RoutingMode = selectedMode;
            await _settingsStore.SaveAsync(_settings);

            if (_processService.IsRunning)
            {
                var stopped = await StopRunningCoreAsync();
                if (!stopped || _processService.IsRunning || _settings.SystemProxyLease is not null)
                {
                    throw new InvalidOperationException("无法安全停止当前内核，因此没有切换代理规则。");
                }

                await StartSelectedNodeAsync();
                if (!_processService.IsRunning)
                {
                    throw new InvalidOperationException("新代理规则配置未能启动内核。");
                }
            }

            PostLog("SYS", $"代理规则已切换为：{GetRoutingModeDisplay(selectedMode)}。");
        }
        catch (Exception exception)
        {
            _settings.RoutingMode = previousMode;
            if (_routingModeSelector.SelectedIndex != (int)previousMode)
            {
                _routingModeSelector.SelectedIndex = (int)previousMode;
            }

            await _settingsStore.SaveAsync(_settings);
            ShowError("无法切换代理规则", exception);
        }
        finally
        {
            _routingModeTransitionBusy = false;
            RefreshHomePage();
        }
    }

    private async Task HandleProxyModeChangedAsync()
    {
        if (_isLoading ||
            _proxyModeTransitionBusy ||
            _proxyModeSelector is null ||
            _proxyModeSelector.Mode == _settings.ProxyMode)
        {
            return;
        }

        var previousMode = _settings.ProxyMode;
        var selectedMode = _proxyModeSelector.Mode;
        _proxyModeTransitionBusy = true;
        try
        {
            // The middle position is the only stopped state.  Sliding to
            // either end starts the selected node; moving between endpoints
            // regenerates the configuration so system-proxy/TUN state cannot
            // leak from the previous run.
            if (selectedMode == ProxyMode.Off)
            {
                _settings.ProxyMode = ProxyMode.Off;
                if (_processService.IsRunning)
                {
                    var stopped = await StopRunningCoreAsync();
                    if (!stopped || _processService.IsRunning || _settings.SystemProxyLease is not null)
                    {
                        throw new InvalidOperationException("未能完整停止内核或恢复系统代理，当前模式保持不变。");
                    }
                }
                else
                {
                    await RestoreSystemProxyAsync();
                    SetStatus("代理已关闭。");
                }

                await _settingsStore.SaveAsync(_settings);
                PostLog("SYS", "代理开关已回到关闭位置，内核已停止。");
                return;
            }

            var selectedNode = SelectedNode;
            if (selectedNode is null || !selectedNode.IsEnabled)
            {
                _settings.ProxyMode = ProxyMode.Off;
                await _settingsStore.SaveAsync(_settings);
                _proxyModeSelector.Mode = ProxyMode.Off;
                MessageBox.Show(
                    this,
                    "请先在节点组中选择一个已启用的节点，再开启代理。",
                    "WFly",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (selectedMode == ProxyMode.Tun &&
                string.Equals(selectedNode.CoreId, "sing-box", StringComparison.OrdinalIgnoreCase) &&
                !SingBoxTunConfigBuilder.IsAdministrator())
            {
                if (_processService.IsRunning)
                {
                    var stopped = await StopRunningCoreAsync();
                    if (!stopped || _processService.IsRunning || _settings.SystemProxyLease is not null)
                    {
                        throw new InvalidOperationException("无法完整停止当前内核或恢复系统代理，因此没有切换到 TUN 模式。");
                    }
                }
                else
                {
                    await RestoreSystemProxyAsync();
                }

                _settings.ProxyMode = ProxyMode.Tun;
                await _settingsStore.SaveAsync(_settings);

                if (TryRestartElevatedForTun())
                {
                    PostLog("SYS", "正在请求管理员权限以启动 TUN 模式。");
                    SetStatus("正在请求管理员权限；确认 UAC 后将自动启动 TUN。");
                    _allowCloseAfterCleanup = true;
                    BeginInvoke(new Action(Close));
                    return;
                }

                _settings.ProxyMode = ProxyMode.Off;
                await _settingsStore.SaveAsync(_settings);
                _proxyModeSelector.Mode = ProxyMode.Off;
                SetStatus("未获得管理员权限，TUN 未启动。");
                return;
            }

            _settings.ProxyMode = selectedMode;
            await _settingsStore.SaveAsync(_settings);

            if (_processService.IsRunning)
            {
                var stopped = await StopRunningCoreAsync();
                if (!stopped || _processService.IsRunning || _settings.SystemProxyLease is not null)
                {
                    throw new InvalidOperationException("无法完整停止当前内核或恢复系统代理，因此没有切换代理模式。");
                }
            }
            else
            {
                await RestoreSystemProxyAsync();
            }

            if (selectedMode == ProxyMode.Tun &&
                !string.Equals(selectedNode.CoreId, "sing-box", StringComparison.OrdinalIgnoreCase))
            {
                _settings.ProxyMode = ProxyMode.Off;
                await _settingsStore.SaveAsync(_settings);
                _proxyModeSelector.Mode = ProxyMode.Off;
                MessageBox.Show(
                    this,
                    "TUN 模式目前仅支持 sing-box 节点。请先切换到 sing-box 节点后再开启。",
                    "WFly",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            await StartSelectedNodeAsync();
            if (!_processService.IsRunning)
            {
                _settings.ProxyMode = ProxyMode.Off;
                await _settingsStore.SaveAsync(_settings);
                _proxyModeSelector.Mode = ProxyMode.Off;
                SetStatus("内核未能启动，代理已保持关闭。");
                return;
            }

            PostLog("SYS", $"代理开关已开启：{GetProxyModeDisplay(_settings.ProxyMode)}。");
        }
        catch (Exception exception)
        {
            // If the old core is still alive, retain the old visual state so
            // the selector never claims that proxy traffic has been stopped.
            // A failed new start has no running core, so it safely falls back
            // to the middle position.
            var fallbackMode = _processService.IsRunning ? previousMode : ProxyMode.Off;
            _settings.ProxyMode = fallbackMode;
            _proxyModeSelector.Mode = fallbackMode;
            await _settingsStore.SaveAsync(_settings);
            ShowError("无法切换代理模式", exception);
        }
        finally
        {
            _proxyModeTransitionBusy = false;
            RefreshHomePage();
        }
    }

    private bool TryRestartElevatedForTun()
    {
        try
        {
            using var elevatedProcess = Process.Start(new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = "--resume-tun",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas",
            });
            return elevatedProcess is not null;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            PostLog("SYS", "用户取消了 TUN 模式所需的管理员授权。");
            return false;
        }
        catch (Exception exception)
        {
            PostLog("SYS", $"无法请求 TUN 所需的管理员权限：{exception.GetType().Name}。");
            return false;
        }
    }

    private async Task ResumeTunAfterElevationAsync()
    {
        if (_settings.ProxyMode != ProxyMode.Tun || _processService.IsRunning)
        {
            return;
        }

        if (!SingBoxTunConfigBuilder.IsAdministrator())
        {
            _settings.ProxyMode = ProxyMode.Off;
            await _settingsStore.SaveAsync(_settings);
            if (_proxyModeSelector is not null)
            {
                _proxyModeSelector.Mode = ProxyMode.Off;
            }
            SetStatus("未获得管理员权限，TUN 未启动。");
            return;
        }

        if (SelectedNode is not { IsEnabled: true })
        {
            _settings.ProxyMode = ProxyMode.Off;
            await _settingsStore.SaveAsync(_settings);
            if (_proxyModeSelector is not null)
            {
                _proxyModeSelector.Mode = ProxyMode.Off;
            }
            SetStatus("未找到已启用节点，TUN 未启动。");
            return;
        }

        PostLog("SYS", "已获得管理员权限，正在自动启动 TUN 模式。");
        SetStatus("正在启动 TUN 模式…");
        await StartSelectedNodeAsync();
        if (_processService.IsRunning)
        {
            PostLog("SYS", "TUN 模式已启动。");
        }
        else
        {
            _settings.ProxyMode = ProxyMode.Off;
            await _settingsStore.SaveAsync(_settings);
            if (_proxyModeSelector is not null)
            {
                _proxyModeSelector.Mode = ProxyMode.Off;
            }
            SetStatus("TUN 启动失败，代理已保持关闭。");
        }

        RefreshHomePage();
    }

    private async Task CheckEgressAsync()
    {
        await RunOperationAsync("正在检测出口 IP 与 Google 延迟…", async cancellationToken =>
        {
            var useLocalProxy = TryGetActiveMixedProxyPort(out var localProxyPort);
            var result = await _networkDiagnosticsService.CheckAsync(
                useLocalProxy,
                useLocalProxy ? localProxyPort : _settings.MixedProxyPort,
                cancellationToken);
            _homeIpLabel?.SetTextSafe(result.IpAddress ?? "未知");
            _homeIpTypeLabel?.SetTextSafe(result.IpTypeDisplay);
            var latencyText = result.GoogleLatency is { } latency ? $"{latency.TotalMilliseconds:0} ms" : "不可达";
            _homeGoogleLatencyLabel?.SetTextSafe(latencyText);
            _compactIpLabel?.SetTextSafe(result.IpAddress ?? "未知");
            _compactIpTypeLabel?.SetTextSafe(result.IpTypeDisplay);
            _compactGoogleLatencyLabel?.SetTextSafe(latencyText);
            _settings.LastExitIpCheckAt = DateTimeOffset.UtcNow;
            await _settingsStore.SaveAsync(_settings, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                PostLog("NET", result.Error);
            }
            else
            {
                PostLog("NET", $"出口检测完成（{(result.UsedLocalProxy ? "本地代理" : "直连")}）。");
            }
        });
    }

    private async Task RefreshTrafficAsync()
    {
        if (_trafficTickBusy ||
            _trafficChart is null ||
            IsDisposed ||
            _isInteractiveResize ||
            IsHomeSliderDragging ||
            !string.Equals(_currentPage, "首页", StringComparison.Ordinal))
        {
            return;
        }

        _trafficTickBusy = true;
        try
        {
            // The chart obtains traffic totals from the local Clash API.  It
            // does not need a host-wide network-interface scan merely to get
            // a timestamp; that scan was expensive enough to contend with
            // interactive resizing on machines with many virtual adapters.
            var capturedAt = DateTimeOffset.UtcNow;
            var breakdown = CoreTrafficBreakdown.Empty;
            var hasControllerCounters = false;
            try
            {
                var connections = await _clashApiClient.TryGetConnectionsAsync(9090);
                // A controller response may complete while the user starts
                // resizing or leaves the home page.  Skip all post-response
                // connection processing in that case so the UI message loop
                // remains available to the native resize gesture.
                if (IsDisposed ||
                    _isInteractiveResize ||
                    !string.Equals(_currentPage, "首页", StringComparison.Ordinal))
                {
                    return;
                }

                if (connections is not null)
                {
                    hasControllerCounters = true;
                    CaptureConnectionLogs(connections);
                    breakdown = SampleCoreTraffic(connections, capturedAt);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
            {
                ResetTrafficCounterBaseline();
            }

            var sample = new TrafficChartSample(
                capturedAt,
                breakdown.ProxyUploadBytesPerSecond,
                breakdown.ProxyDownloadBytesPerSecond,
                breakdown.DirectUploadBytesPerSecond,
                breakdown.DirectDownloadBytesPerSecond);
            if (!IsDisposed && !_isInteractiveResize && _trafficChart.IsHandleCreated)
            {
                _trafficChart.Append(sample);
                if (_homeConnectionCountLabel is not null)
                {
                    _homeConnectionCountLabel.Text = hasControllerCounters
                        ? $"{breakdown.TotalActiveConnections} 个活动连接"
                        : "等待控制器数据";
                }

                _trafficStatistics?.SetSnapshot(new TrafficStatisticsSnapshot(
                    capturedAt,
                    hasControllerCounters ? breakdown.ProxyUploadBytesPerSecond : null,
                    hasControllerCounters ? breakdown.ProxyDownloadBytesPerSecond : null,
                    hasControllerCounters ? breakdown.ProxyActiveConnections : null,
                    hasControllerCounters ? _proxyUploadTotal : null,
                    hasControllerCounters ? _proxyDownloadTotal : null,
                    hasControllerCounters ? breakdown.DirectUploadBytesPerSecond : null,
                    hasControllerCounters ? breakdown.DirectDownloadBytesPerSecond : null,
                    hasControllerCounters ? breakdown.DirectActiveConnections : null,
                    hasControllerCounters ? _directUploadTotal : null,
                    hasControllerCounters ? _directDownloadTotal : null));
            }
        }
        finally
        {
            _trafficTickBusy = false;
        }
    }

    private bool IsHomeSliderDragging =>
        _proxyModeSelector?.IsDragging == true ||
        _routingModeSelector?.IsDragging == true;

    private CoreTrafficBreakdown SampleCoreTraffic(ClashConnectionsSnapshot snapshot, DateTimeOffset capturedAt)
    {
        var seconds = _previousTrafficCounterAt is { } previous
            ? Math.Max(0.25D, (capturedAt - previous).TotalSeconds)
            : 0D;
        var current = new Dictionary<string, ConnectionTrafficCounter>(StringComparer.Ordinal);
        long proxyUploadDelta = 0;
        long proxyDownloadDelta = 0;
        long directUploadDelta = 0;
        long directDownloadDelta = 0;
        var proxyConnections = 0;
        var directConnections = 0;

        foreach (var connection in snapshot.Connections)
        {
            var isDirect = connection.UsesDirectOutbound;
            if (isDirect)
            {
                directConnections++;
            }
            else
            {
                proxyConnections++;
            }

            if (string.IsNullOrWhiteSpace(connection.Id))
            {
                continue;
            }

            current[connection.Id] = new ConnectionTrafficCounter(connection.UploadBytes, connection.DownloadBytes);
            if (!_previousTrafficConnections.TryGetValue(connection.Id, out var previousCounter))
            {
                continue;
            }

            var uploadDelta = Math.Max(0L, connection.UploadBytes - previousCounter.UploadBytes);
            var downloadDelta = Math.Max(0L, connection.DownloadBytes - previousCounter.DownloadBytes);
            if (isDirect)
            {
                directUploadDelta += uploadDelta;
                directDownloadDelta += downloadDelta;
            }
            else
            {
                proxyUploadDelta += uploadDelta;
                proxyDownloadDelta += downloadDelta;
            }
        }

        _previousTrafficConnections.Clear();
        foreach (var (id, counter) in current)
        {
            _previousTrafficConnections[id] = counter;
        }

        _previousTrafficCounterAt = capturedAt;
        _proxyUploadTotal = AddTrafficIncrement(_proxyUploadTotal, proxyUploadDelta, 1D);
        _proxyDownloadTotal = AddTrafficIncrement(_proxyDownloadTotal, proxyDownloadDelta, 1D);
        _directUploadTotal = AddTrafficIncrement(_directUploadTotal, directUploadDelta, 1D);
        _directDownloadTotal = AddTrafficIncrement(_directDownloadTotal, directDownloadDelta, 1D);

        return new CoreTrafficBreakdown(
            seconds <= 0 ? 0L : (long)(proxyUploadDelta / seconds),
            seconds <= 0 ? 0L : (long)(proxyDownloadDelta / seconds),
            proxyConnections,
            seconds <= 0 ? 0L : (long)(directUploadDelta / seconds),
            seconds <= 0 ? 0L : (long)(directDownloadDelta / seconds),
            directConnections);
    }

    private void ResetTrafficCounterBaseline()
    {
        _previousTrafficConnections.Clear();
        _previousTrafficCounterAt = null;
    }

    private void ResetTrafficStatistics()
    {
        ResetTrafficCounterBaseline();
        _proxyUploadTotal = 0;
        _proxyDownloadTotal = 0;
        _directUploadTotal = 0;
        _directDownloadTotal = 0;
        _trafficChart?.Clear();
        _trafficStatistics?.ClearSnapshot();
    }

    private static long AddTrafficIncrement(long total, long increment, double multiplier)
    {
        if (total == long.MaxValue || increment <= 0 || multiplier <= 0)
        {
            return total;
        }

        var scaled = Math.Min((double)long.MaxValue, increment * multiplier);
        return scaled >= long.MaxValue - total
            ? long.MaxValue
            : total + (long)scaled;
    }

    private sealed record ConnectionTrafficCounter(long UploadBytes, long DownloadBytes);

    private sealed record CoreTrafficBreakdown(
        long ProxyUploadBytesPerSecond,
        long ProxyDownloadBytesPerSecond,
        int ProxyActiveConnections,
        long DirectUploadBytesPerSecond,
        long DirectDownloadBytesPerSecond,
        int DirectActiveConnections)
    {
        public static CoreTrafficBreakdown Empty { get; } = new(0, 0, 0, 0, 0, 0);

        public int TotalActiveConnections => ProxyActiveConnections + DirectActiveConnections;
    }

    private async Task RefreshConnectionsAsync()
    {
        if (_connectionGrid is null || _operationBusy)
        {
            return;
        }

        try
        {
            var snapshot = await _clashApiClient.TryGetConnectionsAsync(9090);
            if (_connectionGrid.IsDisposed)
            {
                return;
            }

            _connectionGrid.Rows.Clear();
            if (snapshot is null)
            {
                SetStatus("连接控制器不可用：启动支持 Clash API 的 sing-box 或 Mihomo 后再试。");
                return;
            }

            foreach (var connection in snapshot.Connections)
            {
                _connectionGrid.Rows.Add(
                    connection.Host,
                    connection.Port,
                    connection.Network,
                    connection.Type,
                    connection.Rule,
                    connection.Chains,
                    FormatBytes(connection.UploadBytes),
                    FormatBytes(connection.DownloadBytes),
                    connection.StartedAt);
            }

            SetStatus($"已读取 {snapshot.Connections.Count} 个活动连接。");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
        {
            SetStatus("连接控制器暂不可用。");
        }
    }

    private void CaptureConnectionLogs(ClashConnectionsSnapshot snapshot)
    {
        if (!_settings.ConnectionLoggingEnabled)
        {
            return;
        }

        var current = snapshot.Connections
            .Where(connection => !string.IsNullOrWhiteSpace(connection.Id))
            .Select(connection => connection.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var connection in snapshot.Connections.Where(connection => !string.IsNullOrWhiteSpace(connection.Id) && !_knownConnectionIds.Contains(connection.Id)))
        {
            var endpoint = string.IsNullOrWhiteSpace(connection.Port) ? connection.Host : $"{connection.Host}:{connection.Port}";
            var chain = string.IsNullOrWhiteSpace(connection.Chains) ? "未标注链路" : connection.Chains;
            PostLog("CONN", $"连接建立：{endpoint}（{connection.Network}，{chain}）。");
        }

        foreach (var endedId in _knownConnectionIds.Where(id => !current.Contains(id)))
        {
            PostLog("CONN", "连接结束。 ");
        }

        _knownConnectionIds = current;
    }

    private async Task RunSiteLatencyTestsAsync(bool throughLocalProxy)
    {
        if (_testGrid is null)
        {
            return;
        }

        if (!TryGetSiteLatencyTestPort(throughLocalProxy, null, out var localProxyPort))
        {
            return;
        }

        await RunOperationAsync("正在测试网站访问延迟…", async cancellationToken =>
        {
            var progress = new Progress<SiteLatencyResult>(UpdateSiteLatencyResult);
            var results = await _siteLatencyTestService.TestAsync(
                SiteLatencyTestService.DefaultTargets,
                throughLocalProxy,
                localProxyPort,
                progress,
                cancellationToken);
            PostLog("TEST", $"完成 {results.Count} 个网站延迟测试（{(throughLocalProxy ? "本地代理" : "直连")}）。");
        });
    }

    private async Task TestGridCellContentClickAsync(DataGridViewCellEventArgs eventArgs)
    {
        if (_testGrid is null ||
            _operationBusy ||
            eventArgs.RowIndex < 0 ||
            eventArgs.ColumnIndex < 0 ||
            !string.Equals(_testGrid.Columns[eventArgs.ColumnIndex].Name, "Latency", StringComparison.Ordinal) ||
            _testGrid.Rows[eventArgs.RowIndex].Tag is not SiteLatencyTarget target)
        {
            return;
        }

        await RunSingleSiteLatencyTestAsync(target, _testThroughProxyCheckBox?.Checked ?? true);
    }

    private async Task RunSingleSiteLatencyTestAsync(SiteLatencyTarget target, bool throughLocalProxy)
    {
        if (_testGrid is null || _operationBusy)
        {
            return;
        }

        if (!TryGetSiteLatencyTestPort(throughLocalProxy, target.Name, out var localProxyPort))
        {
            return;
        }

        SetSiteLatencyTesting(target.Name);
        await RunOperationAsync($"正在测试 {target.Name}…", async cancellationToken =>
        {
            var progress = new Progress<SiteLatencyResult>(UpdateSiteLatencyResult);
            var results = await _siteLatencyTestService.TestAsync(
                [target],
                throughLocalProxy,
                localProxyPort,
                progress,
                cancellationToken);
            PostLog("TEST", $"完成 {target.Name} 的单项延迟测试（{(throughLocalProxy ? "本地代理" : "直连")}）。");
        });
    }

    private void SetSiteLatencyTesting(string targetName)
    {
        if (_testGrid is null || _testGrid.IsDisposed)
        {
            return;
        }

        foreach (DataGridViewRow row in _testGrid.Rows)
        {
            if (!string.Equals(row.Cells["Name"].Value as string, targetName, StringComparison.Ordinal))
            {
                continue;
            }

            row.Cells["Status"].Value = "测试中";
            row.Cells["Latency"].Value = "测试中";
            row.Cells["Unlock"].Value = "检测中";
            row.Cells["Latency"].ToolTipText = "正在测试该站点";
            return;
        }
    }

    private void UpdateSiteLatencyResult(SiteLatencyResult result)
    {
        if (_testGrid is null || _testGrid.IsDisposed)
        {
            return;
        }

        foreach (DataGridViewRow row in _testGrid.Rows)
        {
            if (string.Equals(row.Cells["Name"].Value as string, result.Name, StringComparison.Ordinal))
            {
                row.Cells["Status"].Value = result.StatusText;
                row.Cells["Latency"].Value = result.Latency is { } latency ? $"{latency.TotalMilliseconds:0} ms" : "重试";
                row.Cells["Latency"].ToolTipText = "点击可单独重新测试该站点";
                row.Cells["Unlock"].Value = result.UnlockStatusText;
                row.Cells["Unlock"].ToolTipText = result.Error
                    ?? (result.StatusCode is { } statusCode ? $"HTTP {statusCode}；仅表示网页响应，不能代表登录后的地区内容可播放。" : string.Empty);
                break;
            }
        }
    }

    private void RefreshTestsPage()
    {
        if (_testThroughProxyCheckBox is null || _testThroughProxyCheckBox.IsDisposed)
        {
            return;
        }

        var isAvailable = TryGetActiveMixedProxyPort(out _);
        _testThroughProxyCheckBox.Enabled = isAvailable;
        _testThroughProxyCheckBox.Text = isAvailable ? "通过本地代理" : "通过本地代理（未启动）";
        _testThroughProxyCheckBox.AccessibleDescription = isAvailable
            ? "测试请求将通过当前运行的 sing-box 本地代理。"
            : "本地代理未启动。可直接进行直连测试。";
        if (!isAvailable)
        {
            _testThroughProxyCheckBox.Checked = false;
        }
    }

    private bool TryGetSiteLatencyTestPort(bool throughLocalProxy, string? targetName, out int localProxyPort)
    {
        if (!throughLocalProxy)
        {
            localProxyPort = _settings.MixedProxyPort;
            return true;
        }

        if (TryGetActiveMixedProxyPort(out localProxyPort))
        {
            return true;
        }

        const string message = "本地代理未启动，未执行测试。请先在首页开启 sing-box 代理，或取消“通过本地代理”后进行直连测试。";
        MarkSiteLatencyProxyUnavailable(targetName, message);
        SetStatus(message);
        PostLog("TEST", message);
        return false;
    }

    private void MarkSiteLatencyProxyUnavailable(string? targetName, string message)
    {
        if (_testGrid is null || _testGrid.IsDisposed)
        {
            return;
        }

        foreach (DataGridViewRow row in _testGrid.Rows)
        {
            if (targetName is not null &&
                !string.Equals(row.Cells["Name"].Value as string, targetName, StringComparison.Ordinal))
            {
                continue;
            }

            row.Cells["Status"].Value = "本地代理未启动";
            row.Cells["Latency"].Value = "点击测试";
            row.Cells["Unlock"].Value = "未检测";
            row.Cells["Latency"].ToolTipText = message;
            row.Cells["Unlock"].ToolTipText = message;
        }
    }

    private async Task RefreshSettingsPageAsync()
    {
        if (_settingsCoreSelector is null || _coreGrid is null)
        {
            return;
        }

        _isLoading = true;
        try
        {
            var definition = CoreRegistry.GetById(_settings.SelectedCoreId) ?? CoreRegistry.All[0];
            _settingsCoreSelector.SelectedItem = definition;
            _mixedPortInput!.Value = Math.Clamp(_settings.MixedProxyPort, (int)_mixedPortInput.Minimum, (int)_mixedPortInput.Maximum);
            _tunNameInput!.Text = _settings.TunInterfaceName;
            if (_geoFilesSmartRoutingCheckBox is not null)
            {
                _geoFilesSmartRoutingCheckBox.Checked = _settings.GeoFilesSmartRoutingEnabled;
            }
            var installed = await _installedCoreStore.GetAllAsync();
            _coreGrid.Rows.Clear();
            foreach (var core in CoreRegistry.All)
            {
                var latest = installed
                    .Where(candidate => string.Equals(candidate.Id, core.Id, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate.ExecutablePath))
                    .OrderByDescending(candidate => candidate.InstalledAt)
                    .FirstOrDefault();
                var status = _coreDownloadStates.TryGetValue(core.Id, out var currentStatus)
                    ? currentStatus
                    : latest is null ? "未安装" : "已安装";
                var isBusyWithoutLocalVersion = latest is null &&
                                                _coreDownloadProgressActive &&
                                                _coreDownloadStates.ContainsKey(core.Id);
                var installedText = latest?.Version ?? (isBusyWithoutLocalVersion ? string.Empty : "下载");
                var rowIndex = _coreGrid.Rows.Add(core.DisplayName, status, installedText);
                _coreGrid.Rows[rowIndex].Tag = core;
                _coreGrid.Rows[rowIndex].Cells["Installed"].ToolTipText = latest is null && !isBusyWithoutLocalVersion
                    ? $"点击下载 {core.DisplayName}"
                    : string.Empty;
                if (!_coreDownloadProgressActive)
                {
                    SetIdleCoreDownloadProgress(core, latest is not null, status);
                }
            }

            var geoRowIndex = _coreGrid.Rows.Add("GeoFiles", await GetGeoFilesStatusAsync(), "更新");
            _coreGrid.Rows[geoRowIndex].Tag = GeoFilesGridRowTag;
            _coreGrid.Rows[geoRowIndex].Cells["Installed"].ToolTipText = "更新 GeoFiles 智能分流数据";

        }
        catch (Exception exception)
        {
            SetStatus($"读取内核状态失败：{exception.Message}");
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task SaveGeneralSettingsAsync()
    {
        if (_settingsCoreSelector?.SelectedItem is not CoreDefinition definition || _mixedPortInput is null || _tunNameInput is null)
        {
            return;
        }

        _settings.SelectedCoreId = definition.Id;
        var requestedMixedPort = Decimal.ToInt32(_mixedPortInput.Value);
        var mixedPortChanged = requestedMixedPort != _settings.MixedProxyPort;
        _settings.MixedProxyPort = requestedMixedPort;
        _settings.TunInterfaceName = string.IsNullOrWhiteSpace(_tunNameInput.Text) ? "WFly" : _tunNameInput.Text.Trim();
        await _settingsStore.SaveAsync(_settings);
        var currentPortDescription = _runningMixedProxyPort is { } runningPort
            ? runningPort.ToString()
            : "原生配置的监听端口";
        SetStatus(mixedPortChanged && _processService.IsRunning
            ? $"设置已保存；本地混合端口将在下次启动内核后应用，当前会话仍使用 {currentPortDescription}。"
            : "设置已保存。");
    }

    private async Task SaveGeoFilesSettingsAsync()
    {
        if (_isLoading || _geoFilesSmartRoutingCheckBox is null)
        {
            return;
        }

        _settings.GeoFilesSmartRoutingEnabled = _geoFilesSmartRoutingCheckBox.Checked;
        await _settingsStore.SaveAsync(_settings);
        await RefreshSettingsPageAsync();
        var takesEffectAfterRestart = _processService.IsRunning &&
                                      _settings.RoutingMode == ProxyRoutingMode.Rules &&
                                      string.Equals(_runningCoreId, "sing-box", StringComparison.OrdinalIgnoreCase);
        SetStatus(takesEffectAfterRestart
            ? "GeoFiles 智能分流设置已保存；重启 sing-box 后生效。"
            : "GeoFiles 智能分流设置已保存。");
        PostLog("GEO", _settings.GeoFilesSmartRoutingEnabled ? "已启用 GeoFiles 智能分流。" : "已关闭 GeoFiles 智能分流。");
    }

    private async Task<string> GetGeoFilesStatusAsync()
    {
        if (!_settings.GeoFilesSmartRoutingEnabled)
        {
            return "已关闭";
        }

        if (!_geoFileService.HasSmartRoutingFiles())
        {
            return "未下载";
        }

        var state = await _geoFileService.GetStateAsync();
        var latest = state.Files.Values
            .OrderByDescending(static item => item.UpdatedAt)
            .FirstOrDefault();
        return latest is null
            ? "已就绪"
            : "已下载";
    }

    private async Task UpdateGeoFilesAsync()
    {
        var progress = new Progress<GeoFileDownloadProgress>(item =>
        {
            UpdateGeoFilesGridProgress(item);
        });
        try
        {
            _geoFilesDownloadActive = true;
            _geoFilesDownloadPercent = 0;
            var completed = await RunOperationAsync("正在更新 GeoFiles 智能分流数据…", async cancellationToken =>
            {
                await _geoFileService.UpdateSmartRoutingFilesAsync(progress, cancellationToken);
                PostLog("GEO", "GeoFiles 智能分流数据已更新到 data/geofiles。 ");
            });
            if (completed)
            {
                var takesEffectAfterRestart = _processService.IsRunning &&
                                              _settings.RoutingMode == ProxyRoutingMode.Rules &&
                                              string.Equals(_runningCoreId, "sing-box", StringComparison.OrdinalIgnoreCase);
                SetStatus(takesEffectAfterRestart
                    ? "GeoFiles 已更新；重启 sing-box 后生效。"
                    : "GeoFiles 已更新，将在规则模式启动 sing-box 时生效。");
            }
        }
        finally
        {
            _geoFilesDownloadActive = false;
            _geoFilesDownloadPercent = 0;
            await RefreshSettingsPageAsync();
        }
    }

    private void UpdateGeoFilesGridProgress(GeoFileDownloadProgress item)
    {
        _geoFilesDownloadPercent = Math.Clamp(item.Percent, 0, 100);
        if (_coreGrid is null || _coreGrid.IsDisposed)
        {
            return;
        }

        foreach (DataGridViewRow row in _coreGrid.Rows)
        {
            if (!ReferenceEquals(row.Tag, GeoFilesGridRowTag))
            {
                continue;
            }

            row.Cells["State"].Value = item.Percent > 0
                ? $"下载中 {item.Ordinal}/{item.Total} {item.Percent}%"
                : $"{item.Ordinal}/{item.Total} {item.Status}";
            row.Cells["Installed"].Value = string.Empty;
            row.Cells["Installed"].ToolTipText = string.Empty;
            _coreGrid.InvalidateRow(row.Index);
            break;
        }
    }

    private async Task ImportNativeConfigAsync()
    {
        var targetDefinition = _settingsCoreSelector?.SelectedItem as CoreDefinition
            ?? CoreRegistry.GetById(_settings.SelectedCoreId);
        if (targetDefinition is null || !IsNativeConfigCore(targetDefinition.Id))
        {
            MessageBox.Show(this, "请先在“默认内核”中选择 Mihomo 或 Xray-core，再导入其原生配置。", "WFly", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            Filter = "核心配置 (*.json;*.yaml;*.yml)|*.json;*.yaml;*.yml|所有文件 (*.*)|*.*",
            Title = "导入 Mihomo 或 Xray-core 原生配置",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await RunOperationAsync("正在导入原生配置到 data…", async cancellationToken =>
        {
            var sourcePath = Path.GetFullPath(dialog.FileName);
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (extension is not ".json" and not ".yaml" and not ".yml")
            {
                throw new InvalidDataException("原生配置仅允许 JSON、YAML 或 YML 文件。");
            }

            if (string.Equals(targetDefinition.Id, "xray-core", StringComparison.OrdinalIgnoreCase) && extension != ".json")
            {
                throw new InvalidDataException("Xray-core 原生配置必须是 JSON 文件。");
            }

            var sourceInfo = new FileInfo(sourcePath);
            if (!sourceInfo.Exists || sourceInfo.Length == 0 || sourceInfo.Length > 10 * 1024 * 1024)
            {
                throw new InvalidDataException("原生配置必须存在且大小在 1–10 MB 之间。");
            }

            _paths.EnsureDirectories();
            var targetPath = Path.Combine(_paths.ProfilesDirectory, $"native-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{extension}");
            var temporaryPath = targetPath + ".tmp";
            try
            {
                await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }

                File.Move(temporaryPath, targetPath, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            SetNativeConfigPath(targetDefinition.Id, targetPath);
            await _settingsStore.SaveAsync(_settings, cancellationToken);
            PostLog("CFG", "已将原生配置导入 data/profiles。 ");
        });
    }

    private async Task DownloadSelectedCoreAsync()
    {
        var definition = _coreGrid?.CurrentRow?.Tag as CoreDefinition;

        definition ??= _settingsCoreSelector?.SelectedItem as CoreDefinition ?? CoreRegistry.GetById(_settings.SelectedCoreId);
        if (definition is null)
        {
            return;
        }

        await DownloadCoresAsync([definition], isBatch: false);
    }

    private async Task DownloadAllCoresAsync() =>
        await DownloadCoresAsync(CoreRegistry.All, isBatch: true);

    private async Task DownloadCoreFromGridAsync(DataGridViewCellEventArgs args)
    {
        if (_coreGrid is null ||
            args.RowIndex < 0 ||
            args.ColumnIndex < 0 ||
            _coreGrid.Columns[args.ColumnIndex].Name != "Installed" ||
            _operationBusy)
        {
            return;
        }

        var row = _coreGrid.Rows[args.RowIndex];
        if (ReferenceEquals(row.Tag, GeoFilesGridRowTag) &&
            string.Equals(Convert.ToString(row.Cells["Installed"].Value), "更新", StringComparison.Ordinal))
        {
            await UpdateGeoFilesAsync();
            return;
        }

        if (row.Tag is not CoreDefinition definition ||
            !string.Equals(Convert.ToString(row.Cells["Installed"].Value), "下载", StringComparison.Ordinal))
        {
            return;
        }

        await DownloadCoresAsync([definition], isBatch: false);
    }

    private void UpdateCoreGridCursor(DataGridViewCellMouseEventArgs args)
    {
        if (_coreGrid is null || args.RowIndex < 0 || args.ColumnIndex < 0)
        {
            return;
        }

        var row = _coreGrid.Rows[args.RowIndex];
        var cellText = Convert.ToString(row.Cells["Installed"].Value);
        var isDownloadLink = _coreGrid.Columns[args.ColumnIndex].Name == "Installed" &&
                             (string.Equals(cellText, "下载", StringComparison.Ordinal) ||
                              (ReferenceEquals(row.Tag, GeoFilesGridRowTag) && string.Equals(cellText, "更新", StringComparison.Ordinal)));
        _coreGrid.Cursor = isDownloadLink ? Cursors.Hand : Cursors.Default;
    }

    private async Task DownloadCoresAsync(IEnumerable<CoreDefinition> definitions, bool isBatch)
    {
        var requested = definitions
            .GroupBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (requested.Length == 0)
        {
            return;
        }

        var status = isBatch
            ? "正在检查全部内核的官方版本…"
            : $"正在检查 {requested[0].DisplayName} 官方版本…";
        BeginCoreDownloadProgress(requested);
        await RunOperationAsync(status, cancellationToken => DownloadRequestedCoresAsync(requested, isBatch, cancellationToken));
    }

    private async Task DownloadRequestedCoresAsync(
        IReadOnlyList<CoreDefinition> definitions,
        bool isBatch,
        CancellationToken cancellationToken)
    {
        var pending = new List<(CoreDefinition Definition, CoreRelease Release)>();
        var skippedCount = 0;
        var failures = new List<string>();

        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            try
            {
                SetCoreDownloadState(definition, "检查中");
                SetCoreDownloadProgressIndeterminate(definition, isBatch
                    ? $"检查 {index + 1}/{definitions.Count}"
                    : "检查版本");
                SetStatus(isBatch
                    ? $"正在检查（{index + 1}/{definitions.Count}）{definition.DisplayName} 官方版本…"
                    : $"正在检查 {definition.DisplayName} 官方版本…");
                var release = await _catalogService.GetLatestAsync(definition, cancellationToken);
                var current = await _installedCoreStore.GetLatestAsync(definition.Id, cancellationToken);
                if (current is not null
                    && string.Equals(current.Version, release.Version, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(current.ExecutablePath))
                {
                    skippedCount++;
                    SetCoreDownloadState(definition, "已是最新");
                    PostLog("CORE", $"{definition.DisplayName} 已是最新版本 {release.Version}，未重复下载。");
                    continue;
                }

                SetCoreDownloadState(definition, "等待下载");
                pending.Add((definition, release));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetCoreDownloadState(definition, "已取消");
                CompleteCoreDownloadProgress(definition, "已取消", 0);
                EndCoreDownloadProgress();
                throw;
            }
            catch (Exception exception)
            {
                SetCoreDownloadState(definition, "检查失败");
                if (!isBatch)
                {
                    CompleteCoreDownloadProgress(definition, "检查失败", 0);
                    EndCoreDownloadProgress();
                    throw;
                }

                failures.Add($"{definition.DisplayName}：{exception.Message}");
                PostLog("CORE", $"检查 {definition.DisplayName} 失败：{exception.Message}");
            }
        }

        if (pending.Count == 0)
        {
            EndCoreDownloadProgress();
            await RefreshSettingsPageAsync();
            if (isBatch && failures.Count > 0)
            {
                ShowBatchCoreDownloadSummary(0, skippedCount, failures);
            }
            else
            {
                SetStatus("所有选中的内核均已是最新版本。");
            }

            return;
        }

        var confirmation = BuildCoreDownloadConfirmation(pending, isBatch);
        if (MessageBox.Show(this, confirmation, "确认下载内核", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
        {
            foreach (var (definition, _) in pending)
            {
                SetCoreDownloadState(definition, "已取消");
                CompleteCoreDownloadProgress(definition, "已取消", 0);
            }

            EndCoreDownloadProgress();
            SetStatus("已取消下载。");
            return;
        }

        var installedCount = 0;
        for (var index = 0; index < pending.Count; index++)
        {
            var (definition, release) = pending[index];
            try
            {
                var progressPrefix = isBatch ? $"（{index + 1}/{pending.Count}）{definition.DisplayName}：" : string.Empty;
                SetCoreDownloadState(definition, "准备下载");
                var progress = new Progress<DownloadProgress>(value =>
                {
                    UpdateCoreDownloadProgress(definition, value);
                    SetStatus(value.Percentage is { } percentage
                        ? $"{progressPrefix}{value.Stage} {percentage}%"
                        : $"{progressPrefix}{value.Stage}");
                });
                var installed = await _installer.InstallAsync(definition, release, progress, cancellationToken);
                installedCount++;
                SetCoreDownloadState(definition, "已安装");
                CompleteCoreDownloadProgress(definition, "已安装", 100);
                PostLog("CORE", $"{definition.DisplayName} {installed.Version} 已下载、校验并安装。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetCoreDownloadState(definition, "已取消");
                CompleteCoreDownloadProgress(definition, "已取消", 0);
                EndCoreDownloadProgress();
                throw;
            }
            catch (Exception exception)
            {
                SetCoreDownloadState(definition, "下载失败");
                CompleteCoreDownloadProgress(definition, "下载失败", 0);
                if (!isBatch)
                {
                    EndCoreDownloadProgress();
                    throw;
                }

                failures.Add($"{definition.DisplayName}：{exception.Message}");
                PostLog("CORE", $"下载 {definition.DisplayName} 失败：{exception.Message}");
            }
        }

        EndCoreDownloadProgress();
        await RefreshSettingsPageAsync();
        if (isBatch)
        {
            ShowBatchCoreDownloadSummary(installedCount, skippedCount, failures);
        }
    }

    private static string BuildCoreDownloadConfirmation(
        IReadOnlyList<(CoreDefinition Definition, CoreRelease Release)> pending,
        bool isBatch)
    {
        if (!isBatch)
        {
            var (definition, release) = pending[0];
            return $"将从固定官方仓库 {definition.GitHubOwner}/{definition.GitHubRepository} 下载：\n\n" +
                $"内核：{definition.DisplayName}\n版本：{release.Version}\n文件：{release.Asset.Name}\n大小：{FormatBytes(release.Asset.Size)}\nSHA-256：{release.Asset.Sha256}\n\n下载后会校验 SHA-256 并安全解压。是否继续？";
        }

        var confirmation = new StringBuilder("将按顺序从以下固定官方仓库下载内核：\n");
        foreach (var (definition, release) in pending)
        {
            confirmation.Append($"\n{definition.DisplayName}（{definition.GitHubOwner}/{definition.GitHubRepository}）\n")
                .Append($"版本：{release.Version}\n")
                .Append($"文件：{release.Asset.Name}\n")
                .Append($"大小：{FormatBytes(release.Asset.Size)}\n")
                .Append($"SHA-256：{release.Asset.Sha256}\n");
        }

        confirmation.Append("\n每个归档均会校验 SHA-256 并安全解压。是否继续？");
        return confirmation.ToString();
    }

    private void ShowBatchCoreDownloadSummary(int installedCount, int skippedCount, IReadOnlyList<string> failures)
    {
        var summary = new StringBuilder($"下载完成：已安装 {installedCount} 个，已是最新 {skippedCount} 个。");
        if (failures.Count > 0)
        {
            summary.Append("\n\n以下内核未完成：\n")
                .Append(string.Join("\n", failures));
        }

        PostLog("CORE", summary.ToString());
        SetStatus(summary.ToString().Replace(Environment.NewLine, " "));
        MessageBox.Show(
            this,
            summary.ToString(),
            "下载所有内核",
            MessageBoxButtons.OK,
            failures.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void SetCoreDownloadState(CoreDefinition definition, string state)
    {
        _coreDownloadStates[definition.Id] = state;
        if (_coreGrid is null || _coreGrid.IsDisposed)
        {
            return;
        }

        foreach (DataGridViewRow row in _coreGrid.Rows)
        {
            if (row.Tag is CoreDefinition rowDefinition &&
                string.Equals(rowDefinition.Id, definition.Id, StringComparison.OrdinalIgnoreCase))
            {
                row.Cells["State"].Value = state;
                // An uninstalled row initially exposes a "下载" link. Once
                // that core joins the workflow, clear the link immediately;
                // the state column and the in-cell progress line describe the
                // operation without leaving a second, misleading action.
                if (_coreDownloadProgressActive &&
                    string.Equals(Convert.ToString(row.Cells["Installed"].Value), "下载", StringComparison.Ordinal))
                {
                    row.Cells["Installed"].Value = string.Empty;
                    row.Cells["Installed"].ToolTipText = string.Empty;
                }
                break;
            }
        }
    }

    private void BeginCoreDownloadProgress(IEnumerable<CoreDefinition> definitions)
    {
        _coreDownloadProgressActive = true;
        foreach (var definition in definitions)
        {
            _coreDownloadPercentages[definition.Id] = 0;
        }

        _coreGrid?.Invalidate();
    }

    private void SetCoreDownloadProgressIndeterminate(CoreDefinition definition, string text)
    {
        _coreDownloadPercentages[definition.Id] = 0;
        _coreGrid?.Invalidate();
    }

    private void UpdateCoreDownloadProgress(CoreDefinition definition, DownloadProgress progress)
    {
        if (progress.Percentage is not { } percentage)
        {
            SetCoreDownloadState(definition, progress.Stage);
            SetCoreDownloadProgressIndeterminate(definition, progress.Stage);
            return;
        }

        SetCoreDownloadState(definition, $"下载中 {percentage}%");
        _coreDownloadPercentages[definition.Id] = Math.Clamp(percentage, 0, 100);
        _coreGrid?.Invalidate();
    }

    private void CompleteCoreDownloadProgress(CoreDefinition definition, string text, int? percentage)
    {
        _coreDownloadPercentages[definition.Id] = string.Equals(text, "已安装", StringComparison.Ordinal)
            ? 0
            : Math.Clamp(percentage ?? _coreDownloadPercentages.GetValueOrDefault(definition.Id), 0, 100);
        _coreGrid?.Invalidate();
    }

    private void EndCoreDownloadProgress()
    {
        _coreDownloadProgressActive = false;
    }

    private void SetIdleCoreDownloadProgress(CoreDefinition definition, bool isInstalled, string status)
    {
        if (_coreDownloadProgressActive)
        {
            return;
        }

        if (isInstalled)
        {
            _coreDownloadPercentages[definition.Id] = 0;
            return;
        }

        _coreDownloadPercentages[definition.Id] = 0;
    }

    private async Task StartSelectedNodeAsync()
    {
        var node = SelectedNode;
        if (node is null)
        {
            MessageBox.Show(this, "请先创建节点组并添加或更新一个节点。", "WFly", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!node.IsEnabled)
        {
            MessageBox.Show(this, "该节点当前已停用，请在“节点”页面启用后再启动。", "WFly", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await RunOperationAsync("正在生成配置并启动内核…", async cancellationToken =>
        {
            var definition = CoreRegistry.GetById(node.CoreId)
                ?? throw new InvalidOperationException($"不支持的节点内核：{node.CoreId}。");
            var installed = await _installedCoreStore.GetLatestAsync(definition.Id, cancellationToken)
                ?? throw new InvalidOperationException($"尚未安装 {definition.DisplayName}。请前往“设置”下载安装。");
            if (!File.Exists(installed.ExecutablePath))
            {
                throw new FileNotFoundException("已安装内核文件不存在，请重新下载安装。", installed.ExecutablePath);
            }

            string configurationPath;
            if (string.Equals(definition.Id, "sing-box", StringComparison.OrdinalIgnoreCase))
            {
                var profile = await _profileGenerationService.GenerateSingBoxAsync(
                    node,
                    _ruleSets,
                    _settings,
                    _settings.ProxyMode == ProxyMode.Tun,
                    _settings.RoutingMode,
                    cancellationToken);
                configurationPath = profile.Path;
                await ConfigValidator.ValidateAsync(configurationPath, cancellationToken);
            }
            else
            {
                if (_settings.ProxyMode == ProxyMode.Tun)
                {
                    throw new NotSupportedException("WFly 的一键 TUN 配置目前仅由 sing-box 生成。请切换到 sing-box，或在 Mihomo 原生配置中自行维护 TUN。 ");
                }

                if (_settings.ProxyMode == ProxyMode.SystemProxy)
                {
                    throw new NotSupportedException("为避免将 Windows 系统代理指向未知监听端口，自动系统代理目前仅支持 WFly 生成的 sing-box 配置。请切换为关闭代理，或使用 sing-box 节点。");
                }

                configurationPath = ValidateNativeConfigurationPath(definition.Id, GetNativeConfigPath(definition.Id));
                if (string.Equals(definition.Id, "xray-core", StringComparison.OrdinalIgnoreCase))
                {
                    await ConfigValidator.ValidateAsync(configurationPath, cancellationToken);
                }
            }

            _runningCoreId = definition.Id;
            _runningMixedProxyPort = string.Equals(definition.Id, "sing-box", StringComparison.OrdinalIgnoreCase)
                ? _settings.MixedProxyPort
                : null;
            try
            {
                ResetTrafficStatistics();
                await _processService.StartAsync(installed.ExecutablePath, definition.BuildStartArguments(configurationPath), cancellationToken);
                if (!_processService.IsRunning)
                {
                    throw new InvalidOperationException($"{definition.DisplayName} 在启动后立即退出。请检查运行日志和配置。");
                }
            }
            catch
            {
                _runningCoreId = null;
                _runningMixedProxyPort = null;
                throw;
            }

            _settings.SelectedCoreId = definition.Id;
            if (_settings.ProxyMode == ProxyMode.SystemProxy)
            {
                try
                {
                    await ApplySystemProxyAsync();
                }
                catch
                {
                    await _processService.StopAsync(cancellationToken);
                    throw;
                }
            }

            await _settingsStore.SaveAsync(_settings, cancellationToken);
            PostLog("SYS", $"已启动 {definition.DisplayName}：{node.Name}。");
        });
    }

    private async Task<bool> StopRunningCoreAsync()
    {
        return await RunOperationAsync("正在停止内核…", async cancellationToken =>
        {
            await _processService.StopAsync(cancellationToken);
            await RestoreSystemProxyAsync();
            PostLog("SYS", "内核已停止。");
        });
    }

    private static string ValidateNativeConfigurationPath(string coreId, string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            throw new InvalidOperationException($"请先在“设置”中导入 {GetCoreDisplay(coreId)} 原生配置文件。");
        }

        var fullPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("已导入的原生配置不存在，请重新导入。", fullPath);
        }

        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (string.Equals(coreId, "mihomo", StringComparison.OrdinalIgnoreCase) && extension is not ".yaml" and not ".yml" and not ".json")
        {
            throw new InvalidDataException("Mihomo 原生配置必须是 YAML、YML 或 JSON 文件。");
        }

        return fullPath;
    }

    private string? GetNativeConfigPath(string coreId)
    {
        if (!IsNativeConfigCore(coreId) || _settings.NativeConfigPaths is null)
        {
            return null;
        }

        return _settings.NativeConfigPaths.TryGetValue(coreId, out var path) && !string.IsNullOrWhiteSpace(path)
            ? path
            : null;
    }

    private void SetNativeConfigPath(string coreId, string path)
    {
        if (!IsNativeConfigCore(coreId))
        {
            throw new InvalidOperationException("只有 Mihomo 和 Xray-core 可以保存原生配置路径。");
        }

        _settings.NativeConfigPaths ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _settings.NativeConfigPaths[coreId] = Path.GetFullPath(path);
    }

    private bool NormalizeNativeConfigSettings()
    {
        var hadNativeConfigPaths = _settings.NativeConfigPaths is not null;
        _settings.NativeConfigPaths ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(_settings.ConfigPath))
        {
            return !hadNativeConfigPaths;
        }

        var legacyPath = _settings.ConfigPath;
        _settings.ConfigPath = null;
        var selectedCoreId = CoreRegistry.GetById(_settings.SelectedCoreId)?.Id;
        if (selectedCoreId is not null &&
            IsNativeConfigCore(selectedCoreId) &&
            !Path.GetFileName(legacyPath).StartsWith("runtime-", StringComparison.OrdinalIgnoreCase) &&
            !_settings.NativeConfigPaths.ContainsKey(selectedCoreId))
        {
            _settings.NativeConfigPaths[selectedCoreId] = legacyPath;
        }

        return true;
    }

    private static bool IsNativeConfigCore(string? coreId) =>
        string.Equals(coreId, "mihomo", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(coreId, "xray-core", StringComparison.OrdinalIgnoreCase);

    private bool TryGetActiveMixedProxyPort(out int runningPort)
    {
        if (_processService.IsRunning &&
            string.Equals(_runningCoreId, "sing-box", StringComparison.OrdinalIgnoreCase) &&
            _runningMixedProxyPort is { } activePort)
        {
            runningPort = activePort;
            return true;
        }

        runningPort = 0;
        return false;
    }

    private async Task ApplySystemProxyAsync()
    {
        if (!_processService.IsRunning ||
            !string.Equals(_runningCoreId, "sing-box", StringComparison.OrdinalIgnoreCase) ||
            _runningMixedProxyPort is not { } runningPort)
        {
            throw new NotSupportedException("为避免将 Windows 系统代理指向未知监听端口，自动系统代理只适用于当前由 WFly 生成配置运行的 sing-box 会话。");
        }

        var result = _systemProxyService.ApplyLoopbackProxy(runningPort, _settings.SystemProxyLease);
        _settings.SystemProxyLease = result.Lease;
        await _settingsStore.SaveAsync(_settings);
        SetStatus(result.SettingsRefreshNotified ? "系统代理已指向本机监听端口。" : "系统代理已写入，但 Windows 刷新通知未确认。");
    }

    private async Task RestoreSystemProxyAsync()
    {
        var result = _systemProxyService.RestoreIfOwned(_settings.SystemProxyLease);
        if (result.Status == WindowsSystemProxyRestoreStatus.Restored ||
            result.Status == WindowsSystemProxyRestoreStatus.NoLease ||
            result.Status == WindowsSystemProxyRestoreStatus.CurrentSettingsChanged)
        {
            _settings.SystemProxyLease = null;
            await _settingsStore.SaveAsync(_settings);
            SetStatus(result.Status == WindowsSystemProxyRestoreStatus.Restored ? "已恢复先前的系统代理设置。" : "系统代理未由 WFly 接管。");
        }
        else
        {
            SetStatus("检测到系统代理已被其他程序或用户修改；WFly 未覆盖该修改。");
        }
    }

    private async Task RefreshDueSubscriptionsAsync()
    {
        if (_operationBusy)
        {
            return;
        }

        var dueGroups = _groups.Where(group =>
            !string.IsNullOrWhiteSpace(group.SubscriptionUrl) &&
            group.UpdateIntervalHours is { } hours &&
            DateTimeOffset.UtcNow - (group.LastUpdatedAt ?? group.CreatedAt) >= TimeSpan.FromHours(hours)).ToArray();
        foreach (var group in dueGroups)
        {
            try
            {
                var result = await _subscriptionProfileService.RefreshGroupAsync(group, _proxyNodeStore);
                var updatedGroup = await _nodeGroupStore.GetAsync(group.Id) ?? group;
                if (string.Equals(updatedGroup.CoreId, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    updatedGroup.CoreId = result.DetectedCoreId;
                    await _nodeGroupStore.SaveAsync(updatedGroup);
                }

                await _nodeGroupStore.RecordRefreshResultAsync(group.Id, DateTimeOffset.UtcNow, null);
                PostLog("SUB", $"节点组“{group.Name}”已更新 {result.Nodes.Count} 个节点（来源 {result.SourceHost}）。");
            }
            catch (Exception exception)
            {
                await _nodeGroupStore.RecordRefreshResultAsync(group.Id, DateTimeOffset.UtcNow, exception.Message);
                PostLog("SUB", $"节点组“{group.Name}”更新失败：{exception.Message}");
            }
        }

        if (dueGroups.Length > 0)
        {
            await RefreshGroupsAsync();
            await RefreshCurrentNodesAsync();
            RefreshVisiblePage();
        }
    }

    private async Task<bool> RunOperationAsync(
        string status,
        Func<CancellationToken, Task> operation,
        bool showErrorDialog = true)
    {
        if (_operationBusy)
        {
            return false;
        }

        _operationBusy = true;
        _operationCancellation = new CancellationTokenSource();
        SetStatus(status);
        try
        {
            await operation(_operationCancellation.Token);
            if (!IsDisposed)
            {
                SetStatus("完成。");
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消。");
            return false;
        }
        catch (Exception exception)
        {
            if (showErrorDialog)
            {
                ShowError(status, exception);
            }
            else
            {
                // Subscription refresh errors are already persisted against the
                // node group. Keep the page usable and route the safe error
                // summary to the status bar and in-memory log instead of
                // blocking the whole window with a modal dialog.
                var summary = $"{status.TrimEnd('…')}失败：{exception.Message}";
                PostLog("SUB", summary);
                SetStatus($"{summary}。详情见节点组状态或日志。");
            }
            return false;
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            _operationBusy = false;
            if (!IsDisposed)
            {
                RefreshVisiblePage();
            }

            // A tray exit requested while an operation was in flight first
            // cancels that operation.  Close only after its finally block has
            // released the busy state, so the normal proxy/core cleanup path
            // can run without a second user action.
            if (_exitRequestedFromTray && !IsDisposed && IsHandleCreated)
            {
                try
                {
                    BeginInvoke(new Action(Close));
                }
                catch (InvalidOperationException)
                {
                    // The window can be disposed between the state check and
                    // BeginInvoke during shutdown.
                }
            }
        }
    }

    private async Task ExportLogsAsync()
    {
        _paths.EnsureDirectories();
        using var dialog = new SaveFileDialog
        {
            InitialDirectory = _paths.ExportsDirectory,
            FileName = $"wfly-log-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            Title = "导出运行日志",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var lines = _logStore.Snapshot().Select(entry => entry.DisplayText).ToArray();
        await File.WriteAllLinesAsync(dialog.FileName, lines, new UTF8Encoding(false));
        SetStatus($"日志已导出：{Path.GetFileName(dialog.FileName)}");
    }

    private void OnCoreLogReceived(CoreLogEntry entry) => _logStore.Add(entry);

    private void OnCoreRunningStateChanged(bool isRunning)
    {
        // The process event may be queued behind a rapid Stop → Start mode
        // switch.  Only clear this session's metadata when there is still no
        // live core at the moment the notification is handled.
        if (!isRunning && !_processService.IsRunning)
        {
            _runningCoreId = null;
            _runningMixedProxyPort = null;
        }

        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed || Disposing)
                {
                    return;
                }

                // A queued "stopped" notification from the old process can
                // arrive after a mode switch has already started a new core.
                // Never let that stale event reset the fresh session.
                if (!isRunning && _processService.IsRunning)
                {
                    RefreshHomePage();
                    RefreshTestsPage();
                    return;
                }

                if (!isRunning)
                {
                    ResetTrafficCounterBaseline();
                }

                SetStatus(isRunning ? "内核正在运行。" : "内核已停止。");
                if (!isRunning && !_proxyModeTransitionBusy && !_closeCleanupInProgress)
                {
                    _ = HandleUnexpectedCoreExitAsync();
                }

                RefreshHomePage();
                RefreshTestsPage();
            });
        }
        catch (InvalidOperationException)
        {
            // The window handle can disappear between the checks and BeginInvoke.
        }
    }

    private async Task HandleUnexpectedCoreExitAsync()
    {
        if (_proxyModeTransitionBusy || _closeCleanupInProgress || _processService.IsRunning)
        {
            return;
        }

        // A process can terminate independently of a user click.  Move the
        // selector before awaiting any I/O so it never advertises an active
        // system proxy or TUN session that no longer has a core behind it.
        var hadActiveMode = _settings.ProxyMode != ProxyMode.Off;
        _settings.ProxyMode = ProxyMode.Off;
        if (hadActiveMode && _proxyModeSelector is not null && _proxyModeSelector.Mode != ProxyMode.Off)
        {
            _proxyModeSelector.Mode = ProxyMode.Off;
        }

        try
        {
            await RestoreSystemProxyAsync();
            await _settingsStore.SaveAsync(_settings);
            if (hadActiveMode)
            {
                PostLog("SYS", "内核已意外退出，代理开关已回到关闭位置。");
            }
        }
        catch (Exception exception)
        {
            PostLog("ERR", $"内核退出后的系统代理恢复失败：{exception.Message}");
            SetStatus("内核已停止；系统代理恢复失败，请在设置中检查状态。");
        }
        finally
        {
            RefreshHomePage();
        }
    }

    private void OnRuntimeLogAdded(RuntimeLogEntry entry)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && !Disposing)
                {
                    AppendLogToView(entry);
                }
            });
        }
        catch (InvalidOperationException)
        {
            // The form can be disposed just after a background log arrives.
        }
    }

    private void RenderLogs()
    {
        if (_logBox is null || _logBox.IsDisposed)
        {
            return;
        }

        _logBox.Text = string.Join(Environment.NewLine, _logStore.Snapshot().Select(entry => entry.DisplayText));
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private void AppendLogToView(RuntimeLogEntry entry)
    {
        if (_logBox is null || _logBox.IsDisposed)
        {
            return;
        }

        _logBox.AppendText((_logBox.TextLength == 0 ? string.Empty : Environment.NewLine) + entry.DisplayText);
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private void PostLog(string category, string message) => _logStore.AddInfo(category, message);

    private async void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowCloseAfterCleanup)
        {
            return;
        }

        // Keep the proxy core running when the user closes the main window;
        // closing from the tray menu sets the explicit flag and continues into
        // the existing safe shutdown path below.
        if (eventArgs.CloseReason == CloseReason.UserClosing && !_exitRequestedFromTray)
        {
            eventArgs.Cancel = true;
            HideToTray(showHint: true);
            return;
        }

        // Windows shutdown cannot wait for an async UI cleanup cycle. Restore
        // the physical WinINet setting synchronously, but never synchronously
        // wait on SettingsStore's async I/O from the UI context.
        if (eventArgs.CloseReason == CloseReason.WindowsShutDown)
        {
            try
            {
                _systemProxyService.RestoreIfOwned(_settings.SystemProxyLease);
            }
            catch
            {
                // Shutdown must continue even if the registry is unavailable.
            }

            return;
        }

        if (_closeCleanupInProgress)
        {
            eventArgs.Cancel = true;
            return;
        }

        if (_operationBusy)
        {
            var choice = MessageBox.Show(this, "当前操作尚未完成。是否取消操作并退出？", "WFly", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice != DialogResult.Yes)
            {
                eventArgs.Cancel = true;
                _exitRequestedFromTray = false;
                return;
            }

            _operationCancellation?.Cancel();
            eventArgs.Cancel = true;
            SetStatus("正在取消操作；请在操作结束后再次关闭窗口。");
            return;
        }

        eventArgs.Cancel = true;
        _closeCleanupInProgress = true;
        try
        {
            if (_processService.IsRunning)
            {
                await _processService.StopAsync();
            }
        }
        catch
        {
            // Continue with the independently safe system-proxy restore below.
        }

        try
        {
            // A stop failure must never prevent the conditional WinINet
            // restore.  RestoreIfOwned refuses to overwrite a user or other
            // application's later change, so it is safe to attempt here.
            var result = _systemProxyService.RestoreIfOwned(_settings.SystemProxyLease);
            if (result.Status is WindowsSystemProxyRestoreStatus.Restored or
                WindowsSystemProxyRestoreStatus.NoLease or
                WindowsSystemProxyRestoreStatus.CurrentSettingsChanged)
            {
                _settings.SystemProxyLease = null;
                await _settingsStore.SaveAsync(_settings);
            }
        }
        catch
        {
            // Closing the UI must not hide the user's ability to close it.
        }
        finally
        {
            _closeCleanupInProgress = false;
            _allowCloseAfterCleanup = true;
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(new Action(Close));
            }
        }
    }

    private NodeGroup? SelectedGroup => _groups.FirstOrDefault(group => string.Equals(group.Id, _settings.SelectedNodeGroupId, StringComparison.Ordinal));

    private ProxyNode? SelectedNode => _currentNodes.FirstOrDefault(node => string.Equals(node.Id, _settings.SelectedNodeId, StringComparison.Ordinal));

    private void SetStatus(string text)
    {
        if (!IsDisposed)
        {
            _statusLabel.Text = text;
        }
    }

    private void ShowError(string title, Exception exception)
    {
        var message = exception.Message;
        PostLog("ERR", $"{title}：{message}");
        SetStatus($"{title}失败。详见日志。");
        if (!IsDisposed)
        {
            MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Panel CreateScrollablePage() => new()
    {
        AutoScroll = true,
        BackColor = UiPalette.Canvas,
        Padding = new Padding(2, 2, 10, 16),
    };

    /// <summary>
    /// A real vertical layout surface for simple pages.  A plain scrolling
    /// Panel gives every non-docked child the default (0, 0) position, which
    /// was the cause of title/filter/action overlap on the node pages.
    /// </summary>
    private static TableLayoutPanel CreateVerticalPageLayout()
    {
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 0,
            BackColor = UiPalette.Canvas,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        return layout;
    }

    private static void AddPageRow(TableLayoutPanel layout, Control control, int? fixedHeight = null)
    {
        var row = layout.RowCount;
        layout.RowCount++;
        layout.RowStyles.Add(fixedHeight is { } height
            ? new RowStyle(SizeType.Absolute, height)
            : new RowStyle(SizeType.AutoSize));
        control.Dock = fixedHeight.HasValue ? DockStyle.Fill : DockStyle.Top;
        layout.Controls.Add(control, 0, row);
    }

    private static FrostedGroupBox CreateGroup(string title) => new()
    {
        Text = title,
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
    };

    private static Label AddValueRow(TableLayoutPanel layout, string label, int row)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
        var key = new Label
        {
            AutoSize = true,
            Text = label,
            ForeColor = UiPalette.MutedInk,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 12, 0),
        };
        var value = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "—",
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = Padding.Empty,
        };
        layout.Controls.Add(key, 0, row);
        layout.Controls.Add(value, 1, row);
        return value;
    }

    /// <summary>Builds one compact field for the single-row egress card.</summary>
    private static Control CreateInlineValue(string label, out Label value)
    {
        var field = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 12, 0),
            Padding = Padding.Empty,
        };
        field.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        field.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var key = new Label
        {
            AutoSize = true,
            Text = label,
            ForeColor = UiPalette.MutedInk,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 10, 0),
        };
        value = new Label
        {
            Dock = DockStyle.Fill,
            Text = "—",
            ForeColor = UiPalette.Ink,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = Padding.Empty,
        };
        field.Controls.Add(key, 0, 0);
        field.Controls.Add(value, 1, 0);
        return field;
    }

    private static Label CreateSettingsLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = UiPalette.MutedInk,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 6, 18, 6),
    };

    private static Label CreatePageHeader(string title)
    {
        return new Label
        {
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            Text = title,
            Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold),
            ForeColor = UiPalette.Ink,
            Margin = new Padding(2, 0, 0, 14),
        };
    }

    private static RoundedButton CreatePrimaryButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        BackColor = UiPalette.Accent,
        ForeColor = Color.White,
        HoverBackColor = Color.FromArgb(54, 98, 207),
        PressedBackColor = Color.FromArgb(43, 83, 178),
        CornerRadius = 9,
        Padding = new Padding(14, 6, 14, 6),
        Margin = new Padding(0, 0, 8, 0),
    };

    private static RoundedButton CreateSecondaryButton(string text)
    {
        var button = new RoundedButton
        {
            Text = text,
            AutoSize = true,
            BackColor = UiPalette.Hover,
            ForeColor = UiPalette.Ink,
            HoverBackColor = UiPalette.AccentSoft,
            PressedBackColor = Color.FromArgb(214, 227, 251),
            BorderColor = UiPalette.CardBorder,
            BorderThickness = 1,
            CornerRadius = 9,
            Padding = new Padding(12, 5, 12, 5),
            Margin = new Padding(8, 0, 0, 0),
        };
        return button;
    }

    private void CoreGridCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || _coreGrid is null ||
            _coreGrid.Columns[e.ColumnIndex].Name != "Installed")
        {
            return;
        }

        var rowTag = _coreGrid.Rows[e.RowIndex].Tag;
        var definition = rowTag as CoreDefinition;
        var isGeoFilesRow = ReferenceEquals(rowTag, GeoFilesGridRowTag);
        if (definition is null && !isGeoFilesRow)
        {
            return;
        }

        var graphics = e.Graphics;
        if (graphics is null)
        {
            return;
        }

        e.PaintBackground(e.CellBounds, true);
        e.Paint(e.CellBounds, DataGridViewPaintParts.Border);

        var cellStyle = e.CellStyle ?? _coreGrid.DefaultCellStyle;
        var textBounds = new Rectangle(e.CellBounds.X + 10, e.CellBounds.Y + 1, Math.Max(0, e.CellBounds.Width - 20), Math.Max(0, e.CellBounds.Height - 10));
        var isDownloadLink = string.Equals(Convert.ToString(e.FormattedValue), "下载", StringComparison.Ordinal) ||
                             (isGeoFilesRow && string.Equals(Convert.ToString(e.FormattedValue), "更新", StringComparison.Ordinal));
        var textColor = isDownloadLink ? UiPalette.Accent : cellStyle.ForeColor;
        var textFont = isDownloadLink ? new Font(cellStyle.Font ?? Font, FontStyle.Underline) : cellStyle.Font ?? Font;
        TextRenderer.DrawText(
            graphics,
            Convert.ToString(e.FormattedValue) ?? string.Empty,
            textFont,
            textBounds,
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (isDownloadLink)
        {
            textFont.Dispose();
        }

        var isDownloading = definition is not null &&
                            _coreDownloadStates.TryGetValue(definition.Id, out var downloadState) &&
                            downloadState.StartsWith("下载中", StringComparison.Ordinal);
        var percentage = definition is not null
            ? Math.Clamp(_coreDownloadPercentages.GetValueOrDefault(definition.Id), 0, 100)
            : _geoFilesDownloadPercent;
        isDownloading |= isGeoFilesRow && _geoFilesDownloadActive;
        if (isDownloading && percentage > 0)
        {
            var track = new Rectangle(e.CellBounds.X + 10, e.CellBounds.Bottom - 7, Math.Max(0, e.CellBounds.Width - 20), 5);
            using var trackBrush = new SolidBrush(UiPalette.IsDark ? Color.FromArgb(65, 76, 94) : Color.FromArgb(224, 230, 240));
            graphics.FillRectangle(trackBrush, track);
            var fill = new Rectangle(track.X, track.Y, Math.Max(1, track.Width * percentage / 100), track.Height);
            using var fillBrush = new SolidBrush(percentage == 100 ? Color.FromArgb(22, 137, 83) : UiPalette.Accent);
            graphics.FillRectangle(fillBrush, fill);
        }

        e.Handled = true;
    }

    private static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Top,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        UiControlTheme.ApplyDataGridView(grid);
        return grid;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private static string GetProxyModeDisplay(ProxyMode mode) => mode switch
    {
        ProxyMode.SystemProxy => "系统代理",
        ProxyMode.Tun => "TUN 模式",
        _ => "关闭代理",
    };

    private static string GetRoutingModeDisplay(ProxyRoutingMode mode) => mode switch
    {
        ProxyRoutingMode.Global => "全局",
        ProxyRoutingMode.Direct => "直连",
        _ => "规则",
    };
}

internal static class ControlExtensions
{
    public static void SetTextSafe(this Control control, string text)
    {
        if (!control.IsDisposed)
        {
            control.Text = text;
        }
    }
}
