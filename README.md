# WFly

WFly 是一个 Windows x64 优先的轻量代理内核管理器。它只管理用户本地 JSON 配置和从官方 GitHub Release 下载的 sing-box / Xray-core 内核；不内置节点、订阅、账号或流量服务。

当前为可用的 MVP：专注于内核的下载、校验、安装、启动和停止，优先保持边界清晰、占用轻量和可审计。

当前版本：`v0.1.0`。

## 功能

- 支持 sing-box 与 Xray-core 两个受白名单约束的官方内核。
- 发现最新稳定版 Release，并在下载前显示版本、来源、大小与 SHA-256。
- 只在 GitHub Release 元数据提供 SHA-256 时安装；下载后校验、限制归档大小，并安全解压 ZIP。
- 只运行已校验、已登记的内核可执行文件，启动参数使用参数列表而非 Shell 字符串拼接。
- 选择并校验本地 JSON 配置，运行日志只保留在内存中。
- 运行时数据保存在 `%LOCALAPPDATA%\WFly`，不要求管理员权限。

## 当前边界

第一版只支持 Windows x64。它不修改系统代理、TUN、DNS、防火墙或路由表，也不处理订阅、节点或用户凭据。后续平台移植可复用下载、校验、安装和进程管理层，但需要为新平台补充资产规则与界面宿主。

## 使用

1. 安装 [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0)。
2. 获取已发布的 `WFly.exe`；若从源码使用，请先执行下方构建命令，产物会生成到工作区根目录的 `release/`。
3. 运行 `WFly.exe`，选择内核并点击“检查并下载”，确认官方来源、版本和 SHA-256。
4. 选择自己的本地 JSON 配置文件，点击“启动”。

内核会安装到 `%LOCALAPPDATA%\WFly\cores`；程序只保存所选配置文件的路径，不复制配置内容。

## 从源码构建

要求：Windows x64、.NET SDK 8.0（已在 `global.json` 中约束主版本）。

```powershell
dotnet restore .\WFly.csproj -r win-x64
dotnet publish .\WFly.csproj -c Release --no-restore -o ..\release
```

产物为框架依赖的单文件程序：`../release/WFly.exe`。发布目录位于当前 Git 仓库外，因此不会进入该仓库。

## 目录

```text
.
├─ docs/                 计划、设计与发行说明
├─ Models/               数据模型
├─ Services/             下载、校验、安装与进程管理
├─ UI/                   Windows Forms 界面
├─ WFly.csproj           应用项目
├─ WFly.sln              Visual Studio 解决方案
├─ .editorconfig         跨编辑器代码格式约定
├─ .gitattributes        文本与换行策略
├─ .gitignore            构建输出、本地缓存和敏感文件规则
└─ README.md             项目入口文档

../release/              工作区级本地发布输出（不属于本仓库）
```

详细设计见 [计划书](./docs/计划书.md)。

## 安全说明

WFly 固定官方仓库与 Windows x64 资产规则，拒绝 draft、预发布版本、非 HTTPS 下载地址、缺少哈希的资产及不安全 ZIP 条目。SHA-256 校验用于确认下载完整性；它不能替代对上游发布者及本地配置来源的信任判断。

本地配置仅检查文件存在性、大小、JSON 语法和对象根节点；是否符合所选内核的具体语义仍由该内核在启动时判断。

## 上游项目

- [sing-box](https://github.com/SagerNet/sing-box)
- [Xray-core](https://github.com/XTLS/Xray-core)

WFly 不随程序分发上述内核。使用代理软件时，请遵守所在地法律、网络规则和上游项目许可证。

## 许可证

本仓库尚未声明开源许可证；在添加许可证前，默认保留所有权利。
