# WFly

Windows x64 轻量代理桌面客户端，当前版本：`v0.0.8`。

WFly 以节点组为中心管理订阅和手动节点，提供 sing-box、Xray-core 与 Mihomo 的官方内核下载/更新，并把全部运行数据放在项目同级的 `data/` 目录。订阅支持常规 Base64/逐行链接，以及受限转换的 Clash/Mihomo YAML `proxies` 中 VLESS 节点。

## 功能

- 可拖动比例的右侧导航：首页、节点、节点组、连接、规则、日志、测试、设置。
- 节点组是节点的唯一父级；不会创建“全部”分类。空订阅可创建手动节点组。
- 节点编辑器提供 VMess、VLESS、Shadowsocks、Trojan、Hysteria2、TUIC、WireGuard、SOCKS、HTTP、AnyTLS、Naive、策略组、代理链和自定义出站等类型。
- HTTPS 订阅组支持 SS、VMess、VLESS、Trojan 分享链接；首次填写订阅默认每 6 小时更新，留空则默认不更新。更新在单次原子写入中替换该组节点。
- 手动节点粘贴 SS、VMess、VLESS、Trojan 链接时会生成 sing-box 出站；AnyTLS、Naive、策略组、代理链和自定义出站等高级类型需要填写完整的、与当前 sing-box 版本兼容的出站 JSON。策略组/代理链不会自动由多个独立节点拼装。
- 规则提供图形化编辑与 JSON 配置文件双视图；图形规则支持域名、IP/CIDR、端口、进程、网络、协议和入站匹配，复杂 sing-box 规则可填写原生 JSON。规则副本保存在 `data/rules/`。
- 首页提供节点状态、自动启停的图形化三档模式开关、用户触发的出口 IP / Google 延迟检测，以及 250ms 刷新的四条可悬停读取流量曲线。
- 日志只保存在内存；点击导出后才写入 `data/exports/`。
- 测试页可检测百度、Google、Netflix、YouTube、Disney+、GitHub、Pornhub 等站点的实际 HTTP 访问延迟。
- 设置页可安全下载和更新 sing-box、Xray-core、Mihomo。Mihomo/Xray-core 可运行用户导入到 `data/profiles/` 的原生配置。
- sing-box 支持由选中节点和图形规则生成的本地配置；TUN 模式需要以管理员身份运行。

## 快速使用

1. 从发布目录运行 `WFly.exe`。首次运行会在发布目录同级创建 `data/`，不会在 `C:\Users` 下新建 WFly 数据。
2. 在“节点组”创建一个组：订阅链接留空即可建立手动组；填写 HTTPS 链接会建立订阅组并立即尝试导入。
3. 在“节点”选择该组并添加节点，或等待订阅更新导入节点。
4. 在“设置”选择所需内核，点击“检查并下载选中内核”，核对版本和 SHA-256 后确认。
5. 在首页把三档开关拨到“系统代理”或 “TUN 模式”即可启动所选节点；回到中间“关闭代理”会停止内核。系统代理只会在使用 WFly 生成的 sing-box 配置时指向本机 `127.0.0.1`；关闭/退出时仅在设置仍由 WFly 持有时恢复原有值。

## 数据与隐私

发布版的数据根目录为：

```text
<工作目录>/data/
├── cores/       已验证的内核
├── profiles/    生成或导入的核心配置
├── rules/       可读规则 JSON 副本
├── state/       节点组、节点、设置和规则状态
├── exports/     用户手动导出的日志
└── temp/        可安全清理的下载临时文件
```

订阅 URL 常含访问令牌，因此会存放在本地 `data/state/` 以支持定时更新，但界面只显示主机名，运行日志会隐藏 URL 参数和片段。请妥善保护整个 `data/` 目录。旧版 `%LOCALAPPDATA%\WFly` 数据会在首次启动时安全迁移；新数据不会写入那里。

出口检测仅在点击“检测”后访问 IP 检测服务和 Google。IP 地址本身不能可靠判定住宅/原生属性，因此没有接入可信信誉数据库时会显示“未知”，不会猜测。流量图中的代理曲线来自本机 Clash API；直连曲线以系统接口总量扣除可用代理计数估算。

## 内核和运行边界

- sing-box：WFly 可从选中图形节点生成运行配置，并可生成受限的 Windows TUN 入站。
- Mihomo：支持固定官方 Windows x64 内核的下载/更新和导入原生 YAML/JSON 配置运行；不会把标准分享链接自动转换为 Mihomo 配置。
- Xray-core：支持固定官方 Windows x64 内核的下载/更新和导入原生 JSON 配置运行；不会把标准分享链接自动转换为 Xray 配置。Mihomo 与 Xray-core 的导入路径分别保存，不会被 sing-box 的临时运行配置覆盖。
- TUN 必须使用管理员身份运行；WFly 不会自动提权。当前一键生成 TUN 配置仅适用于 sing-box。
- 为避免把 Windows 代理指向未知端口，自动“系统代理”仅适用于 WFly 生成的 sing-box 配置；运行导入的 Mihomo/Xray 原生配置时请选择“关闭代理”，或在该原生配置中自行管理代理模式。
- WFly 只运行已校验、已登记的内核文件，并使用参数列表启动，不拼接 shell 命令。

节点类型和规则字段的交互设计参考了本地 v2rayN 工程，但 WFly 不打包其代码或内核。

## 安全设计

- 内核来源、仓库、Windows x64 资产名、可执行文件名和启动参数均内置白名单。
- 仅接受 GitHub Release 给出的 SHA-256 摘要；下载流式校验后才会安全解压 ZIP。
- 解压拒绝路径穿越、符号链接、重复路径、异常压缩比和超限归档。
- 订阅仅接受 HTTPS，限制重定向和响应大小；导入前请确认服务商可信。
- 系统代理服务只允许设置 `127.0.0.1:<端口>`，并在恢复前确认设置仍由 WFly 持有，避免覆盖用户或其他程序的更改。

SHA-256 可验证传输完整性，但不能替代对内核发布者或订阅服务商的信任判断。

## 构建

要求：Windows x64、.NET SDK 8。

```powershell
$env:DOTNET_CLI_HOME = "$PWD\.dotnet-cli"
$env:NUGET_PACKAGES = "$PWD\.nuget"
dotnet restore .\WFly.csproj -r win-x64
dotnet publish .\WFly.csproj -c Release --no-restore -o ..\release
```

发布产物是依赖 .NET 8 Desktop Runtime x64 的单文件程序：`../release/WFly.exe`。运行数据位于发布目录同级的 `../data/`，不会被提交到源代码仓库。

## 上游项目

- [sing-box](https://github.com/SagerNet/sing-box)
- [Xray-core](https://github.com/XTLS/Xray-core)
- [Mihomo](https://github.com/MetaCubeX/mihomo)

WFly 不随程序分发上述内核。请遵守所在地法律、网络规则和上游项目许可证。

## 许可

本仓库尚未声明开源许可证；在添加许可证前，默认保留所有权利。
