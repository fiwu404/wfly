# 变更日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)；版本标签使用 `v` 前缀。

## [0.1.0] - 2026-08-15

### 新增

- Windows x64 的 WinForms 轻量管理界面。
- sing-box 与 Xray-core 官方稳定版发现、下载、安装、启动和停止。
- 本地 JSON 配置选择、基础格式校验与内存运行日志。

### 安全

- 固定官方 GitHub 仓库、Windows x64 ZIP 资产规则和启动参数。
- 仅接受带 SHA-256 摘要的 Release 资产，并在安装前完成完整性校验。
- 提供 ZIP 路径穿越、符号链接、重复路径、异常压缩比和解压大小限制防护。
- 不内置节点或订阅，不修改系统代理、TUN、DNS、防火墙或路由表。

### 发布

- 发布为框架依赖的 Windows x64 单文件程序；需要 .NET 8 Desktop Runtime x64。
