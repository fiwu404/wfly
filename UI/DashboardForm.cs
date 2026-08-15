using System.ComponentModel;
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
    private readonly SubscriptionProfileService _subscriptionProfileService;
    private readonly ProfileGenerationService _profileGenerationService;
    private readonly CoreProcessService _processService;
    private readonly InMemoryLogStore _logStore;
    private readonly NetworkDiagnosticsService _networkDiagnosticsService;
    private readonly SiteLatencyTestService _siteLatencyTestService;
    private readonly ClashApiClient _clashApiClient;
    private readonly WindowsSystemProxyService _systemProxyService;
    private readonly NetworkTrafficSampler _networkTrafficSampler = new();
    // Keep this aligned with NetworkTrafficSampler's 250 ms minimum delta.
    // The busy guard in RefreshTrafficAsync prevents controller calls overlapping.
    private readonly System.Windows.Forms.Timer _trafficTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _subscriptionTimer = new() { Interval = 5 * 60 * 1_000 };
    private readonly Dictionary<string, Control> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _navigationButtons = new(StringComparer.Ordinal);
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill, BackColor = UiPalette.Canvas };
    private readonly Label _statusLabel = new();

    private AppSettings _settings = new();
    private IReadOnlyList<NodeGroup> _groups = [];
    private IReadOnlyList<ProxyNode> _currentNodes = [];
    private IReadOnlyList<RuleSet> _ruleSets = [];
    private string _currentPage = "首页";
    private CancellationTokenSource? _operationCancellation;
    private bool _operationBusy;
    private bool _isLoading;
    private bool _closeCleanupInProgress;
    private bool _allowCloseAfterCleanup;
    private bool _trafficTickBusy;
    private bool _proxyModeTransitionBusy;
    private string? _runningCoreId;
    private int? _runningMixedProxyPort;
    private long _previousProxyUploadTotal;
    private long _previousProxyDownloadTotal;
    private DateTimeOffset? _previousProxyCounterAt;
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
    private TrafficChartControl? _trafficChart;

    // 节点组 / 节点（由 partial 文件建立）
    private DataGridView? _groupGrid;
    private ComboBox? _nodeGroupSelector;
    private DataGridView? _nodeGrid;

    // 规则
    private ListBox? _ruleSetList;
    private DataGridView? _ruleGrid;
    private RichTextBox? _ruleJsonBox;
    private RuleSet? _activeRuleSet;

    // 日志、测试、连接、设置
    private RichTextBox? _logBox;
    private DataGridView? _testGrid;
    private DataGridView? _connectionGrid;
    private ComboBox? _settingsCoreSelector;
    private DataGridView? _coreGrid;
    private NumericUpDown? _mixedPortInput;
    private TextBox? _tunNameInput;
    private TextBox? _nativeConfigPathTextBox;
    private Label? _installedCoreLabel;

    public DashboardForm(
        AppPaths paths,
        InstalledCoreStore installedCoreStore,
        SettingsStore settingsStore,
        NodeGroupStore nodeGroupStore,
        ProxyNodeStore proxyNodeStore,
        RuleSetStore ruleSetStore,
        CoreCatalogService catalogService,
        CoreInstaller installer,
        SubscriptionProfileService subscriptionProfileService,
        ProfileGenerationService profileGenerationService,
        CoreProcessService processService,
        InMemoryLogStore logStore,
        NetworkDiagnosticsService networkDiagnosticsService,
        SiteLatencyTestService siteLatencyTestService,
        ClashApiClient clashApiClient,
        WindowsSystemProxyService systemProxyService)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _installedCoreStore = installedCoreStore ?? throw new ArgumentNullException(nameof(installedCoreStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _nodeGroupStore = nodeGroupStore ?? throw new ArgumentNullException(nameof(nodeGroupStore));
        _proxyNodeStore = proxyNodeStore ?? throw new ArgumentNullException(nameof(proxyNodeStore));
        _ruleSetStore = ruleSetStore ?? throw new ArgumentNullException(nameof(ruleSetStore));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _subscriptionProfileService = subscriptionProfileService ?? throw new ArgumentNullException(nameof(subscriptionProfileService));
        _profileGenerationService = profileGenerationService ?? throw new ArgumentNullException(nameof(profileGenerationService));
        _processService = processService ?? throw new ArgumentNullException(nameof(processService));
        _logStore = logStore ?? throw new ArgumentNullException(nameof(logStore));
        _networkDiagnosticsService = networkDiagnosticsService ?? throw new ArgumentNullException(nameof(networkDiagnosticsService));
        _siteLatencyTestService = siteLatencyTestService ?? throw new ArgumentNullException(nameof(siteLatencyTestService));
        _clashApiClient = clashApiClient ?? throw new ArgumentNullException(nameof(clashApiClient));
        _systemProxyService = systemProxyService ?? throw new ArgumentNullException(nameof(systemProxyService));

        InitializeComponent();
        _processService.LogReceived += OnCoreLogReceived;
        _processService.RunningStateChanged += OnCoreRunningStateChanged;
        _logStore.EntryAdded += OnRuntimeLogAdded;
        _trafficTimer.Tick += async (_, _) => await RefreshTrafficAsync();
        _subscriptionTimer.Tick += async (_, _) => await RefreshDueSubscriptionsAsync();
        Shown += async (_, _) => await LoadStateAsync();
        FormClosing += OnFormClosing;
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
        }

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        Text = $"WFly {ProductInfo.Version}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1060, 690);
        ClientSize = new Size(1280, 820);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = UiPalette.Canvas;
        HandleCreated += (_, _) => WindowBackdrop.Apply(this);

        // The navigation stays on the right, separated by one drawn line.
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

            const int contentMinimum = 720;
            const int navigationMinimum = 148;
            if (root.ClientSize.Width < contentMinimum + navigationMinimum + root.SplitterWidth)
            {
                return;
            }

            // Initialise after Dock has given SplitContainer a real width;
            // setting panel minimums or SplitterDistance on its default
            // design-time width would throw before the form can be shown.
            root.Panel1MinSize = contentMinimum;
            root.Panel2MinSize = navigationMinimum;
            var maximum = root.ClientSize.Width - navigationMinimum - root.SplitterWidth;
            root.SplitterDistance = Math.Clamp(root.ClientSize.Width - 188, contentMinimum, maximum);
            splitInitialized = true;
        };
        root.Panel1.BackColor = UiPalette.Canvas;
        root.Panel2.BackColor = UiPalette.Canvas;
        root.Panel2.Paint += (_, args) =>
        {
            using var divider = new Pen(UiPalette.CardBorder);
            args.Graphics.DrawLine(divider, 0, 0, 0, root.Panel2.ClientSize.Height);
        };

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiPalette.Canvas,
            Padding = new Padding(28, 24, 22, 14),
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
            BackColor = Color.Transparent,
            Padding = new Padding(16, 26, 16, 18),
        };
        navigation.Paint += (_, args) =>
        {
            using var divider = new Pen(UiPalette.CardBorder);
            args.Graphics.DrawLine(divider, 0, 0, 0, navigation.ClientSize.Height);
        };
        var navigationLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = NavigationItems.Length + 4,
            BackColor = Color.Transparent,
        };
        navigationLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var brand = new Label
        {
            AutoSize = true,
            Text = "WFly",
            ForeColor = UiPalette.Ink,
            Font = new Font(Font.FontFamily, 19F, FontStyle.Bold),
            Margin = new Padding(10, 0, 0, 26),
        };
        navigationLayout.Controls.Add(brand, 0, 0);

        for (var index = 0; index < NavigationItems.Length; index++)
        {
            var page = NavigationItems[index];
            navigationLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var button = new Button
            {
                Text = page,
                Dock = DockStyle.Top,
                Height = 44,
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0 },
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 0, 0),
                ForeColor = UiPalette.MutedInk,
                BackColor = Color.Transparent,
                TabStop = true,
                Margin = new Padding(0, 3, 0, 3),
                Font = new Font(Font.FontFamily, 10F, FontStyle.Regular),
            };
            button.FlatAppearance.MouseOverBackColor = UiPalette.Hover;
            button.FlatAppearance.MouseDownBackColor = UiPalette.AccentSoft;
            button.Click += (_, _) => ShowPage(page);
            navigationLayout.Controls.Add(button, 0, index + 1);
            _navigationButtons.Add(page, button);
        }

        navigationLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        navigation.Controls.Add(navigationLayout);

        root.Panel1.Controls.Add(main);
        root.Panel2.Controls.Add(navigation);
        Controls.Add(root);
        ShowPage("首页");
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
            button.BackColor = selected ? UiPalette.AccentSoft : Color.Transparent;
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
    }

    private Control BuildHomePage()
    {
        var root = CreateScrollablePage();
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 0, 0, 18),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var overview = CreateGroup("节点信息");
        var overviewLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(10) };
        overviewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        overviewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _homeNodeLabel = AddValueRow(overviewLayout, "节点", 0);
        _homeCoreLabel = AddValueRow(overviewLayout, "内核", 1);
        _homeRunningLabel = AddValueRow(overviewLayout, "状态", 2);
        _homeConnectionCountLabel = AddValueRow(overviewLayout, "连接", 3);
        overview.Controls.Add(overviewLayout);
        layout.Controls.Add(overview, 0, 0);

        var modeGroup = CreateGroup("代理模式");
        var modeLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Padding = new Padding(10) };
        _proxyModeSelector = new ProxyModeSelector { Dock = DockStyle.Top, BackColor = UiPalette.Card, ForeColor = UiPalette.Ink };
        _proxyModeSelector.ModeChanged += async (_, _) => await HandleProxyModeChangedAsync();
        modeLayout.Controls.Add(_proxyModeSelector, 0, 0);
        modeGroup.Controls.Add(modeLayout);
        layout.Controls.Add(modeGroup, 1, 0);

        var egress = CreateGroup("IP 出口检测与真实延迟");
        var egressLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(10) };
        egressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        egressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _homeIpLabel = AddValueRow(egressLayout, "出口 IP", 0);
        _homeIpTypeLabel = AddValueRow(egressLayout, "IP 类型", 1);
        _homeGoogleLatencyLabel = AddValueRow(egressLayout, "Google", 2);
        var checkEgressButton = CreateSecondaryButton("检测");
        checkEgressButton.Anchor = AnchorStyles.Left;
        checkEgressButton.Click += async (_, _) => await CheckEgressAsync();
        egressLayout.Controls.Add(checkEgressButton, 1, 3);
        egress.Controls.Add(egressLayout);
        layout.Controls.Add(egress, 0, 1);

        var trafficGroup = CreateGroup("实时流量");
        _trafficChart = new TrafficChartControl { Dock = DockStyle.Fill, Margin = new Padding(8), BackColor = UiPalette.Card };
        trafficGroup.Controls.Add(_trafficChart);
        trafficGroup.MinimumSize = new Size(300, 330);
        layout.Controls.Add(trafficGroup, 1, 1);

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
        var throughProxy = new CheckBox { Text = "通过本地代理", Checked = true, AutoSize = true, Padding = new Padding(0, 6, 8, 0) };
        var runButton = CreatePrimaryButton("开始延迟测试");
        runButton.Click += async (_, _) => await RunSiteLatencyTestsAsync(throughProxy.Checked);
        controls.Controls.Add(throughProxy);
        controls.Controls.Add(runButton);
        AddPageRow(content, controls);
        _testGrid = CreateGrid();
        _testGrid.Columns.Add("Name", "站点");
        _testGrid.Columns.Add("Host", "主机");
        _testGrid.Columns.Add("Status", "状态");
        _testGrid.Columns.Add("Latency", "延迟");
        _testGrid.Columns.Add("Error", "说明");
        foreach (var target in SiteLatencyTestService.DefaultTargets)
        {
            _testGrid.Rows.Add(target.Name, target.Uri.Host, "等待测试", "—", string.Empty);
        }

        _testGrid.Height = 410;
        AddPageRow(content, _testGrid, 410);
        root.Controls.Add(content);
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
            ColumnCount = 1,
            RowCount = 3,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(CreatePageHeader("设置"), 0, 0);

        var general = CreateGroup("本地运行设置");
        general.Dock = DockStyle.Fill;
        var generalLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 5,
            Padding = new Padding(4),
        };
        generalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        generalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var row = 0; row < 5; row++)
        {
            generalLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        generalLayout.Controls.Add(CreateSettingsLabel("默认内核"), 0, 0);
        _settingsCoreSelector = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(260, 30),
            DisplayMember = nameof(CoreDefinition.DisplayName),
            ValueMember = nameof(CoreDefinition.Id),
            DataSource = CoreRegistry.All.ToArray(),
        };
        _settingsCoreSelector.SelectedIndexChanged += (_, _) =>
        {
            if (!_isLoading)
            {
                RefreshNativeConfigPathDisplay();
            }
        };
        generalLayout.Controls.Add(_settingsCoreSelector, 1, 0);
        generalLayout.Controls.Add(CreateSettingsLabel("本地混合端口"), 0, 1);
        _mixedPortInput = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = 160, Anchor = AnchorStyles.Left };
        generalLayout.Controls.Add(_mixedPortInput, 1, 1);
        generalLayout.Controls.Add(CreateSettingsLabel("TUN 接口名"), 0, 2);
        _tunNameInput = new TextBox { Dock = DockStyle.Fill, MinimumSize = new Size(260, 30) };
        generalLayout.Controls.Add(_tunNameInput, 1, 2);
        generalLayout.Controls.Add(CreateSettingsLabel("原生配置文件"), 0, 3);
        var nativeConfigPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        nativeConfigPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        nativeConfigPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _nativeConfigPathTextBox = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, MinimumSize = new Size(260, 30), PlaceholderText = "用于 Mihomo / Xray-core 的已导入本地配置" };
        var importNativeConfig = CreateSecondaryButton("导入…");
        importNativeConfig.Click += async (_, _) => await ImportNativeConfigAsync();
        nativeConfigPanel.Controls.Add(_nativeConfigPathTextBox, 0, 0);
        nativeConfigPanel.Controls.Add(importNativeConfig, 1, 0);
        generalLayout.Controls.Add(nativeConfigPanel, 1, 3);
        var saveSettings = CreatePrimaryButton("保存设置");
        saveSettings.Anchor = AnchorStyles.Left;
        saveSettings.Margin = new Padding(0, 10, 0, 0);
        saveSettings.Click += async (_, _) => await SaveGeneralSettingsAsync();
        generalLayout.Controls.Add(saveSettings, 1, 4);
        general.Controls.Add(generalLayout);
        content.Controls.Add(general, 0, 1);

        var cores = CreateGroup("内核下载与更新");
        cores.Dock = DockStyle.Fill;
        var coreLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(4),
        };
        coreLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        coreLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        coreLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 210F));
        coreLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _installedCoreLabel = new Label { AutoSize = true, ForeColor = UiPalette.MutedInk, Text = "正在读取已安装内核…", Margin = new Padding(0, 0, 0, 8) };
        coreLayout.Controls.Add(_installedCoreLabel, 0, 0);
        _coreGrid = CreateGrid();
        _coreGrid.Dock = DockStyle.Fill;
        _coreGrid.Columns.Add("Name", "内核");
        _coreGrid.Columns.Add("Id", "标识");
        _coreGrid.Columns.Add("Installed", "已安装版本");
        coreLayout.Controls.Add(_coreGrid, 0, 1);
        var downloadCore = CreatePrimaryButton("检查并下载选中内核");
        downloadCore.Anchor = AnchorStyles.Left;
        downloadCore.Margin = new Padding(0, 10, 0, 0);
        downloadCore.Click += async (_, _) => await DownloadSelectedCoreAsync();
        coreLayout.Controls.Add(downloadCore, 0, 2);
        cores.Controls.Add(coreLayout);
        content.Controls.Add(cores, 0, 2);

        root.Controls.Add(content);

        return root;
    }

    private async Task LoadStateAsync()
    {
        _isLoading = true;
        try
        {
            _settings = await _settingsStore.LoadAsync();
            var settingsChanged = NormalizeNativeConfigSettings();
            // A core is not persisted across GUI launches.  Keep the three
            // position control honest: without a running core the middle
            // position is the only active-safe state.
            if (!_processService.IsRunning && _settings.ProxyMode != ProxyMode.Off)
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
        _currentNodes = string.IsNullOrWhiteSpace(_settings.SelectedNodeGroupId)
            ? []
            : await _proxyNodeStore.GetByGroupAsync(_settings.SelectedNodeGroupId);
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
            _proxyModeSelector.Enabled = !_operationBusy && !_proxyModeTransitionBusy;
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

    private async Task CheckEgressAsync()
    {
        await RunOperationAsync("正在检测出口 IP 与 Google 延迟…", async cancellationToken =>
        {
            var useLocalProxy = _processService.IsRunning && _settings.ProxyMode != ProxyMode.Off;
            var result = await _networkDiagnosticsService.CheckAsync(useLocalProxy, GetActiveMixedProxyPort(), cancellationToken);
            _homeIpLabel?.SetTextSafe(result.IpAddress ?? "未知");
            _homeIpTypeLabel?.SetTextSafe(result.IpTypeDisplay);
            _homeGoogleLatencyLabel?.SetTextSafe(result.GoogleLatency is { } latency ? $"{latency.TotalMilliseconds:0} ms" : "不可达");
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
        if (_trafficTickBusy || _trafficChart is null || IsDisposed)
        {
            return;
        }

        _trafficTickBusy = true;
        try
        {
            var hostRate = _networkTrafficSampler.Sample();
            var proxyUploadRate = 0L;
            var proxyDownloadRate = 0L;
            var activeConnections = 0;
            try
            {
                var connections = await _clashApiClient.TryGetConnectionsAsync(9090);
                if (connections is not null)
                {
                    activeConnections = connections.Connections.Count;
                    CaptureConnectionLogs(connections);
                    if (_previousProxyCounterAt is { } previousAt)
                    {
                        var seconds = Math.Max(0.25, (hostRate.CapturedAt - previousAt).TotalSeconds);
                        proxyUploadRate = Math.Max(0, (long)((connections.UploadTotal - _previousProxyUploadTotal) / seconds));
                        proxyDownloadRate = Math.Max(0, (long)((connections.DownloadTotal - _previousProxyDownloadTotal) / seconds));
                    }

                    _previousProxyUploadTotal = connections.UploadTotal;
                    _previousProxyDownloadTotal = connections.DownloadTotal;
                    _previousProxyCounterAt = hostRate.CapturedAt;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
            {
                // A controller is optional. The chart continues with real host
                // counters while leaving unavailable proxy curves at zero.
            }

            var sample = new TrafficChartSample(
                hostRate.CapturedAt,
                proxyUploadRate,
                proxyDownloadRate,
                Math.Max(0, hostRate.UploadBytesPerSecond - proxyUploadRate),
                Math.Max(0, hostRate.DownloadBytesPerSecond - proxyDownloadRate));
            if (!IsDisposed && _trafficChart.IsHandleCreated)
            {
                _trafficChart.Append(sample);
                if (_homeConnectionCountLabel is not null)
                {
                    _homeConnectionCountLabel.Text = activeConnections > 0 ? $"{activeConnections} 个活动连接" : "0 个活动连接";
                }
            }
        }
        finally
        {
            _trafficTickBusy = false;
        }
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

        await RunOperationAsync("正在测试网站访问延迟…", async cancellationToken =>
        {
            var progress = new Progress<SiteLatencyResult>(UpdateSiteLatencyResult);
            var results = await _siteLatencyTestService.TestAsync(
                SiteLatencyTestService.DefaultTargets,
                throughLocalProxy,
                GetActiveMixedProxyPort(),
                progress,
                cancellationToken);
            PostLog("TEST", $"完成 {results.Count} 个网站延迟测试（{(throughLocalProxy ? "本地代理" : "直连")}）。");
        });
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
                row.Cells["Latency"].Value = result.Latency is { } latency ? $"{latency.TotalMilliseconds:0} ms" : "—";
                row.Cells["Error"].Value = result.Error ?? string.Empty;
                break;
            }
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
            _nativeConfigPathTextBox!.Text = GetNativeConfigPath(definition.Id) ?? string.Empty;
            var installed = await _installedCoreStore.GetAllAsync();
            _coreGrid.Rows.Clear();
            foreach (var core in CoreRegistry.All)
            {
                var latest = installed
                    .Where(candidate => string.Equals(candidate.Id, core.Id, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate.ExecutablePath))
                    .OrderByDescending(candidate => candidate.InstalledAt)
                    .FirstOrDefault();
                _coreGrid.Rows.Add(core.DisplayName, core.Id, latest?.Version ?? "未安装");
            }

            _installedCoreLabel!.Text = $"默认内核：{definition.DisplayName}。下载目录：{_paths.CoresDirectory}";
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
            if (_nativeConfigPathTextBox is not null)
            {
                _nativeConfigPathTextBox.Text = targetPath;
            }

            PostLog("CFG", "已将原生配置导入 data/profiles。 ");
        });
    }

    private async Task DownloadSelectedCoreAsync()
    {
        CoreDefinition? definition = null;
        if (_coreGrid?.CurrentRow?.Cells["Id"].Value is string selectedId)
        {
            definition = CoreRegistry.GetById(selectedId);
        }

        definition ??= _settingsCoreSelector?.SelectedItem as CoreDefinition ?? CoreRegistry.GetById(_settings.SelectedCoreId);
        if (definition is null)
        {
            return;
        }

        await RunOperationAsync($"正在检查 {definition.DisplayName} 官方版本…", async cancellationToken =>
        {
            var release = await _catalogService.GetLatestAsync(definition, cancellationToken);
            var current = await _installedCoreStore.GetLatestAsync(definition.Id, cancellationToken);
            if (current is not null
                && string.Equals(current.Version, release.Version, StringComparison.OrdinalIgnoreCase)
                && File.Exists(current.ExecutablePath))
            {
                SetStatus($"{definition.DisplayName} 已是最新版本 {release.Version}。");
                PostLog("CORE", $"{definition.DisplayName} 已是最新版本 {release.Version}，未重复下载。");
                return;
            }

            var confirmation = $"将从固定官方仓库 {definition.GitHubOwner}/{definition.GitHubRepository} 下载：\n\n" +
                $"内核：{definition.DisplayName}\n版本：{release.Version}\n文件：{release.Asset.Name}\n大小：{FormatBytes(release.Asset.Size)}\nSHA-256：{release.Asset.Sha256}\n\n下载后会校验 SHA-256 并安全解压。是否继续？";
            if (MessageBox.Show(this, confirmation, "确认下载内核", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
            {
                SetStatus("已取消下载。");
                return;
            }

            var progress = new Progress<DownloadProgress>(value => SetStatus(value.Percentage is { } percentage ? $"{value.Stage} {percentage}%" : value.Stage));
            var installed = await _installer.InstallAsync(definition, release, progress, cancellationToken);
            PostLog("CORE", $"{definition.DisplayName} {installed.Version} 已下载、校验并安装。");
            await RefreshSettingsPageAsync();
        });
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

    private void RefreshNativeConfigPathDisplay()
    {
        if (_nativeConfigPathTextBox is null)
        {
            return;
        }

        var definition = _settingsCoreSelector?.SelectedItem as CoreDefinition;
        _nativeConfigPathTextBox.Text = definition is null ? string.Empty : GetNativeConfigPath(definition.Id) ?? string.Empty;
    }

    private static bool IsNativeConfigCore(string? coreId) =>
        string.Equals(coreId, "mihomo", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(coreId, "xray-core", StringComparison.OrdinalIgnoreCase);

    private int GetActiveMixedProxyPort() =>
        _processService.IsRunning &&
        string.Equals(_runningCoreId, "sing-box", StringComparison.OrdinalIgnoreCase) &&
        _runningMixedProxyPort is { } runningPort
            ? runningPort
            : _settings.MixedProxyPort;

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

    private async Task<bool> RunOperationAsync(string status, Func<CancellationToken, Task> operation)
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
            ShowError(status, exception);
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
                    return;
                }

                SetStatus(isRunning ? "内核正在运行。" : "内核已停止。");
                if (!isRunning && !_proxyModeTransitionBusy && !_closeCleanupInProgress)
                {
                    _ = HandleUnexpectedCoreExitAsync();
                }

                RefreshHomePage();
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
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var key = new Label { AutoSize = true, Text = label, ForeColor = UiPalette.MutedInk, Anchor = AnchorStyles.Left, Margin = new Padding(0, 5, 12, 5) };
        var value = new Label { AutoSize = true, Text = "—", Anchor = AnchorStyles.Left, Margin = new Padding(0, 5, 0, 5), MaximumSize = new Size(330, 0) };
        layout.Controls.Add(key, 0, row);
        layout.Controls.Add(value, 1, row);
        return value;
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

    private static Button CreatePrimaryButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        BackColor = UiPalette.Accent,
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderSize = 0 },
        Padding = new Padding(14, 6, 14, 6),
        Margin = new Padding(0, 0, 8, 0),
    };

    private static Button CreateSecondaryButton(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            BackColor = UiPalette.Hover,
            ForeColor = UiPalette.Ink,
            FlatStyle = FlatStyle.Flat,
            Padding = new Padding(12, 5, 12, 5),
            Margin = new Padding(8, 0, 0, 0),
        };
        button.FlatAppearance.BorderColor = UiPalette.CardBorder;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = UiPalette.AccentSoft;
        return button;
    }

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Top,
        AutoGenerateColumns = false,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        ReadOnly = true,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = UiPalette.Card,
        BorderStyle = BorderStyle.None,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        GridColor = UiPalette.CardBorder,
        DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = UiPalette.Card,
            ForeColor = UiPalette.Ink,
            SelectionBackColor = UiPalette.AccentSoft,
            SelectionForeColor = UiPalette.Ink,
            Padding = new Padding(3, 2, 3, 2),
        },
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = UiPalette.Hover,
            ForeColor = UiPalette.Ink,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Padding = new Padding(3, 4, 3, 4),
        },
        EnableHeadersVisualStyles = false,
    };

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
