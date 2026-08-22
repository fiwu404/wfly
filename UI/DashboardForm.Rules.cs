using System.Text;
using System.Globalization;
using System.Text.Json;
using WFly.Models;
using WFly.UI.Controls;
using WFly.UI.Dialogs;

namespace WFly.UI;

internal sealed partial class DashboardForm
{
    private static readonly HashSet<string> GeneratedRuleMatchKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "domain", "domain_suffix", "domain_keyword", "ip_cidr", "port",
        "process_name", "process_path", "network", "protocol", "inbound",
    };

    private static readonly HashSet<string> GeneratedRuleActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "proxy", "direct", "block",
    };

    private Control BuildRulesPage()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = UiPalette.Canvas,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var header = CreatePageHeader("规则");
        root.Controls.Add(header, 0, 0);
        root.SetColumnSpan(header, 2);

        var listPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(0, 0, 12, 0), BackColor = UiPalette.Canvas };
        listPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        listPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _ruleSetList = new ListBox { Dock = DockStyle.Fill, DisplayMember = nameof(RuleSet.Name), IntegralHeight = false, BackColor = UiPalette.Card, ForeColor = UiPalette.Ink };
        UiControlTheme.ApplyListBox(_ruleSetList);
        _ruleSetList.SelectedIndexChanged += (_, _) => SelectRuleSetFromList();
        listPanel.Controls.Add(_ruleSetList, 0, 0);
        var ruleSetActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0), BackColor = UiPalette.Canvas };
        var addSet = CreatePrimaryButton("新建规则集");
        addSet.Click += async (_, _) => await AddRuleSetAsync();
        var deleteSet = CreateSecondaryButton("删除");
        deleteSet.Click += async (_, _) => await DeleteActiveRuleSetAsync();
        ruleSetActions.Controls.Add(addSet);
        ruleSetActions.Controls.Add(deleteSet);
        listPanel.Controls.Add(ruleSetActions, 0, 1);
        root.Controls.Add(listPanel, 0, 1);

        var editorTabs = new TabControl { Dock = DockStyle.Fill, BackColor = UiPalette.Canvas };
        var graphicalTab = new TabPage("图形化") { BackColor = UiPalette.Canvas };
        var graphicalRoot = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(10), BackColor = UiPalette.Canvas };
        graphicalRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        graphicalRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var ruleActions = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 8), BackColor = UiPalette.Canvas };
        var addRule = CreatePrimaryButton("添加规则");
        addRule.Click += async (_, _) => await AddOrEditRuleEntryAsync(null);
        var editRule = CreateSecondaryButton("编辑");
        editRule.Click += async (_, _) => await AddOrEditRuleEntryAsync(GetSelectedRuleEntry());
        var removeRule = CreateSecondaryButton("删除");
        removeRule.Click += async (_, _) => await DeleteSelectedRuleEntryAsync();
        var saveGraphical = CreateSecondaryButton("保存图形规则");
        saveGraphical.Click += async (_, _) => await SaveActiveRuleSetAsync();
        ruleActions.Controls.Add(addRule);
        ruleActions.Controls.Add(editRule);
        ruleActions.Controls.Add(removeRule);
        ruleActions.Controls.Add(saveGraphical);
        graphicalRoot.Controls.Add(ruleActions, 0, 0);
        _ruleGrid = CreateGrid();
        _ruleGrid.Dock = DockStyle.Fill;
        _ruleGrid.Columns.Add("Name", "规则");
        _ruleGrid.Columns.Add("MatchKind", "匹配类型");
        _ruleGrid.Columns.Add("MatchValue", "匹配值");
        _ruleGrid.Columns.Add("Action", "动作");
        _ruleGrid.Columns.Add("Outbound", "出口");
        _ruleGrid.Columns.Add("Enabled", "启用");
        _ruleGrid.Columns.Add("Priority", "优先级");
        graphicalRoot.Controls.Add(_ruleGrid, 0, 1);
        graphicalTab.Controls.Add(graphicalRoot);
        editorTabs.TabPages.Add(graphicalTab);

        var jsonTab = new TabPage("配置文件") { BackColor = UiPalette.Canvas };
        var jsonRoot = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(10), BackColor = UiPalette.Canvas };
        jsonRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        jsonRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _ruleJsonBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Cascadia Mono", 9F),
            WordWrap = false,
            BackColor = UiPalette.Card,
            ForeColor = UiPalette.Ink,
            DetectUrls = false,
        };
        UiControlTheme.ApplyRichTextBox(_ruleJsonBox);
        jsonRoot.Controls.Add(_ruleJsonBox, 0, 0);
        var jsonActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0), BackColor = UiPalette.Canvas };
        var formatJson = CreateSecondaryButton("从图形规则刷新 JSON");
        formatJson.Click += (_, _) => RenderActiveRuleSetJson();
        var applyJson = CreatePrimaryButton("校验并保存 JSON");
        applyJson.Click += async (_, _) => await SaveRuleSetFromJsonAsync();
        jsonActions.Controls.Add(formatJson);
        jsonActions.Controls.Add(applyJson);
        jsonRoot.Controls.Add(jsonActions, 0, 1);
        jsonTab.Controls.Add(jsonRoot);
        editorTabs.TabPages.Add(jsonTab);
        root.Controls.Add(editorTabs, 1, 1);
        return root;
    }

    private void RefreshRulesPage()
    {
        if (_ruleSetList is null || _ruleGrid is null || _ruleSetList.IsDisposed)
        {
            return;
        }

        _isLoading = true;
        try
        {
            _ruleSetList.DataSource = null;
            _ruleSetList.DataSource = _ruleSets.ToArray();
            var activeId = _activeRuleSet?.Id ?? _settings.SelectedRuleSetId;
            var index = Array.FindIndex(_ruleSets.ToArray(), item => string.Equals(item.Id, activeId, StringComparison.Ordinal));
            _ruleSetList.SelectedIndex = index;
            if (index < 0 && _ruleSets.Count > 0)
            {
                _ruleSetList.SelectedIndex = 0;
            }

            _activeRuleSet = _ruleSetList.SelectedItem as RuleSet;
            RenderActiveRuleSet();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void SelectRuleSetFromList()
    {
        if (_isLoading || _ruleSetList?.SelectedItem is not RuleSet selected)
        {
            return;
        }

        _activeRuleSet = CloneRuleSet(selected);
        _settings.SelectedRuleSetId = selected.Id;
        _ = _settingsStore.SaveAsync(_settings);
        RenderActiveRuleSet();
    }

    private void RenderActiveRuleSet()
    {
        if (_ruleGrid is null || _ruleGrid.IsDisposed)
        {
            return;
        }

        _ruleGrid.Rows.Clear();
        if (_activeRuleSet is null)
        {
            _ruleJsonBox?.Clear();
            return;
        }

        foreach (var entry in _activeRuleSet.Entries.OrderBy(entry => entry.Priority))
        {
            var rowIndex = _ruleGrid.Rows.Add(
                entry.Name,
                entry.MatchKind,
                entry.MatchValue,
                entry.Action,
                entry.OutboundTag ?? string.Empty,
                entry.IsEnabled ? "启用" : "停用",
                entry.Priority);
            _ruleGrid.Rows[rowIndex].Tag = entry.Id;
        }

        RenderActiveRuleSetJson();
    }

    private void RenderActiveRuleSetJson()
    {
        if (_ruleJsonBox is null)
        {
            return;
        }

        _ruleJsonBox.Text = _activeRuleSet is null
            ? string.Empty
            : JsonSerializer.Serialize(_activeRuleSet, JsonOptions);
    }

    private async Task AddRuleSetAsync()
    {
        var name = PromptText("新建规则集", "规则集名称", "新规则集");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunOperationAsync("正在创建规则集…", async cancellationToken =>
        {
            var saved = await _ruleSetStore.SaveAsync(new RuleSet
            {
                Name = name,
                CoreId = "sing-box",
                IsEnabled = true,
            }, cancellationToken);
            _settings.SelectedRuleSetId = saved.Id;
            await _settingsStore.SaveAsync(_settings, cancellationToken);
            _ruleSets = await _ruleSetStore.GetAllAsync(cancellationToken);
            _activeRuleSet = saved;
            await WriteRuleSetCopyAsync(saved, cancellationToken);
            PostLog("RULE", $"规则集“{saved.Name}”已创建。");
        });
    }

    private async Task DeleteActiveRuleSetAsync()
    {
        var ruleSet = _activeRuleSet;
        if (ruleSet is null)
        {
            return;
        }

        if (MessageBox.Show(this, $"删除规则集“{ruleSet.Name}”？", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        await RunOperationAsync("正在删除规则集…", async cancellationToken =>
        {
            await _ruleSetStore.DeleteAsync(ruleSet.Id, cancellationToken);
            var copyPath = GetRuleSetCopyPath(ruleSet.Id);
            if (File.Exists(copyPath))
            {
                File.Delete(copyPath);
            }

            if (string.Equals(_settings.SelectedRuleSetId, ruleSet.Id, StringComparison.Ordinal))
            {
                _settings.SelectedRuleSetId = null;
                await _settingsStore.SaveAsync(_settings, cancellationToken);
            }

            _ruleSets = await _ruleSetStore.GetAllAsync(cancellationToken);
            _activeRuleSet = null;
            PostLog("RULE", $"规则集“{ruleSet.Name}”已删除。");
        });
    }

    private async Task AddOrEditRuleEntryAsync(RuleEntry? existing)
    {
        if (_activeRuleSet is null)
        {
            MessageBox.Show(this, "请先创建或选择一个规则集。", "WFly", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!RuleEntryDialog.TryEdit(this, existing, out var draft))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.Id))
        {
            draft.Id = Guid.NewGuid().ToString("N");
        }

        var candidate = CloneRuleSet(_activeRuleSet);
        var index = candidate.Entries.FindIndex(entry => string.Equals(entry.Id, draft.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            candidate.Entries.Add(draft);
        }
        else
        {
            candidate.Entries[index] = draft;
        }

        if (!TryValidateGeneratedRules(candidate, out var validationError))
        {
            MessageBox.Show(this, validationError, "规则不支持", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _activeRuleSet = candidate;
        await SaveActiveRuleSetAsync();
    }

    private async Task DeleteSelectedRuleEntryAsync()
    {
        if (_activeRuleSet is null || GetSelectedRuleEntry() is not { } entry)
        {
            return;
        }

        if (MessageBox.Show(this, $"删除规则“{entry.Name}”？", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        _activeRuleSet.Entries.RemoveAll(item => string.Equals(item.Id, entry.Id, StringComparison.Ordinal));
        await SaveActiveRuleSetAsync();
    }

    private async Task SaveActiveRuleSetAsync()
    {
        if (_activeRuleSet is null)
        {
            return;
        }

        if (!TryValidateGeneratedRules(_activeRuleSet, out var validationError))
        {
            MessageBox.Show(this, validationError, "规则不支持", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await RunOperationAsync("正在保存规则…", async cancellationToken =>
        {
            var saved = await _ruleSetStore.SaveAsync(_activeRuleSet, cancellationToken);
            _activeRuleSet = saved;
            _settings.SelectedRuleSetId = saved.Id;
            await _settingsStore.SaveAsync(_settings, cancellationToken);
            _ruleSets = await _ruleSetStore.GetAllAsync(cancellationToken);
            await WriteRuleSetCopyAsync(saved, cancellationToken);
            PostLog("RULE", $"规则集“{saved.Name}”已保存。");
        });
    }

    private async Task SaveRuleSetFromJsonAsync()
    {
        if (_activeRuleSet is null || _ruleJsonBox is null)
        {
            return;
        }

        RuleSet? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RuleSet>(_ruleJsonBox.Text, JsonOptions);
        }
        catch (JsonException exception)
        {
            MessageBox.Show(this, exception.Message, "规则 JSON 无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (parsed is null)
        {
            MessageBox.Show(this, "规则 JSON 必须是对象。", "规则 JSON 无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        parsed.Id = _activeRuleSet.Id;
        parsed.CreatedAt = _activeRuleSet.CreatedAt;
        if (string.IsNullOrWhiteSpace(parsed.Name))
        {
            MessageBox.Show(this, "规则集名称不能为空。", "规则 JSON 无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!TryValidateGeneratedRules(parsed, out var validationError))
        {
            MessageBox.Show(this, validationError, "规则 JSON 无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _activeRuleSet = parsed;
        await SaveActiveRuleSetAsync();
    }

    private static bool TryValidateGeneratedRules(RuleSet ruleSet, out string error)
    {
        if (ruleSet.Entries is null)
        {
            error = "规则 JSON 的 Entries 必须是数组；空规则集请使用 []。";
            return false;
        }

        foreach (var entry in ruleSet.Entries)
        {
            if (entry is null)
            {
                error = "规则 JSON 的 Entries 不能包含 null。";
                return false;
            }

            if (!entry.IsEnabled || !string.IsNullOrWhiteSpace(entry.ConfigurationJson))
            {
                continue;
            }

            if (!GeneratedRuleMatchKinds.Contains(entry.MatchKind))
            {
                error = $"图形规则“{entry.Name}”的匹配类型“{entry.MatchKind}”尚不支持。请改用受支持字段，或在该规则的配置 JSON 中填写完整的 sing-box 原生规则。";
                return false;
            }

            if (!GeneratedRuleActions.Contains(entry.Action))
            {
                error = $"图形规则“{entry.Name}”的动作“{entry.Action}”尚不支持。请使用代理、直连或拦截，或改填完整的 sing-box 原生规则 JSON。";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(entry.OutboundTag))
            {
                error = $"图形规则“{entry.Name}”只能使用当前选中节点、直连或拦截，不能引用自定义出站标签。请清空出站标签，或改填完整的 sing-box 原生规则 JSON。";
                return false;
            }

            var values = (entry.MatchValue ?? string.Empty)
                .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values.Length == 0)
            {
                error = $"图形规则“{entry.Name}”至少需要一个匹配值。";
                return false;
            }

            if (values.Length > 256)
            {
                error = $"图形规则“{entry.Name}”最多允许 256 个匹配值。";
                return false;
            }

            if (string.Equals(entry.MatchKind, "port", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var value in values)
                {
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                    {
                        error = $"图形规则“{entry.Name}”的端口“{value}”无效；请输入 1 到 65535 的整数。";
                        return false;
                    }
                }
            }
        }

        error = string.Empty;
        return true;
    }

    private RuleEntry? GetSelectedRuleEntry() =>
        _ruleGrid?.CurrentRow?.Tag is string entryId && _activeRuleSet is not null
            ? _activeRuleSet.Entries.FirstOrDefault(entry => string.Equals(entry.Id, entryId, StringComparison.Ordinal))
            : null;

    private async Task WriteRuleSetCopyAsync(RuleSet ruleSet, CancellationToken cancellationToken)
    {
        _paths.EnsureDirectories();
        var targetPath = GetRuleSetCopyPath(ruleSet.Id);
        var temporaryPath = Path.Combine(_paths.RulesDirectory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(ruleSet, JsonOptions), new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetRuleSetCopyPath(string id)
    {
        var safeId = new string(id.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(safeId))
        {
            throw new InvalidDataException("规则集标识无效。");
        }

        return Path.Combine(_paths.RulesDirectory, $"{safeId}.json");
    }

    private static RuleSet CloneRuleSet(RuleSet source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        CoreId = source.CoreId,
        IsEnabled = source.IsEnabled,
        ConfigurationJson = source.ConfigurationJson,
        Entries = (source.Entries ?? [])
        .Where(static entry => entry is not null)
        .Select(entry => new RuleEntry
        {
            Id = entry.Id,
            Name = entry.Name,
            MatchKind = entry.MatchKind,
            MatchValue = entry.MatchValue,
            Action = entry.Action,
            OutboundTag = entry.OutboundTag,
            IsEnabled = entry.IsEnabled,
            Priority = entry.Priority,
            ConfigurationJson = entry.ConfigurationJson,
        }).ToList(),
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private string? PromptText(string title, string caption, string defaultValue)
    {
        using var dialog = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ClientSize = new Size(400, 132),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };
        dialog.HandleCreated += (_, _) => WindowBackdrop.Apply(dialog);
        var label = new Label { Text = caption, AutoSize = true, Location = new Point(14, 14) };
        var textBox = new TextBox { Text = defaultValue, Location = new Point(14, 38), Width = 370 };
        UiControlTheme.ApplyTextBox(textBox);
        var confirm = new RoundedButton
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Location = new Point(222, 88),
            BackColor = UiPalette.Accent,
            ForeColor = Color.White,
            HoverBackColor = Color.FromArgb(54, 98, 207),
            PressedBackColor = Color.FromArgb(43, 83, 178),
            CornerRadius = 9,
            Padding = new Padding(13, 5, 13, 5),
        };
        var cancel = new RoundedButton
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Location = new Point(308, 88),
            BackColor = UiPalette.Hover,
            ForeColor = UiPalette.Ink,
            HoverBackColor = UiPalette.AccentSoft,
            PressedBackColor = Color.FromArgb(214, 227, 251),
            BorderColor = UiPalette.CardBorder,
            BorderThickness = 1,
            CornerRadius = 9,
            Padding = new Padding(13, 5, 13, 5),
        };
        dialog.Controls.AddRange([label, textBox, confirm, cancel]);
        dialog.AcceptButton = confirm;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? textBox.Text.Trim() : null;
    }
}
