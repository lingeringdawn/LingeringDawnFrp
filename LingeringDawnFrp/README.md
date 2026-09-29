# LingeringDawn Frp

基于 C# WPF 的 Windows 桌面客户端：登录 LingeringDawn 面板 → 拉取 frp 配置 → 启动 frpc 隧道。

面板**不再内嵌浏览器**（原先用 WebView2），改为调用系统默认浏览器打开，登录状态由浏览器自己维护，
客户端因此少了一个运行时依赖，也不必再自己实现浏览器里的登录流程。

![界面截图](docs/screenshot.png)

## 登录方式

面板已改为「博客账号统一登录」（OAuth2 授权码 + PKCE），因此：

- 程序内的「登录页」按钮打开 `index.php?page=sso`，会 302 跳转到博客的授权页
- 本地密码登录仍保留，但只是应急入口：`index.php?page=login&local=1`
- **访问密钥不会随登录自动获取**，需手动填写（见下）

## 访问密钥（token）

- 获取位置：面板「用户中心」内可查看自己的 token
- 格式：**16 位十六进制**。面板侧 `api/index.php` 用 `Regex::isLetter()`（`^[A-Za-z0-9]+$`）校验字符集，
  而 `checktoken`/`checkproxy` 又要求长度恰为 16；token 由 `substr(md5(...), 0, 16)` 生成，故实际固定为 16 位十六进制
- 保存位置：数据目录下的 `user.token`（明文，仅用于下次自动填充）

## 节点 ID

- 正整数，默认 `1`
- 面板接口只提供 `getconf`（按 token + node 取配置），**没有「列出可用节点」的接口**，所以节点 ID 必须手动填写

## 数据目录

程序优先使用**自身所在目录**；若该目录不可写（例如安装在 `Program Files`），
自动退回到 `%LOCALAPPDATA%\LingeringDawnFrp`。启动后日志里会打印实际使用的路径。

| 文件 | 用途 |
| --- | --- |
| `frpc.ini` | 从面板接口拉取后写入的 frpc 配置（先写 `.tmp` 再替换，避免 frpc 读到半个文件） |
| `user.token` | 上次使用的访问密钥 |
| `node.id` | 上次使用的节点 ID |
| `app.log` | 运行日志，超过 2MB 自动轮转为 `app.log.1` |

## 如何运行

1. 把 `frpc.exe` 放到程序可执行文件同目录（调试时为 `bin/Debug/net8.0-windows/`）。
2. 用 Visual Studio 打开 `LingeringDawnFrp.csproj`，还原 NuGet 包并运行。

## 打包发布

一条命令产出可分发的压缩包：

```powershell
.\build-release.ps1
```

脚本流程：读取 csproj 里的 `<Version>` → `dotnet publish` → 放入 `frpc.exe` 与 `README.md`
→ 清理 `.pdb` → 打成 zip → 生成 SHA256 并核对包内是否含关键文件。

产物在仓库根的 `dist\` 下：

| 文件 | 说明 |
| --- | --- |
| `LingeringDawnFrp-<版本>-<运行时>\` | 发布目录（zip 解压后即为此结构） |
| `LingeringDawnFrp-<版本>-<运行时>.zip` | 分发包 |
| `LingeringDawnFrp-<版本>-<运行时>.zip.sha256` | 校验值 |

常用参数：

```powershell
.\build-release.ps1 -Version 1.2.0    # 指定版本号（默认读 csproj 的 <Version>）
.\build-release.ps1 -SelfContained    # 自包含发布：体积大，但目标机无需安装 .NET 8 桌面运行时
.\build-release.ps1 -SkipZip          # 只发布、不打包
```

**两个前提**：

- `frpc.exe` 必须存在。脚本会依次在项目目录、`bin\Debug\net8.0-windows\`、
  `bin\Release\net8.0-windows\<运行时>\publish\` 里查找；找不到会自动从 frp 官方发布页下载
  （见下方版本要求），也可用 `-FrpcPath` 手动指定。
- 默认是**框架依赖**发布，目标机需要安装 **.NET 8 桌面运行时**；要免依赖请加 `-SelfContained`。

### ⚠️ frpc 版本必须匹配服务端，不要用最新版

服务端是**官方原版 frps 0.29.0**。2026-09-29 用同一份面板配置实测：

| frpc 版本 | 对 frps 0.29.0 | 实测输出 |
| --- | --- | --- |
| 0.28.0 | ✅ 可用 | `login to server success` → `proxy added` → `start proxy success` |
| **0.29.0（推荐，与服务端一致）** | ✅ 可用 | 同上 |
| 0.52.3（现代版本） | ❌ **登录失败** | `login to server failed: session shutdown` |

**从 frp 官网随手下载最新版会连不上**，而且报错完全看不出是版本问题。
因此打包默认取 `0.29.0`；改动版本请用本地脚本的 `-FrpVersion`、或工作流的 `frp_version` 输入。

客户端启动隧道前会运行 `frpc.exe --version` 并检查：不在 0.28.x / 0.29.x 范围内会明确警告。

> 补充：面板生成的配置里有 `privilege_mode` 与 `api_server`，这两个字段在 0.29.0 中**并不存在**
> （`privilege_mode` 是 0.9 时代的选项）。实测它们会被安全忽略、不影响登录，无需处理。

## 自动构建与发布

仓库自带 GitHub Actions 工作流 `.github/workflows/release.yml`：

| 触发方式 | 行为 |
| --- | --- |
| push 到 `main` | 只构建并上传工件（验证能打包，不发布） |
| 打 tag（如 `v1.2.0`） | 构建 → 上传工件 → **自动创建 GitHub Release**，附上 zip 与 sha256 |
| 手动触发 `workflow_dispatch` | 可指定版本号与 frp 版本 |

工作流按 `frp_version` 输入（默认 `0.29.0`，与服务端 frps 版本保持一致）
从 frp 官方 Release 下载 `frpc.exe` 后再打包 —— 因此仓库里不需要存放 10MB 的二进制。

发布新版本：

```powershell
git tag v1.2.0
git push origin v1.2.0
```

> 若服务端升级了 frps，请同步调整工作流的 `frp_version`（frpc 与 frps 需版本对应）。

## 功能

- **面板入口**：调用系统默认浏览器打开面板首页 / 用户中心（不内嵌浏览器）
- **浏览器获取密钥**：在浏览器里完成面板登录后，通过本机回环回调自动取回访问密钥并填入保存
  （登录入口已并入该按钮，不再单设「授权登录」）
- **一键启动**：请求配置 → 写入 `frpc.ini` → 执行 `frpc.exe -c frpc.ini`
- **停止隧道**：结束 frpc 进程（含子进程）
- **断线自动重连**：frpc 意外退出时按 5/10/15/20/25 秒退避重试，最多 5 次；手动停止不会触发重连。可在界面关闭
- **开机自启**：写入当前用户 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- **系统托盘**：关闭窗口最小化到托盘；托盘菜单可打开窗口 / 打开面板 / 打开用户中心 / 启动 / 停止 / 退出
- **日志**：界面按级别着色显示 + 同时落盘（崩溃提示里会给出日志文件路径），最多保留最近 800 行
- **界面主题**：`Themes/Theme.xaml` 统一配色（与博客 / 面板的 Kizumi 紫 `#8B3DFF` 一致），圆角卡片 + 自定义按钮 / 复选框 / 输入框样式

## 已知限制

- 访问密钥与节点 ID 需手动填写（原因见上，面板没有节点列表接口）
- 多节点切换尚未实现：`Models/Node.cs`、`Models/TunnelConfig.cs`、`FrpProcessManager.Restart()` 目前未被使用
