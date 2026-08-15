using System.Drawing;
using WFly.Models;
using WFly.Services;

namespace WFly.UI;

internal sealed class MainForm : Form
{
    private const int MaximumLogCharacters = 80_000;

    private readonly AppPaths _paths;
    private readonly InstalledCoreStore _installedCoreStore;
    private readonly SettingsStore _settingsStore;
    private readonly CoreCatalogService _catalogService;
    private readonly CoreInstaller _installer;
    private readonly CoreProcessService _processService;

    private readonly ComboBox _coreComboBox = new();
    private readonly Label _installedStatusLabel = new();
    private readonly TextBox _configPathTextBox = new();
    private readonly Button _browseConfigButton = new();
    private readonly Button _downloadButton = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Label _operationStatusLabel = new();
    private readonly ProgressBar _progressBar = new();
    private readonly RichTextBox _logBox = new();

    private AppSettings _settings = new();
    private InstalledCore? _selectedInstalledCore;
    private CancellationTokenSource? _operationCancellation;
    private bool _isBusy = true;
    private bool _isLoading = true;
    private bool _closeAfterOperation;

    public MainForm(
        AppPaths paths,
        InstalledCoreStore installedCoreStore,
        SettingsStore settingsStore,
        CoreCatalogService catalogService,
        CoreInstaller installer,
        CoreProcessService processService)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _installedCoreStore = installedCoreStore ?? throw new ArgumentNullException(nameof(installedCoreStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _processService = processService ?? throw new ArgumentNullException(nameof(processService));

        InitializeComponent();
        _processService.LogReceived += OnCoreLogReceived;
        _processService.RunningStateChanged += OnCoreRunningStateChanged;
        Shown += async (_, _) => await LoadStateAsync();
        FormClosing += OnFormClosing;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _operationCancellation?.Dispose();
            _processService.LogReceived -= OnCoreLogReceived;
            _processService.RunningStateChanged -= OnCoreRunningStateChanged;
        }

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        Text = $"WFly {ProductInfo.Version} — 轻量代理内核管理器";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 560);
        ClientSize = new Size(800, 650);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 3,
            RowCount = 8,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Windows x64 · 官方内核下载、校验与运行",
            Margin = new Padding(0, 0, 0, 12),
        };
        root.Controls.Add(title, 0, 0);
        root.SetColumnSpan(title, 3);

        AddFieldLabel(root, "内核", 1);
        _coreComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _coreComboBox.DisplayMember = nameof(CoreDefinition.DisplayName);
        _coreComboBox.ValueMember = nameof(CoreDefinition.Id);
        _coreComboBox.DataSource = CoreRegistry.All.ToArray();
        _coreComboBox.Dock = DockStyle.Fill;
        _coreComboBox.SelectedIndexChanged += async (_, _) => await HandleCoreChangedAsync();
        root.Controls.Add(_coreComboBox, 1, 1);

        _downloadButton.AutoSize = true;
        _downloadButton.Text = "检查并下载";
        _downloadButton.Click += async (_, _) => await DownloadSelectedCoreAsync();
        root.Controls.Add(_downloadButton, 2, 1);

        AddFieldLabel(root, "安装状态", 2);
        _installedStatusLabel.AutoSize = true;
        _installedStatusLabel.Anchor = AnchorStyles.Left;
        _installedStatusLabel.Text = "正在读取…";
        root.Controls.Add(_installedStatusLabel, 1, 2);
        root.SetColumnSpan(_installedStatusLabel, 2);

        AddFieldLabel(root, "JSON 配置", 3);
        _configPathTextBox.Dock = DockStyle.Fill;
        _configPathTextBox.ReadOnly = true;
        _configPathTextBox.PlaceholderText = "请选择本地 sing-box 或 Xray JSON 配置文件";
        _configPathTextBox.TextChanged += (_, _) => UpdateButtonState();
        root.Controls.Add(_configPathTextBox, 1, 3);

        _browseConfigButton.AutoSize = true;
        _browseConfigButton.Text = "选择文件…";
        _browseConfigButton.Click += async (_, _) => await BrowseConfigAsync();
        root.Controls.Add(_browseConfigButton, 2, 3);

        _operationStatusLabel.AutoSize = true;
        _operationStatusLabel.Text = "就绪";
        _operationStatusLabel.Margin = new Padding(0, 10, 8, 0);
        root.Controls.Add(_operationStatusLabel, 0, 4);

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Margin = new Padding(0, 10, 8, 0);
        root.Controls.Add(_progressBar, 1, 4);

        var actionPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 8, 0, 8),
        };
        _startButton.AutoSize = true;
        _startButton.Text = "启动";
        _startButton.Click += async (_, _) => await StartSelectedCoreAsync();
        _stopButton.AutoSize = true;
        _stopButton.Text = "停止";
        _stopButton.Click += async (_, _) => await StopCoreAsync();
        actionPanel.Controls.Add(_startButton);
        actionPanel.Controls.Add(_stopButton);
        root.Controls.Add(actionPanel, 0, 5);
        root.SetColumnSpan(actionPanel, 3);

        var logGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "运行日志（仅保留在内存中）",
            Padding = new Padding(8),
        };
        _logBox.Dock = DockStyle.Fill;
        _logBox.ReadOnly = true;
        _logBox.BackColor = SystemColors.Window;
        _logBox.BorderStyle = BorderStyle.None;
        _logBox.Font = new Font("Cascadia Mono", 9F);
        _logBox.WordWrap = false;
        _logBox.DetectUrls = false;
        logGroup.Controls.Add(_logBox);
        root.Controls.Add(logGroup, 0, 6);
        root.SetColumnSpan(logGroup, 3);

        var dataDirectory = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = $"运行时数据：{_paths.RootDirectory}",
            Margin = new Padding(0, 10, 0, 0),
        };
        root.Controls.Add(dataDirectory, 0, 7);
        root.SetColumnSpan(dataDirectory, 3);

        Controls.Add(root);
        UpdateButtonState();
    }

    private static void AddFieldLabel(TableLayoutPanel layout, string text, int row)
    {
        var label = new Label
        {
            AutoSize = true,
            Text = text,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 7, 12, 7),
        };
        layout.Controls.Add(label, 0, row);
    }

    private async Task LoadStateAsync()
    {
        SetBusy(true, "正在读取本地状态…");
        try
        {
            _settings = await _settingsStore.LoadAsync();
            _isLoading = true;
            try
            {
                var savedCore = CoreRegistry.GetById(_settings.SelectedCoreId);
                if (savedCore is not null)
                {
                    _coreComboBox.SelectedItem = savedCore;
                }

                _configPathTextBox.Text = _settings.ConfigPath ?? string.Empty;
            }
            finally
            {
                _isLoading = false;
            }

            await RefreshInstalledStatusAsync();
            AppendLog("SYS", "已准备就绪。请先选择本地 JSON 配置，再启动已验证的内核。");
        }
        catch (Exception exception)
        {
            ShowFailure("无法读取 WFly 本地状态", exception);
        }
        finally
        {
            _isLoading = false;
            SetBusy(false, "就绪");
        }
    }

    private async Task HandleCoreChangedAsync()
    {
        if (_isLoading || _isBusy)
        {
            return;
        }

        var definition = SelectedDefinition;
        if (definition is null)
        {
            return;
        }

        try
        {
            _settings.SelectedCoreId = definition.Id;
            await _settingsStore.SaveAsync(_settings);
            await RefreshInstalledStatusAsync();
        }
        catch (Exception exception)
        {
            ShowFailure("无法保存内核选择", exception);
        }
    }

    private async Task BrowseConfigAsync()
    {
        if (_isBusy)
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = "JSON 配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            Multiselect = false,
            Title = "选择本地内核配置文件",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _configPathTextBox.Text = Path.GetFullPath(dialog.FileName);
        try
        {
            _settings.ConfigPath = _configPathTextBox.Text;
            await _settingsStore.SaveAsync(_settings);
            AppendLog("SYS", "已选择配置文件路径；配置内容不会被复制或保存。 ");
        }
        catch (Exception exception)
        {
            ShowFailure("无法保存配置文件路径", exception);
        }
    }

    private async Task DownloadSelectedCoreAsync()
    {
        var definition = SelectedDefinition;
        if (definition is null || _isBusy || _processService.IsRunning)
        {
            return;
        }

        SetBusy(true, "正在读取官方发布信息…");
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        try
        {
            AppendLog("SYS", $"正在检查 {definition.DisplayName} 的官方稳定版 Release…");
            var release = await _catalogService.GetLatestAsync(definition, cancellation.Token);
            var confirmation =
                $"将从 {definition.GitHubOwner}/{definition.GitHubRepository} 的官方 GitHub Release 下载：\n\n" +
                $"内核：{definition.DisplayName}\n" +
                $"版本：{release.Version}\n" +
                $"文件：{release.Asset.Name}\n" +
                $"大小：{FormatBytes(release.Asset.Size)}\n" +
                $"SHA-256：{release.Asset.Sha256}\n\n" +
                $"来源：{release.Asset.DownloadUrl}\n\n" +
                "下载后将校验 SHA-256；校验失败时不会解压或执行。是否继续？";

            if (MessageBox.Show(this, confirmation, "确认下载官方内核", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
            {
                AppendLog("SYS", "已取消下载。");
                return;
            }

            var progress = new Progress<DownloadProgress>(UpdateDownloadProgress);
            var installedCore = await _installer.InstallAsync(definition, release, progress, cancellation.Token);
            _selectedInstalledCore = installedCore;
            _installedStatusLabel.Text = $"已安装 {installedCore.Version}（SHA-256 已验证）";
            AppendLog("SYS", $"{definition.DisplayName} {installedCore.Version} 安装完成，尚未启动。");
        }
        catch (OperationCanceledException)
        {
            AppendLog("SYS", "下载已取消；临时文件已清理。 ");
        }
        catch (Exception exception)
        {
            ShowFailure("下载或安装内核失败", exception);
        }
        finally
        {
            _operationCancellation = null;
            SetBusy(false, "就绪");
            await RefreshInstalledStatusAsync();
        }
    }

    private async Task StartSelectedCoreAsync()
    {
        var definition = SelectedDefinition;
        var installedCore = _selectedInstalledCore;
        if (definition is null || installedCore is null || _isBusy || _processService.IsRunning)
        {
            return;
        }

        try
        {
            SetBusy(true, "正在验证配置…");
            var configPath = Path.GetFullPath(_configPathTextBox.Text);
            await ConfigValidator.ValidateAsync(configPath);
            _settings.ConfigPath = configPath;
            _settings.SelectedCoreId = definition.Id;
            await _settingsStore.SaveAsync(_settings);

            await _processService.StartAsync(installedCore.ExecutablePath, definition.BuildStartArguments(configPath));
            _operationStatusLabel.Text = $"正在运行 {definition.DisplayName}";
            AppendLog("SYS", $"已向 {definition.DisplayName} 发送启动请求。");
        }
        catch (Exception exception)
        {
            ShowFailure("无法启动内核", exception);
        }
        finally
        {
            SetBusy(false, _processService.IsRunning ? "内核运行中" : "就绪");
        }
    }

    private async Task StopCoreAsync()
    {
        if (_isBusy || !_processService.IsRunning)
        {
            return;
        }

        try
        {
            SetBusy(true, "正在停止内核…");
            await _processService.StopAsync();
        }
        catch (Exception exception)
        {
            ShowFailure("无法停止内核", exception);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task RefreshInstalledStatusAsync()
    {
        var definition = SelectedDefinition;
        if (definition is null)
        {
            _selectedInstalledCore = null;
            _installedStatusLabel.Text = "未选择内核";
            UpdateButtonState();
            return;
        }

        _selectedInstalledCore = await _installedCoreStore.GetLatestAsync(definition.Id);
        if (_selectedInstalledCore is null)
        {
            _installedStatusLabel.Text = "未安装";
        }
        else if (!File.Exists(_selectedInstalledCore.ExecutablePath))
        {
            _installedStatusLabel.Text = "已安装登记失效：找不到可执行文件，请重新下载";
            _selectedInstalledCore = null;
        }
        else
        {
            _installedStatusLabel.Text = $"已安装 {_selectedInstalledCore.Version}（SHA-256 已验证）";
        }

        UpdateButtonState();
    }

    private void UpdateDownloadProgress(DownloadProgress progress)
    {
        _operationStatusLabel.Text = progress.Percentage is { } percentage
            ? $"{progress.Stage} {percentage}%"
            : progress.Stage;

        if (progress.Percentage is { } value)
        {
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = value;
        }
        else
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
        }
    }

    private void SetBusy(bool isBusy, string status)
    {
        _isBusy = isBusy;
        _operationStatusLabel.Text = status;
        if (!isBusy)
        {
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 0;
        }

        UpdateButtonState();
        if (!isBusy && _closeAfterOperation)
        {
            _closeAfterOperation = false;
            BeginInvoke(Close);
        }
    }

    private void UpdateButtonState()
    {
        var running = _processService.IsRunning;
        var hasConfiguration = !string.IsNullOrWhiteSpace(_configPathTextBox.Text) && File.Exists(_configPathTextBox.Text);
        var hasInstalledCore = _selectedInstalledCore is not null && File.Exists(_selectedInstalledCore.ExecutablePath);

        _coreComboBox.Enabled = !_isBusy && !running;
        _downloadButton.Enabled = !_isBusy && !running && SelectedDefinition is not null;
        _browseConfigButton.Enabled = !_isBusy && !running;
        _startButton.Enabled = !_isBusy && !running && hasInstalledCore && hasConfiguration;
        _stopButton.Enabled = !_isBusy && running;
    }

    private void OnCoreLogReceived(CoreLogEntry entry) =>
        PostToUi(() => AppendLog(entry.Stream, entry.Message, entry.Timestamp));

    private void OnCoreRunningStateChanged(bool isRunning) =>
        PostToUi(() =>
        {
            _operationStatusLabel.Text = isRunning ? "内核运行中" : "内核已停止";
            UpdateButtonState();
        });

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_isBusy)
        {
            var result = MessageBox.Show(
                this,
                "当前操作尚未完成。是否取消操作并在清理临时文件后退出？",
                "WFly",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (result != DialogResult.Yes)
            {
                eventArgs.Cancel = true;
                return;
            }

            eventArgs.Cancel = true;
            _closeAfterOperation = true;
            _operationCancellation?.Cancel();
            return;
        }

        if (_processService.IsRunning)
        {
            var result = MessageBox.Show(
                this,
                "退出 WFly 会停止当前由 WFly 启动的内核进程。是否继续？",
                "确认退出",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (result != DialogResult.Yes)
            {
                eventArgs.Cancel = true;
            }
        }
    }

    private void ShowFailure(string heading, Exception exception)
    {
        AppendLog("ERR", $"{heading}：{exception.Message}");
        MessageBox.Show(this, $"{heading}\n\n{exception.Message}", "WFly", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void AppendLog(string stream, string message, DateTimeOffset? timestamp = null)
    {
        if (IsDisposed)
        {
            return;
        }

        if (_logBox.TextLength > MaximumLogCharacters)
        {
            var removeLength = Math.Min(_logBox.TextLength - MaximumLogCharacters + 8_000, 16_000);
            _logBox.Select(0, removeLength);
            _logBox.SelectedText = string.Empty;
        }

        var time = timestamp ?? DateTimeOffset.Now;
        _logBox.AppendText($"[{time:HH:mm:ss}] [{stream}] {message}{Environment.NewLine}");
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // The form is being destroyed.
            }

            return;
        }

        action();
    }

    private CoreDefinition? SelectedDefinition => _coreComboBox.SelectedItem as CoreDefinition;

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
