using System.Text.Json;
using WFly.Models;

namespace WFly.UI.Dialogs;

/// <summary>
/// A core-neutral proxy-node editor. It accepts either a share link, a
/// sing-box outbound JSON object, or both, and leaves parsing to the caller.
/// </summary>
internal sealed class ProxyNodeDialog : Form
{
    private static readonly string[] Protocols =
    [
        "VMess", "VLESS", "Shadowsocks", "Trojan", "Hysteria2", "TUIC",
        "WireGuard", "SOCKS", "HTTP", "AnyTLS", "Naive", "Custom",
        "策略组", "代理链", "sing-box 自定义出站"
    ];

    private readonly ProxyNode _draft;
    private readonly IReadOnlyList<NodeGroup> _groups;
    private readonly TextBox _nameTextBox = DialogControls.CreateTextBox();
    private readonly ComboBox _groupComboBox = DialogControls.CreateComboBox();
    private readonly ComboBox _protocolComboBox = DialogControls.CreateComboBox();
    private readonly TextBox _shareLinkTextBox = DialogControls.CreateTextBox(multiline: true);
    private readonly TextBox _outboundJsonTextBox = DialogControls.CreateTextBox(multiline: true);
    private readonly CheckBox _enabledCheckBox = new()
    {
        Text = "启用此节点",
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 7, 0, 7)
    };

    private ProxyNodeDialog(ProxyNode draft, IReadOnlyList<NodeGroup> groups)
    {
        _draft = Clone(draft);
        _groups = groups;
        InitializeComponent();
        LoadDraft();
    }

    /// <summary>
    /// Opens an editor for a node belonging to one of <paramref name="groups"/>.
    /// A node cannot be added when there are no user-created groups.
    /// </summary>
    public static bool TryEdit(
        IWin32Window? owner,
        ProxyNode? existing,
        IEnumerable<NodeGroup> groups,
        out ProxyNode result)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var availableGroups = groups
            .Where(group => !string.IsNullOrWhiteSpace(group.Id) && !string.IsNullOrWhiteSpace(group.Name))
            .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        if (availableGroups.Length == 0)
        {
            result = Clone(existing ?? new ProxyNode());
            if (owner is not null)
            {
                DialogControls.ShowValidationError(owner, "请先创建节点组，再添加节点。");
            }

            return false;
        }

        using var dialog = new ProxyNodeDialog(existing ?? new ProxyNode(), availableGroups);
        var dialogResult = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        result = dialogResult == DialogResult.OK ? Clone(dialog._draft) : Clone(existing ?? new ProxyNode());
        return dialogResult == DialogResult.OK;
    }

    private void InitializeComponent()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = string.IsNullOrWhiteSpace(_draft.Id) ? "添加节点" : "编辑节点";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 615);
        MinimumSize = new Size(620, 520);

        foreach (var group in _groups)
        {
            _groupComboBox.Items.Add(new GroupOption(group.Id, group.Name));
        }

        _protocolComboBox.Items.AddRange(Protocols);
        _shareLinkTextBox.PlaceholderText = "例如 vless://、vmess://、ss://、trojan:// 等分享链接";
        _outboundJsonTextBox.PlaceholderText = "可选：sing-box outbound JSON 对象";
        _shareLinkTextBox.Height = 80;
        _outboundJsonTextBox.Height = 190;
        _outboundJsonTextBox.Font = new Font(FontFamily.GenericMonospace, 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 14, 16, 8),
            ColumnCount = 2,
            RowCount = 6
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        DialogControls.AddLabeledControl(layout, 0, "节点名称", _nameTextBox);
        DialogControls.AddLabeledControl(layout, 1, "所属节点组", _groupComboBox);
        DialogControls.AddLabeledControl(layout, 2, "协议类型", _protocolComboBox);
        DialogControls.AddLabeledControl(layout, 3, "分享链接", _shareLinkTextBox);
        DialogControls.AddLabeledControl(layout, 4, "sing-box 出站 JSON", _outboundJsonTextBox);
        DialogControls.AddLabeledControl(layout, 5, "状态", _enabledCheckBox);

        var okButton = new Button { Text = "保存" };
        okButton.Click += OnSaveClick;
        var cancelButton = new Button { Text = "取消" };
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
        _shareLinkTextBox.Text = _draft.ShareLink ?? string.Empty;
        _outboundJsonTextBox.Text = _draft.ConfigurationJson ?? string.Empty;
        _enabledCheckBox.Checked = _draft.IsEnabled;

        SelectGroup(_draft.GroupId);
        SelectProtocol(_draft.Protocol);
    }

    private void OnSaveClick(object? sender, EventArgs eventArgs)
    {
        var name = _nameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            DialogControls.ShowValidationError(this, "请输入节点名称。");
            _nameTextBox.Focus();
            return;
        }

        if (_groupComboBox.SelectedItem is not GroupOption group)
        {
            DialogControls.ShowValidationError(this, "请选择节点所属的节点组。");
            _groupComboBox.Focus();
            return;
        }

        var shareLink = DialogControls.NullIfWhiteSpace(_shareLinkTextBox.Text);
        var outboundJson = DialogControls.NullIfWhiteSpace(_outboundJsonTextBox.Text);
        if (shareLink is null && outboundJson is null)
        {
            DialogControls.ShowValidationError(this, "请填写分享链接或 sing-box 出站 JSON。");
            _shareLinkTextBox.Focus();
            return;
        }

        if (!IsValidOutboundObject(outboundJson, out var jsonError))
        {
            DialogControls.ShowValidationError(this, $"sing-box 出站 JSON 无效：{jsonError}");
            _outboundJsonTextBox.Focus();
            return;
        }

        _draft.Name = name;
        _draft.GroupId = group.Id;
        _draft.Protocol = _protocolComboBox.SelectedItem?.ToString() ?? "Custom";
        _draft.ShareLink = shareLink;
        _draft.ConfigurationJson = outboundJson;
        _draft.IsEnabled = _enabledCheckBox.Checked;
        if (string.IsNullOrWhiteSpace(_draft.CoreId))
        {
            _draft.CoreId = "sing-box";
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static bool IsValidOutboundObject(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "根元素必须是 JSON 对象。";
                return false;
            }

            return true;
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private void SelectGroup(string? groupId)
    {
        for (var index = 0; index < _groupComboBox.Items.Count; index++)
        {
            if (_groupComboBox.Items[index] is GroupOption option
                && string.Equals(option.Id, groupId, StringComparison.Ordinal))
            {
                _groupComboBox.SelectedIndex = index;
                return;
            }
        }

        _groupComboBox.SelectedIndex = 0;
    }

    private void SelectProtocol(string? protocol)
    {
        var wanted = string.IsNullOrWhiteSpace(protocol) ? "VMess" : protocol;
        var index = _protocolComboBox.FindStringExact(wanted);
        if (index >= 0)
        {
            _protocolComboBox.SelectedIndex = index;
            return;
        }

        _protocolComboBox.Items.Add(wanted);
        _protocolComboBox.SelectedIndex = _protocolComboBox.Items.Count - 1;
    }

    private static ProxyNode Clone(ProxyNode source)
    {
        return new ProxyNode
        {
            Id = source.Id,
            GroupId = source.GroupId,
            Name = source.Name,
            Protocol = source.Protocol,
            CoreId = source.CoreId,
            ShareLink = source.ShareLink,
            ConfigurationJson = source.ConfigurationJson,
            IsEnabled = source.IsEnabled,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
    }

    private sealed record GroupOption(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}
