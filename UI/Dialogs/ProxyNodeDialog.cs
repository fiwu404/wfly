using System.Text.Json;
using WFly.Models;
using WFly.Services;
using WFly.UI.Controls;

namespace WFly.UI.Dialogs;

/// <summary>
/// Protocol-aware node editor modelled after v2rayN's AddServerWindow.
/// </summary>
internal sealed class ProxyNodeDialog : Form
{
    private static readonly string[] VmessSecurities = ["auto", "aes-128-gcm", "chacha20-poly1305", "none", "zero"];
    private static readonly string[] SsMethods =
    [
        "aes-256-gcm", "aes-192-gcm", "aes-128-gcm", "chacha20-ietf-poly1305",
        "xchacha20-ietf-poly1305", "2022-blake3-aes-128-gcm",
        "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
        "aes-128-ctr", "aes-192-ctr", "aes-256-ctr", "aes-128-cfb",
        "aes-192-cfb", "aes-256-cfb", "rc4-md5", "chacha20-ietf", "xchacha20", "none"
    ];

    private readonly ProxyNode _draft;
    private readonly IReadOnlyList<NodeGroup> _groups;
    private readonly Func<string, ParsedShareLink>? _shareLinkParser;
    private ManualNodeOptions _options;

    private readonly TextBox _name = Input();
    private readonly ComboBox _group = Combo();
    private readonly ComboBox _protocol = Combo();
    private readonly CheckBox _enabled = Check("启用此节点");

    private readonly TextBox _server = Input();
    private readonly NumericUpDown _port = Number(1, 65535, 443);
    private readonly TextBox _username = Input();
    private readonly TextBox _password = Input();
    private readonly ComboBox _vmessSecurity = Combo(VmessSecurities);
    private readonly NumericUpDown _alterId = Number(0, int.MaxValue, 0);
    private readonly ComboBox _vlessEncryption = Combo(["none", "mlkem768x25519plus.native.0rtt", "mlkem768x25519plus.native.1rtt"]);
    private readonly ComboBox _flow = Combo(["", "xtls-rprx-vision", "xtls-rprx-vision-udp443"]);
    private readonly ComboBox _ssMethod = Combo(SsMethods);
    private readonly TextBox _plugin = Input();
    private readonly TextBox _pluginOptions = Input();
    private readonly ComboBox _socksVersion = Combo(["5", "4", "4a"]);
    private readonly TextBox _httpHeaders = Input(multiline: true);
    private readonly CheckBox _udpOverTcp = Check("启用 UDP over TCP");
    private readonly CheckBox _mux = Check("启用多路复用（Mux）");

    private readonly TextBox _serverPorts = Input();
    private readonly TextBox _hopInterval = Input();
    private readonly NumericUpDown _upMbps = Number(0, 1_000_000, 0);
    private readonly NumericUpDown _downMbps = Number(0, 1_000_000, 0);
    private readonly ComboBox _obfsType = Combo(["salamander"]);
    private readonly TextBox _obfsPassword = Input();
    private readonly TextBox _hysteriaRealmUrl = Input();
    private readonly NumericUpDown _geckoMinPacket = Number(0, 65535, 0);
    private readonly NumericUpDown _geckoMaxPacket = Number(0, 65535, 0);

    private readonly ComboBox _congestion = Combo(["bbr", "cubic", "new_reno", "bbr2", "reno"]);
    private readonly ComboBox _udpRelayMode = Combo(["native", "quic"]);
    private readonly CheckBox _zeroRtt = Check("启用 0-RTT 握手");
    private readonly TextBox _heartbeat = Input();

    private readonly TextBox _wgPublicKey = Input();
    private readonly TextBox _wgPreSharedKey = Input();
    private readonly TextBox _wgLocalAddress = Input();
    private readonly TextBox _wgReserved = Input();
    private readonly NumericUpDown _wgMtu = Number(576, 65535, 1280);

    private readonly TextBox _idleCheckInterval = Input();
    private readonly TextBox _idleTimeout = Input();
    private readonly NumericUpDown _minIdleSession = Number(0, 100_000, 0);
    private readonly NumericUpDown _insecureConcurrency = Number(0, 100_000, 0);
    private readonly CheckBox _naiveQuic = Check("使用 QUIC");

    private readonly ComboBox _network = Combo(["raw", "ws", "http", "httpupgrade", "grpc", "xhttp", "kcp"]);
    private readonly ComboBox _headerType = Combo(["none", "http", "srtp", "utp", "wechat-video", "dtls", "wireguard", "dns"]);
    private readonly TextBox _host = Input();
    private readonly TextBox _path = Input();
    private readonly ComboBox _transportMode = Combo(["", "auto", "packet-up", "stream-up", "stream-one", "gun", "multi"]);
    private readonly TextBox _transportExtra = Input(multiline: true);
    private readonly NumericUpDown _kcpMtu = Number(0, 65535, 0);

    private readonly ComboBox _tlsSecurity = Combo(["none", "tls", "reality"]);
    private readonly CheckBox _allowInsecure = Check("跳过证书验证（不安全）");
    private readonly TextBox _sni = Input();
    private readonly ComboBox _alpn = Combo(["", "h3", "h2", "http/1.1", "h3,h2", "h2,http/1.1", "h3,h2,http/1.1"]);
    private readonly ComboBox _fingerprint = Combo(["", "chrome", "firefox", "safari", "ios", "android", "edge", "360", "qq", "random", "randomized"]);
    private readonly TextBox _realityPublicKey = Input();
    private readonly TextBox _realityShortId = Input();
    private readonly TextBox _realitySpiderX = Input();
    private readonly TextBox _mldsa65Verify = Input();
    private readonly TextBox _certificate = Input(multiline: true);
    private readonly TextBox _certificateSha = Input();
    private readonly TextBox _echConfigList = Input(multiline: true);
    private readonly TextBox _verifyPeerByName = Input();
    private readonly TextBox _finalMask = Input();

    private readonly TextBox _shareLink = Input(multiline: true);
    private readonly TextBox _outboundJson = Input(multiline: true);
    private readonly CheckBox _customJsonOverride = Check("使用下方 JSON 覆盖表单生成的配置");
    private readonly TableLayoutPanel _protocolFields = FormLayout();
    private readonly TabPage _transportPage = new("传输设置");
    private readonly TabPage _tlsPage = new("TLS / Reality");

    private ProxyNodeDialog(
        ProxyNode draft,
        IReadOnlyList<NodeGroup> groups,
        Func<string, ParsedShareLink>? shareLinkParser)
    {
        _draft = Clone(draft);
        _groups = groups;
        _shareLinkParser = shareLinkParser;
        _options = ManualNodeConfiguration.Load(_draft.ManualOptionsJson, _draft.ConfigurationJson);
        InitializeComponent();
        LoadDraft();
    }

    public static bool TryEdit(
        IWin32Window? owner,
        ProxyNode? existing,
        IEnumerable<NodeGroup> groups,
        Func<string, ParsedShareLink>? shareLinkParser,
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
            if (owner is not null) DialogControls.ShowValidationError(owner, "请先创建节点组，再添加节点。");
            return false;
        }

        using var dialog = new ProxyNodeDialog(existing ?? new ProxyNode(), availableGroups, shareLinkParser);
        var dialogResult = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        result = dialogResult == DialogResult.OK ? Clone(dialog._draft) : Clone(existing ?? new ProxyNode());
        return dialogResult == DialogResult.OK;
    }

    private void InitializeComponent()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = string.IsNullOrWhiteSpace(_draft.Id) ? "添加节点" : "编辑节点";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(900, 720);
        MinimumSize = new Size(720, 580);
        HandleCreated += (_, _) => WindowBackdrop.Apply(this);

        foreach (var group in _groups) _group.Items.Add(new GroupOption(group.Id, group.Name));
        _protocol.Items.AddRange(ManualNodeConfiguration.Protocols);
        _protocol.SelectedIndexChanged += (_, _) => RebuildProtocolFields();
        _network.SelectedIndexChanged += (_, _) => UpdateTransportFieldState();
        _tlsSecurity.SelectedIndexChanged += (_, _) => UpdateTlsFieldState();

        _shareLink.Height = 82;
        _shareLink.PlaceholderText = "粘贴 vmess://、vless://、ss:// 或 trojan:// 分享链接后点击“解析到表单”";
        _outboundJson.Height = 230;
        _outboundJson.Font = new Font(FontFamily.GenericMonospace, 9F);
        _outboundJson.PlaceholderText = "sing-box outbound JSON 对象";
        _httpHeaders.Height = 86;
        _transportExtra.Height = 100;
        _certificate.Height = 110;
        _echConfigList.Height = 70;

        var header = FormLayout();
        header.Padding = new Padding(16, 10, 16, 4);
        AddRows(header,
            ("节点名称", _name),
            ("所属节点组", _group),
            ("协议类型", _protocol),
            ("状态", _enabled));

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 5) };
        var protocolPage = new TabPage("服务器与协议") { Padding = new Padding(12) };
        protocolPage.Controls.Add(ScrollHost(_protocolFields));

        _transportPage.Padding = new Padding(12);
        var transportForm = FormLayout();
        AddRows(transportForm,
            ("传输方式", _network),
            ("伪装 / Header 类型", _headerType),
            ("Host / Authority", _host),
            ("路径 / ServiceName / Seed", _path),
            ("xHTTP / gRPC 模式", _transportMode),
            ("xHTTP 额外参数 JSON", _transportExtra),
            ("KCP MTU（0 为默认）", _kcpMtu));
        _transportPage.Controls.Add(ScrollHost(transportForm));

        _tlsPage.Padding = new Padding(12);
        var tlsForm = FormLayout();
        AddRows(tlsForm,
            ("传输层安全", _tlsSecurity),
            ("允许不安全", _allowInsecure),
            ("SNI / Server Name", _sni),
            ("ALPN（逗号分隔）", _alpn),
            ("uTLS 指纹", _fingerprint),
            ("Reality 公钥", _realityPublicKey),
            ("Reality Short ID", _realityShortId),
            ("Reality SpiderX", _realitySpiderX),
            ("ML-DSA-65 Verify", _mldsa65Verify),
            ("证书 PEM", _certificate),
            ("证书 SHA-256", _certificateSha),
            ("ECH Config List", _echConfigList),
            ("按名称校验证书", _verifyPeerByName),
            ("Final Mask", _finalMask));
        _tlsPage.Controls.Add(ScrollHost(tlsForm));

        var advancedPage = new TabPage("分享链接与高级") { Padding = new Padding(12) };
        var advancedForm = FormLayout();
        var importButton = DialogControls.CreateSecondaryButton("解析到表单");
        importButton.Click += OnImportShareLink;
        var importPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        importPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        importPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        importPanel.Controls.Add(_shareLink, 0, 0);
        importPanel.Controls.Add(importButton, 1, 0);
        AddRows(advancedForm,
            ("分享链接", importPanel),
            ("JSON 覆盖", _customJsonOverride),
            ("sing-box 出站 JSON", _outboundJson));
        advancedPage.Controls.Add(ScrollHost(advancedForm));

        tabs.TabPages.AddRange([protocolPage, _transportPage, _tlsPage, advancedPage]);

        var okButton = DialogControls.CreatePrimaryButton("保存");
        okButton.Click += OnSaveClick;
        var cancelButton = DialogControls.CreateSecondaryButton("取消");
        var buttons = DialogControls.CreateButtonPanel(okButton, cancelButton);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(tabs, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private void LoadDraft()
    {
        _name.Text = _draft.Name;
        _shareLink.Text = _draft.ShareLink ?? string.Empty;
        _outboundJson.Text = _draft.ConfigurationJson ?? string.Empty;
        _enabled.Checked = _draft.IsEnabled;
        SelectGroup(_draft.GroupId);
        var detected = ManualNodeConfiguration.DetectProtocol(_draft.ConfigurationJson);
        SelectProtocol(detected ?? _draft.Protocol);
        ApplyOptionsToControls();
        RebuildProtocolFields();
        UpdateTransportFieldState();
        UpdateTlsFieldState();
    }

    private void RebuildProtocolFields()
    {
        _protocolFields.SuspendLayout();
        _protocolFields.Controls.Clear();
        _protocolFields.RowStyles.Clear();
        _protocolFields.RowCount = 0;

        var protocol = CurrentProtocol;
        if (protocol == "sing-box 自定义出站")
        {
            var notice = new Label { AutoSize = true, Text = "请在“分享链接与高级”页填写完整 sing-box 出站 JSON。", ForeColor = UiPalette.MutedInk, Padding = new Padding(0, 10, 0, 10) };
            AddRows(_protocolFields, ("自定义配置", notice));
            _transportPage.Enabled = false;
            _tlsPage.Enabled = false;
            _customJsonOverride.Checked = true;
            _protocolFields.ResumeLayout(true);
            return;
        }

        _transportPage.Enabled = protocol is "VMess" or "VLESS" or "Trojan";
        _tlsPage.Enabled = protocol is "VMess" or "VLESS" or "Trojan" or "Hysteria2" or "TUIC" or "HTTP" or "AnyTLS" or "Naive";
        var fields = new List<(string, Control)> { ("服务器地址", _server), ("服务器端口", _port) };
        switch (protocol)
        {
            case "VMess":
                fields.AddRange([("用户 ID / UUID", _password), ("Alter ID", _alterId), ("加密方式", _vmessSecurity), ("多路复用", _mux)]);
                break;
            case "VLESS":
                fields.AddRange([("用户 ID / UUID", _password), ("Encryption", _vlessEncryption), ("Flow", _flow), ("多路复用", _mux)]);
                break;
            case "Shadowsocks":
                fields.AddRange([("密码", _password), ("加密方法", _ssMethod), ("插件", _plugin), ("插件参数", _pluginOptions), ("UDP over TCP", _udpOverTcp)]);
                break;
            case "Trojan":
                fields.AddRange([("密码", _password), ("多路复用", _mux)]);
                break;
            case "Hysteria2":
                fields.AddRange([("密码", _password), ("端口跳跃范围", _serverPorts), ("跳跃间隔", _hopInterval), ("上传带宽 Mbps", _upMbps), ("下载带宽 Mbps", _downMbps), ("混淆类型", _obfsType), ("混淆密码", _obfsPassword), ("Realm URL", _hysteriaRealmUrl), ("Gecko 最小包长", _geckoMinPacket), ("Gecko 最大包长", _geckoMaxPacket)]);
                break;
            case "TUIC":
                fields.AddRange([("用户 ID / UUID", _username), ("密码", _password), ("拥塞控制", _congestion), ("UDP Relay 模式", _udpRelayMode), ("0-RTT", _zeroRtt), ("心跳间隔", _heartbeat)]);
                break;
            case "WireGuard":
                fields.AddRange([("本机私钥", _password), ("对端公钥", _wgPublicKey), ("预共享密钥", _wgPreSharedKey), ("本机地址（逗号分隔）", _wgLocalAddress), ("Reserved（逗号分隔）", _wgReserved), ("MTU", _wgMtu)]);
                break;
            case "SOCKS":
                fields.AddRange([("SOCKS 版本", _socksVersion), ("用户名", _username), ("密码", _password), ("UDP over TCP", _udpOverTcp)]);
                break;
            case "HTTP":
                fields.AddRange([("用户名", _username), ("密码", _password), ("请求头 JSON", _httpHeaders)]);
                break;
            case "AnyTLS":
                fields.AddRange([("密码", _password), ("空闲会话检查间隔", _idleCheckInterval), ("空闲会话超时", _idleTimeout), ("最少空闲会话", _minIdleSession)]);
                break;
            case "Naive":
                fields.AddRange([("用户名", _username), ("密码", _password), ("拥塞控制", _congestion), ("不安全并发数", _insecureConcurrency), ("QUIC", _naiveQuic)]);
                break;
        }
        AddRows(_protocolFields, fields.ToArray());
        _protocolFields.ResumeLayout(true);
    }

    private void UpdateTransportFieldState()
    {
        var network = _network.Text.Trim().ToLowerInvariant();
        _headerType.Enabled = network is "raw" or "kcp";
        _host.Enabled = network is "raw" or "ws" or "http" or "httpupgrade" or "grpc" or "xhttp";
        _path.Enabled = network is "raw" or "ws" or "http" or "httpupgrade" or "grpc" or "xhttp" or "kcp";
        _transportMode.Enabled = network is "grpc" or "xhttp";
        _transportExtra.Enabled = network == "xhttp";
        _kcpMtu.Enabled = network == "kcp";
    }

    private void UpdateTlsFieldState()
    {
        var enabled = !string.Equals(_tlsSecurity.Text, "none", StringComparison.OrdinalIgnoreCase);
        foreach (var control in new Control[] { _allowInsecure, _sni, _alpn, _fingerprint, _certificate, _certificateSha, _echConfigList, _verifyPeerByName, _finalMask }) control.Enabled = enabled;
        var reality = string.Equals(_tlsSecurity.Text, "reality", StringComparison.OrdinalIgnoreCase);
        foreach (var control in new Control[] { _realityPublicKey, _realityShortId, _realitySpiderX, _mldsa65Verify }) control.Enabled = reality;
    }

    private void OnImportShareLink(object? sender, EventArgs eventArgs)
    {
        if (_shareLinkParser is null) { DialogControls.ShowValidationError(this, "当前无法使用分享链接解析器。"); return; }
        var link = _shareLink.Text.Trim();
        if (string.IsNullOrWhiteSpace(link)) { DialogControls.ShowValidationError(this, "请先粘贴分享链接。"); _shareLink.Focus(); return; }
        try
        {
            var parsed = _shareLinkParser(link);
            _outboundJson.Text = parsed.ConfigurationJson;
            _options = ManualNodeConfiguration.Load(null, parsed.ConfigurationJson);
            ApplyOptionsToControls();
            SelectProtocol(ManualNodeConfiguration.DetectProtocol(parsed.ConfigurationJson) ?? parsed.Protocol);
            if (string.IsNullOrWhiteSpace(_name.Text)) _name.Text = parsed.Name;
            _customJsonOverride.Checked = false;
            RebuildProtocolFields();
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            DialogControls.ShowValidationError(this, exception.Message);
        }
    }

    private void OnSaveClick(object? sender, EventArgs eventArgs)
    {
        var name = _name.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) { DialogControls.ShowValidationError(this, "请输入节点名称。"); _name.Focus(); return; }
        if (_group.SelectedItem is not GroupOption group) { DialogControls.ShowValidationError(this, "请选择节点所属的节点组。"); _group.Focus(); return; }

        var protocol = CurrentProtocol;
        ReadOptionsFromControls();
        string outboundJson;
        try
        {
            if (protocol == "sing-box 自定义出站" || _customJsonOverride.Checked) outboundJson = RequireOutboundJson(_outboundJson.Text);
            else { outboundJson = ManualNodeConfiguration.Build(protocol, "proxy", _options); _outboundJson.Text = outboundJson; }
        }
        catch (InvalidDataException exception)
        {
            DialogControls.ShowValidationError(this, exception.Message);
            return;
        }

        _draft.Name = name;
        _draft.GroupId = group.Id;
        _draft.Protocol = protocol;
        _draft.ShareLink = DialogControls.NullIfWhiteSpace(_shareLink.Text);
        _draft.ConfigurationJson = outboundJson;
        _draft.ManualOptionsJson = ManualNodeConfiguration.Serialize(_options);
        _draft.IsEnabled = _enabled.Checked;
        if (string.IsNullOrWhiteSpace(_draft.CoreId)) _draft.CoreId = "sing-box";
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ApplyOptionsToControls()
    {
        _server.Text = _options.Server; SetNumber(_port, _options.Port); _username.Text = _options.Username; _password.Text = _options.Password;
        SetCombo(_vmessSecurity, _options.VmessSecurity); SetNumber(_alterId, _options.AlterId); SetCombo(_vlessEncryption, _options.VlessEncryption); SetCombo(_flow, _options.Flow);
        SetCombo(_ssMethod, _options.ShadowsocksMethod); _plugin.Text = _options.Plugin; _pluginOptions.Text = _options.PluginOptions; SetCombo(_socksVersion, _options.SocksVersion);
        _httpHeaders.Text = _options.HttpHeadersJson; _udpOverTcp.Checked = _options.UdpOverTcp; _mux.Checked = _options.MuxEnabled;
        _serverPorts.Text = _options.ServerPorts; _hopInterval.Text = _options.HopInterval; SetOptionalNumber(_upMbps, _options.UpMbps); SetOptionalNumber(_downMbps, _options.DownMbps);
        SetCombo(_obfsType, _options.ObfsType); _obfsPassword.Text = _options.ObfsPassword; _hysteriaRealmUrl.Text = _options.HysteriaRealmUrl; SetOptionalNumber(_geckoMinPacket, _options.GeckoMinPacketSize); SetOptionalNumber(_geckoMaxPacket, _options.GeckoMaxPacketSize);
        SetCombo(_congestion, _options.CongestionControl); SetCombo(_udpRelayMode, _options.UdpRelayMode); _zeroRtt.Checked = _options.ZeroRttHandshake; _heartbeat.Text = _options.Heartbeat;
        _wgPublicKey.Text = _options.WireGuardPublicKey; _wgPreSharedKey.Text = _options.WireGuardPreSharedKey; _wgLocalAddress.Text = _options.WireGuardLocalAddress; _wgReserved.Text = _options.WireGuardReserved; SetNumber(_wgMtu, _options.WireGuardMtu);
        _idleCheckInterval.Text = _options.IdleSessionCheckInterval; _idleTimeout.Text = _options.IdleSessionTimeout; SetOptionalNumber(_minIdleSession, _options.MinIdleSession); SetOptionalNumber(_insecureConcurrency, _options.InsecureConcurrency); _naiveQuic.Checked = _options.NaiveQuic;
        SetCombo(_network, _options.Network); SetCombo(_headerType, _options.HeaderType); _host.Text = _options.Host; _path.Text = _options.Path; SetCombo(_transportMode, _options.TransportMode); _transportExtra.Text = _options.TransportExtra; SetOptionalNumber(_kcpMtu, _options.KcpMtu);
        SetCombo(_tlsSecurity, _options.TlsSecurity); _allowInsecure.Checked = _options.AllowInsecure; _sni.Text = _options.Sni; SetCombo(_alpn, _options.Alpn); SetCombo(_fingerprint, _options.Fingerprint);
        _realityPublicKey.Text = _options.RealityPublicKey; _realityShortId.Text = _options.RealityShortId; _realitySpiderX.Text = _options.RealitySpiderX; _mldsa65Verify.Text = _options.Mldsa65Verify;
        _certificate.Text = _options.Certificate; _certificateSha.Text = _options.CertificateSha256; _echConfigList.Text = _options.EchConfigList; _verifyPeerByName.Text = _options.VerifyPeerCertificateByName; _finalMask.Text = _options.FinalMask;
    }

    private void ReadOptionsFromControls()
    {
        _options.Server = _server.Text.Trim(); _options.Port = decimal.ToInt32(_port.Value); _options.Username = _username.Text.Trim(); _options.Password = _password.Text.Trim();
        _options.VmessSecurity = _vmessSecurity.Text.Trim(); _options.AlterId = decimal.ToInt32(_alterId.Value); _options.VlessEncryption = _vlessEncryption.Text.Trim(); _options.Flow = _flow.Text.Trim();
        _options.ShadowsocksMethod = _ssMethod.Text.Trim(); _options.Plugin = _plugin.Text.Trim(); _options.PluginOptions = _pluginOptions.Text.Trim(); _options.SocksVersion = _socksVersion.Text.Trim();
        _options.HttpHeadersJson = _httpHeaders.Text.Trim(); _options.UdpOverTcp = _udpOverTcp.Checked; _options.MuxEnabled = _mux.Checked;
        _options.ServerPorts = _serverPorts.Text.Trim(); _options.HopInterval = _hopInterval.Text.Trim(); _options.UpMbps = OptionalNumber(_upMbps); _options.DownMbps = OptionalNumber(_downMbps); _options.ObfsType = _obfsType.Text.Trim(); _options.ObfsPassword = _obfsPassword.Text.Trim();
        _options.HysteriaRealmUrl = _hysteriaRealmUrl.Text.Trim(); _options.GeckoMinPacketSize = OptionalNumber(_geckoMinPacket); _options.GeckoMaxPacketSize = OptionalNumber(_geckoMaxPacket);
        _options.CongestionControl = _congestion.Text.Trim(); _options.UdpRelayMode = _udpRelayMode.Text.Trim(); _options.ZeroRttHandshake = _zeroRtt.Checked; _options.Heartbeat = _heartbeat.Text.Trim();
        _options.WireGuardPublicKey = _wgPublicKey.Text.Trim(); _options.WireGuardPreSharedKey = _wgPreSharedKey.Text.Trim(); _options.WireGuardLocalAddress = _wgLocalAddress.Text.Trim(); _options.WireGuardReserved = _wgReserved.Text.Trim(); _options.WireGuardMtu = decimal.ToInt32(_wgMtu.Value);
        _options.IdleSessionCheckInterval = _idleCheckInterval.Text.Trim(); _options.IdleSessionTimeout = _idleTimeout.Text.Trim(); _options.MinIdleSession = OptionalNumber(_minIdleSession); _options.InsecureConcurrency = OptionalNumber(_insecureConcurrency); _options.NaiveQuic = _naiveQuic.Checked;
        _options.Network = _network.Text.Trim(); _options.HeaderType = _headerType.Text.Trim(); _options.Host = _host.Text.Trim(); _options.Path = _path.Text.Trim(); _options.TransportMode = _transportMode.Text.Trim(); _options.TransportExtra = _transportExtra.Text.Trim(); _options.KcpMtu = OptionalNumber(_kcpMtu);
        _options.TlsSecurity = _tlsSecurity.Text.Trim(); _options.AllowInsecure = _allowInsecure.Checked; _options.Sni = _sni.Text.Trim(); _options.Alpn = _alpn.Text.Trim(); _options.Fingerprint = _fingerprint.Text.Trim();
        _options.RealityPublicKey = _realityPublicKey.Text.Trim(); _options.RealityShortId = _realityShortId.Text.Trim(); _options.RealitySpiderX = _realitySpiderX.Text.Trim(); _options.Mldsa65Verify = _mldsa65Verify.Text.Trim();
        _options.Certificate = _certificate.Text.Trim(); _options.CertificateSha256 = _certificateSha.Text.Trim(); _options.EchConfigList = _echConfigList.Text.Trim(); _options.VerifyPeerCertificateByName = _verifyPeerByName.Text.Trim(); _options.FinalMask = _finalMask.Text.Trim();
    }

    private string CurrentProtocol => _protocol.SelectedItem?.ToString() ?? "VMess";

    private static string RequireOutboundJson(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("请填写 sing-box 出站 JSON。");
        try
        {
            using var document = JsonDocument.Parse(source);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("sing-box 出站 JSON 的根元素必须是对象。");
            if (!document.RootElement.TryGetProperty("type", out var type) || string.IsNullOrWhiteSpace(type.GetString())) throw new InvalidDataException("sing-box 出站 JSON 必须包含 type 字段。");
            return JsonSerializer.Serialize(document.RootElement, JsonStore.IndentedOptions);
        }
        catch (JsonException exception) { throw new InvalidDataException($"sing-box 出站 JSON 无效：{exception.Message}", exception); }
    }

    private void SelectGroup(string? groupId)
    {
        for (var index = 0; index < _group.Items.Count; index++)
        {
            if (_group.Items[index] is GroupOption option && string.Equals(option.Id, groupId, StringComparison.Ordinal)) { _group.SelectedIndex = index; return; }
        }
        _group.SelectedIndex = 0;
    }

    private void SelectProtocol(string? protocol)
    {
        var wanted = string.IsNullOrWhiteSpace(protocol) ? "VMess" : protocol.Trim();
        for (var index = 0; index < _protocol.Items.Count; index++)
        {
            if (string.Equals(_protocol.Items[index]?.ToString(), wanted, StringComparison.OrdinalIgnoreCase)) { _protocol.SelectedIndex = index; return; }
        }
        _protocol.SelectedItem = "sing-box 自定义出站";
    }

    private static Panel ScrollHost(Control child)
    {
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        child.Dock = DockStyle.Top;
        panel.Controls.Add(child);
        return panel;
    }

    private static TableLayoutPanel FormLayout()
    {
        var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Top };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 205));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }

    private static void AddRows(TableLayoutPanel layout, params (string Label, Control Control)[] fields)
    {
        foreach (var field in fields)
        {
            var row = layout.RowCount++;
            var tall = field.Control is TextBox { Multiline: true } || field.Control.Controls.OfType<TextBox>().Any(static text => text.Multiline);
            layout.RowStyles.Add(new RowStyle(tall ? SizeType.Absolute : SizeType.AutoSize, tall ? Math.Max(96, field.Control.Height + 8) : 0));
            layout.Controls.Add(DialogControls.CreateLabel(field.Label), 0, row);
            field.Control.Dock = DockStyle.Fill;
            layout.Controls.Add(field.Control, 1, row);
        }
    }

    private static TextBox Input(bool multiline = false) => DialogControls.CreateTextBox(multiline);

    private static ComboBox Combo(IEnumerable<string>? items = null)
    {
        var combo = DialogControls.CreateComboBox();
        if (items is not null) combo.Items.AddRange(items.Cast<object>().ToArray());
        combo.DropDownStyle = ComboBoxStyle.DropDown;
        return combo;
    }

    private static NumericUpDown Number(int minimum, int maximum, int value) => new()
    {
        Minimum = minimum, Maximum = maximum, Value = Math.Clamp(value, minimum, maximum), Dock = DockStyle.Left, Width = 180, Margin = new Padding(0, 4, 0, 4),
    };

    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 0, 7) };

    private static void SetCombo(ComboBox combo, string? value)
    {
        var wanted = value ?? string.Empty;
        for (var index = 0; index < combo.Items.Count; index++)
        {
            if (string.Equals(combo.Items[index]?.ToString(), wanted, StringComparison.OrdinalIgnoreCase)) { combo.SelectedIndex = index; return; }
        }
        combo.Text = wanted;
    }

    private static void SetNumber(NumericUpDown control, int value) => control.Value = Math.Clamp(value, decimal.ToInt32(control.Minimum), decimal.ToInt32(control.Maximum));
    private static void SetOptionalNumber(NumericUpDown control, int? value) => SetNumber(control, value ?? 0);
    private static int? OptionalNumber(NumericUpDown control) => control.Value > 0 ? decimal.ToInt32(control.Value) : null;

    private static ProxyNode Clone(ProxyNode source) => new()
    {
        Id = source.Id, GroupId = source.GroupId, Name = source.Name, Protocol = source.Protocol, CoreId = source.CoreId,
        ShareLink = source.ShareLink, ConfigurationJson = source.ConfigurationJson, ManualOptionsJson = source.ManualOptionsJson,
        PingResult = source.PingResult, TcpingResult = source.TcpingResult,
        RealConnectionResult = source.RealConnectionResult, UdpResult = source.UdpResult, LastTestedAt = source.LastTestedAt,
        IsEnabled = source.IsEnabled, CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt,
    };

    private sealed record GroupOption(string Id, string Name) { public override string ToString() => Name; }
}
