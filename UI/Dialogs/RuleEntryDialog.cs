using WFly.Models;
using WFly.UI.Controls;

namespace WFly.UI.Dialogs;

/// <summary>
/// Edits one graphical routing rule while preserving an optional raw JSON
/// fragment for configuration-file mode.
/// </summary>
internal sealed class RuleEntryDialog : Form
{
    private static readonly string[] MatchKinds =
    [
        "domain", "domain_suffix", "domain_keyword", "ip_cidr", "port",
        "process_name", "process_path", "network", "protocol", "inbound"
    ];

    private static readonly string[] Actions =
    ["proxy", "direct", "block"];

    private readonly RuleEntry _draft;
    private readonly TextBox _nameTextBox = DialogControls.CreateTextBox();
    private readonly ComboBox _matchKindComboBox = DialogControls.CreateComboBox();
    private readonly TextBox _matchValueTextBox = DialogControls.CreateTextBox();
    private readonly ComboBox _actionComboBox = DialogControls.CreateComboBox();
    private readonly TextBox _outboundTagTextBox = DialogControls.CreateTextBox();
    private readonly NumericUpDown _priorityNumeric = new()
    {
        Minimum = -10000,
        Maximum = 10000,
        Anchor = AnchorStyles.Left,
        Width = 100,
        Margin = new Padding(0, 4, 0, 4)
    };
    private readonly CheckBox _enabledCheckBox = new()
    {
        Text = "启用此规则",
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 7, 0, 7)
    };
    private readonly TextBox _configurationJsonTextBox = DialogControls.CreateTextBox(multiline: true);

    private RuleEntryDialog(RuleEntry draft)
    {
        _draft = Clone(draft);
        InitializeComponent();
        LoadDraft();
    }

    /// <summary>
    /// Shows the graphical rule editor and returns an edited detached copy on
    /// Save. The caller owns ordering and persistence of the result.
    /// </summary>
    public static bool TryEdit(IWin32Window? owner, RuleEntry? existing, out RuleEntry result)
    {
        using var dialog = new RuleEntryDialog(existing ?? new RuleEntry());
        var dialogResult = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        result = dialogResult == DialogResult.OK ? Clone(dialog._draft) : Clone(existing ?? new RuleEntry());
        return dialogResult == DialogResult.OK;
    }

    private void InitializeComponent()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = string.IsNullOrWhiteSpace(_draft.Id) ? "添加规则" : "编辑规则";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 590);
        MinimumSize = new Size(580, 500);
        HandleCreated += (_, _) => WindowBackdrop.Apply(this);
        UiControlTheme.ApplyNumericUpDown(_priorityNumeric);

        _matchKindComboBox.Items.AddRange(MatchKinds);
        _actionComboBox.Items.AddRange(Actions);
        _matchValueTextBox.PlaceholderText = "例如 example.com、1.2.3.0/24、443、chrome.exe";
        _outboundTagTextBox.PlaceholderText = "图形规则留空；自定义出站请填写完整的 sing-box JSON";
        _configurationJsonTextBox.PlaceholderText = "可选：保留的 core-specific JSON 规则片段";
        _configurationJsonTextBox.Height = 165;
        _configurationJsonTextBox.Font = new Font(FontFamily.GenericMonospace, 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 14, 16, 8),
            ColumnCount = 2,
            RowCount = 8
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        DialogControls.AddLabeledControl(layout, 0, "规则名称", _nameTextBox);
        DialogControls.AddLabeledControl(layout, 1, "匹配类型", _matchKindComboBox);
        DialogControls.AddLabeledControl(layout, 2, "匹配值", _matchValueTextBox);
        DialogControls.AddLabeledControl(layout, 3, "处理方式", _actionComboBox);
        DialogControls.AddLabeledControl(layout, 4, "出站标签", _outboundTagTextBox);
        DialogControls.AddLabeledControl(layout, 5, "优先级", _priorityNumeric);
        DialogControls.AddLabeledControl(layout, 6, "状态", _enabledCheckBox);
        DialogControls.AddLabeledControl(layout, 7, "配置 JSON", _configurationJsonTextBox);

        var okButton = DialogControls.CreatePrimaryButton("保存");
        okButton.Click += OnSaveClick;
        var cancelButton = DialogControls.CreateSecondaryButton("取消");
        var buttons = DialogControls.CreateButtonPanel(okButton, cancelButton);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(layout, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private void LoadDraft()
    {
        _nameTextBox.Text = _draft.Name;
        _matchValueTextBox.Text = _draft.MatchValue;
        _outboundTagTextBox.Text = _draft.OutboundTag ?? string.Empty;
        _configurationJsonTextBox.Text = _draft.ConfigurationJson ?? string.Empty;
        _enabledCheckBox.Checked = _draft.IsEnabled;
        _priorityNumeric.Value = Math.Clamp(_draft.Priority,
            decimal.ToInt32(_priorityNumeric.Minimum), decimal.ToInt32(_priorityNumeric.Maximum));
        SelectChoice(_matchKindComboBox, _draft.MatchKind, "domain_suffix");
        SelectChoice(_actionComboBox, _draft.Action, "proxy");
    }

    private void OnSaveClick(object? sender, EventArgs eventArgs)
    {
        var name = _nameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            DialogControls.ShowValidationError(this, "请输入规则名称。");
            _nameTextBox.Focus();
            return;
        }

        var matchKind = _matchKindComboBox.SelectedItem?.ToString() ?? "domain_suffix";
        var matchValue = _matchValueTextBox.Text.Trim();
        var configurationJson = DialogControls.NullIfWhiteSpace(_configurationJsonTextBox.Text);
        if (string.IsNullOrWhiteSpace(matchValue) && configurationJson is null)
        {
            DialogControls.ShowValidationError(this, "请输入图形化匹配值，或填写配置 JSON。");
            _matchValueTextBox.Focus();
            return;
        }

        if (!DialogControls.IsValidJson(configurationJson, out var jsonError))
        {
            DialogControls.ShowValidationError(this, $"配置 JSON 无效：{jsonError}");
            _configurationJsonTextBox.Focus();
            return;
        }

        _draft.Name = name;
        _draft.MatchKind = matchKind;
        _draft.MatchValue = matchValue;
        _draft.Action = _actionComboBox.SelectedItem?.ToString() ?? "proxy";
        _draft.OutboundTag = DialogControls.NullIfWhiteSpace(_outboundTagTextBox.Text);
        _draft.Priority = decimal.ToInt32(_priorityNumeric.Value);
        _draft.IsEnabled = _enabledCheckBox.Checked;
        _draft.ConfigurationJson = configurationJson;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static void SelectChoice(ComboBox comboBox, string? value, string defaultValue)
    {
        var wanted = string.IsNullOrWhiteSpace(value) ? defaultValue : value;
        var index = comboBox.FindStringExact(wanted);
        if (index >= 0)
        {
            comboBox.SelectedIndex = index;
            return;
        }

        comboBox.Items.Add(wanted);
        comboBox.SelectedIndex = comboBox.Items.Count - 1;
    }

    private static RuleEntry Clone(RuleEntry source)
    {
        return new RuleEntry
        {
            Id = source.Id,
            Name = source.Name,
            MatchKind = source.MatchKind,
            MatchValue = source.MatchValue,
            Action = source.Action,
            OutboundTag = source.OutboundTag,
            IsEnabled = source.IsEnabled,
            Priority = source.Priority,
            ConfigurationJson = source.ConfigurationJson
        };
    }
}
