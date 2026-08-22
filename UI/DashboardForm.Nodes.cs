using WFly.Models;
using WFly.Services;
using WFly.UI.Controls;
using WFly.UI.Dialogs;

namespace WFly.UI;

internal sealed partial class DashboardForm
{
    private Control BuildNodeGroupsPage()
    {
        // A grid must own the remaining page area.  An auto-sized table
        // inside a scrolling panel can measure a DataGridView as header-only
        // after a DPI/layout pass, leaving its data rows invisible.
        var root = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = false,
            BackColor = UiPalette.Canvas,
            Padding = new Padding(2, 2, 10, 4),
        };
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = UiPalette.Canvas,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        content.Controls.Add(CreatePageHeader("节点组"), 0, 0);

        var actions = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 10), BackColor = UiPalette.Canvas };
        var add = CreatePrimaryButton("新建节点组");
        add.Click += async (_, _) => await AddOrEditNodeGroupAsync(null);
        var edit = CreateSecondaryButton("编辑");
        edit.Click += async (_, _) => await AddOrEditNodeGroupAsync(GetSelectedGroupFromGrid());
        var update = CreateSecondaryButton("立即更新订阅");
        update.Click += async (_, _) => await RefreshSelectedGroupSubscriptionAsync();
        var delete = CreateSecondaryButton("删除节点组");
        delete.Click += async (_, _) => await DeleteSelectedNodeGroupAsync();
        actions.Controls.Add(add);
        actions.Controls.Add(edit);
        actions.Controls.Add(update);
        actions.Controls.Add(delete);
        actions.Dock = DockStyle.Top;
        content.Controls.Add(actions, 0, 1);

        _groupGrid = CreateGrid();
        _groupGrid.Columns.Add("Name", "节点组");
        _groupGrid.Columns.Add("Subscription", "订阅");
        _groupGrid.Columns.Add("Core", "内核");
        _groupGrid.Columns.Add("Interval", "更新时间");
        _groupGrid.Columns.Add("LastUpdated", "上次更新");
        _groupGrid.Columns.Add("Status", "状态");
        // Keep the node-group header and its first few rows comfortably
        // readable instead of leaving a thin, header-only strip above the
        // otherwise empty data surface.
        _groupGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing;
        _groupGrid.ColumnHeadersHeight = 32;
        _groupGrid.RowTemplate.Height = 30;
        _groupGrid.MinimumSize = new Size(0, 240);
        _groupGrid.Dock = DockStyle.Fill;
        _groupGrid.Margin = Padding.Empty;
        _groupGrid.SelectionChanged += async (_, _) => await SelectGroupFromGridAsync();
        content.Controls.Add(_groupGrid, 0, 2);
        root.Controls.Add(content);
        return root;
    }

    private Control BuildNodesPage()
    {
        var root = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = false,
            BackColor = UiPalette.Canvas,
            Padding = new Padding(2, 2, 10, 4),
        };
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = UiPalette.Canvas,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        content.Controls.Add(CreatePageHeader("节点"), 0, 0);

        var groupPanel = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 10), BackColor = UiPalette.Canvas };
        groupPanel.Controls.Add(new Label { Text = "节点组", AutoSize = true, Padding = new Padding(0, 7, 8, 0) });
        _nodeGroupSelector = UiControlTheme.CreateComboBox();
        _nodeGroupSelector.Width = 260;
        _nodeGroupSelector.SelectedIndexChanged += async (_, _) => await SelectGroupFromNodeSelectorAsync();
        groupPanel.Controls.Add(_nodeGroupSelector);
        groupPanel.Dock = DockStyle.Top;
        content.Controls.Add(groupPanel, 0, 1);

        var actions = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 10), BackColor = UiPalette.Canvas };
        var add = CreatePrimaryButton("添加节点");
        add.Click += async (_, _) => await AddOrEditNodeAsync(null);
        var edit = CreateSecondaryButton("编辑节点");
        edit.Click += async (_, _) => await AddOrEditNodeAsync(GetSelectedNodeFromGrid());
        var speedTest = CreateSecondaryButton("节点测速");
        var delete = CreateSecondaryButton("删除节点");
        delete.Click += async (_, _) => await DeleteSelectedNodeAsync();
        actions.Controls.Add(add);
        actions.Controls.Add(edit);
        actions.Controls.Add(speedTest);
        actions.Controls.Add(delete);
        actions.Dock = DockStyle.Top;
        content.Controls.Add(actions, 0, 2);

        _nodeGrid = CreateGrid();
        _nodeGrid.MultiSelect = true;
        _nodeGrid.Columns.Add("Name", "节点");
        _nodeGrid.Columns.Add("Protocol", "协议");
        _nodeGrid.Columns.Add("Address", "服务器");
        _nodeGrid.Columns.Add("Port", "端口");
        _nodeGrid.Columns.Add("Ping", "Ping");
        _nodeGrid.Columns.Add("Tcping", "TCPing");
        _nodeGrid.Columns.Add("RealConnection", "真连接");
        _nodeGrid.Columns.Add("Udp", "UDP");
        _nodeGrid.Columns["Name"].FillWeight = 150;
        _nodeGrid.Columns["Protocol"].FillWeight = 85;
        _nodeGrid.Columns["Address"].FillWeight = 150;
        _nodeGrid.Columns["Port"].FillWeight = 65;
        _nodeGrid.Columns["Ping"].FillWeight = 70;
        _nodeGrid.Columns["Tcping"].FillWeight = 75;
        _nodeGrid.Columns["RealConnection"].FillWeight = 85;
        _nodeGrid.Columns["Udp"].FillWeight = 70;
        _nodeGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing;
        _nodeGrid.ColumnHeadersHeight = 32;
        _nodeGrid.RowTemplate.Height = 30;
        _nodeGrid.MinimumSize = new Size(0, 240);
        _nodeGrid.Dock = DockStyle.Fill;
        _nodeGrid.Margin = Padding.Empty;
        _nodeGrid.SelectionChanged += async (_, _) => await SelectNodeFromGridAsync();
        _nodeGrid.CellMouseDown += OnNodeGridCellMouseDown;
        _nodeTestMenu = CreateNodeTestMenu();
        _nodeGrid.ContextMenuStrip = _nodeTestMenu;
        speedTest.Click += (_, _) => _nodeTestMenu.Show(speedTest, new Point(0, speedTest.Height));
        content.Controls.Add(_nodeGrid, 0, 3);
        root.Controls.Add(content);
        return root;
    }

    private void RefreshNodeGroupsPage()
    {
        if (_groupGrid is null || _groupGrid.IsDisposed)
        {
            return;
        }

        _isLoading = true;
        try
        {
            _groupGrid.Rows.Clear();
            foreach (var group in _groups)
            {
                var rowIndex = _groupGrid.Rows.Add(
                    group.Name,
                    GetSubscriptionDisplay(group.SubscriptionUrl),
                    GetCoreDisplay(group.CoreId),
                    group.UpdateIntervalHours is { } hours ? $"每 {hours} 小时" : "不更新",
                    group.LastUpdatedAt is { } updatedAt ? updatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "从未",
                    string.IsNullOrWhiteSpace(group.LastUpdateError) ? "正常" : "更新失败（查看日志）");
                _groupGrid.Rows[rowIndex].Tag = group.Id;
                if (string.Equals(group.Id, _settings.SelectedNodeGroupId, StringComparison.Ordinal))
                {
                    _groupGrid.Rows[rowIndex].Selected = true;
                }
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void RefreshNodesPage()
    {
        if (_nodeGroupSelector is null || _nodeGrid is null || _nodeGroupSelector.IsDisposed || _nodeGrid.IsDisposed)
        {
            return;
        }

        _isLoading = true;
        try
        {
            var choices = _groups.Select(group => new NodeGroupChoice(group.Id, group.Name)).ToArray();
            _nodeGroupSelector.DataSource = choices;
            _nodeGroupSelector.DisplayMember = nameof(NodeGroupChoice.Name);
            _nodeGroupSelector.ValueMember = nameof(NodeGroupChoice.Id);
            var selectedIndex = Array.FindIndex(choices, choice => string.Equals(choice.Id, _settings.SelectedNodeGroupId, StringComparison.Ordinal));
            _nodeGroupSelector.SelectedIndex = selectedIndex;

            _nodeGrid.Rows.Clear();
            foreach (var node in _currentNodes)
            {
                var manual = ManualNodeConfiguration.Load(node.ManualOptionsJson, node.ConfigurationJson);
                var rowIndex = _nodeGrid.Rows.Add(
                    node.Name,
                    node.Protocol,
                    string.IsNullOrWhiteSpace(manual.Server) ? "—" : manual.Server,
                    manual.Port is > 0 and < 65536 ? manual.Port : "—",
                    node.PingResult ?? "—",
                    node.TcpingResult ?? "—",
                    node.RealConnectionResult ?? "—",
                    node.UdpResult ?? "—");
                _nodeGrid.Rows[rowIndex].Tag = node.Id;
                if (string.Equals(node.Id, _settings.SelectedNodeId, StringComparison.Ordinal))
                {
                    _nodeGrid.Rows[rowIndex].Selected = true;
                }
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task SelectGroupFromGridAsync()
    {
        if (_isLoading || _groupGrid?.CurrentRow?.Tag is not string groupId || string.Equals(groupId, _settings.SelectedNodeGroupId, StringComparison.Ordinal))
        {
            return;
        }

        _settings.SelectedNodeGroupId = groupId;
        _settings.SelectedNodeId = null;
        await _settingsStore.SaveAsync(_settings);
        await RefreshCurrentNodesAsync();
        RefreshNodesPage();
        RefreshHomePage();
    }

    private async Task SelectGroupFromNodeSelectorAsync()
    {
        if (_isLoading || _nodeGroupSelector?.SelectedItem is not NodeGroupChoice choice || string.Equals(choice.Id, _settings.SelectedNodeGroupId, StringComparison.Ordinal))
        {
            return;
        }

        _settings.SelectedNodeGroupId = choice.Id;
        _settings.SelectedNodeId = null;
        await _settingsStore.SaveAsync(_settings);
        await RefreshCurrentNodesAsync();
        RefreshNodesPage();
        RefreshNodeGroupsPage();
        RefreshHomePage();
    }

    private async Task SelectNodeFromGridAsync()
    {
        if (_isLoading || _nodeGrid?.CurrentRow?.Tag is not string nodeId || string.Equals(nodeId, _settings.SelectedNodeId, StringComparison.Ordinal))
        {
            return;
        }

        _settings.SelectedNodeId = nodeId;
        await _settingsStore.SaveAsync(_settings);
        RefreshHomePage();
    }

    private async Task AddOrEditNodeGroupAsync(NodeGroup? existing)
    {
        if (!NodeGroupDialog.TryEdit(this, existing, out var draft))
        {
            return;
        }

        await RunOperationAsync(existing is null ? "正在创建节点组…" : "正在保存节点组…", async cancellationToken =>
        {
            var saved = await _nodeGroupStore.SaveAsync(draft, cancellationToken);
            _settings.SelectedNodeGroupId = saved.Id;
            _settings.SelectedNodeId = null;
            await _settingsStore.SaveAsync(_settings, cancellationToken);
            await RefreshGroupsAsync();
            await RefreshCurrentNodesAsync();
            PostLog("GROUP", $"节点组“{saved.Name}”已保存。");
            if (!string.IsNullOrWhiteSpace(saved.SubscriptionUrl))
            {
                await RefreshGroupSubscriptionAsync(saved, cancellationToken);
            }
        });
    }

    private async Task RefreshSelectedGroupSubscriptionAsync()
    {
        var group = GetSelectedGroupFromGrid() ?? SelectedGroup;
        if (group is null)
        {
            MessageBox.Show(this, "请先选择一个节点组。", "WFly", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(group.SubscriptionUrl))
        {
            MessageBox.Show(this, "该节点组没有订阅链接；它是一个可手动添加节点的空组。", "WFly", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await RunOperationAsync(
            "正在更新订阅…",
            cancellationToken => RefreshGroupSubscriptionAsync(group, cancellationToken),
            showErrorDialog: false);
    }

    private async Task RefreshGroupSubscriptionAsync(NodeGroup group, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _subscriptionProfileService.RefreshGroupAsync(group, _proxyNodeStore, cancellationToken);
            var storedGroup = await _nodeGroupStore.GetAsync(group.Id, cancellationToken) ?? group;
            var coreWasAutoDetected = string.Equals(storedGroup.CoreId, "auto", StringComparison.OrdinalIgnoreCase);
            if (coreWasAutoDetected)
            {
                storedGroup.CoreId = result.DetectedCoreId;
                await _nodeGroupStore.SaveAsync(storedGroup, cancellationToken);
            }

            await _nodeGroupStore.RecordRefreshResultAsync(group.Id, DateTimeOffset.UtcNow, null, cancellationToken);
            var coreMessage = coreWasAutoDetected
                ? $"已自动选择 {GetCoreDisplay(result.DetectedCoreId)}"
                : $"检测为 {GetCoreDisplay(result.DetectedCoreId)}，保留 {GetCoreDisplay(storedGroup.CoreId)}";
            PostLog("SUB", $"节点组“{group.Name}”已更新 {result.Nodes.Count} 个节点（来源 {result.SourceHost}，{coreMessage}）。");
        }
        catch (Exception exception)
        {
            await _nodeGroupStore.RecordRefreshResultAsync(group.Id, DateTimeOffset.UtcNow, exception.Message, cancellationToken);
            throw;
        }
        finally
        {
            await RefreshGroupsAsync();
            await RefreshCurrentNodesAsync();
            RefreshNodeGroupsPage();
            RefreshNodesPage();
            RefreshHomePage();
        }
    }

    private async Task DeleteSelectedNodeGroupAsync()
    {
        var group = GetSelectedGroupFromGrid() ?? SelectedGroup;
        if (group is null)
        {
            return;
        }

        if (MessageBox.Show(this, $"删除节点组“{group.Name}”会同时删除其中所有节点。是否继续？", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        await RunOperationAsync("正在删除节点组…", async cancellationToken =>
        {
            var removedNodes = await _proxyNodeStore.DeleteByGroupAsync(group.Id, cancellationToken);
            await _nodeGroupStore.DeleteAsync(group.Id, cancellationToken);
            if (string.Equals(_settings.SelectedNodeGroupId, group.Id, StringComparison.Ordinal))
            {
                _settings.SelectedNodeGroupId = null;
                _settings.SelectedNodeId = null;
            }

            await _settingsStore.SaveAsync(_settings, cancellationToken);
            await RefreshGroupsAsync();
            await RefreshCurrentNodesAsync();
            PostLog("GROUP", $"节点组“{group.Name}”及 {removedNodes} 个节点已删除。");
        });
    }

    private async Task AddOrEditNodeAsync(ProxyNode? existing)
    {
        if (_groups.Count == 0)
        {
            MessageBox.Show(this, "请先在“节点组”页面创建节点组。", "WFly", MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowPage("节点组");
            return;
        }

        var seed = existing ?? new ProxyNode { GroupId = _settings.SelectedNodeGroupId ?? string.Empty, IsEnabled = true };
        if (!ProxyNodeDialog.TryEdit(
            this,
            seed,
            _groups,
            _subscriptionProfileService.ParseSingleShareLink,
            out var draft))
        {
            return;
        }

        var owner = _groups.FirstOrDefault(group => string.Equals(group.Id, draft.GroupId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("所选节点组已不存在。");
        draft.CoreId = string.Equals(owner.CoreId, "auto", StringComparison.OrdinalIgnoreCase) ? "sing-box" : owner.CoreId;
        if (string.IsNullOrWhiteSpace(draft.ConfigurationJson) &&
            !string.IsNullOrWhiteSpace(draft.ShareLink) &&
            string.Equals(draft.CoreId, "sing-box", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var parsed = _subscriptionProfileService.ParseSingleShareLink(draft.ShareLink);
                draft.ConfigurationJson = parsed.ConfigurationJson;
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                ShowError("无法解析节点分享链接", exception);
                return;
            }
        }

        await RunOperationAsync(existing is null ? "正在添加节点…" : "正在保存节点…", async cancellationToken =>
        {
            var saved = await _proxyNodeStore.SaveAsync(draft, cancellationToken);
            _settings.SelectedNodeGroupId = saved.GroupId;
            _settings.SelectedNodeId = saved.Id;
            await _settingsStore.SaveAsync(_settings, cancellationToken);
            await RefreshCurrentNodesAsync();
            PostLog("NODE", $"节点“{saved.Name}”已保存到“{owner.Name}”。");
        });
    }

    private ContextMenuStrip CreateNodeTestMenu()
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false,
            AutoSize = true,
            Padding = new Padding(5),
            Renderer = new ToolStripProfessionalRenderer(new NodeTestMenuColorTable()),
        };
        menu.Items.Add(CreateNodeTestMenuItem("测试 Ping", NodeTestKind.Ping));
        menu.Items.Add(CreateNodeTestMenuItem("测试 TCPing", NodeTestKind.Tcping));
        menu.Items.Add(CreateNodeTestMenuItem("测试真连接", NodeTestKind.RealConnection));
        menu.Items.Add(CreateNodeTestMenuItem("测试 UDP", NodeTestKind.Udp));
        menu.Opening += (_, eventArgs) =>
        {
            var hasSelection = GetSelectedNodesFromGrid().Count > 0;
            foreach (ToolStripItem item in menu.Items)
            {
                item.Enabled = hasSelection && !_operationBusy;
                item.BackColor = UiPalette.Card;
                item.ForeColor = UiPalette.Ink;
                item.Padding = new Padding(12, 6, 24, 6);
            }
            menu.BackColor = UiPalette.Card;
            menu.ForeColor = UiPalette.Ink;
            if (!hasSelection) eventArgs.Cancel = true;
        };
        return menu;
    }

    private ToolStripMenuItem CreateNodeTestMenuItem(string text, NodeTestKind kind)
    {
        var item = new ToolStripMenuItem(text) { AutoSize = true };
        item.Click += async (_, _) => await RunSelectedNodeTestAsync(kind);
        return item;
    }

    private void OnNodeGridCellMouseDown(object? sender, DataGridViewCellMouseEventArgs eventArgs)
    {
        if (_nodeGrid is null || eventArgs.Button != MouseButtons.Right || eventArgs.RowIndex < 0)
        {
            return;
        }

        var row = _nodeGrid.Rows[eventArgs.RowIndex];
        if (!row.Selected)
        {
            _nodeGrid.ClearSelection();
            row.Selected = true;
        }
        if (eventArgs.ColumnIndex >= 0)
        {
            _nodeGrid.CurrentCell = row.Cells[eventArgs.ColumnIndex];
        }
    }

    private IReadOnlyList<ProxyNode> GetSelectedNodesFromGrid()
    {
        if (_nodeGrid is null)
        {
            return [];
        }

        var ids = _nodeGrid.SelectedRows
            .Cast<DataGridViewRow>()
            .OrderBy(static row => row.Index)
            .Select(static row => row.Tag as string)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0 && _nodeGrid.CurrentRow?.Tag is string currentId)
        {
            ids.Add(currentId);
        }
        return _currentNodes.Where(node => ids.Contains(node.Id)).ToArray();
    }

    private async Task RunSelectedNodeTestAsync(NodeTestKind kind)
    {
        var nodes = GetSelectedNodesFromGrid();
        if (nodes.Count == 0)
        {
            return;
        }

        var display = kind switch
        {
            NodeTestKind.Ping => "Ping",
            NodeTestKind.Tcping => "TCPing",
            NodeTestKind.RealConnection => "真连接",
            _ => "UDP",
        };
        await RunOperationAsync($"正在测试 {nodes.Count} 个节点的 {display}…", async cancellationToken =>
        {
            var progress = new Progress<NodeTestUpdate>(ApplyNodeTestUpdate);
            await _nodeSpeedTestService.TestAsync(kind, nodes, progress, cancellationToken);
            await Task.Yield();
            foreach (var node in nodes)
            {
                await _proxyNodeStore.SaveAsync(node, cancellationToken);
            }
            await RefreshCurrentNodesAsync();
            RefreshNodesPage();
            PostLog("TEST", $"已完成 {nodes.Count} 个节点的 {display} 测试。");
        });
    }

    private void ApplyNodeTestUpdate(NodeTestUpdate update)
    {
        var node = _currentNodes.FirstOrDefault(item => string.Equals(item.Id, update.NodeId, StringComparison.Ordinal));
        if (node is null)
        {
            return;
        }

        var columnName = update.Kind switch
        {
            NodeTestKind.Ping => "Ping",
            NodeTestKind.Tcping => "Tcping",
            NodeTestKind.RealConnection => "RealConnection",
            _ => "Udp",
        };
        switch (update.Kind)
        {
            case NodeTestKind.Ping:
                node.PingResult = update.Display;
                break;
            case NodeTestKind.Tcping:
                node.TcpingResult = update.Display;
                break;
            case NodeTestKind.RealConnection:
                node.RealConnectionResult = update.Display;
                break;
            case NodeTestKind.Udp:
                node.UdpResult = update.Display;
                break;
        }
        if (update.Completed) node.LastTestedAt = DateTimeOffset.UtcNow;

        if (_nodeGrid is null || !_nodeGrid.Columns.Contains(columnName))
        {
            return;
        }
        foreach (DataGridViewRow row in _nodeGrid.Rows)
        {
            if (row.Tag is string id && string.Equals(id, update.NodeId, StringComparison.Ordinal))
            {
                row.Cells[columnName].Value = update.Display;
                break;
            }
        }
    }

    private async Task ToggleSelectedNodeAsync()
    {
        var node = GetSelectedNodeFromGrid() ?? SelectedNode;
        if (node is null)
        {
            return;
        }

        node.IsEnabled = !node.IsEnabled;
        await RunOperationAsync("正在更新节点状态…", async cancellationToken =>
        {
            await _proxyNodeStore.SaveAsync(node, cancellationToken);
            await RefreshCurrentNodesAsync();
            PostLog("NODE", $"节点“{node.Name}”已{(node.IsEnabled ? "启用" : "停用")}。");
        });
    }

    private async Task DeleteSelectedNodeAsync()
    {
        var node = GetSelectedNodeFromGrid() ?? SelectedNode;
        if (node is null)
        {
            return;
        }

        if (MessageBox.Show(this, $"删除节点“{node.Name}”？", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        await RunOperationAsync("正在删除节点…", async cancellationToken =>
        {
            await _proxyNodeStore.DeleteAsync(node.Id, cancellationToken);
            if (string.Equals(_settings.SelectedNodeId, node.Id, StringComparison.Ordinal))
            {
                _settings.SelectedNodeId = null;
                await _settingsStore.SaveAsync(_settings, cancellationToken);
            }

            await RefreshCurrentNodesAsync();
            PostLog("NODE", $"节点“{node.Name}”已删除。");
        });
    }

    private NodeGroup? GetSelectedGroupFromGrid() =>
        _groupGrid?.CurrentRow?.Tag is string groupId
            ? _groups.FirstOrDefault(group => string.Equals(group.Id, groupId, StringComparison.Ordinal))
            : null;

    private ProxyNode? GetSelectedNodeFromGrid() =>
        _nodeGrid?.CurrentRow?.Tag is string nodeId
            ? _currentNodes.FirstOrDefault(node => string.Equals(node.Id, nodeId, StringComparison.Ordinal))
            : null;

    private static string GetSubscriptionDisplay(string? subscriptionUrl)
    {
        if (string.IsNullOrWhiteSpace(subscriptionUrl))
        {
            return "空组（手动节点）";
        }

        return Uri.TryCreate(subscriptionUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : "已配置";
    }

    private static string GetCoreDisplay(string coreId) =>
        string.Equals(coreId, "auto", StringComparison.OrdinalIgnoreCase)
            ? "自动判断"
            : CoreRegistry.GetById(coreId)?.DisplayName ?? coreId;

    private sealed record NodeGroupChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}

internal sealed class NodeTestMenuColorTable : ProfessionalColorTable
{
    public override Color MenuBorder => UiPalette.CardBorder;
    public override Color MenuItemBorder => UiPalette.Accent;
    public override Color MenuItemSelected => UiPalette.AccentSoft;
    public override Color MenuItemSelectedGradientBegin => UiPalette.AccentSoft;
    public override Color MenuItemSelectedGradientEnd => UiPalette.AccentSoft;
    public override Color ToolStripDropDownBackground => UiPalette.Card;
    public override Color ImageMarginGradientBegin => UiPalette.Card;
    public override Color ImageMarginGradientMiddle => UiPalette.Card;
    public override Color ImageMarginGradientEnd => UiPalette.Card;
}
