using WFly.Models;
using WFly.UI.Dialogs;

namespace WFly.UI;

internal sealed partial class DashboardForm
{
    private Control BuildNodeGroupsPage()
    {
        var root = CreateScrollablePage();
        root.Controls.Add(CreatePageHeader("节点组", "节点组是节点的唯一父级。订阅链接留空时会创建一个空组；不会创建“全部”分类。"));

        var actions = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        var add = CreatePrimaryButton("新建节点组");
        add.Click += async (_, _) => await AddOrEditNodeGroupAsync(null);
        var edit = new Button { Text = "编辑", AutoSize = true };
        edit.Click += async (_, _) => await AddOrEditNodeGroupAsync(GetSelectedGroupFromGrid());
        var update = new Button { Text = "立即更新订阅", AutoSize = true };
        update.Click += async (_, _) => await RefreshSelectedGroupSubscriptionAsync();
        var delete = new Button { Text = "删除节点组", AutoSize = true };
        delete.Click += async (_, _) => await DeleteSelectedNodeGroupAsync();
        actions.Controls.Add(add);
        actions.Controls.Add(edit);
        actions.Controls.Add(update);
        actions.Controls.Add(delete);
        root.Controls.Add(actions);

        _groupGrid = CreateGrid();
        _groupGrid.Columns.Add("Name", "节点组");
        _groupGrid.Columns.Add("Subscription", "订阅");
        _groupGrid.Columns.Add("Core", "内核");
        _groupGrid.Columns.Add("Interval", "更新时间");
        _groupGrid.Columns.Add("LastUpdated", "上次更新");
        _groupGrid.Columns.Add("Status", "状态");
        _groupGrid.Height = 500;
        _groupGrid.SelectionChanged += async (_, _) => await SelectGroupFromGridAsync();
        root.Controls.Add(_groupGrid);
        return root;
    }

    private Control BuildNodesPage()
    {
        var root = CreateScrollablePage();
        root.Controls.Add(CreatePageHeader("节点", "请先选择或创建节点组。节点只能归属于一个已创建的节点组，不提供“全部”分类。"));

        var groupPanel = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        groupPanel.Controls.Add(new Label { Text = "节点组", AutoSize = true, Padding = new Padding(0, 7, 8, 0) });
        _nodeGroupSelector = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        _nodeGroupSelector.SelectedIndexChanged += async (_, _) => await SelectGroupFromNodeSelectorAsync();
        groupPanel.Controls.Add(_nodeGroupSelector);
        root.Controls.Add(groupPanel);

        var actions = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        var add = CreatePrimaryButton("添加节点");
        add.Click += async (_, _) => await AddOrEditNodeAsync(null);
        var edit = new Button { Text = "编辑节点", AutoSize = true };
        edit.Click += async (_, _) => await AddOrEditNodeAsync(GetSelectedNodeFromGrid());
        var toggle = new Button { Text = "启用/停用", AutoSize = true };
        toggle.Click += async (_, _) => await ToggleSelectedNodeAsync();
        var delete = new Button { Text = "删除节点", AutoSize = true };
        delete.Click += async (_, _) => await DeleteSelectedNodeAsync();
        actions.Controls.Add(add);
        actions.Controls.Add(edit);
        actions.Controls.Add(toggle);
        actions.Controls.Add(delete);
        root.Controls.Add(actions);

        _nodeGrid = CreateGrid();
        _nodeGrid.Columns.Add("Name", "节点");
        _nodeGrid.Columns.Add("Protocol", "协议");
        _nodeGrid.Columns.Add("Core", "内核");
        _nodeGrid.Columns.Add("Enabled", "启用");
        _nodeGrid.Columns.Add("Updated", "更新时间");
        _nodeGrid.Height = 500;
        _nodeGrid.SelectionChanged += async (_, _) => await SelectNodeFromGridAsync();
        root.Controls.Add(_nodeGrid);
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
                    string.IsNullOrWhiteSpace(group.LastUpdateError) ? "正常" : "上次失败");
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
                var rowIndex = _nodeGrid.Rows.Add(
                    node.Name,
                    node.Protocol,
                    GetCoreDisplay(node.CoreId),
                    node.IsEnabled ? "启用" : "停用",
                    node.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
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

        await RunOperationAsync("正在更新订阅…", cancellationToken => RefreshGroupSubscriptionAsync(group, cancellationToken));
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
        if (!ProxyNodeDialog.TryEdit(this, seed, _groups, out var draft))
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
