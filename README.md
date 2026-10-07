# IPScaner WinUI — 局域网IP扫描工具（WinUI 3 重构版）

[IPScaner V1.28.2](E:/IPScaner%20V1.28.2.exe) 是一次性打包的 .NET Framework / WinForms 单文件程序
（299 KB，15 个窗体，约 7,000 行反编译源码）。本项目把它重构为 **Windows App SDK / WinUI 3** 应用：

- 界面：**WinUI 3**（Fluent Design、Mica/主题感知、NavigationView 外壳）
- 引擎：拆分为独立、可单元测试的类库 **`IPScaner.Core`**
- 数据：**与旧版文件格式完全兼容**，可直接沿用已有的配置文件与备注
- 目标框架：`.NET 8` / `net8.0-windows10.0.19041.0`，**非打包（unpackaged）** 部署

---

## 1. 目录结构

```
ipscaner-winui/
├─ IPScaner.WinUI.sln
├─ src/
│  ├─ IPScaner.Core/              扫描引擎与数据层（无 UI 依赖）
│  │  ├─ Configuration/           AppConfig（= IPScaner.cfg）、ConfigStore、EventName
│  │  ├─ Models/                  HostResult、AdapterInfo、PortScanResult、LocalPortInfo、WifiProfile
│  │  ├─ Net/                     IpMath、SubnetCalculator、LivenessProbe、ScanEngine、
│  │  │                           ArpTable、NameResolver、TcpProbe、PortScanner、
│  │  │                           LocalPortTable、AdapterService、WifiService、NetworkConfigurator
│  │  ├─ Memo/                    MemoStore（= IPScanerMemo.dat）
│  │  ├─ Commands/                DiyCommandStore（= command.txt）
│  │  ├─ Export/                  TableExporter（CSV + 真 .xlsx）
│  │  ├─ Logging/                 AppLog（= Logs\yyyy-MM-dd.log）
│  │  ├─ Caching/                 ExpiringCache（替代 System.Runtime.Caching）
│  │  ├─ Shell/                   ShellLauncher（ping/tracert/netstat/共享目录/控制面板）
│  │  └─ Storage/                 TextFileEncoding（BOM/GBK 探测）、AppPaths（数据目录解析）
│  └─ IPScaner.WinUI/             WinUI 3 应用
│     ├─ App.xaml(.cs)            应用入口
│     ├─ MainWindow.xaml(.cs)     外壳：自定义标题栏 + NavigationView + 状态栏 + 托盘 + 桌面叠加窗
│     ├─ Views/                   10 个功能页面
│     ├─ ViewModels/              IpBlock、AppConfigColors
│     ├─ Services/                AppServices、UiKit、NavigationArgs、TrayIcon、DesktopOverlay
│     ├─ Styles/AppStyles.xaml    共享样式
│     └─ Assets/app.ico           应用图标（同时供窗口图标与托盘图标读取）
├─ tests/IPScaner.Core.Tests/     xUnit 测试（356 个）
├─ build/
│  ├─ package.ps1                 一键产出 portable/msi/setup.exe
│  ├─ publish-github.ps1          建仓库 + 推源码 + 发 Release（走 REST API，无需 git）
│  └─ installer/                  WiX .wxs 模板与 IExpress .sed 模板
├─ artifacts/                     打包输出（已在 .gitignore 中，仅作 Release 资源）
├─ docs/
│  ├─ re/                         逆向工程规格（01–05，见 §6）
│  ├─ verification/               各页面的运行验证截图
│  └─ PAGE-CONTRACT.md            页面实现契约
└─ tools/grab_window.py           窗口截图辅助脚本
```

---

## 2. 构建与运行

前置：**.NET 8 SDK**（或更高）+ **Windows App Runtime 1.8**（本机已安装 `Microsoft.WindowsAppRuntime.1.8`）。

```powershell
cd ipscaner-winui

dotnet build IPScaner.WinUI.sln -c Debug

# 运行
.\src\IPScaner.WinUI\bin\Debug\net8.0-windows10.0.19041.0\win-x64\IPScaner.exe

# 测试
dotnet test tests\IPScaner.Core.Tests\IPScaner.Core.Tests.csproj
```

发布（自包含、单目录）：

```powershell
dotnet publish src\IPScaner.WinUI\IPScaner.WinUI.csproj -c Release -r win-x64 `
  -p:WindowsAppSDKSelfContained=true -p:SelfContained=true
```

---

## 3. 打包与发布

一条命令产出全部发布形态（输出到 `artifacts/`）：

```powershell
pwsh -File build\package.ps1 -Version 1.28.2
```

| 产物 | 大小 | 说明 |
|---|---|---|
| `IPScaner-<ver>-win-x64-portable.zip` | ~84 MB | **免安装**。内置 .NET 8 + Windows App SDK，解压即用，不写注册表 |
| `IPScaner-<ver>-win-x64-portable-lite.zip` | ~11 MB | **免安装（精简）**。需预装 .NET 8 桌面运行时 + Windows App Runtime 1.8 |
| `IPScaner-<ver>-win-x64.msi` | ~65 MB | **安装版**（WiX）。装入 `Program Files`，创建开始菜单与桌面快捷方式 |
| `IPScaner-<ver>-win-x64-setup.exe` | ~65 MB | **安装版**（IExpress 引导程序，内部调用上面的 MSI） |

打包依赖：`.NET 8 SDK`、`dotnet tool install --global wix --version 5.*`（**必须 v5**，见下）、
Windows 自带的 `iexpress.exe`。`7-Zip` 可选。

> **为什么锁定 WiX v5**：WiX v6/v7 要求接受 Open Source Maintenance Fee（OSMF）EULA，
> 可能涉及商业授权费用；v5 是最后一个免该条款的版本。

**数据目录**：免安装版把 `IPScaner.cfg` / `IPScanerMemo.dat` / `command.txt` / `ipScaner_his.xml` / `Logs\`
放在程序目录（与旧版一致，配置随文件夹走）；安装版因 `Program Files` 不可写，由
`IPScaner.Core.Storage.AppPaths` 自动回退到 `%APPDATA%\IPScaner`。同一个构建两种形态都正确。

### 发布到 GitHub

本机没有 git / gh，因此 `build\publish-github.ps1` 走 **GitHub REST API**：
建仓库 → 用 Git Data API 一次性提交全部源码 → 建 Release → 上传 4 个安装包。

```powershell
$env:GITHUB_TOKEN = "github_pat_xxx"   # 需要 Contents:RW + Administration:RW
pwsh -File build\publish-github.ps1 -Repo IPScaner-WinUI -Visibility public
```

脚本可重复执行：仓库已存在时复用，分支已存在时追加提交，Release 已存在时补传缺失资源。

---

## 4. 功能对照

原版 readme 列出的 11 项功能全部保留：

| # | 原功能 | 新实现 | 页面 |
|---|---|---|---|
| 1 | IP段扫描（254 小色块） | `ScanPage` + `IpBlock`，色块背景即状态 | IP段扫描 |
| 2 | 修改本地IP | `LocalIpPage` + `NetworkConfigurator` | 修改本地IP |
| 3 | IP批量扫描（单网段/范围/掩码位，定时） | `BatchScanPage` | IP批量扫描 |
| 4 | IP地址计算器 | `CalculatorPage` + `SubnetCalculator` | IP地址计算器 |
| 5 | 目标端口扫描 | `PortScanPage` + `PortScanner` | 端口扫描 |
| 6 | 本机端口占用查看 | `LocalPortPage` + `LocalPortTable` | 本机端口占用 |
| 7 | WiFi密码查看 | `WifiPage` + `WifiService` | WiFi密码查看 |
| 8 | Windows命令（18 个 + 自定义） | `ShellLauncher.BuiltInTools` + `DiyCommandStore` | 侧边/右键菜单 |
| 9 | 备注管理 | `MemoPage` + `MemoStore` | 备注管理 |
| 10 | 桌面显示本机IP | `DesktopOverlay`（`WS_EX_LAYERED\|TRANSPARENT\|TOOLWINDOW`）+ 选项配置 · 桌面 | 桌面叠加窗 |
| 11 | 关于 | `AboutPage` | 关于 |

另外补回了原版的**系统托盘图标**（`TrayIcon`，`Shell_NotifyIcon` P/Invoke）与**最小化到托盘**
（`HideMainEnabled`）行为；WinUI 3 本身不提供 `NotifyIcon`，因此以消息窗口 + `Shell_NotifyIcon` 实现。

---

## 5. 数据文件兼容性

新版本读写与旧版**完全相同的文件**。程序放在旧版同目录即可无缝沿用全部设置。

| 文件 | 位置 | 格式 |
|---|---|---|
| `IPScaner.cfg` | 程序目录 | XML 根元素 `root`，全部设置为**特性**；`<?xml version="1.0"?>`（无 `encoding`），UTF-8 无 BOM |
| `IPScanerMemo.dat` | 程序目录 | `键=值` 逐行；`#` 注释；换行转义为 `^v^`；UTF-8 带 BOM（旧版 `IPScaner.dat` 自动迁移） |
| `command.txt` | 程序目录 | `名称 命令`，首个空格前为菜单名；`#` 注释 |
| `ipScaner_his.xml` | 程序目录 | `<root><array><AdapterInfo …/></array></root>` |
| `Logs\yyyy-MM-dd.log` | 程序目录 | `yyyy-MM-dd HH:mm:ss - 类.方法  文本`，保留 30 天 |

`AppConfig` 的属性**声明顺序刻意与旧版 `ConfigInfo` 一致**——`XmlSerializer` 按声明顺序输出特性，
顺序改变会用不同的特性排列重写用户已有的 `.cfg`。颜色一律以**有符号 ARGB 整数**存储
（WinForms `Color.ToArgb()` 约定），例如天蓝 `-7876885`、柠檬绿 `-13447886`、印度红 `-3318692`。

---

## 6. 逆向工程规格

`docs/re/` 下是按窗体整理的实现级规格，均从反编译源码逐行提取（含精确中文串、控件几何、
API 调用与实测行为），是本项目的行为依据：

| 文档 | 覆盖 |
|---|---|
| `01-mainwindow.md` | 主窗体：29 项菜单树、扫描引擎求值顺序、色块/双击/右键菜单、导出、异动监测、托盘、菜单固定 |
| `02-config-memo.md` | 选项配置（3 个选项卡、27 个控件、数值范围）、备注系统与剪贴板导入解析 |
| `03-batchscan-calc.md` | IP批量扫描（三种模式、列、排序、定时、导出）、地址计算器 |
| `04-portscan.md` | 目标端口扫描（四种形态）、本机端口占用（原 `netstat` 解析与替代 API） |
| `05-localip-wifi-infra.md` | 修改本地IP、WiFi 查看、`Utility`/`LogTool`/`XmlUtility`/`Program`/桌面叠加窗 |

---

## 7. 重构中修复的缺陷

原版存在若干真实缺陷。以下为**有意修复**的清单（其余行为尽量逐位保持）：

### 扫描正确性
- **ARP 子串误匹配**：原版用 `text.Contains(ip)` 解析 `arp -a`，`192.168.1.5` 会命中 `192.168.1.50`。
  现改为正则精确匹配整行。
- **停止扫描后仍会改写色块**：原版「停止」只停止派发，在途结果继续回调，且回调里重新读取已解锁的
  IP 输入框，导致结果落到错误网段。现由 `CancellationToken` 取消并丢弃在途结果。
- **每台主机启动一次 `arp -a` 子进程**：/24 全网段开启主机名查询会拉起约 254 个进程。
  现整表读取一次、复用 3 秒。
- **254 个并发探测无上限**：现默认 64 并发（`ScanEngine.DefaultConcurrency`），主机名解析独立限流 16。

### 健壮性
- **IP 校验**：原版正则只匹配前缀，`999.999.999.1`、`10.0.0.1abc` 均可通过并最终抛
  `OverflowException`；现逐段范围校验。
- **配置数值越界**：原版把配置值直接赋给 `NumericUpDown.Value`，越界会抛异常致选项窗口无法打开；
  现一律钳位。
- **IPv4/IPv6 与状态**：本机端口原版靠 `netstat -ano` 文本解析，看不到 TCP 状态与 IPv6 行，且依赖
  系统区域设置；现改用 `GetExtendedTcpTable`/`GetExtendedUdpTable`（保留 `netstat` 回退路径），
  并新增状态列。
- **WiFi 解析依赖中文标记**：原版匹配 `所有用户配置文件` / `关键内容`，在英文 Windows 上**返回空列表**
  （本机实测 netsh 输出英文标记，原版确实取不到任何结果）；现按结构解析，兼容中英文与全/半角冒号。
  同时修复 Win11 24H2 的编码回退（UTF-8 → GBK）。
- **修改本地IP 永远报成功**：原版把 netsh 命令写进 `cmd.exe` 标准输入后不等待、不读退出码，
  无条件提示「本地IP地址修改成功」；现等待退出、捕获输出、按真实退出码报告。

### 功能可达性
- **端口范围写法**：原版只按 `,` 分割并 `int.TryParse`，`1000-2000` 被静默丢弃；现支持范围与
  `all`/`全部端口`。
- **掩码位 /31、/32**：原版界面把掩码位上限设为 30，`NetworkCalculator` 中的 `two hosts`/`one host`
  分支是**不可达死代码**；现开放 0–32。
- **首次点击列头排序方向**：原版布尔标志在排序后才翻转，首次点击为降序且全局共用；现改为按列记录、
  首次升序，并在结果刷新后保持。
- **定时扫描不可取消**：原版用 `Thread.Sleep(分钟)` 等待，取消要等睡眠结束；现用 `Task.Delay(token)`。
- **导出编码**：CSV 统一 UTF-8 **带 BOM**（原版批量扫描不带 BOM，Excel 中文乱码）；并新增真正的
  `.xlsx` 输出（自行生成 SpreadsheetML，无第三方依赖）。
- **提权模型**：原版每次启动都自我 `runas` 强制 UAC；现为 `asInvoker`，仅「修改本地IP」「清空ARP缓存」
  需要提权，界面提供按需提权入口与徽标。

---

## 8. 验证

- `dotnet build IPScaner.WinUI.sln`：**0 错误 0 警告**（全部 11 个页面接入后重新验证）。
- `dotnet test`：**356 个测试全部通过**（0 失败），覆盖 `IpMath`、`SubnetCalculator`、`AppConfig`、
  `ConfigStore`、`MemoStore`、`PortScanner` 解析、`TableExporter`、`ExpiringCache`、`TextFileEncoding`。
- **配置文件字节级兼容**：把实际的 `E:\IPScaner.cfg`（691 字节）读入再写回，输出与原件
  **逐字节完全相同**（`_verify\cfg`）。这同时验证了「.NET Framework 的 `XmlTextWriter` 不写
  `encoding` 属性、而 .NET 8 会写」这一差异已被正确处理。
- **`.xlsx` 真实可用**：`TableExporter.WriteXlsx` 生成的工簿用 openpyxl 回读通过，中文完好、
  数字按数值类型写入。
- `LocalPortTable`：IP Helper 原生化路径与 `netstat` 回退路径**逐行一致**（548 行 / TCP 184 / UDP 364），
  开启 IPv6 后 670 行；结构体步长经 `Marshal.SizeOf` 与行数对账验证。
- `WifiService`：本机读到 2 个已保存配置（**英文** netsh 输出，证明已摆脱中文标记依赖）。
- `NetworkConfigurator`：非法输入在**不启动进程**的前提下返回失败（1.1 ms，进程数 0→0）。
- `NetworkHistory`：`<?xml version="1.0"?>` 无 BOM，`<AdapterInfo …/>` 结构，可加载旧版写出的文件。
- 各功能页面在开发过程中均由实现者在**运行中的应用**上逐项验证（截图见 `docs/verification/`），
  包括：选项配置越界值钳位、计算器 `/31` `/32` 行为、备注校验与剪贴板导入预览、WiFi 明文切换与导出、
  修改本地IP 的未提权告警与历史回填。
- 集成后的最终程序**仅以编译 + 单元测试验证**（用户要求减少弹窗，未再次启动 GUI）。
  各页面在集成前的运行验证结果记录在 `docs/verification/` 的截图中。

### 7.1 本机环境实测（重要）

用 `_verify\net` 做了无界面实测，发现两件影响使用的事：

1. **本机的 Ping 调用被拦截。** `System.Net.NetworkInformation.Ping` 对**任何**地址（含 `127.0.0.1`）
   都抛 `PingException: An exception occurred during a Ping request`，`IPStatus` 为 `Unknown`。
   这不是对端不回包，而是本机 ICMP 能力被限制（该机装有 360 安全卫士等软件）。
   后果：**在默认配置下（两个备选侦测均为关闭），整段扫描会全部显示「不通」** —— 旧版在同样环境下
   也是这个表现，这正是 v1.28 更新说明里「修复部分电脑禁PING导致显示不在线」要解决的问题。
   你的 `E:\IPScaner.cfg` 中已经设置了 `PortInsteadPingEnabled="true"`（端口 `80,135,445,500,3389`），
   所以实测走 TCP 备选路径时结果正确：`192.168.32.47 / .200 → 在线 (TCP)`。
   为此新增了**运行时提示**：当一轮扫描中 ICMP 全部失败且两个备选都未启用时，主界面会弹窗说明原因，
   并可一键启用【Ping失败时侦测端口】后自动重扫。
2. **`arp -a` 可用，ARP 备选工作正常**（36 条表项、103 ms）。但未提权时 `arp -d *` 会失败，
   旧实现会把 `拒绝访问` 直接打到父进程控制台；现已改为捕获 stderr 并写入日志。

另外记录一个工具陷阱：`tools\grab_window.py` 原先用 `PrintWindow(PW_RENDERFULLCONTENT)` 截图，
**会让 WinUI 3 窗口在随后的框架调度中因 `AccessViolationException` 崩溃**（表现为看似无关的页面 bug）。
该脚本已改为默认使用屏幕 BitBlt，并保留 `--printwindow` 仅用于 Win32/WinForms 窗口。

---

## 9. 已知差距

- 主界面的 **IP 异动监测**（v1.26：对比前后扫描差异，标记【上线】【下线】）尚未实现；
  批量扫描页有结果对比所需的数据，但主界面没有基线快照 UI。
- 主界面菜单栏尚未移植原版的 29 项菜单树（快捷工具 / 外部工具 / 我的命令 / 菜单固定）。
  相关能力已在 `ShellLauncher.BuiltInTools`（18 个系统工具）与 `DiyCommandStore` 中就绪，
  右键菜单已提供 ping / tracert / telnet / netstat / arp / 网页 / 共享目录 全部动作。
- `StarMenu`（菜单固定）可读写配置，但未接入 UI。
- 仅在 Windows 11 Insider Preview（zh-CN，x64，150% DPI）单机验证；英文区域设置下的
  `netstat` 文本回退、非提权下的进程路径解析未实测。
- **托盘图标在 DSH 环境下可能不显示**：实测由 DSH 启动的 .NET 进程运行在**低完整性级别**
  （RID 0x1000），此时 `Shell_NotifyIcon(NIM_ADD)` 会被系统以 `ERROR_ACCESS_DENIED (5)` 拒绝。
  用同一个 ctypes 脚本做 A/B 对照：从 pwsh（High，0x3000）启动 → `NIM_ADD ok=True`、
  `Shell_NotifyIconGetRect hr=0x0`，矩形 `(1332,1032)-(1368,1080)`；从低完整性 .NET 父进程启动
  → `ok=False err=5`。**这是环境限制，不是代码缺陷**，正常双击运行时不受影响。
- 正常前台会话下的鼠标/键盘输入在本机被屏蔽（`GetForegroundWindow()` 返回 0），因此托盘右键菜单
  **菜单项的真实点击**未能验证，命令分发仅经代码审查。

### 桌面叠加窗与托盘（已实测）

- 四个角定位与 `Screen.PrimaryScreen.Bounds` 语义一致（不排除任务栏、仅主屏）：
  偏移 100,100 时 `loc0 (100,100)` / `loc1 (1016,100)` / `loc2 (1016,705)` / `loc3 (100,705)`（1920×1080）。
- 扩展样式实测 `0x801a0` = `LAYERED|TRANSPARENT|TOOLWINDOW`；`GetLayeredWindowAttributes`
  返回 `LWA_ALPHA`，透明度 70% → alpha 178，20% → alpha 51。**WinUI 3 的 DComp 内容确实遵循
  `LWA_ALPHA`**（截图可见桌面透出），这是实现前最大的不确定点。
- 鼠标穿透：在角标中心调用 `WindowFromPoint` 返回其**下方**窗口。
- 内容与原版逐项一致，含多网卡首行：
  `本地IP地址：192.168.32.47; 172.24.252.160; 172.23.32.1; 192.168.137.1` +
  名称 / MAC / 掩码 / 网关 / DHCP / DNS。
  顺带修掉原版一个组合 bug：它只在 `FirstOrDefault(r => r.IP == "ip1; ip2")` 命中时才输出明细行，
  即多网卡机器上**永远不输出**。
- 越界偏移（−500 与 5000，原版会让窗口跑出屏幕）会被夹回屏幕内；`WM_CLOSE → Dispose` 后无残留窗口。
- 最小化到托盘：`HideMainEnabled=true` 时最小化 → 窗口 `visible=False`（从任务栏与 Alt+Tab 消失）；
  `=false` 时正常最小化。托盘双击可恢复。退出前会弹出 `是否确认退出程序？`（与原版一致）。
