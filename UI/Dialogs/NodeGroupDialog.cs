using WFly.Models;
using WFly.UI.Controls;

namespace WFly.UI.Dialogs;

/// <summary>
/// Edits the descriptive fields of a node group. The dialog returns a copy;
/// it never writes a group to disk and never fetches a subscription.
/// </summary>
internal sealed class NodeGroupDialog : Form
{
    private readonly TextBox _nameTextBox = DialogControls.CreateTextBox();
    private readonly TextBox _subscriptionUrlTextBox = DialogControls.CreateTextBox();
    private readonly ComboBox _coreComboBox = DialogControls.CreateComboBox();
    private readonly CheckBox _autoUpdateCheckBox = new()
    {
        Text = "启用自动更新",
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 7, 0, 7)
    };
    private readonly NumericUpDown _updateIntervalNumeric = new()
    {
        Minimum = 1,
        Maximum = 720,
        Value = 6,
        Width = 88,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 4, 0, 4)
    };
    private readonly Label _hoursLabel = new()
    {
        Text = "小时",
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(8, 7, 0, 7)
    };
    private readonly NodeGroup _draft;
    private bool _synchronizing;
    private bool _hadSubscription;

    private NodeGroupDialog(NodeGroup draft)
    {
        _draft = Clone(draft);
        InitializeComponent();
        LoadDraft();
    }

    /// <summary>
    /// Displays the dialog and returns a detached edited copy when the user
    /// chooses Save. Existing identifiers and timestamps are preserved for the
    /// caller to update when it persists the result.
    /// </summary>
    public static bool TryEdit(IWin32Window? owner, NodeGroup? existing, out NodeGroup result)
    {
        using var dialog = new NodeGroupDialog(existing ?? new NodeGroup());
        var dialogResult = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        result = dialogResult == DialogResult.OK ? Clone(dialog._draft) : Clone(existing ?? new NodeGroup());
        return dialogResult == DialogResult.OK;
    }

    private void InitializeComponent()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = string.IsNullOrWhiteSpace(_draft.Id) ? "新建节点组" : "编辑节点组";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(530, 306);
        MinimumSize = Size;
        HandleCreated += (_, _) => WindowBackdrop.Apply(this);
        UiControlTheme.ApplyNumericUpDown(_updateIntervalNumeric);

        _subscriptionUrlTextBox.PlaceholderText = "留空即可创建手动节点组；订阅仅支持 HTTPS";

        AddCoreOption("auto", "自动判断（订阅）");
        AddCoreOption("sing-box", "sing-box");
        AddCoreOption("mihomo", "mihomo");
        AddCoreOption("xray-core", "Xray-core");

        var updatePanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0)
        };
        updatePanel.Controls.Add(_updateIntervalNumeric);
        updatePanel.Controls.Add(_hoursLabel);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 14, 16, 8),
            ColumnCount = 2,
            RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        DialogControls.AddLabeledControl(layout, 0, "节点组名称", _nameTextBox);
        DialogControls.AddLabeledControl(layout, 1, "订阅链接", _subscriptionUrlTextBox);
        DialogControls.AddLabeledControl(layout, 2, "代理内核", _coreComboBox);
        DialogControls.AddLabeledControl(layout, 3, "订阅更新", _autoUpdateCheckBox);
        DialogControls.AddLabeledControl(layout, 4, "更新间隔", updatePanel);

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
        _subscriptionUrlTextBox.TextChanged += (_, _) => SynchronizeSubscriptionControls();
        _autoUpdateCheckBox.CheckedChanged += (_, _) => SynchronizeUpdateIntervalState();
    }

    private void LoadDraft()
    {
        _synchronizing = true;
        _nameTextBox.Text = _draft.Name;
        _subscriptionUrlTextBox.Text = _draft.SubscriptionUrl ?? string.Empty;
        _hadSubscription = !string.IsNullOrWhiteSpace(_draft.SubscriptionUrl);
        SelectCore(_draft.CoreId);
        _autoUpdateCheckBox.Checked = _draft.UpdateIntervalHours is not null;
        _updateIntervalNumeric.Value = Math.Clamp(_draft.UpdateIntervalHours ?? 6,
            decimal.ToInt32(_updateIntervalNumeric.Minimum), decimal.ToInt32(_updateIntervalNumeric.Maximum));
        _synchronizing = false;
        SynchronizeSubscriptionControls();
    }

    private void SynchronizeSubscriptionControls()
    {
        if (_synchronizing)
        {
            return;
        }

        var hasSubscription = !string.IsNullOrWhiteSpace(_subscriptionUrlTextBox.Text);
        _synchronizing = true;
        if (!hasSubscription)
        {
            if (_coreComboBox.SelectedItem is null)
            {
                SelectCore("sing-box");
            }

            _autoUpdateCheckBox.Checked = false;
        }
        else if (!_hadSubscription)
        {
            SelectCore("auto");
            _updateIntervalNumeric.Value = 6;
            _autoUpdateCheckBox.Checked = true;
        }

        // A manual group can choose its core. Subscription-backed groups are
        // deliberately detected by the importer instead of letting a manual
        // choice claim compatibility with a format the importer cannot render.
        _coreComboBox.Enabled = !hasSubscription;
        _autoUpdateCheckBox.Enabled = hasSubscription;
        _synchronizing = false;
        _hadSubscription = hasSubscription;
        SynchronizeUpdateIntervalState();
    }

    private void SynchronizeUpdateIntervalState()
    {
        var enabled = !_synchronizing
            && !string.IsNullOrWhiteSpace(_subscriptionUrlTextBox.Text)
            && _autoUpdateCheckBox.Checked;
        _updateIntervalNumeric.Enabled = enabled;
        _hoursLabel.Enabled = enabled;
    }

    private void OnSaveClick(object? sender, EventArgs eventArgs)
    {
        var name = _nameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            DialogControls.ShowValidationError(this, "请输入节点组名称。");
            _nameTextBox.Focus();
            return;
        }

        var subscriptionUrl = DialogControls.NullIfWhiteSpace(_subscriptionUrlTextBox.Text);
        if (subscriptionUrl is not null
            && (!Uri.TryCreate(subscriptionUrl, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(uri.Host)
                || !string.IsNullOrEmpty(uri.UserInfo)))
        {
            DialogControls.ShowValidationError(this, "订阅链接必须是没有账号信息的有效 HTTPS 地址。");
            _subscriptionUrlTextBox.Focus();
            return;
        }

        _draft.Name = name;
        _draft.SubscriptionUrl = subscriptionUrl;
        _draft.CoreId = SelectedCoreId();
        _draft.UpdateIntervalHours = subscriptionUrl is not null && _autoUpdateCheckBox.Checked
            ? decimal.ToInt32(_updateIntervalNumeric.Value)
            : null;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void AddCoreOption(string id, string label) => _coreComboBox.Items.Add(new CoreOption(id, label));

    private string SelectedCoreId() => _coreComboBox.SelectedItem is CoreOption option ? option.Id : "auto";

    private void SelectCore(string? coreId)
    {
        var wanted = string.IsNullOrWhiteSpace(coreId) ? "sing-box" : coreId;
        for (var index = 0; index < _coreComboBox.Items.Count; index++)
        {
            if (_coreComboBox.Items[index] is CoreOption option
                && string.Equals(option.Id, wanted, StringComparison.OrdinalIgnoreCase))
            {
                _coreComboBox.SelectedIndex = index;
                return;
            }
        }

        _coreComboBox.Items.Add(new CoreOption(wanted, wanted));
        _coreComboBox.SelectedIndex = _coreComboBox.Items.Count - 1;
    }

    private static NodeGroup Clone(NodeGroup source)
    {
        return new NodeGroup
        {
            Id = source.Id,
            Name = source.Name,
            SubscriptionUrl = source.SubscriptionUrl,
            CoreId = source.CoreId,
            UpdateIntervalHours = source.UpdateIntervalHours,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            LastUpdatedAt = source.LastUpdatedAt,
            LastUpdateError = source.LastUpdateError
        };
    }

    private sealed record CoreOption(string Id, string Label)
    {
        public override string ToString() => Label;
    }
}
