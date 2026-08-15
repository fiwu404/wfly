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
    private readonly System.Windows.Forms.Timer _trafficTimer = new() { Interval = 1_000 };
    private readonly System.Windows.Forms.Timer _subscriptionTimer = new() { Interval = 5 * 60 * 1_000 };
    private readonly Dictionary<string, Control> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _navigationButtons = new(StringComparer.Ordinal);
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(247, 249, 252) };
    private readonly Label _pageTitleLabel = new();
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
    private Button? _homeStartButton;
    private Button? _homeStopButton;

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
        BackColor = Color.FromArgb(247, 249, 252);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.FromArgb(247, 249, 252),
            Padding = new Padding(24, 20, 24, 12),
        };
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _pageTitleLabel.AutoSize = true;
        _pageTitleLabel.Font = new Font(Font.FontFamily, 18F, FontStyle.Bold);
        _pageTitleLabel.Margin = new Padding(0, 0, 0, 14);
        _pageTitleLabel.Text = "首页";
        main.Controls.Add(_pageTitleLabel, 0, 0);
        main.Controls.Add(_pageHost, 0, 1);
        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = Color.FromArgb(97, 108, 124);
        _statusLabel.Margin = new Padding(0, 10, 0, 0);
        _statusLabel.Text = "正在读取本地数据…";
        main.Controls.Add(_statusLabel, 0, 2);

        var navigation = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(25, 34, 50),
            Padding = new Padding(12, 20, 12, 18),
        };
        var navigationLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = NavigationItems.Length + 4,
        };
        navigationLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var brand = new Label
        {
            AutoSize = true,
            Text = "WFly",
            ForeColor = Color.White,
            Font = new Font(Font.FontFamily, 17F, FontStyle.Bold),
            Margin = new Padding(8, 0, 0, 22),
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
                Height = 42,
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0 },
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0),
                ForeColor = Color.FromArgb(214, 222, 237),
                BackColor = Color.FromArgb(25, 34, 50),
                TabStop = true,
                Margin = new Padding(0, 2, 0, 2),
            };
            button.Click += (_, _) => ShowPage(page);
            navigationLayout.Controls.Add(button, 0, index + 1);
            _navigationButtons.Add(page, button);
        }

        navigationLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var dataCaption = new Label
        {
            AutoSize = true,
            Text = "便携数据目录",
            ForeColor = Color.FromArgb(132, 146, 170),
            Margin = new Padding(8, 12, 0, 0),
        };
        navigationLayout.Controls.Add(dataCaption, 0, NavigationItems.Length + 2);
        var dataPath = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(125, 0),
            Text = _paths.RootDirectory,
            ForeColor = Color.FromArgb(177, 188, 207),
            Margin = new Padding(8, 3, 0, 0),
        };
        navigationLayout.Controls.Add(dataPath, 0, NavigationItems.Length + 3);
        navigation.Controls.Add(navigationLayout);

        root.Controls.Add(main, 0, 0);
        root.Controls.Add(navigation, 1, 0);
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
        _pageTitleLabel.Text = page;
        foreach (var (name, button) in _navigationButtons)
        {
            var selected = string.Equals(name, page, StringComparison.Ordinal);
            button.BackColor = selected ? Color.FromArgb(53, 79, 123) : Color.FromArgb(25, 34, 50);
            button.ForeColor = selected ? Color.White : Color.FromArgb(214, 222, 237);
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
            RowCount = 4,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 0, 0, 18),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

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

        var actions = CreateGroup("运行控制");
        var actionsLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        _homeStartButton = CreatePrimaryButton("启动选中节点");
        _homeStartButton.Click += async (_, _) => await StartSelectedNodeAsync();
        _homeStopButton = new Button { Text = "停止内核", AutoSize = true };
        _homeStopButton.Click += async (_, _) => await StopRunningCoreAsync();
        var hint = new Label { AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = Color.FromArgb(101, 112, 129), Text = "节点需先归属于节点组；首次运行前请在设置中下载安装相应内核。" };
        actionsLayout.Controls.Add(_homeStartButton);
        actionsLayout.Controls.Add(_homeStopButton);
        actionsLayout.Controls.Add(hint);
        actions.Controls.Add(actionsLayout);
        layout.Controls.Add(actions, 1, 0);

        var modeGroup = CreateGroup("代理模式");
        var modeLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        _proxyModeSelector = new ProxyModeSelector { Dock = DockStyle.Top, BackColor = Color.White };
        _proxyModeSelector.ModeChanged += async (_, _) => await HandleProxyModeChangedAsync();
        var modeHint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(430, 0),
            ForeColor = Color.FromArgb(101, 112, 129),
            Text = "系统代理仅指向本机 127.0.0.1；TUN 需要管理员权限，并会在下次启动时写入 sing-box 配置。",
        };
        modeLayout.Controls.Add(_proxyModeSelector, 0, 0);
        modeLayout.Controls.Add(modeHint, 0, 1);
        modeGroup.Controls.Add(modeLayout);
        layout.Controls.Add(modeGroup, 0, 1);

        var egress = CreateGroup("IP 出口检测与真实延迟");
        var egressLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(10) };
        egressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        egressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _homeIpLabel = AddValueRow(egressLayout, "出口 IP", 0);
        _homeIpTypeLabel = AddValueRow(egressLayout, "IP 类型", 1);
        _homeGoogleLatencyLabel = AddValueRow(egressLayout, "Google", 2);
        var checkEgressButton = new Button { Text = "检测", AutoSize = true, Anchor = AnchorStyles.Left };
        checkEgressButton.Click += async (_, _) => await CheckEgressAsync();
        egressLayout.Controls.Add(checkEgressButton, 1, 3);
        egress.Controls.Add(egressLayout);
        layout.Controls.Add(egress, 1, 1);

        var trafficGroup = CreateGroup("实时流量");
        _trafficChart = new TrafficChartControl { Dock = DockStyle.Fill, Margin = new Padding(8), BackColor = Color.White };
        trafficGroup.Controls.Add(_trafficChart);
        trafficGroup.MinimumSize = new Size(640, 285);
        layout.Controls.Add(trafficGroup, 0, 2);
        layout.SetColumnSpan(trafficGroup, 2);

        var privacyNote = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            Text = "IP 类型不会凭地址猜测；没有接入信誉数据库时会显示“未知”。流量曲线仅保存在内存；代理来自核心 API，直连以主机接口总量扣除代理计数估算，鼠标悬停可查看每个采样点。",
            ForeColor = Color.FromArgb(101, 112, 129),
            Margin = new Padding(4, 12, 0, 0),
        };
        layout.Controls.Add(privacyNote, 0, 3);
        layout.SetColumnSpan(privacyNote, 2);
        root.Controls.Add(layout);
        return root;
    }

    private Control BuildConnectionsPage()
    {
        var root = CreateScrollablePage();
        var header = CreatePageHeader("运行中的连接", "通过 sing-box / Mihomo 的本机 Clash API 读取；没有启用控制器时不会伪造连接记录。");
        root.Controls.Add(header);
        var refresh = new Button { Text = "刷新连接", AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        refresh.Click += async (_, _) => await RefreshConnectionsAsync();
        root.Controls.Add(refresh);
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
        root.Controls.Add(_connectionGrid);
        return root;
    }

    private Control BuildLogsPage()
    {
        var root = CreateScrollablePage();
        root.Controls.Add(CreatePageHeader("日志", "显示当前进程的全部内存日志。只有点击导出时才会写入 data/exports。"));
        var actions = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        var exportButton = new Button { Text = "导出日志", AutoSize = true };
        exportButton.Click += async (_, _) => await ExportLogsAsync();
        var clearButton = new Button { Text = "清空内存日志", AutoSize = true };
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
        root.Controls.Add(actions);
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
        root.Controls.Add(_logBox);
        RenderLogs();
        return root;
    }

    private Control BuildTestsPage()
    {
        var root = CreateScrollablePage();
        root.Controls.Add(CreatePageHeader("测试", "点击后通过本机代理测试国外站点的真实 HTTP 到达时间；未启动代理时可切换为直连。"));
        var controls = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        var throughProxy = new CheckBox { Text = "通过本地代理", Checked = true, AutoSize = true, Padding = new Padding(0, 6, 8, 0) };
        var runButton = CreatePrimaryButton("开始延迟测试");
        runButton.Click += async (_, _) => await RunSiteLatencyTestsAsync(throughProxy.Checked);
        controls.Controls.Add(throughProxy);
        controls.Controls.Add(runButton);
        root.Controls.Add(controls);
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
        root.Controls.Add(_testGrid);
        return root;
    }

    private Control BuildSettingsPage()
    {
        var root = CreateScrollablePage();
        root.Controls.Add(CreatePageHeader("设置", "内核仅从固定的官方 GitHub Release 下载、校验 SHA-256 后安装到 data/cores。"));
        var general = CreateGroup("本地运行设置");
        var generalLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(10) };
        generalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        generalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        generalLayout.Controls.Add(new Label { Text = "默认内核", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _settingsCoreSelector = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Left, Width = 240, DisplayMember = nameof(CoreDefinition.DisplayName), ValueMember = nameof(CoreDefinition.Id), DataSource = CoreRegistry.All.ToArray() };
        _settingsCoreSelector.SelectedIndexChanged += (_, _) =>
        {
            if (!_isLoading)
            {
                RefreshNativeConfigPathDisplay();
            }
        };
        generalLayout.Controls.Add(_settingsCoreSelector, 1, 0);
        generalLayout.Controls.Add(new Label { Text = "本地混合端口", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _mixedPortInput = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = 140, Dock = DockStyle.Left };
        generalLayout.Controls.Add(_mixedPortInput, 1, 1);
        generalLayout.Controls.Add(new Label { Text = "TUN 接口名", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _tunNameInput = new TextBox { Width = 240, Dock = DockStyle.Left };
        generalLayout.Controls.Add(_tunNameInput, 1, 2);
        generalLayout.Controls.Add(new Label { Text = "原生配置文件", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        var nativeConfigPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        nativeConfigPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        nativeConfigPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _nativeConfigPathTextBox = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, PlaceholderText = "用于 Mihomo / Xray-core 的已导入本地配置" };
        var importNativeConfig = new Button { Text = "导入…", AutoSize = true };
        importNativeConfig.Click += async (_, _) => await ImportNativeConfigAsync();
        nativeConfigPanel.Controls.Add(_nativeConfigPathTextBox, 0, 0);
        nativeConfigPanel.Controls.Add(importNativeConfig, 1, 0);
        generalLayout.Controls.Add(nativeConfigPanel, 1, 3);
        var saveSettings = CreatePrimaryButton("保存设置");
        saveSettings.Click += async (_, _) => await SaveGeneralSettingsAsync();
        generalLayout.Controls.Add(saveSettings, 1, 4);
        general.Controls.Add(generalLayout);
        root.Controls.Add(general);

        var cores = CreateGroup("内核下载与更新");
        var coreLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        _installedCoreLabel = new Label { AutoSize = true, ForeColor = Color.FromArgb(101, 112, 129), Text = "正在读取已安装内核…" };
        coreLayout.Controls.Add(_installedCoreLabel, 0, 0);
        _coreGrid = CreateGrid();
        _coreGrid.Height = 180;
        _coreGrid.Columns.Add("Name", "内核");
        _coreGrid.Columns.Add("Id", "标识");
        _coreGrid.Columns.Add("Installed", "已安装版本");
        coreLayout.Controls.Add(_coreGrid, 0, 1);
        var downloadCore = CreatePrimaryButton("检查并下载选中内核");
        downloadCore.Click += async (_, _) => await DownloadSelectedCoreAsync();
        coreLayout.Controls.Add(downloadCore, 0, 2);
        cores.Controls.Add(coreLayout);
        root.Controls.Add(cores);

        var dataGroup = CreateGroup("数据位置");
        var dataLabel = new Label
        {
            AutoSize = true,
            Padding = new Padding(10),
            MaximumSize = new Size(850, 0),
            Text = $"所有 WFly 运行数据：{_paths.RootDirectory}\n包含内核、节点组、节点、规则、配置和手动导出的日志。不会在 C:\\Users 下创建新的 WFly 数据。",
        };
        dataGroup.Controls.Add(dataLabel);
        root.Controls.Add(dataGroup);
        return root;
    }

    private async Task LoadStateAsync()
    {
        _isLoading = true;
        try
        {
            _settings = await _settingsStore.LoadAsync();
            if (NormalizeNativeConfigSettings())
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
        _homeStartButton!.Enabled = !_operationBusy && !_processService.IsRunning && selectedNode is { IsEnabled: true };
        _homeStopButton!.Enabled = !_operationBusy && _processService.IsRunning;
        if (_proxyModeSelector is not null && _proxyModeSelector.Mode != _settings.ProxyMode)
        {
            _proxyModeSelector.Mode = _settings.ProxyMode;
        }
    }

    private async Task HandleProxyModeChangedAsync()
    {
        if (_isLoading || _proxyModeSelector is null || _proxyModeSelector.Mode == _settings.ProxyMode)
        {
            return;
        }

        var previousMode = _settings.ProxyMode;
        var selectedMode = _proxyModeSelector.Mode;
        try
        {
            // The TUN inbound is part of the running sing-box configuration.
            // Merely changing the selector cannot remove it, so stop the core
            // whenever entering or leaving TUN. This prevents a stale TUN
            // interface from continuing to capture traffic after "关闭代理".
            if (_processService.IsRunning && (previousMode == ProxyMode.Tun || selectedMode == ProxyMode.Tun))
            {
                await _processService.StopAsync();
                await RestoreSystemProxyAsync();
                _settings.ProxyMode = selectedMode;
                await _settingsStore.SaveAsync(_settings);
                SetStatus($"内核已停止；请重新启动以应用{GetProxyModeDisplay(selectedMode)}。");
                PostLog("SYS", $"代理模式已选择：{GetProxyModeDisplay(selectedMode)}；为安全应用 TUN 切换，已停止内核。");
                return;
            }

            _settings.ProxyMode = selectedMode;
            if (_settings.ProxyMode == ProxyMode.Off)
            {
                await RestoreSystemProxyAsync();
            }
            else if (_settings.ProxyMode == ProxyMode.SystemProxy && _processService.IsRunning)
            {
                await ApplySystemProxyAsync();
            }
            else if (_settings.ProxyMode == ProxyMode.Tun && _processService.IsRunning)
            {
                await RestoreSystemProxyAsync();
                SetStatus("TUN 模式将在停止并重新启动 sing-box 后生效。");
            }

            await _settingsStore.SaveAsync(_settings);
            PostLog("SYS", $"代理模式已选择：{GetProxyModeDisplay(_settings.ProxyMode)}。");
        }
        catch (Exception exception)
        {
            _settings.ProxyMode = previousMode;
            _proxyModeSelector.Mode = previousMode;
            await _settingsStore.SaveAsync(_settings);
            ShowError("无法切换代理模式", exception);
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

    private async Task StopRunningCoreAsync()
    {
        await RunOperationAsync("正在停止内核…", async cancellationToken =>
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

    private async Task RunOperationAsync(string status, Func<CancellationToken, Task> operation)
    {
        if (_operationBusy)
        {
            return;
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
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消。");
        }
        catch (Exception exception)
        {
            ShowError(status, exception);
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
        if (!isRunning)
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

                SetStatus(isRunning ? "内核正在运行。" : "内核已停止。");
                if (!isRunning && _settings.SystemProxyLease is not null)
                {
                    _ = RestoreSystemProxyAsync();
                }

                RefreshHomePage();
            });
        }
        catch (InvalidOperationException)
        {
            // The window handle can disappear between the checks and BeginInvoke.
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

            var result = _systemProxyService.RestoreIfOwned(_settings.SystemProxyLease);
            if (result.Status is WindowsSystemProxyRestoreStatus.Restored or WindowsSystemProxyRestoreStatus.NoLease)
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
        BackColor = Color.FromArgb(247, 249, 252),
        Padding = new Padding(0, 0, 8, 12),
    };

    private static GroupBox CreateGroup(string title) => new()
    {
        Text = title,
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        BackColor = Color.White,
        Padding = new Padding(10),
        Margin = new Padding(0, 0, 12, 12),
    };

    private static Label AddValueRow(TableLayoutPanel layout, string label, int row)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var key = new Label { AutoSize = true, Text = label, ForeColor = Color.FromArgb(101, 112, 129), Anchor = AnchorStyles.Left, Margin = new Padding(0, 5, 12, 5) };
        var value = new Label { AutoSize = true, Text = "—", Anchor = AnchorStyles.Left, Margin = new Padding(0, 5, 0, 5), MaximumSize = new Size(330, 0) };
        layout.Controls.Add(key, 0, row);
        layout.Controls.Add(value, 1, row);
        return value;
    }

    private static Label CreatePageHeader(string title, string subtitle)
    {
        return new Label
        {
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            Text = $"{title}\n{subtitle}",
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 12),
        };
    }

    private static Button CreatePrimaryButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        BackColor = Color.FromArgb(53, 121, 246),
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderSize = 0 },
        Padding = new Padding(10, 4, 10, 4),
    };

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
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(239, 243, 250), Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) },
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
