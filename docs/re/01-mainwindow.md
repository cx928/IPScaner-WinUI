# 01 — Main Window (`FormMain`) — 局域网IP扫描工具

Reverse-engineering spec for rebuilding **IPScaner V1.28.2** as a WinUI 3 app.
This document covers the main window only: layout, startup, the scan engine, colour blocks, click/double-click,
right-click commands, the list view, export, the IP-change monitor, the tray icon and the menu-pinning feature.

**Sources of truth**

| Artefact | Path (relative to `default-workspace/`) | Size |
| --- | --- | --- |
| Main form (all logic + designer) | `ipscaner-re/src/IPScaner/FormMain.cs` | 2222 lines |
| Runtime config model | `ipscaner-re/src/IPScaner/ConfigInfo.cs` | 215 lines |
| Globals (memo dict, adapter list) | `ipscaner-re/src/IPScaner/Global.cs` | 25 lines |
| Network helpers (ping fallbacks, ARP, DNS) | `ipscaner-re/src/IPScaner/Utility.cs` | 417 lines |
| Grid row model | `ipscaner-re/src/IPScaner/NetworkInfo.cs` | 51 lines |
| Per-block model | `ipscaner-re/src/IPScaner.Models/IPBtnInfo.cs` | 16 lines |
| Double-click action enum | `ipscaner-re/src/IPScaner/EventName.cs` | 12 lines |
| DNS/ARP result cache | `ipscaner-re/src/IPScaner/MemoryCacheManager.cs` | 41 lines |
| Adapter model | `ipscaner-re/src/IPScaner/AdapterInfo.cs` | 40 lines |
| List-view dialog (`列表展示`) | `ipscaner-re/src/IPScaner/FormIPDetail.cs` | 167 lines |
| Memo dialog | `ipscaner-re/src/IPScaner/FormIPMemo.cs` | 193 lines |
| Memo manager (`备注管理`) | `ipscaner-re/src/IPScaner/FormMemoManager.cs` | 345 lines |
| Options dialog (`选项配置`) | `ipscaner-re/src/IPScaner/FormConfig.cs` | 877 lines |
| Config persistence | `ipscaner-re/src/IPScaner/XmlUtility.cs` | 23 lines |
| Entry point | `ipscaner-re/src/IPScaner/Program.cs` | 40 lines |
| Changelog / readme (embedded in AboutBox) | `ipscaner-re/src/IPScaner.readme.txt` | 149 lines |
| Assembly version | `ipscaner-re/src/Properties/AssemblyInfo.cs` | 17 lines |

Line numbers in this document refer to the **decompiled files above**. Code is quoted verbatim except that tabs are
normalised to spaces and long `System.Windows.Forms.` qualifiers are sometimes elided.

---

## 0. Window metadata

| Property | Value (designer, `FormMain.cs:2189-2204`) |
| --- | --- |
| `Name` | `FormMain` |
| `Text` | `局域网IP扫描工具` → becomes `局域网IP扫描工具  Ver: 1.28.2` at load (**two** spaces before `Ver:`) |
| `AppTitle` (static) | `局域网IP扫描工具` — captured **before** the version suffix is appended (`FormMain.cs:273`), so all `MessageBox` captions are the bare product name |
| `ClientSize` | `1531 x 850` |
| `AutoScaleDimensions` / `AutoScaleMode` | `(11F, 21F)` / `Font` — all literal pixel numbers below were authored at that scale factor; WinUI is DIP-based, use the *relative* layout, not these pixels |
| `StartPosition` | `CenterScreen` |
| `IsMdiContainer` | `true` (never actually used — no MDI children anywhere) |
| `MainMenuStrip` | `menuStrip1` |
| `Padding` / `Margin` | `(4)` / `(4)` |
| `Icon` | `resources."$this.Icon"` (`ipscaner-re/src/app.ico`) |
| `DoubleBuffered` | `true` (set in the constructor, `FormMain.cs:265`) |
| `Control.CheckForIllegalCrossThreadCalls` | `false` (constructor, `FormMain.cs:264`) — **the whole app relies on cross-thread UI writes being legal** |
| Version shown | `Application.ProductVersion` = `1.28.2` (from `AssemblyFileVersion("1.28.2")`) |
| Process elevation | `Program.Main` re-launches itself with `Verb = "runas"` if not already administrator (`Program.cs:14-31`). Required by `arp -d *` and by 修改本地IP |

Child controls of the form, in `Controls.Add` order (`FormMain.cs:2192-2195`): `groupBox2`, `groupBox1`, `statusStrip1`, `menuStrip1`
→ resolved docking: menu at the top (0,0,1531x37), `groupBox1` right under it (0,37,1531x84), status strip at the bottom (0,809,1531x41),
`groupBox2` filling the rest (0,121,1531x688).

---

## 1. Window layout

### 1.1 Menu bar tree (`menuStrip1`, `FormMain.cs:1884-2098`)

`ImageScalingSize = 28x28`, `GripMargin = (2,2,0,2)`, size 1531x37.

Top-level items, in order (`FormMain.cs:1886`):

| # | Field | `Text` | Behaviour |
| --- | --- | --- | --- |
| 1 | `mnuStar` | `固定快捷` | Click → `mnuStar_Click` (help) until a target is pinned, then → `ExecuteStarAction` (§11) |
| 2 | `mnuCommon` | `快捷工具(&T)` | Drop-down, `MouseEnter` → hover-open (§11) |
| 3 | `mnuMemoManage` | `备注管理(&M)` | **Top-level item with a direct `Click`** → `mnuMemoManage_Click`, opens `FormMemoManager` modally. No drop-down |
| 4 | `mnuOptionSet` | `选项配置(&O)` | **Top-level direct `Click`** → `mnuOptionSet_Click`, opens `FormConfig` modally |
| 5 | `mnuWindows` | `windows命令(&C)` (lower-case `w`) | Drop-down, `MouseEnter` → hover-open |
| 6 | `mnuAbout` | `关于(&A)` | `Click` → `mnuAbout_Click` → `new AboutBox().ShowDialog()` |

`mnuStar` extras: `ToolTipText = "快捷工具或windows命令 下的菜单均可固定在此"`, left-aligned image, `ImageScaling = None`.
`mnuMemoManage` / `mnuOptionSet` / `mnuAbout` carry images (`ImageAlign = MiddleLeft`).

#### 1.1.1 `快捷工具(&T)` (`mnuCommon`) — 8 entries, `FormMain.cs:1900-1946`

| # | Field | `Text` | Handler → what opens |
| --- | --- | --- | --- |
| 1 | `mnuLocalIP` | `修改本地IP(&L)` | `mnuLocalIP_Click` → `new FormLocalIP().Show()` (window `一键设置IP`), non-modal |
| 2 | `mnuIPSegment` | `IP批量扫描(&S)` | `mnuIPSegment_Click` → `new FormIPSegment(txtIPPart.Text).Show()` (window `IP批量扫描`, see doc 03) |
| 3 | `mnuAddressCalc` | `IP地址计算器(&C)` | `mnuAddressCalc_Click` → `new FormAddressCalc().Show()` (window `IP地址计算器`, see doc 03) |
| 4 | `toolStripMenuItem9` | — | **separator** |
| 5 | `mnuPortScan` | `端口扫描(&P)` | `mnuPortScan_Click` → `new FormPortScan().Show()` (window `端口扫描`) |
| 6 | `mnuProcessPort` | `系统端口查看(&V)` | `mnuProcessPort_Click` → `new FormPortByPid().Show()` (window `系统端口查看工具`) |
| 7 | `toolStripMenuItem1` | — | **separator** |
| 8 | `mnuWiFiView` | `WiFi密码(&W)` | `mnuWiFiView_Click` → `new FormWiFiViewer().Show()` (window `WiFi密码查看器`) |

All entries use 28 px unscaled images (`ImageScaling = None`); drop-down item height is 40 px.

#### 1.1.2 `windows命令(&C)` (`mnuWindows`) — 23 entries, `FormMain.cs:1960-2091`

Every leaf below routes to the **same** handler `mnuOutTool_Click`, which executes
`utility.ProcessStartCommand((string)item.Tag, cmdVisible: Utility.IsCommonCmd(firstTokenOfTag))` — see §6.4.

| # | Field | `Text` | `Tag` (exact command string) |
| --- | --- | --- | --- |
| 1 | `mnuDIY` | `我的命令(&M)` | *(sub-menu — see below)* |
| 2 | `toolStripMenuItem6` | — | **separator** |
| 3 | `控制面板ToolStripMenuItem` | `控制面板(&C)` | `control Panel` (capital `P`, exactly as decompiled) |
| 4 | `卸载程序ToolStripMenuItem` | `卸载程序(&U)` | `control Appwiz.cpl` |
| 5 | `区域语言ToolStripMenuItem` | `区域语言` | `control Intl.cpl` |
| 6 | `日期和时间ToolStripMenuItem` | `日期和时间` | `control Timedate.cpl` |
| 7 | `toolStripMenuItem3` | — | **separator** |
| 8 | `网络连接ToolStripMenuItem` | `网络连接(&N)` | `control Ncpa.cpl` |
| 9 | `windows防火墙ToolStripMenuItem` | `防火墙(&F)` | `control Firewall.cpl` |
| 10 | `internet属性ToolStripMenuItem` | `Internet属性` | `control Inetcpl.cpl` |
| 11 | `toolStripMenuItem4` | — | **separator** |
| 12 | `计算机管理ToolStripMenuItem` | `计算机管理` | `compmgmt.msc` |
| 13 | `组策略ToolStripMenuItem` | `组策略` | `gpedit.msc` |
| 14 | `注册表ToolStripMenuItem` | `注册表(&R)` | `regedit` |
| 15 | `服务ToolStripMenuItem` | `服务(&S)` | `services.msc` |
| 16 | `系统属性ToolStripMenuItem` | `系统属性` | `control sysdm.cpl` |
| 17 | `环境变量ToolStripMenuItem` | `环境变量(&V)` | `rundll32 sysdm.cpl,EditEnvironmentVariables` |
| 18 | `用户账户ToolStripMenuItem` | `用户账户` | `control userpasswords2` |
| 19 | `toolStripMenuItem5` | — | **separator** |
| 20 | `设备管理ToolStripMenuItem` | `设备管理(&D)` | `devmgmt.msc` |
| 21 | `电源选项ToolStripMenuItem` | `电源选项(&P)` | `control powercfg.cpl` |
| 22 | `设备和打印机ToolStripMenuItem` | `设备和打印机` | `control printers` |
| 23 | `磁盘管理ToolStripMenuItem` | `磁盘管理` | `diskmgmt.msc` |

`mnuDIY` (`我的命令(&M)`) drop-down, built at designer time (`FormMain.cs:1972-1985`) plus runtime items:

| # | Field | `Text` | Behaviour |
| --- | --- | --- | --- |
| 1 | `mnuDIYHelp` | `功能说明` | `mnuDIYHelp_Click` → `MessageBox.Show(<long help text>, AppTitle, OK)` — full text quoted in §2.8 |
| 2 | `toolStripMenuItem7` | — | **separator** |
| 3..n | runtime `ToolStripMenuItem`s | `<description from command.txt>` | `Tag` = full command text, `Click → mnuOutTool_Click` |

The `command.txt` file lives in `Application.StartupPath`. Format per line: `描述 完整命令` — the **first token is the display
text, the rest (joined with single spaces) is the command** (`FormMain.cs:538-548`). Blank lines and lines starting with `#`
are ignored. `Utility.GetFileEncoding` is used to decode it (§8.3). The menu is built on a `ThreadPool` thread, so **DIY items can
appear a moment after the window shows**.

### 1.2 `groupBox1` — the toolbar strip (`FormMain.cs:1600-1717`)

`Dock = Top`, `Location = (0,37)`, `Size = 1531 x 84`, `Padding = (4)`, `Margin = (6,5,6,5)`, no caption text.

| Field | Type | `Text` | Location | Size | Font / colours | Tooltip (`toolTip1`) | Handler |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `cmbAdapter` | ComboBox | *(bound)* | (13,33) | 132x29 | default; `DropDownStyle = DropDownList`, `ItemHeight = 21` | `切换多个网卡` | `SelectedIndexChanged` |
| `txtIPPart` | TextBox | `192.168.100` | (156,33) | 162x34 | 宋体 10pt | — | — |
| `btnStart` | Button | `开始` | (324,32) | 103x38 | 宋体 10pt, `BackColor = SkyBlue`, `UseVisualStyleBackColor = false` | — | `btnStart_Click` |
| `btnStop` | Button | `停止` | (449,32) | 103x38 | 宋体 10pt, `BackColor = IndianRed` | — | `btnStop_Click` |
| `label1` | Label | `图例：` | (585,38) | AutoSize | 宋体 10pt | — | — |
| `btnNG` | Button | `不通` | (658,32) | 103x38 | 宋体 10pt, `ForeColor = Black`, `BackColor = IndianRed`, `Tag = ""` | `点击即可复制通讯【不通】的IP列表` | `btnLegend_Click` |
| `btnOK` | Button | `正常` | (770,32) | 103x38 | 宋体 10pt, `ForeColor = Black`, `BackColor = LimeGreen` | `点击即可复制通讯【正常】的IP列表` | `btnLegend_Click` |
| `btnDetail` | Button | `列表展示` | (880,32) | 145x40 | image, `ImageAlign = MiddleLeft`, `TextAlign = MiddleRight` | `在列表中展示所有IP信息，包括主机名、MAC、备注等` | `btnDetail_Click` |
| `btnExport` | Button | `导出` | (1032,32) | 103x40 | image, `ImageAlign = MiddleLeft`, `TextAlign = MiddleRight` | `将当前IP段的扫描结果导出到EXCEL中` | `btnExport_Click` |
| `lblStat` | Label | *(empty)* | (1142,40) | AutoSize | default | — | — |

> **Naming correction for the port:** there is **no IP-range ComboBox**. The IP prefix input is the plain
> TextBox `txtIPPart` (accepts `A.B.C`), and the only ComboBox on the window is `cmbAdapter`
> (the NIC / multi-IP selector). `btnStart`/`btnStop` are the 开始/停止 buttons listed above.

`btnNG` / `btnOK` are the colour **legend** swatches; their `BackColor` is overwritten from config on every
`UpdateControlByCfgInfo()` (`btnNG.BackColor = Global.ConfigInfo.NetworkNGColor`, `btnOK.BackColor = NetworkOKColor`,
`FormMain.cs:1321-1322`), which is why they visually double as the current 不通/正常 colours.

**Legend click** (`btnLegend_Click`, `FormMain.cs:1030-1066`):

```csharp
if (lblStat.Text == "---")                       // dead branch: lblStat is never set to "---"
{ MessageBox.Show("请先扫描IP段", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Exclamation); return; }
Button button = sender as Button;
string text = button.Text;                        // "不通" or "正常"
int num  = ipBtnDict.Values.Count(r => r.Btn.BackColor == Global.ConfigInfo.NetworkNGColor);
int num2 = ipBtnDict.Values.Count(r => r.Btn.BackColor == Global.ConfigInfo.NetworkOKColor);
int num3 = (text == "不通") ? num : num2;
if (num3 == 0) return;                            // silent no-op when nothing matches
string text2 = $"IP段: {GetIPSegment(txtIPPart.Text)}, 状态【{text}】地址共有{num3}个，已复制剪贴版";   // sic: 剪贴版
StringBuilder stringBuilder = new StringBuilder($"IP段: {txtIPPart.Text}, 状态【{text}】地址共有{num3}个");
foreach (IPBtnInfo value in ipBtnDict.Values)
{
    string text3 = GetIPSegment(txtIPPart.Text) + "." + value.IPNum;
    string text4 = Global.GetMemoByMacOrIp(text3).Replace("\r\n", "");     // IP string passed as the *mac* argument
    if (text == "不通") { if (value.Btn.BackColor == NetworkNGColor) stringBuilder.AppendLine(text3 + "  " + text4); }
    else                { if (value.Btn.BackColor == NetworkOKColor) stringBuilder.AppendLine(text3 + "  " + value.HostName + " " + value.MAC + " " + text4); }
}
Clipboard.SetText(stringBuilder.ToString());
MessageBox.Show(text2, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
```

Clipboard layout — `不通`: `IP段: 192.168.1, 状态【不通】地址共有N个` then one line per address `192.168.1.7  <备注>` (note the
**two spaces**). `正常`: same header, then `192.168.1.7  PC-01 AA-BB-CC-DD-EE-FF <备注>` (single spaces between name/MAC/memo).

### 1.3 `groupBox2` — `扫描结果` (`FormMain.cs:1854-1883`)

`Dock = Fill`, `Location = (0,121)`, `Size = 1531 x 688`, `Padding = (4)`, caption `扫描结果`.
Contains `tableLayoutPanel1`: 1 column (100 %), 2 rows — row 0 `AutoSize`, row 1 `100 %`.

* row 1 → `flowLayoutPanel` (the 254 colour blocks): `AutoScroll = true`, `Dock = Fill`, `Margin = (4)`, 1515x611.
* row 0 → `tableTop` (the header strip, 1523x37): 3 columns = `50 %`, `Absolute 550`, `50 %`; 1 row `100 %`.
  * cell (0,0) → `flowLayoutPanel1` (the monitoring row, see §9)
  * cell (1,0) → `lblTitle`
  * cell (2,0) → empty (balances the centred title)

`lblTitle`: `AutoSize = true` but `Dock = Fill`, `TextAlign = MiddleCenter`, `Padding = (15,0,15,0)`,
font **微软雅黑 12pt**, `ForeColor = SystemColors.Highlight`, `Tag = "网段IP使用情况"`, initial `Text = "正在绘制表格视图"`.
At runtime it is set to `txtIPPart.Text + lblTitle.Tag` in exactly two places — `DelayDisplayTableView` (`FormMain.cs:613`) and
`btnStart_Click` (`FormMain.cs:780`) — producing e.g. `192.168.100网段IP使用情况` (**no separator** between prefix and 网段).

`flowLayoutPanel1` (monitoring row) holds, left→right: `lblMonitor`, `chkOnline`, `chkOffline`, `lblOnoffRes` — details in §9.

### 1.4 The 254 colour blocks (see also §4)

Created at runtime in `DelayDisplayTableView` (`FormMain.cs:591-632`) on a thread-pool thread, marshalled to the UI thread
via `flowLayoutPanel.BeginInvoke`:

```csharp
flowLayoutPanel.SuspendLayout();
ipBtnDict = new Dictionary<int, IPBtnInfo>();
List<Button> list = new List<Button>();
for (int i = 1; i < 255; i++)                       // 1 .. 254 inclusive -> exactly 254 buttons
{
    Button button = GeneralButtonControl("btn_" + i, i.ToString());
    list.Add(button);
    ipBtnDict[i] = new IPBtnInfo(button, i);
}
flowLayoutPanel.Controls.AddRange(list.ToArray());
flowLayoutPanel.ResumeLayout();
Application.DoEvents();
lblTitle.Text = txtIPPart.Text + lblTitle.Tag.ToString();
while (flowLayoutPanel.VerticalScroll.Visible)      // grow the FORM until every block is visible without scrolling
{
    Width  += BtnWidth;
    Height += BtnHeight;
}
```

Block geometry (`GeneralButtonControl`, `FormMain.cs:634-654`):

```csharp
button.BackColor = Global.ConfigInfo.DefaultColor;
button.FlatStyle = FlatStyle.Flat;
button.Font      = new Font("微软雅黑", BtnFontSize, FontStyle.Regular, GraphicsUnit.Point);
button.Size      = new Size(BtnWidth, BtnHeight);     // 39 x 24 at the default font size
button.Padding   = new Padding(0);
button.Margin    = new Padding(1);                    // -> 41 x 26 px footprint per block
button.TextAlign = ContentAlignment.TopCenter;
button.Text      = text;                              // the last octet, "1" .. "254"
button.Name      = name;                              // "btn_1" .. "btn_254"
if (txtIPPart.Text + "." + text == localIpAddress) button.ForeColor = Color.OrangeRed;
button.UseVisualStyleBackColor = false;
button.Enabled = true;                                // blocks are NEVER disabled, even mid-scan
button.MouseDown += Btn_MouseDown;                    // the ONLY event wired up
```

There is **no `Click`, no `MouseEnter`, no `MouseHover`, no `MouseUp`** handler on a block — everything funnels through
`MouseDown` (§5, §6). The block text is *always* the last octet; hostname/MAC/memo never appear as block text.

The `while (flowLayoutPanel.VerticalScroll.Visible) { Width += BtnWidth; Height += BtnHeight; }` loop is the v1.10 fix
「优化254个小色块按钮，使得全部展示在用户面前」: it enlarges the top-level window until the flow panel no longer needs a
vertical scrollbar. On a 1920x1080 display this typically yields a 2–3 row block of ~1500x850. In WinUI 3 use an
`ItemsRepeater`/`GridView` with a `UniformGridLayout` instead and simply let it scroll.

### 1.5 Status bar (`statusStrip1`, `FormMain.cs:1808-1853`)

`ImageScalingSize = 20x20`, `Padding = (2,0,18,0)`, `Stretch = false`, height 41, docked bottom.
7 items, in order (`FormMain.cs:1809`):

| # | Field | Initial `Text` | Decoration / behaviour |
| --- | --- | --- | --- |
| 1 | `statusTip` | `程序准备就绪` | `TextAlign = MiddleLeft`; main progress text (see §3.9) |
| 2 | `toolStripStatusLabel1` | *(empty, width 0)* | `DisplayStyle = Text`; decorative spacer only, never written |
| 3 | `statusFecthName` | *(empty, width 0)* | `IsLink = true`; `Click → statusFecthName_Click` which is an **empty method** (dead) |
| 4 | `statusHostName` | `已禁用主机名查询` | `BorderSides = Left`, `IsLink = true`, `LinkColor = Red` (Blue when enabled), `ToolTipText = "点击可打开【选项】"`, `Click → statusHostName_Click` → same code as `mnuOptionSet_Click` (opens `FormConfig` modally, saves on OK) |
| 5 | `statusFriendlyTip2` | `你知道吗？` | `BorderSides = Left`, `ForeColor = SystemColors.MenuHighlight`; replaced by a random tip at startup, then rotated (§2.9) |
| 6 | `statusIP` | `  ` → `活动IP:<ip>[; <ip>...]` | `BorderSides = Left`, `Margin = (10,3,0,2)`; `Click → statusIP_Click` → if the text starts with `活动IP:` copy the remainder to the clipboard and set `statusTip.Text = "IP已复制到剪贴板"` |
| 7 | `statusTime` | ` ` | `BorderSides = Left`, `Margin = (10,3,0,2)`, `TextAlign = MiddleRight`; `yyyy-MM-dd HH:mm:ss`, refreshed every second |

`statusHostName` text/colour is driven purely by `ConfigInfo.QueryHostNameEnabled`
(`UpdateControlByCfgInfo`, `FormMain.cs:1297-1306`): `已启用主机名查询` / Blue, `已禁用主机名查询` / Red.

### 1.6 Tray icon

`notifyIcon1` is declared in `FormMain`'s designer and is **always visible** – see §10.

---

## 2. Startup sequence

### 2.1 `Program.Main` (`Program.cs:11-31`)

```csharp
WindowsIdentity current = WindowsIdentity.GetCurrent();
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.ThreadException += Application_ThreadException;            // empty handler
AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;   // empty handler
if (new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator))
    Application.Run(new FormMain());
else
{
    ProcessStartInfo psi = new ProcessStartInfo();
    psi.FileName = Application.ExecutablePath;
    psi.Verb = "runas";                                                 // UAC self-elevation
    Process.Start(psi);
    Application.Exit();
}
```

Consequence for the port: the app **must run elevated** — `arp -d *`, the ARP fallback and 修改本地IP all assume it.
Both global exception handlers are empty (silently swallowed), which hides many defects.

### 2.2 Constructor (`FormMain.cs:261-267`)

```csharp
InitializeComponent();
Control.CheckForIllegalCrossThreadCalls = false;
DoubleBuffered = true;
Load += FormMain_Load;
```

### 2.3 `FormMain_Load` — exact order (`FormMain.cs:269-304`)

| Step | Statement | Log line written |
| --- | --- | --- |
| 1 | `log.Log("**************程序开始启动*******************")` | ✓ |
| 2 | `cmbAdapter.Items.Add("加载中...")` (placeholder while adapters enumerate) | |
| 3 | `AppTitle = Text` → `局域网IP扫描工具` | |
| 4 | `statusTip.Text = "程序准备就绪".PadRight(2)` (`PadRight(2)` is a no-op here) | |
| 5 | `Text = Text + "  Ver: " + Application.ProductVersion` → `局域网IP扫描工具  Ver: 1.28.2` | |
| 6 | `Global.AdapterList = Utility.GetAllAdapterIP()` | |
| 7 | `cmbAdapter.DataSource = null; cmbAdapter.Items.Clear();` then `DisplayMember = "Name"; ValueMember = "IP"; DataSource = Global.AdapterList` | |
| 8 | `activeIp = string.Join("; ", Global.AdapterList.Where(r => r.Status == 1).Select(r => r.IP))` | |
| 9 | `statusIP.Text = "活动IP:" + activeIp` | |
| 10 | `Show(); Application.DoEvents();` — the window is visible **before** the config is read | |
| 11 | `log.Log("加载配置文件...")` → `LoadConfig()` (`IPScaner.cfg`, §2.5) | ✓ |
| 12 | `log.Log("初始化小色块控件...")` → `DelayDisplayTableView()` (§1.4; the 254 buttons are built asynchronously) | ✓ |
| 13 | `InitTipList()` (§2.9) | |
| 14 | `tip = new ToolTip()` — the `ToolTip` used by all blocks | |
| 15 | `Timer timer = new Timer { Interval = 1000 }; timer.Tick += Timer_Tick; timer.Start();` | |
| 16 | `log.Log("加载自定义命令文件...")` → `LoadDIYCommand()` (§2.8) | ✓ |
| 17 | `log.Log("加载备注文件...")` → `LoadMemoInfo()` (§2.7) | ✓ |
| 18 | `DislayDesktopOverlay()` (§2.10) | |
| 19 | `log.Log("程序启动完成")` | ✓ |
| 20 | `AttachRightClickEvent(new ToolStripItem[2] { mnuCommon, mnuWindows })` (§11.2) | |

**Auto-fill of the scan prefix.** Step 7 rebinding the ComboBox raises `SelectedIndexChanged` synchronously
(selected index becomes 0), which runs (`FormMain.cs:1214-1221`):

```csharp
private void cmbAdapter_SelectedIndexChanged(object sender, EventArgs e)
{
    if (cmbAdapter.SelectedIndex > -1)
    {
        localIpAddress = cmbAdapter.SelectedValue.ToString();   // the IP of the selected adapter
        txtIPPart.Text = GetIPSegment(localIpAddress);          // first three octets, e.g. "192.168.100"
    }
}
```

So the local IP segment is auto-detected and typed into `txtIPPart` **during load, before the window is shown**. If the adapter
list is empty, `txtIPPart` keeps the designer default `192.168.100`.

`GetIPSegment` (`FormMain.cs:581-589`):

```csharp
private string GetIPSegment(string ip)
{
    string[] array = ip.Split('.');
    if (array.Length >= 3) return string.Join(".", array.Take(3));
    return string.Empty;
}
```

### 2.4 Multi-NIC handling (`Utility.GetAllAdapterIP`, `Utility.cs:268-340`)

```csharp
foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
{
    if ((ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
         ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) || ni.Name.Contains("VMware"))
        continue;                                                    // only Ethernet + Wi-Fi, no VMware virtual NICs
    AdapterInfo a = new AdapterInfo();
    a.Name   = ni.Name;
    a.Status = (int)ni.OperationalStatus;                             // Up == 1
    a.MAC    = string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
    IPInterfaceProperties props = ni.GetIPProperties();
    IPv4InterfaceProperties v4 = props.GetIPv4Properties();           // NOTE: called unconditionally
    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
    {
        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
        if (ua.Address.ToString().StartsWith("169.254")) continue;    // APIPA
        if (ua.Address.ToString().StartsWith("127.")) continue;
        if (ua.Address.ToString().EndsWith(".0")) continue;
        ... AdapterInfo { Name, IP, Status, MAC, SubnetMask = ua.IPv4Mask.ToString(),
                          IsDhcpEnabled = v4?.IsDhcpEnabled ?? false, Gateway = <first IPv4 gateway>,
                          DnsServers = <all IPv4 DNS servers> }
    }
}
```

Then names are de-duplicated: the list is sorted by `(Name, Status)` and any repeated name gets `_1`, `_2`, … appended
(`Utility.cs:323-338`). This is what makes **one entry per IP** in the ComboBox while still showing the NIC name — i.e. a NIC
with several IPv4 addresses (multi-IP single NIC, v1.12 feature) appears as `以太网`, `以太网_1`, …

| Aspect | Behaviour |
| --- | --- |
| ComboBox contents | `Global.AdapterList` (`List<AdapterInfo>`), `DisplayMember = "Name"`, `ValueMember = "IP"` → the visible text is the NIC name, the value is the IP |
| Selecting an entry | replaces `localIpAddress` and rewrites `txtIPPart.Text` to that adapter's /24 prefix, e.g. `192.168.1` |
| `statusIP` | `活动IP:` + **all** adapters whose `Status == 1` joined with `"; "`, e.g. `活动IP:192.168.1.10; 10.0.0.5` |
| Local-block highlight | a block whose full IP equals `localIpAddress` gets `ForeColor = Color.OrangeRed` at creation |
| Desktop overlay | `DislayDesktopOverlay` looks up `Global.AdapterList.FirstOrDefault(r => r.IP == activeIp)` — with several active IPs the joined string never matches, so the detail lines are omitted (§2.10) |
| Robustness | `GetIPv4Properties()` throws for adapters without IPv4 (e.g. some virtual/TAP adapters) — an unguarded call in the original |

### 2.5 Config load / `IPScaner.cfg` (`FormMain.cs:450-470`, `1291-1323`)

```csharp
private string CfgPath = Path.Combine(Application.StartupPath, "IPScaner.cfg");
private void LoadConfig()
{
    if (File.Exists(CfgPath))
    {
        Global.ConfigInfo = XmlUtility.ReadXml<ConfigInfo>(CfgPath);
        LoadStarMnu();                  // §11.3
        UpdateControlByCfgInfo();       // §4.2 / §11.1
    }
}
```

* Format: `XmlSerializer` of `ConfigInfo`, root element `root`, **attributes only**, no XML namespace,
  written with `FileMode.Create` (overwrite) to `IPScaner.cfg` next to the exe.
  Example fragment: `<root Version="1.0" QueryHostNameEnabled="false" PingTimeout="500" PingCount="4" … StarMenu="mnuCommon.mnuPortScan" … />`.
* If the file does not exist, the `ConfigInfo` constructor defaults are used and **nothing is written until the first save**
  (选项配置 → 保存, or pinning a star menu item).
* Colour settings are stored as **ARGB `int`s**: `DefaultColorArgb`, `NetworkOKColorArgb`, `NetworkNGColorArgb`, `MemoColorArgb`,
  `DesktopForeColorArgb`, `DesktopBgColorArgb`; the `Color` properties are `[XmlIgnore]` and fall back
  (`SkyBlue` / `LimeGreen` / `IndianRed` / `Blue`) if `Color.FromArgb` throws.

Default `ConfigInfo` values (`ConfigInfo.cs:186-214`) — these are the effective defaults on a fresh install:

| Property | Default | Meaning (options dialog label) |
| --- | --- | --- |
| `QueryHostNameEnabled` | `false` | 启用主机名查询 (`启用`/`禁用`) |
| `PingTimeout` | `500` ms | Ping超时毫秒 (10…5000, step 100) |
| `PingCount` | `4` | Ping请求次数 (min 1) |
| `ARPInsteadPingEnabled` | `false` | 启用Ping失败时检测ARP表 |
| `PortInsteadPingEnabled` | `false` | 启用Ping失败时侦测端口 |
| `PrePortArray` | `"80,135,445,500"` | TCP侦测端口 |
| `PortTimeout` | `50` ms | TCP超时毫秒 (10…2000, step 50) |
| `DoubleClickTime` | `200` ms | 双击间隔毫秒 (100…500, step 50) |
| `BtnFontSize` | `9` | 小色块字号 (`小`=8 … `特大`=12) |
| `MenuAutoOpen` | `false` | 自动弹出菜单 (`是`/`否`) |
| `DoubleEvent` | `Ping` | 小色块双击事件 |
| `DefaultColorArgb` | SkyBlue | 初始色块颜色 |
| `NetworkOKColorArgb` | LimeGreen | 通讯正常颜色 |
| `NetworkNGColorArgb` | IndianRed | 通讯异常颜色 |
| `MemoColorArgb` | Blue | 备注色块颜色 (block *text* colour) |
| `DesktopOverlayEnabled` | `false` | 启用桌面显示本机IP标签 |
| `DesktopOverlayPre` | `"本地IP地址："` | IP前缀 (note: the dialog's designer default text is `本地IP地址为`) |
| `DesktopOverlayOffsetX/Y` | `100` / `100` | 偏移量x,y |
| `DesktopOverlayLocation` | `2` | 显示位置 (`左上角`,`右上角`,`右下角`,`左下角`) |
| `DesktopOverlayOpacity` | `70` | 透明度 |
| `LogEnabled` | `false` | 启用调试日志 |
| `HideMainEnabled` | `false` | 最小化时隐藏到托盘 |
| `StarMenu` | `null` | pinned menu path (§11.3) |

### 2.6 `LoadStarMnu` / `StarMenu`

See §11.3.

### 2.7 Memo file (`LoadMemoInfo` / `SaveMemoInfo`, `FormMain.cs:472-522`)

* Path: `Path.Combine(Application.StartupPath, "IPScanerMemo.dat")`.
* Migration: if the legacy `IPScaner.dat` exists it is `File.Move`d to `IPScanerMemo.dat` (exceptions swallowed).
* Format: one `key=value` per line; `#`-prefixed and empty lines ignored; the value is everything after the **first** `=`
  (so values may contain `=`); literal `^v^` means a newline (`Environment.NewLine`). Written back with
  `File.WriteAllText(path, text, Encoding.UTF8)` → **UTF-8 with BOM**.
* Keys are **either** IPv4 addresses **or** MAC addresses (both may exist for the same host, v1.27) → `Global.MemoDict`.
* `SaveMemoInfo` returns immediately if `MemoDict.Count == 0`, so clearing all memos never erases the file.
* Lookup precedence (`Global.GetMemoByMacOrIp(mac, ip)`): **MAC key first, then IP key**, else `string.Empty`.

### 2.8 `LoadDIYCommand` (`FormMain.cs:524-553`)

Runs on a `ThreadPool` thread. Reads `command.txt` from the exe directory using `Utility.GetFileEncoding` (§8.3).
Each non-empty, non-`#` line is split on `' '`: token 0 = menu title, tokens 1..n re-joined with `" "` = the command; the item's
`Tag` is the command and its `Click` handler is `mnuOutTool_Click`. Items are appended to `mnuDIY.DropDownItems`
(after 功能说明 + separator). `mnuDIYHelp_Click` shows this exact text:

```
自定义命令说明
此处可以添加自己常用的命令、程序快捷菜单。
比如远程指定服务器、打开某个excel文档、打开网络调试助手程序等等。

具体操作步骤如下：
在程序所在目录新建文本文件，命名为：command.txt (忽略冒号开头的命令)
每一个命令占用一行，第一个空格前面的是命令描述，剩余的都是完整命令
例如：ping一下百度 ping www.baidu.com /t，标题为“ping一下百度”，命令为“ping www.baidu.com /t”

以下command.txt具体示例命令：
ping一下百度 ping www.baidu.com /t
远程服务器 mstsc /v 192.168.100.100
网络调试助手  D:\Program Files\网络调试助手.exe
设备清单Excel D:\Program Files\设备台账清单.xlsx
```

(with `\r\n` line breaks). 功能说明 has `Image`, `ImageScaling = None`.

### 2.9 Status-bar tips + clock (`InitTipList`, `Timer_Tick`, `FormMain.cs:555-579`)

17 tips, verbatim, in order:

```
Tip:右击某一个IP小色块，可调出ping命令窗口
Tip:右击某一个IP小色块，可复制IP、计算机名、MAC地址
注意:开启【主机名查询】功能,将会降低查询效率
Tip:点击【不通】图例,可复制所有通讯【异常】的IP地址
Tip:点击【正常】图例,可复制所有通讯【正常】的IP地址
Tip:点击【IP小色块】,可快速复制IP地址
Tip:鼠标右击【IP小色块】,在右键菜单中,点击菜单可复制到剪粘板
Tip:点击顶部IP下拉框,可切换IP扫描段
Tip:快捷工具中的所有工具,都可以打开多个哦
Tip:想扫描A类的多段IP,可以使用快捷工具中的【IP批量扫描】
Tip:快捷工具中的【修改本地IP】,支持历史IP快速切换
Tip:快捷工具中的【端口扫描】,可以查看计算机开放了哪些端口
Tip:快捷工具中的【IP批量扫描】,可快速扫描指定的批量IP
Tip:鼠标悬停在【IP小色块】上,会显示计算机名称和MAC地址
Tip:单击【IP小色块】,可以快速查询计算机名称和MAC地址
Tip:本工具只支持查询 当前电脑所在网段下的所有MAC地址
Tip:外部工具栏中可快速打开本地控制面板中的某些设置项
Tip:小色块的双击功能支持自定义
```

Initial `statusFriendlyTip2.Text = TipList[new Random().Next(TipList.Count)]`. A `Queue<string>` is rotated by
`Timer_Tick` when `DateTime.Now.Second % 10 == 0` (the 1000 ms `System.Windows.Forms.Timer` also updates
`statusTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")` every tick). When the queue empties it is refilled
(`TipQueue = null; TipQueue = new Queue<string>(TipList);`).

### 2.10 Desktop overlay (`DislayDesktopOverlay`, `FormMain.cs:424-448`)

Only when `ConfigInfo.DesktopOverlayEnabled`. Closes/disposes any previous `DesktopOverlayForm`, then builds:

```csharp
AdapterInfo a = Global.AdapterList.FirstOrDefault(r => r.IP == activeIp);
string text = Global.ConfigInfo.DesktopOverlayPre + activeIp;         // e.g. "本地IP地址：192.168.1.10"
if (a != null) {
    text += Environment.NewLine + "名称：" + Utility.GetHostName(activeIp);
    text += Environment.NewLine + "MAC："   + a.MAC;
    text += Environment.NewLine + "掩码：" + a.SubnetMask;
    text += Environment.NewLine + "网关：" + a.Gateway;
    text += Environment.NewLine + "DHCP：" + a.IsDhcpEnabled;
    text += Environment.NewLine + "DNS："  + string.Join("; ", a.DnsServers);
}
desktopForm = new DesktopOverlayForm(text); desktopForm.Show();
```

Note: with several active IPs `activeIp` is `"ip1; ip2"` so the lookup fails and only the prefix line is shown.
The overlay itself is a borderless, `TransparencyKey = LightYellow`, `Opacity = config/100`, click-through
(`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW`) top-most-less label window positioned at one of 4 corners with
`(OffsetX, OffsetY)` (`DesktopOverlayForm.cs:35-70`). It is called once at startup and again after 选项配置 saves.

---

## 3. The scan engine

### 3.1 Input parsing — what a "scan" means on this form

There is **no CIDR parsing and no range parsing on the main window**. The input is only the first three octets.

```csharp
private void btnStart_Click(object sender, EventArgs e)
{
    txtIPPart.Text = GetIPSegment(txtIPPart.Text);        // normalise: keep the first 3 dot-separated parts
    string[] array = txtIPPart.Text.Split('.');
    if (array.Length != 3)                                 // ANY 3-part string passes; no numeric validation
    {
        MessageBox.Show("请输入IP段信息，比如192.168.1", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
        return;
    }
    ...
}
```

| Accepted input | Result |
| --- | --- |
| `192.168.1` | scans `192.168.1.1` … `192.168.1.254` |
| `192.168.1.77` | truncated to `192.168.1` by `GetIPSegment`, scans `.1`…`.254` |
| `192.168.1.0/24` | `GetIPSegment` keeps `192.168.1` (the `/24` is dropped because it is part of the 4th token) → works by accident |
| `192.168.1.1-254` | 4th token is `1-254` → dropped → prefix `192.168.1` → works by accident |
| `10.0.0` | scans `10.0.0.1`…`.254` |
| `abc.def.ghi` | **passes** the length check; the scan thread then sends an unresolvable host name to `Ping.SendAsync` → `PingException` (see §3.10 caveats) |
| `192.168.1.` | `Split('.')` yields `["192","168","1",""]` → 4 parts → rejected with `请输入IP段信息，比如192.168.1` |
| empty | 1 part → rejected |

* The block set is **fixed at creation time**: exactly `1..254` (`for (int i = 1; i < 255; i++)`). `.0` and `.255` never get a
  block and are never probed. There is no way to scan a range on this window — that is `IP批量扫描` (`FormIPSegment`, doc 03),
  which does support `start-end` and mask-bit ranges via `Utility.GetIPRange` (skipping `.0`/`.255`).
* Blocks are not re-created when the prefix changes; only their background colours and tooltips are reset.

### 3.2 Start / Stop

`btnStart_Click` (`FormMain.cs:771-805`) — full body:

```csharp
txtIPPart.Text = GetIPSegment(txtIPPart.Text);
... validation (above) ...
lblTitle.Text   = txtIPPart.Text + lblTitle.Tag.ToString();     // "192.168.1网段IP使用情况"
statusTip.Text  = "开始扫描IP".PadRight(2);
btnStart.Enabled  = false;
btnStop.Enabled   = true;
txtIPPart.Enabled = false;
foreach (IPBtnInfo value in ipBtnDict.Values)
{
    Button btn = value.Btn;
    btn.BackColor = Global.ConfigInfo.DefaultColor;              // reset every block
    string text  = txtIPPart.Text + "." + btn.Text;
    string text2 = Global.GetMemoByMacOrIp(text);                // lookup by IP string (passed as the mac argument)
    if (!string.IsNullOrEmpty(text2))
    {
        text2 = Environment.NewLine + "备注：" + text2;
        btn.ForeColor = Global.ConfigInfo.MemoColor;             // memo -> configurable text colour
    }
    else btn.ForeColor = Color.Black;                            // overwrites the OrangeRed local-IP highlight
    tip.SetToolTip(btn, "IP：" + text + text2);
}
CurrentIp = 1;
CanScanIp = true;
StartScanIP();
```

`btnStop_Click` (`FormMain.cs:1022-1028`):

```csharp
CanScanIp = false;
btnStart.Enabled  = true;
btnStop.Enabled   = false;
txtIPPart.Enabled = true;      // the prefix becomes editable again — while late pings are still completing
```

### 3.3 Ping dispatch (`StartScanIP` / `ScanIP`, `FormMain.cs:807-836`)

```csharp
private void StartScanIP()
{
    Thread thread = new Thread(ScanIP);
    thread.IsBackground = true;
    thread.Start();
}

private void ScanIP(object obj)
{
    while (CanScanIp && CurrentIp < 255)                       // CurrentIp: 1 .. 254
    {
        string text = txtIPPart.Text + "." + CurrentIp;
        statusTip.Text = ("开始扫描IP: " + text).PadRight(2);
        Ping ping = new Ping();                                // NEW Ping instance per address, never reused
        ping.PingCompleted += Ping_PingCompleted;
        ping.SendAsync(text, Global.ConfigInfo.PingTimeout, CurrentIp);   // userState = the integer octet
        CurrentIp++;
        Thread.Sleep(10);                                      // 10 ms between dispatches (~100/s)
    }
    if (CanScanIp) statusTip.Text = "扫描完毕".PadRight(2);
    else { statusTip.Text = "用户取消了操作".PadRight(2); CallMonitorIPChangedStatus(); }
    btnStop.PerformClick();                                    // resets the Start/Stop enable state
}
```

* **Work queue**: there is none. The "queue" is the single `int CurrentIp` counter walked by one background thread; each
  iteration fires one `Ping.SendAsync` and never waits for it. Dispatch takes ≈2.6 s for all 254 addresses at the default
  `PingTimeout` (10 ms × 254 + overhead), so effectively **all 254 probes overlap**.
* **Concurrency: up to 254 concurrent ICMP requests**, one `Ping` object each (each `Ping` owns an OS handle and completes on a
  thread-pool callback). No `SemaphoreSlim`, no `Parallel.For`, no cap.
* `PingTimeout` (`ConfigInfo.PingTimeout`, default 500 ms, UI range 10…5000) is the **per-request timeout** passed to
  `Ping.SendAsync(host, timeout, state)`. It is *not* a retry policy.
* `PingCount` (default 4) is **not used by the main window's scan at all** — it is only interpolated into the `ping` command
  line for the double-click/right-click actions (`-n {PingCount}`).
* `statusTip` shows the last **dispatched** address, not the last completed one.

### 3.4 The "filter ping duration > 2 s" rule

The v1.5 changelog line 「过滤ping时长>2秒的IP」 is **not** implemented as a separate RTT filter in V1.28.2.
The current implementation of that rule is the configurable timeout:

* `ConfigInfo.PingTimeout` is passed straight into `Ping.SendAsync(..., timeout, ...)`; a reply that does not arrive within
  that window yields `IPStatus.TimedOut` and is treated as 不通.
* The options dialog labels it `Ping超时毫秒` and explains it with the tooltip **`Ping时长超过此值，会认为网络异常`**
  (“if the ping duration exceeds this value the network is considered abnormal”), which is the surviving wording of the
  v1.5 rule. (v1.1 configured 100 ms; v1.5 used 2 s; V1.28.2 default 500 ms.)
* `Ping_PingCompleted` **never inspects `e.Reply.RoundtripTime`**. A reply that arrives inside the timeout is always
  accepted, no matter how slow. There is no 2000 ms constant anywhere in the codebase.
* A WinUI port should keep `PingTimeout` semantics (timeout → dead) and may optionally add the literal “> 2 s ⇒ 异常”
  filter only if bit-exact legacy behaviour is required; today it would be a behaviour *change*.

### 3.5 Liveness decision tree — exact order of evaluation

`Ping_PingCompleted` (`FormMain.cs:906-982`), verbatim structure:

```csharp
private void Ping_PingCompleted(object sender, PingCompletedEventArgs e)
{
    Ping ping = sender as Ping;
    ping.Dispose();
    PingReply reply = e.Reply;                     // NOTE: e.Error / e.Cancelled are never checked
    int ipNum = Convert.ToInt32(e.UserState);
    string ipAddress = txtIPPart.Text + "." + ipNum;      // re-derived from the CURRENT text box content!

    if (reply.Status == IPStatus.Success)                                   // (1) ICMP
    {
        ipBtnDict[ipNum].Btn.BackColor = Global.ConfigInfo.NetworkOKColor;
        GetHostNameAndMac(ipAddress);
    }
    else if (Global.ConfigInfo.ARPInsteadPingEnabled)                       // (2) ARP table
    {
        if (Utility.ValidOnLineByARP(ipAddress))
        {
            ipBtnDict[ipNum].Btn.BackColor = Global.ConfigInfo.NetworkOKColor;
            GetHostNameAndMac(ipAddress);
        }
        else if (Global.ConfigInfo.PortInsteadPingEnabled &&
                 !string.IsNullOrEmpty(Global.ConfigInfo.PrePortArray))     // (3) TCP port probe
        {
            Utility.TcpPortTestMuli(ipAddress, Global.ConfigInfo.PrePortArray, (bool res) =>
            {
                if (res) { OK colour; GetHostNameAndMac(ipAddress); }
                else     { HostName = ""; MAC = ""; NG colour; }
                lblStat.Text = $"数量合计：正常 {ok}，不通 {ng}";
            });
        }
        else { ipBtnDict[ipNum].HostName = ""; ipBtnDict[ipNum].MAC = ""; NG colour; }
    }
    else if (Global.ConfigInfo.PortInsteadPingEnabled &&
             !string.IsNullOrEmpty(Global.ConfigInfo.PrePortArray))         // (3') TCP port probe (ARP option off)
    { ... identical TCP callback ... }
    else { ipBtnDict[ipNum].HostName = ""; ipBtnDict[ipNum].MAC = ""; NG colour; }

    int num  = ipBtnDict.Values.Count(r => r.Btn.BackColor == Global.ConfigInfo.NetworkNGColor);
    int num2 = ipBtnDict.Values.Count(r => r.Btn.BackColor == Global.ConfigInfo.NetworkOKColor);
    lblStat.Text = $"数量合计：正常 {num2}，不通 {num}";
    CallMonitorIPChangedStatus();                                          // §9
}
```

**Decision order (exact):**

1. `IPStatus.Success` → **在线**.
2. else if `ARPInsteadPingEnabled` → `Utility.ValidOnLineByARP(ip)`:
   * true → **在线**;
   * false → if `PortInsteadPingEnabled && PrePortArray != ""` → TCP probe;
   * else → **不通**.
3. else if `PortInsteadPingEnabled && PrePortArray != ""` → TCP probe.
4. else → **不通**.

Truth table (settings × ICMP × ARP-hit × port-open):

| `ARPInsteadPingEnabled` | `PortInsteadPingEnabled` | ICMP | ARP entry | TCP port open | Verdict |
| --- | --- | --- | --- | --- | --- |
| any | any | Success | — | — | 正常 (green) |
| false | false | fail | — | — | 不通 (red) |
| false | true | fail | — | yes | 正常 |
| false | true | fail | — | no | 不通 |
| true | false | fail | yes | — | 正常 |
| true | false | fail | no | — | 不通 |
| true | true | fail | yes | — | 正常 (**ARP wins, ports are not probed**) |
| true | true | fail | no | yes | 正常 |
| true | true | fail | no | no | 不通 |

Consequences worth reproducing deliberately:

* The ARP check is **synchronous and blocking** on the ping-completion thread (`Utility.GetMacAddressFromARP` may spawn
  `arp -a`); the TCP probe is asynchronous (`Task.Factory.StartNew`).
* The TCP callback updates `lblStat` with its own duplicate counter code; the surrounding method updates it again
  immediately after — the synchronous paths win, the asynchronous TCP path may overwrite it later.
* On failure the code explicitly clears `HostName` and `MAC` on the model (but the tooltip is *not* refreshed).
* `HostName`/`MAC` are only ever filled by `GetHostNameAndMac`, which is gated on `QueryHostNameEnabled` (§3.7).

### 3.6 TCP port probe (`Utility.TcpPortTestMuli` / `TcpPortTest`, `Utility.cs:140-180`)

```csharp
public static void TcpPortTestMuli(string ipAddress, string portArray, Action<bool> actionCompleted = null)
{
    Task.Factory.StartNew(() =>
    {
        string[] array = portArray.Split(new char[1] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        bool obj = false;
        for (int i = 0; i < array.Length; i++)
            if (TcpPortTest(ipAddress, int.Parse(array[i]))) { obj = true; break; }
        actionCompleted?.Invoke(obj);
    });
}

public static bool TcpPortTest(string ipAddress, int port)
{
    using (TcpClient tcpClient = new TcpClient())
    {
        IAsyncResult asyncResult = tcpClient.BeginConnect(ipAddress, port, null, null);
        if (!asyncResult.AsyncWaitHandle.WaitOne(Global.ConfigInfo.PortTimeout)) tcpClient.Close();
        else { try { tcpClient.EndConnect(asyncResult); return true; } catch { } }
    }
    return false;
}
```

* Ports come from the comma-separated string `PrePortArray` (default `80,135,445,500`); the options dialog normalises
  full-width `，` to `,` on `Leave` and validates each token as `0 < port <= 65535`, else
  `[{token}] 输入错误，请输入正确的端口号` and refocuses the box.
* Probes are **sequential, first-success-wins**; worst-case latency = `ports × PortTimeout`
  (default 4 × 50 ms = 200 ms; maximum 4 × 2000 ms = 8 s with the UI max).
* The port list is *not* validated again at scan time — a hand-edited `IPScaner.cfg` with a non-numeric token throws inside
  `int.Parse` on a task thread.
* Options labels: checkbox `启用Ping失败时侦测端口` (tooltip `Ping失败时侦测TCP端口，判断设备是否在线`),
  label `TCP侦测端口` (tooltip `Ping失败时，可通过此端口判断设备状态。多个端口之间用逗号分隔`),
  label `TCP超时毫秒` (its tooltip is a copy-paste bug: `Ping时长超过此值，会认为网络异常`).
* Saving is blocked if the box is checked but empty: `请输入要侦测的TCP端口`.

### 3.7 ARP-table check + MAC retrieval (`Utility.cs:32-138`)

The 1.28 ARP fallback and the MAC lookup share one function, `Utility.GetMacAddressFromARP`. **There is no `SendARP`
P/Invoke anywhere in the codebase** (`[DllImport]` appears only in `DesktopOverlayForm` for `GetWindowLong`/`SetWindowLong`
and in `NetAPIUtility` for `Netapi32.dll`), so MACs come from the OS ARP cache plus the local adapter list.

```csharp
public static string GetMacAddressFromARP(string ipAddress)
{
    // (a) local adapters: our own IPs are not in the ARP table
    if (Global.AdapterList.Any(r => r.IP == ipAddress))
    {
        Global.AdapterList = GetAllAdapterIP();                        // re-enumerate
        AdapterInfo adapterInfo = Global.AdapterList.First(r => r.IP.Equals(ipAddress));
        if (adapterInfo != null)
        {
            string text = adapterInfo.MAC.Replace(":", "-");
            if (!ValidIP(text.Trim()))                                 // bogus sanity check; a MAC never matches "^\d+\.\d+\.\d+\.\d+"
            {
                logTool.Log("从本地适配器中获取" + ipAddress + "对应的MAC【" + text + "】");
                return text;
            }
        }
    }

    // (b) 1-hour memory cache, key = "IPScaner.Utility.GetMacAddressFromARP - <ip>"
    string cacheKey = MemoryCacheManager.CreateCacheKey(MethodBase.GetCurrentMethod(), ipAddress);
    if (MemoryCacheManager.HasCacheKey(cacheKey)) return MemoryCacheManager.GetObject(cacheKey) as string;

    // (c) flush the whole ARP cache at most once per hour, then scrape "arp -a"
    DeleteARPTable();
    ProcessStartInfo startInfo = new ProcessStartInfo
    {
        FileName               = "arp",
        Arguments              = "-a",
        RedirectStandardOutput = true,
        UseShellExecute        = false,
        CreateNoWindow         = true
    };
    using (Process process = Process.Start(startInfo))
    using (StreamReader streamReader = process.StandardOutput)
    {
        string text2 = streamReader.ReadToEnd();
        foreach (string line in text2.Split(new string[1] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains(ipAddress)) continue;                    // substring match, not token match
            string[] array3 = line.Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int num2 = 0; num2 < array3.Length; num2++)
                if (Regex.IsMatch(array3[num2], "([0-9A-Fa-f]{2}[-:]){5}([0-9A-Fa-f]{2})"))
                {
                    MemoryCacheManager.AddObject(array3[num2].ToUpper(), cacheKey);   // only successes are cached
                    logTool.Log("从ARP表中获取" + ipAddress + "对应的MAC【" + array3[num2].ToUpper() + "】");
                    return array3[num2].ToUpper();
                }
        }
    }
    return null;                                                            // failures are NOT cached
}

private static void DeleteARPTable()
{
    try
    {
        if ((DateTime.Now - lastExecutionTime).TotalHours >= 1.0)
        {
            ProcessStartCommand("arp -d *", cmdVisible: false);              // cmd.exe /c arp -d * , window hidden
            lastExecutionTime = DateTime.Now;
        }
    }
    catch { }
}

public static bool ValidOnLineByARP(string ipAddress, Action<bool> actionCompleted = null)
{
    string macAddressFromARP = GetMacAddressFromARP(ipAddress);
    bool flag = !string.IsNullOrEmpty(macAddressFromARP);
    actionCompleted?.Invoke(flag);
    return flag;
}
```

Key facts for the port:

| Question | Answer |
| --- | --- |
| API used | **`arp -a` child process** (stdout redirected, no window) — not `SendARP`, not `GetIpNetTable` |
| Cache flush | `arp -d *` through `cmd.exe /c` with `WindowStyle = Hidden`, rate-limited to once per hour by the static `Utility.lastExecutionTime` (initialised to `DateTime.MinValue`, so the **first ever call flushes the table**) |
| Online criterion | *a MAC address can be extracted for that IP* → i.e. the host answered ARP at L2 recently |
| Cache | `System.Runtime.Caching.MemoryCache.Default`, absolute expiration **1 hour**; key `IPScaner.Utility.GetMacAddressFromARP - <ip>` |
| Cache misses | not cached → every failed check re-runs `arp -a` |
| MAC format returned | upper-case, as printed by `arp -a` (`AA-BB-CC-DD-EE-FF`); local adapters are returned with `:` → `-` |
| Cost | one `arp -a` process launch per uncached query; in the worst case 254 of them concurrently (one per failed ping) |
| Known false positive | `line.Contains(ipAddress)` is a substring test, so querying `192.168.1.5` can match the line of `192.168.1.50`/`192.168.1.51…`; the `arp -a` output itself is ordered by IP so the exact line usually wins |
| Elevation | needed for `arp -d *`; the app always runs elevated |

**Why this fixes 禁PING hosts (the 1.28 mechanism):** a Windows host that blocks ICMP (default firewall rule
“File and Printer Sharing (Echo Request – ICMPv4-In)” disabled) still answers ARP, because ARP is a link-layer protocol the
NIC/stack always handles. The failed ping attempt itself makes the OS resolve the neighbour, so a fresh entry exists in the
cache by the time the completion callback runs; the app then treats “MAC known” as “alive”. So the v1.28 improvement is
*not* a new protocol implementation — it is the ARP-cache fallback inserted as the **first** fallback (ahead of the v1.20 TCP
probe), controlled by the new option `启用Ping失败时检测ARP表` which ships **off by default** (changelog item 2 says 默认关闭).

### 3.8 Hostname + MAC enrichment (`GetHostNameAndMac`, `FormMain.cs:984-1020`)

```csharp
private void GetHostNameAndMac(string ip)
{
    if (!Global.ConfigInfo.QueryHostNameEnabled) return;               // <-- hard gate: no DNS, no MAC, no tooltip update

    ThreadPool.QueueUserWorkItem(obj =>
    {
        string text = obj as string;
        int ipNum = Convert.ToInt32(text.Split('.').Last());
        string ipTip = "IP：" + text;
        string hostName = Utility.GetHostName(text);
        if (hostName == "bogon") { }                                   // dead statement in the original
        if (hostName != "未知") ipBtnDict[ipNum].HostName = hostName;
        ipTip = "名称：" + hostName + Environment.NewLine + ipTip;
        string macAddressFromARP = Utility.GetMacAddressFromARP(text);
        if (!string.IsNullOrEmpty(macAddressFromARP))
        {
            ipTip = ipTip + Environment.NewLine + "Mac：" + macAddressFromARP;
            ipBtnDict[ipNum].MAC = macAddressFromARP;
        }
        string memoByMacOrIp = Global.GetMemoByMacOrIp(macAddressFromARP, text);
        if (!string.IsNullOrEmpty(memoByMacOrIp)) ipTip = ipTip + Environment.NewLine + "备注：" + memoByMacOrIp;
        ipBtnDict[ipNum].Btn.BeginInvoke((Action)(() => tip.SetToolTip(ipBtnDict[ipNum].Btn, ipTip)));
    }, ip);
}
```

* **API:** `Utility.GetHostName` → `Dns.GetHostEntry(ipAddress).HostName`; on any exception the literal `未知` is returned and
  cached. Trailing `domain` and `.local` are stripped in a loop with a case-sensitive `EndsWith` + `Replace`
  (`Utility.cs:104-130`).
* **Gating:** whole method is skipped unless `QueryHostNameEnabled` (options: `启用主机名查询` = `启用`/`禁用`, default **禁用**).
  The status bar nudges the user: `已禁用主机名查询` in red, tooltip `点击可打开【选项】`, click opens `FormConfig`.
* **Cost / throttling:** one `ThreadPool` work item per **online** host (up to 254), each performing a *blocking* reverse DNS
  lookup — reverse lookups on a /24 with no PTR records are the single slowest part of the app (the in-app tip says
  `注意:开启【主机名查询】功能,将会降低查询效率` and `Tip:本工具只支持查询 当前电脑所在网段下的所有MAC地址`). The **only** throttle is
  `MemoryCacheManager` (1 hour per IP, and the `未知` result *is* cached); there is no concurrency limiter and no timeout —
  `Dns.GetHostEntry` uses the OS resolver defaults. A MAC lookup runs afterwards for each such host (usually an ARP-cache hit
  already made during the ping fallback; otherwise a process launch).
* **MAC retrieval summary:** (1) local-adapter MAC for the machine's own addresses, (2) 1-hour `MemoryCache`, (3) `arp -a`
  stdout scrape with regex `([0-9A-Fa-f]{2}[-:]){5}([0-9A-Fa-f]{2})`, upper-cased.
* The tooltip is written back through `Button.BeginInvoke` (one of only three explicit marshalling sites in the file).

### 3.9 Counters and progress text

| Surface | Text | When |
| --- | --- | --- |
| `statusTip` | `程序准备就绪` | designer / load |
| `statusTip` | `开始扫描IP` | `btnStart_Click` |
| `statusTip` | `开始扫描IP: {ip}` | every dispatch iteration in `ScanIP` |
| `statusTip` | `扫描完毕` | dispatch loop finished normally |
| `statusTip` | `用户取消了操作` | dispatch loop ended because 停止 was pressed |
| `statusTip` | `IP已复制到剪贴板` | `statusIP_Click` |
| `lblStat` | *(empty)* → `数量合计：正常 {ok}，不通 {ng}` | recomputed on **every** ping completion (and in the TCP callback) |
| `lblTitle` | `正在绘制表格视图` → `{prefix}网段IP使用情况` | block creation; re-set by `btnStart_Click` |
| `statusIP` | `活动IP:{ip}[; {ip}…]` | load only |

There is **no progress bar and no percentage**. Counting is done by comparing `BackColor` against
`NetworkOKColor` / `NetworkNGColor` over all 254 dictionary entries (`ipBtnDict.Values.Count(...)`), which is
O(254) on every completion — a WinUI port should keep counters incrementally.

### 3.10 Concurrency, cancellation, UI marshalling — the short version

| Concern | V1.28.2 behaviour | WinUI 3 recommendation |
| --- | --- | --- |
| Dispatch | 1 background `Thread` (`IsBackground = true`), `CurrentIp` 1→254, `Thread.Sleep(10)` between sends | `Task` + `Channel`/`SemaphoreSlim` with a bounded degree (e.g. 64) |
| In-flight probes | up to 254 ICMP (`Ping` object each), up to 254 `Task`s for TCP fallbacks, up to 254 thread-pool items for DNS/MAC, up to 254 `arp -a` processes | bounded parallelism; batch ARP tables (read `arp -a` **once** per sweep) |
| Cancellation | plain `bool CanScanIp`; only the dispatch loop observes it. **Already-dispatched pings still complete, still recolour blocks, still update `lblStat`, still fire the monitor.** | `CancellationTokenSource`, mark late results stale |
| Late-result hazard | the IP is recomputed as `txtIPPart.Text + "." + ipNum` in the callback, and `txtIPPart` is re-enabled on 停止 → late replies can colour the wrong block after a prefix edit | pass the full IP string (or an immutable sweep id) as the ping state |
| Results → UI | `Control.CheckForIllegalCrossThreadCalls = false` (ctor) makes *all* cross-thread property writes legal; only 3 sites use `BeginInvoke` explicitly: `flowLayoutPanel.BeginInvoke` in `DelayDisplayTableView`, `Btn.BeginInvoke` in `GetHostNameAndMac`, and (in other forms) `dgvResult.BeginInvoke` | all mutations via `DispatcherQueue.TryEnqueue` on an `ObservableCollection` |
| Exception safety | `Ping_PingCompleted` does not check `e.Error` / `e.Cancelled`; if the ICMP attempt itself failed (unresolvable host) `e.Reply` is `null` → `NullReferenceException` on a thread-pool thread. `ScanIP` has no try/catch either, and both global handlers are empty → an invalid prefix can kill the process | validate the octets, use `PingReply`-less APIs (`PingException` handling), wrap the sweep |
| Thread-safety of `CanScanIp` | ordinary `bool` field, no `volatile`/lock (works in practice on x86/x64) | use `volatile`/`Interlocked` or a token |

---

## 4. Colour-block behaviour

### 4.1 Which colour is applied when

| Trigger | Code | Effect |
| --- | --- | --- |
| block creation | `button.BackColor = Global.ConfigInfo.DefaultColor` | all 254 blocks start as `DefaultColor` (default SkyBlue) |
| 开始 pressed | `btn.BackColor = Global.ConfigInfo.DefaultColor` (loop over all) | full reset, tooltips rebuilt |
| ping chain verdict 在线 | `ipBtnDict[ipNum].Btn.BackColor = Global.ConfigInfo.NetworkOKColor` | green (default LimeGreen) |
| ping chain verdict 不通 | `ipBtnDict[ipNum].Btn.BackColor = Global.ConfigInfo.NetworkNGColor` | red (default IndianRed, palette `IndianRed`, not pure red) |
| single click on a block | `btn.BackColor = Global.ConfigInfo.DefaultColor` then a new ping | brief flash back to default, then re-coloured by the chain |
| options saved | `btnNG.BackColor`/`btnOK.BackColor` only | **existing blocks keep their old colours** until the next scan; there is no repaint pass |
| legend counting | `Count(r => r.Btn.BackColor == NetworkOKColor/NGColor)` | exact `Color` equality — a block that is mid-transition or was never scanned is counted as neither |

`Color` values are stored in `IPScaner.cfg` as ARGB ints; a fresh install is SkyBlue / LimeGreen / IndianRed / Blue.

### 4.2 Block text and font

* Text is always the **last octet** as a decimal string: `1` … `254`.
* Font: `new Font("微软雅黑", BtnFontSize, FontStyle.Regular, GraphicsUnit.Point)`; `TextAlign = TopCenter`;
  `FlatStyle = Flat` with `UseVisualStyleBackColor = false`.
* `BtnFontSize` effect (`UpdateControlByCfgInfo`, `FormMain.cs:1307-1320`):

```csharp
if ((float)Global.ConfigInfo.BtnFontSize != BtnFontSize)
{
    int num = Math.Abs(Global.ConfigInfo.BtnFontSize - 8);
    BtnFontSize = 8 + num;          // 8 .. 12
    BtnHeight   = 22 + num * 2;     // 22 .. 30
    BtnWidth    = 35 + num * 4;     // 35 .. 51
    if (flowLayoutPanel.Controls.Count > 0 &&
        MessageBox.Show("字号已变更，是否立即重启程序？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
    {
        ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath) { WorkingDirectory = Application.StartupPath };
        Process.Start(psi);
        Application.Exit();
    }
}
```

| Combo item (`cmbFontSize`) | index | `ConfigInfo.BtnFontSize` | `num` | font pt | block size |
| --- | --- | --- | --- | --- | --- |
| `小` | 0 | 8 | 0 | 8 | 35 x 22 |
| `中` (default) | 1 | 9 | 1 | 9 | **39 x 24** |
| `大` | 2 | 10 | 2 | 10 | 43 x 26 |
| `超大` | 3 | 11 | 3 | 11 | 47 x 28 |
| `特大` | 4 | 12 | 4 | 12 | 51 x 30 |

Options note (exact): `注：变更小色块字号需要重启` (blue 宋体 9pt). Because the geometry fields are recomputed but already-created
buttons keep their old `Size`, the restart prompt is the real mechanism: at startup `LoadConfig()` → `UpdateControlByCfgInfo()`
runs **before** `DelayDisplayTableView()` (so `flowLayoutPanel.Controls.Count == 0` and the prompt is skipped), which is exactly
how the new size takes effect on the next run. From the options dialog the controls already exist → the prompt appears; “Yes”
relaunches the exe.

### 4.3 Tooltip content

Three producers, all `tip.SetToolTip(btn, …)`, `Environment.NewLine` = `\r\n`:

| Producer | Exact string |
| --- | --- |
| `btnStart_Click` / `Btn_Click` | `"IP：" + ip` + (memo ? `"\r\n备注：" + memo` : `""`) |
| `GetHostNameAndMac` (post-scan) | `"名称：" + hostName + "\r\nIP：" + ip` + (`"\r\nMac：" + mac`) + (`"\r\n备注：" + memo`) |
| `UpdateTooltip` (after editing a memo) | `"名称：" + hostName` (if known) + `"\r\nIP：" + ip` + (`"\r\nMac：" + mac`) + (`"\r\n备注：" + memo`) |

Notes:

* The **prefix order flips** between the click-time tooltip (`IP：` first) and the enriched one (`名称：` first). The
  right-click regexes in `Btn_MouseDown` rely on exactly these prefixes (`IP：`, `名称：`, `Mac：`) — the code is
  case-sensitive on `Mac：` (capital M, lower-case `ac`).
* `备注` in the tooltip is resolved with `Global.GetMemoByMacOrIp(mac, ip)` → MAC key first, then IP key; the
  start-of-scan pass can only use the IP key (no MACs known yet).
* Tooltips are only populated after an interaction: there is **no hover handler** — `Tip:鼠标悬停在【IP小色块】上,会显示计算机名称和MAC地址`
  only holds after a scan with 主机名查询 enabled, or after a single click (IP + memo only).
* `ToolTip` timings are the WinForms defaults (initial ≈500 ms, auto-pop 5000 ms, no re-show) — nothing is customised.
* The right-click menu *scrapes the tooltip string* to build its first three items (§6.1) — the tooltip is de-facto state.

### 4.4 Memo colour and its precedence

* `ForeColor` precedence at scan start: **memo present (looked up by IP only) → `ConfigInfo.MemoColor`** (default Blue,
  label `备注色块颜色`); otherwise **`Color.Black`**.
* This overwrites the initial `Color.OrangeRed` local-machine marker assigned at block creation
  (`if (txtIPPart.Text + "." + text == localIpAddress) button.ForeColor = Color.OrangeRed;`) — so the “this is me” highlight
  survives only until the first 开始.
* The colour is a *one-shot* decision taken in `btnStart_Click`; if a memo is created or a MAC-based memo becomes resolvable
  later, the text colour is **not** updated (only the tooltip is, via `UpdateTooltip`).
* Admin: `备注管理(&M)` → `FormMemoManager` (`备注信息管理`) edits the same dictionary; on OK `Global.MemoDict` is rebuilt as
  IP-keyed ∪ MAC-keyed and `SaveMemoInfo()` rewrites `IPScanerMemo.dat`.

### 4.5 `Blink` (present but unused)

```csharp
public void Blink(Button btn)                     // FormMain.cs:656-673
{
    if (blinkTimer != null && blinkTimer.Enabled) { blinkTimer.Stop(); currentBlinkButton.BackColor = originalColor; }
    originalColor = btn.BackColor; currentBlinkButton = btn;
    blinkTimer = new System.Windows.Forms.Timer(); blinkTimer.Interval = 500;
    blinkTimer.Tick += (s, e) => { btn.BackColor = (btn.BackColor == originalColor) ? Color.Gold : originalColor; btn.Invalidate(); };
    blinkTimer.Start();
}
```

**Nothing in V1.28.2 calls `Blink`** (verified by grep across the whole source tree) — it is dead public API. A 500 ms
`originalColor ⇄ Color.Gold` pulse; keep it in mind only if a “newly discovered host” animation is wanted.

---

## 5. Click / double-click semantics

### 5.1 The `MouseDown` state machine + `DoubleClickTime`

```csharp
private void Btn_MouseDown(object sender, MouseEventArgs e)         // FormMain.cs:724-769
{
    Button button = sender as Button;
    if (e.Button == MouseButtons.Left)
    {
        clickCount++;
        if (clickCount == 1) { clickTimer.Start(); senderButton = sender as Button; }
        else if (clickCount == 2)
        {
            if (button == senderButton) { clickTimer.Stop(); clickCount = 0; Btn_DoubleClick(button, e); }
            else Btn_Click(button);                                 // second click landed on a *different* block
        }
    }
    else if (e.Button == MouseButtons.Right) { ... §6.1 ... }
}
```

The timer is created in `DelayDisplayTableView` (`FormMain.cs:621-631`):

```csharp
clickTimer = new System.Windows.Forms.Timer();
clickTimer.Interval = Global.ConfigInfo.DoubleClickTime;            // default 200 ms, UI range 100..500 (step 50)
clickTimer.Tick += (s, e) =>
{
    clickTimer.Stop();
    if (clickCount == 1) Btn_Click(senderButton);                   // single-click action fires only after the interval
    clickCount = 0;
};
```

**Why a custom interval is needed** (v1.13 changelog 「双击间隔默认为200毫秒，可以在参数配置页面修改值」): `Button`’s own
`Click`/`DoubleClick` pair cannot be re-timed — the OS double-click time (`GetDoubleClickTime()`) is a global user setting and
WinForms does not expose it per-control. The app therefore suppresses the normal click semantics entirely and discriminates
single vs. double itself on `MouseDown`, deferring the single-click action by `ConfigInfo.DoubleClickTime` so that a
user-tunable value (200 ms default, options label `双击间隔毫秒`) decides whether a second press is a double-click.
`UpdateControlByCfgInfo` re-applies `clickTimer.Interval` whenever options are saved.

Behavioural consequences to reproduce:

* A single click has **no immediate visual effect** other than… none, until the interval elapses (the colour reset + ping +
  clipboard copy happen in `Btn_Click`, i.e. 200 ms later).
* Clicking two *different* blocks within the interval fires the single-click action for the second one immediately and
  *cancels* the first one’s deferred action (the tick sees `clickCount == 2`).
* A third click while `clickCount` is already 2 is ignored by the state machine (no branch) but leaves `clickCount` at 3;
  the next tick resets it to 0.
* Right-clicks never touch `clickCount`.

### 5.2 Single click → `Btn_Click` (`FormMain.cs:675-693`)

```csharp
private void Btn_Click(Button btn)
{
    string[] array = txtIPPart.Text.Split('.');
    if (array.Length == 3)
    {
        btn.BackColor = Global.ConfigInfo.DefaultColor;             // clear the previous verdict
        string text  = txtIPPart.Text + "." + btn.Text;
        string text2 = Global.GetMemoByMacOrIp(text);               // IP string passed as the mac argument
        if (!string.IsNullOrEmpty(text2)) text2 = Environment.NewLine + "备注：" + text2;
        tip.SetToolTip(btn, "IP：" + text + text2);
        Ping ping = new Ping();
        ping.PingCompleted += Ping_PingCompleted;                   // same handler as the sweep
        ping.SendAsync(text, Global.ConfigInfo.PingTimeout, btn.Text);
        Clipboard.SetText(text);                                    // side effect: the IP lands on the clipboard
    }
}
```

* Single click = **quick re-check of one host + copy its IP to the clipboard**. This is the
  `Tip:点击【IP小色块】,可快速复制IP地址` behaviour.
* The completion goes through the *same* `Ping_PingCompleted`, so the full liveness chain (ARP/TCP) and the 主机名/MAC lookup
  apply, `lblStat` is refreshed, and the IP-change monitor is notified (`CallMonitorIPChangedStatus`) — a single click counts
  as a scan event (§9).
* Note `e.UserState` is `btn.Text` (a *string* octet) here but `CurrentIp` (an **int**) during the sweep;
  `Convert.ToInt32(e.UserState)` handles both.
* `Clipboard.SetText` can throw `ExternalException` if another process holds the clipboard — unguarded (fires on the UI thread,
  swallowed by the empty `Application.ThreadException` handler).

### 5.3 Double click → `Btn_DoubleClick` (`FormMain.cs:695-722`) and the `DoubleEvent` enum

```csharp
private void Btn_DoubleClick(Button btn, EventArgs e)
{
    string text = txtIPPart.Text + "." + btn.Text;
    switch (Global.ConfigInfo.DoubleEvent)
    {
    case EventName.Ping:    Utility.ProcessStartCommand($"ping {text} -n {Global.ConfigInfo.PingCount} &pause"); break;
    case EventName.ViewWeb: Process.Start("http://" + text); break;
    case EventName.Tracert: Utility.ProcessStartCommand("tracert " + text + " &pause"); break;
    case EventName.Telnet:  Utility.ProcessStartCommand("telnet " + text + " 23 &pause"); break;
    case EventName.Netstat: Utility.ProcessStartCommand("netstat -ano | findstr " + text + " &pause"); break;
    case EventName.ARP:     Utility.ProcessStartCommand("arp -a " + text + " &pause"); break;
    case EventName.Share:   Process.Start("explorer.exe", "\\\\" + text); break;
    }
}
```

`EventName` (`EventName.cs`) and the options combo `小色块双击事件` (`cmbDoubleEvent`, `Items` in this exact order — the
selected **index** is the enum value):

| Enum | Value | Combo text | Exact action | Actual process launched |
| --- | --- | --- | --- | --- |
| `EventName.Ping` | 0 (default) | `Ping` | `Utility.ProcessStartCommand($"ping {ip} -n {PingCount} &pause")` | `cmd.exe /c ping <ip> -n 4 &pause`, `WindowStyle = Normal` (visible console) |
| `EventName.ViewWeb` | 1 | `浏览网页` | `Process.Start("http://" + ip)` | shell-open `http://<ip>` in the default browser |
| `EventName.Tracert` | 2 | `Tracert` | `Utility.ProcessStartCommand("tracert " + ip + " &pause")` | `cmd.exe /c tracert <ip> &pause`, visible |
| `EventName.Telnet` | 3 | `Telnet` | `Utility.ProcessStartCommand("telnet " + ip + " 23 &pause")` | `cmd.exe /c telnet <ip> 23 &pause`, visible |
| `EventName.Netstat` | 4 | `Netstat` | `Utility.ProcessStartCommand("netstat -ano \| findstr " + ip + " &pause")` | `cmd.exe /c netstat -ano \| findstr <ip> &pause`, visible |
| `EventName.ARP` | 5 | `ARP` | `Utility.ProcessStartCommand("arp -a " + ip + " &pause")` | `cmd.exe /c arp -a <ip> &pause`, visible |
| `EventName.Share` | 6 | `共享目录` | `Process.Start("explorer.exe", "\\\\" + ip)` | Explorer at `\\<ip>` |

Options tooltip for the combo: `用右键菜单中的某一项代替双击事件` (v1.18: 小色块双击事件支持自定义，便于快速执行某一右键事件).

---

## 6. Right-click context menu on a colour block

### 6.1 Menu construction and dynamic header items

Fixed item list (`contextMenuStrip`, `FormMain.cs:2099-2159`), in `Items.AddRange` order — `Size = 232 x 424`,
`ImageScalingSize = 28x28`, `contextMenuStrip.Text = "网络信息"` (invisible; context menus show no caption):

| # | Field | Designer `Text` | Click → |
| --- | --- | --- | --- |
| 0 | `mnuIP` | `请先点击IP色块` | `mnuCommand_Click` |
| 1 | `mnuName` | `等待查询IP信息` | `mnuCommand_Click` |
| 2 | `mnuMAC` | `再查看右键菜单` | `mnuCommand_Click` |
| 3 | `toolStripMenuItem8` | — | **separator** |
| 4 | `mnuScan` | `端口扫描(&S)` | `mnuCommand_Click` → `new FormPortScan(ip).Show()` |
| 5 | `mnuShareFolder` | `访问共享目录` | `mnuCommand_Click` → `Process.Start("explorer.exe", "\\\\" + ip)` |
| 6 | `mnuViewWeb` | `浏览网页(&V)` | `mnuCommand_Click` → `Process.Start("http://" + ip)` |
| 7 | `mnuPing` | `调用Ping命令` | `mnuCommand_Click` → `ping <ip> -n {PingCount} &pause` |
| 8 | `mnuTracert` | `调用Tracert(&R)` | `mnuCommand_Click` → `tracert <ip> &pause` |
| 9 | `mnuTelnet` | `调用Telnet(&T)` | `mnuCommand_Click` → `telnet <ip> 23 &pause` |
| 10 | `mnuNetstat` | `调用netstat(&N)` | `mnuCommand_Click` → `netstat -ano \| findstr <ip> &pause` |
| 11 | `mnuARP` | `调用ARP(&A)` | `mnuCommand_Click` → `arp -a <ip> &pause` |
| 12 | `toolStripMenuItem10` | — | **separator** |
| 13 | `mnuMemo` | `备注信息(&M)` | `mnuCommand_Click` → `FormIPMemo` dialog |

All leaf items are 231 x 34 px. On right `MouseDown` the three header items are rewritten from the block's tooltip
(`FormMain.cs:749-768`):

```csharp
else if (e.Button == MouseButtons.Right)
{
    string toolTip = tip.GetToolTip(button);
    if (!string.IsNullOrEmpty(toolTip))
    {
        mnuName.Text = Regex.Match(toolTip, "名称：(.+)(\\r\\n)?").Value.Trim();
        mnuIP.Text   = Regex.Match(toolTip, "IP：(.+)(\\r\\n)?").Value.Trim();
        mnuMAC.Text  = Regex.Match(toolTip, "Mac：(.+)(\\r\\n)?").Value.Trim();
    }
    else
    {
        mnuName.Text = "请先点击IP色块";
        mnuIP.Text   = "等待查询IP信息";
        mnuMAC.Text  = "再查看右键菜单";
        mnuIP.Tag    = button.Text;                       // the last octet, used as a fallback
    }
    mnuName.Visible = !string.IsNullOrEmpty(mnuName.Text);
    mnuMAC.Visible  = !string.IsNullOrEmpty(mnuMAC.Text);
    contextMenuStrip.Show(button, e.Location);
}
```

* The item **texts keep the Chinese label prefix** (`IP：192.168.1.7`, `名称：PC-01`, `Mac：AA-BB-…`) — clicking them copies
  only the part after the first `：` (§6.2).
* `mnuName` and `mnuMAC` are hidden when the corresponding line is absent (no hostname query yet); `mnuIP` is never hidden.
* In the “no tooltip yet” case the three fallback strings are assigned **shifted** relative to the designer values:
  `mnuName` gets `请先点击IP色块`, `mnuIP` gets `等待查询IP信息`, `mnuMAC` gets `再查看右键菜单` (the block has never been clicked or
  scanned, so nothing is known). `mnuIP.Tag` then holds the octet so the commands can still work.
* **Right-click never selects, pings or copies anything by itself** — it only opens the menu (and the IP string is taken from
  whichever block was right-clicked last, via the tooltip).

### 6.2 `mnuCommand_Click` — dispatch (`FormMain.cs:1087-1163`)

```csharp
ToolStripItem toolStripItem = sender as ToolStripItem;
try
{
    string ipAddressByMnuCmd = GetIpAddressByMnuCmd();
    if (toolStripItem.Text.Contains("："))                       // (A) copy-variant: mnuIP / mnuName / mnuMAC
    {
        string value = Regex.Match(toolStripItem.Text, "：(.+)").Groups[1].Value;
        if (value != "未知")
        {
            Clipboard.SetText(value);
            MessageBox.Show("已复制【" + value + "】", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
        }
    }
    else if (toolStripItem.Name == mnuScan.Name)          new FormPortScan(ipAddressByMnuCmd).Show();
    else if (toolStripItem.Name == mnuPing.Name)          Utility.ProcessStartCommand($"ping {ipAddressByMnuCmd} -n {Global.ConfigInfo.PingCount} &pause");
    else if (toolStripItem.Name == mnuTracert.Name)       Utility.ProcessStartCommand("tracert " + ipAddressByMnuCmd + " &pause");
    else if (toolStripItem.Name == mnuTelnet.Name)        Utility.ProcessStartCommand("telnet " + ipAddressByMnuCmd + " 23 &pause");
    else if (toolStripItem.Name == mnuNetstat.Name)       Utility.ProcessStartCommand("netstat -ano | findstr " + ipAddressByMnuCmd + " &pause");
    else if (toolStripItem.Name == mnuARP.Name)           Utility.ProcessStartCommand("arp -a " + ipAddressByMnuCmd + " &pause");
    else if (toolStripItem.Name == mnuShareFolder.Name)   Process.Start("explorer.exe", "\\\\" + ipAddressByMnuCmd);
    else if (toolStripItem.Name == mnuViewWeb.Name)       Process.Start("http://" + ipAddressByMnuCmd);
    else if (toolStripItem.Name == mnuMemo.Name)          { ... §6.5 ... }
}
catch (Exception ex) { MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Hand); }
```

`GetIpAddressByMnuCmd` (`FormMain.cs:1185-1193`):

```csharp
string text = Regex.Match(mnuIP.Text, "IP：(.+)?").Groups[1].Value.Trim();
if (string.IsNullOrEmpty(text)) text = txtIPPart.Text + "." + mnuIP.Tag.ToString();
return text;
```

**Copy variants (A):** clicking the `IP：…`, `名称：…` or `Mac：…` header item copies the value after the first `：` (the literal
`未知` is ignored) and confirms with `已复制【<value>】`. For the never-clicked fallback state the texts contain no `：`, so the
click falls through the name chain and returns silently.

**Share folder (exact UNC construction):** `explorer.exe` with the argument `"\\\\" + ip` → `\\192.168.1.7`
(C# `"\\\\"` = two literal backslashes). The window lives in the Options section of Game Bar's Game Mode scheme; a WinUI port
should call `Windows.System.Launcher.LaunchUriAsync(new Uri(@"\\192.168.1.7"))` or `Process.Start("explorer.exe", @"\\192.168.1.7")`.

**Command launches / window style** (`Utility.ProcessStartCommand`, `Utility.cs:234-266`):

```csharp
public static void ProcessStartCommand(string command, bool cmdVisible = true)
{
    if (File.Exists(command)) { try { Process.Start(command); return; } catch (Exception ex) { throw ex; } }   // DIY file paths
    string arguments = "/c " + command;
    ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", arguments);
    if (cmdVisible) psi.WindowStyle = ProcessWindowStyle.Normal;
    else            psi.WindowStyle = ProcessWindowStyle.Hidden;
    try { Process.Start(psi); } catch (Exception ex2) { throw ex2; }
}
```

So every command above runs as **`cmd.exe /c <command>` with a visible console** (the default `cmdVisible: true`), the
trailing `&pause` keeps the window open with `请按任意键继续. . .`. `IsCommonCmd` (`ping, tracert, telnet, netstat, arp, nslookup,
ipconfig, route, netsh`) only matters for the Windows-tools menu (§1.1.2) and the DIY commands, where non-common commands get
`WindowStyle = Hidden`.

Full command lines for each menu item, exactly as executed:

| Menu item | `FileName` | `Arguments` | Window |
| --- | --- | --- | --- |
| `调用Ping命令` | `cmd.exe` | `/c ping 192.168.1.7 -n 4 &pause` | Normal |
| `调用Tracert(&R)` | `cmd.exe` | `/c tracert 192.168.1.7 &pause` | Normal |
| `调用Telnet(&T)` | `cmd.exe` | `/c telnet 192.168.1.7 23 &pause` | Normal |
| `调用netstat(&N)` | `cmd.exe` | `/c netstat -ano \| findstr 192.168.1.7 &pause` | Normal |
| `调用ARP(&A)` | `cmd.exe` | `/c arp -a 192.168.1.7 &pause` | Normal |
| `浏览网页(&V)` | *(shell)* | `http://192.168.1.7` | default browser |
| `访问共享目录` | `explorer.exe` | `\\192.168.1.7` | Explorer |
| `端口扫描(&S)` | — | `new FormPortScan("192.168.1.7").Show()` (window `端口扫描`, `txtIP` prefilled) | in-app |

### 6.3 `端口扫描(&S)` — “快速端口扫描”

v1.14 added this as *快速端口扫描*; the V1.28.2 label is `端口扫描(&S)`. It opens the non-modal `FormPortScan` with the
single IP pre-filled into `txtIP` (`FormPortScan(string ip) : this() { txtIP.Text = ip; }`, `FormPortScan.cs:117-121`).
The form shows `TCP超时毫秒：{PortTimeout}` in a label, uses `Semaphore(1000, 1000)` for its own concurrency and cancels via
`CancellationTokenSource` on close (`FormPortScan.cs:18-22,109-115`) — i.e. the port scanner uses the opposite strategy
(bounded + cancellable) to the main sweep. Details belong to a separate doc.

### 6.4 Windows-tools / DIY execution (`mnuOutTool_Click`, `FormMain.cs:1195-1212`)

```csharp
ToolStripMenuItem toolStripMenuItem = sender as ToolStripMenuItem;
if (toolStripMenuItem.Tag != null && !string.IsNullOrEmpty(toolStripMenuItem.Tag.ToString()))
{
    string text = toolStripMenuItem.Tag.ToString();
    try
    {
        string command = text.Split(' ')[0];
        bool cmdVisible = Utility.IsCommonCmd(command);          // true only for ping/tracert/telnet/netstat/arp/nslookup/ipconfig/route/netsh
        Utility.ProcessStartCommand(text, cmdVisible);
    }
    catch (Exception ex) { MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Hand); }
}
```

So all built-in Windows tools (`control …`, `*.msc`, `regedit`, `rundll32 …`) run as `cmd.exe /c <tag>` with a **hidden**
console; DIY commands starting with a common network tool get a visible one; a DIY `Tag` that is an existing file path is
launched directly via `Process.Start(command)` (this is how `D:\…\设备台账清单.xlsx` opens).

### 6.5 `备注信息(&M)` — memo dialog flow

```csharp
string empty = string.Empty;
empty = Global.GetMemoByMacOrIp(ipAddressByMnuCmd);
int key = Convert.ToInt32(ipAddressByMnuCmd.Split('.').Last());
FormIPMemo formIPMemo = new FormIPMemo(ipAddressByMnuCmd, ipBtnDict[key].MAC, empty, ipBtnDict[key].HostName);
if (formIPMemo.ShowDialog() == DialogResult.OK)
{
    if (!string.IsNullOrEmpty(formIPMemo.IP))  Global.MemoDict[formIPMemo.IP]  = formIPMemo.Memo;
    if (!string.IsNullOrEmpty(formIPMemo.MAC)) Global.MemoDict[formIPMemo.MAC] = formIPMemo.Memo;
    SaveMemoInfo();
    UpdateTooltip(ipAddressByMnuCmd);
}
```

`FormIPMemo` (window title `"备注 " + ip`, i.e. `备注 192.168.1.7`):

| Control | `Text` |
| --- | --- |
| `label2` | `IP地址：` |
| `txtIP` | prefilled with the IP (187x21) |
| `label3` | `MAC地址：` |
| `txtMAC` | prefilled with `ipBtnDict[key].MAC` |
| `label1` | `备注内容：` |
| `txtMemo` | multiline 189x88; prefilled with the existing memo, else with the **host name** |
| `btnSave` | `保存` |
| `btnClose` | `关闭` (`DialogResult.Cancel`) |
| `label4` | `IP或MAC只填一个即可。\r\n若同时存在，以MAC为主` (blue 宋体 9pt) |

Validation on 保存: both IP and MAC empty → `IP地址或MAC地址不能同时为空`; non-empty IP failing `Utility.ValidIP` →
`请填写正确的IP地址`. On success `IP = txtIP.Text.Trim()`, `MAC = txtMAC.Text.Trim()`, `Memo = txtMemo.Text.TrimEnd()`.

After saving, `UpdateTooltip(ip)` rebuilds the tooltip (§4.3, producer 3) and persists the dictionary to
`IPScanerMemo.dat`. Note the memo dialog is the **only** way to write a memo from the main window; `备注管理(&M)` manages the
whole list (dialog `备注信息管理`, modal, `ClientSize` 491-ish layout; group `查询条件` with `txtFilter` (tooltip
`查询IP或备注信息`) + `btnQuery` `查询` + `btnImport` `导入剪贴板数据` (tooltip `导入剪贴板数据，格式为192.168.0.1=监控服务器`)
+ `btnNew` `新增` / `btnEdit` `修改` / `btnDel` `删除`; group `数据列表` with `dgvMemo` columns `IP地址` / `MAC地址` / `备注信息`,
even rows LightBlue, 1-based row headers, double-click a row → 修改).

---

## 7. The list view (`列表展示` → `FormIPDetail`)

There is **no `ListView`/`TreeView` anywhere in the app**. The “list view alternative” is the modal dialog
`FormIPDetail` (`FormIPDetail.cs`), opened by `btnDetail_Click` and reused by the IP-change monitor:

```csharp
private void btnDetail_Click(object sender, EventArgs e)
{
    List<NetworkInfo> allComps = GetAllComps();            // isAll: true -> includes 待检测
    new FormIPDetail(allComps).ShowDialog();               // modal
}

private List<NetworkInfo> GetAllComps(bool isAll = true)   // FormMain.cs:1354-1375
{
    List<NetworkInfo> list = new List<NetworkInfo>();
    foreach (IPBtnInfo value in ipBtnDict.Values)          // dictionary order 1..254
    {
        string text = GetIPSegment(txtIPPart.Text) + "." + value.IPNum;
        string memo = Global.GetMemoByMacOrIp(value.MAC, text).Replace("\r\n", "");
        if      (value.Btn.BackColor == Global.ConfigInfo.NetworkNGColor) list.Add(new NetworkInfo(text, "不通", "", "", memo));
        else if (value.Btn.BackColor == Global.ConfigInfo.NetworkOKColor) list.Add(new NetworkInfo(text, "正常", value.HostName, value.MAC, memo));
        else if (isAll)                                                   list.Add(new NetworkInfo(text, "待检测", "", "", memo));
    }
    return list;
}
```

So the list view is used **only on demand** (button click, or clicking the monitor result); the colour-block grid is always the
primary view and stays visible. `GetAllComps` re-derives every IP from the *current* `txtIPPart.Text`, and `NetworkInfo`’s
constructor derives the flags: `IsOnline = Status == "正常"`, `IsOffline = Status == "不通"`, `IsPinged = Status != "待检测"`.

`FormIPDetail` window: `Text = "IP段扫描结果"`, `FormBorderStyle = FixedSingle`, `ClientSize = 720x420`,
`StartPosition = CenterParent`, `Padding = (10)`, group box caption `列表展示`, `DataGridView` `dgvResult`
(`ReadOnly`, `AllowUserToAddRows = false`, `AllowUserToDeleteRows = false`, `BackgroundColor = SystemColors.Control`,
`RowHeadersWidth = 60`, `RowTemplate.Height = 23`, `AutoGenerateColumns = false`).

| # | Field | `HeaderText` | `DataPropertyName` | Width / mode | Source |
| --- | --- | --- | --- | --- | --- |
| 0 | `Column1` | `IP` | `IP` | 120, `MinimumWidth = 100`, `SortMode = NotSortable` | `GetIPSegment(txtIPPart.Text) + "." + IPNum` |
| 1 | `Column3` | `状态` | `Status` | 60 | `正常` / `不通` / `待检测` / (monitor) `上线` / `下线` |
| 2 | `Column4` | `名称` | `HostName` | 150 | `IPBtnInfo.HostName` (only when 主机名查询 enabled) |
| 3 | `Column2` | `MAC` | `MAC` | 120 | `IPBtnInfo.MAC` (ARP cache) |
| 4 | `Column5` | `备注` | `Memo` | `AutoSizeMode = Fill` | `GetMemoByMacOrIp(MAC, IP)` with newlines stripped |

Row header cells are numbered `1, 2, 3 …`; the **状态 cell only** is coloured
(`e.Row.Cells[1].Style.BackColor = NetworkOKColor` for `正常`, `NetworkNGColor` for `不通`), not the whole row.

**Sorting: none.** `FormIPDetail` has no `ColumnHeaderMouseClick` handler and no `SortMode` other than `NotSortable` on column 0 —
rows appear in `ipBtnDict` order (1…254). Compare with `IP批量扫描` (`FormIPSegment`), where clicking a header sorts the grid
(and the first click sorts *descending*; see doc 03 §A.5). A WinUI port should add sorting here (the natural expectation), while
noting it is an addition, not a port.

---

## 8. Export — `导出` (`btnExport_Click`, `FormMain.cs:1325-1352`)

```csharp
private void btnExport_Click(object sender, EventArgs e)
{
    if (ipBtnDict.Count == 0) return;                              // silent no-op before the blocks exist

    StringBuilder stringBuilder = new StringBuilder();
    stringBuilder.AppendLine("IP,状态,主机名,MAC,备注");            // header row, grid order
    List<NetworkInfo> allComps = GetAllComps();                     // isAll: true -> every one of the 254 rows
    foreach (NetworkInfo item in allComps) stringBuilder.AppendLine(item.ToString());

    string text = Path.Combine(Application.StartupPath, "IP扫描结果-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".csv");
    File.WriteAllText(text, stringBuilder.ToString(), Encoding.UTF8);

    if (!File.Exists(text) || MessageBox.Show("IP扫描结果导出成功。是否要打开Excel文档？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        return;
    try { Process.Start(text); } catch (Exception ex) { MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Hand); }
}
```

| Aspect | Exact behaviour |
| --- | --- |
| Format | plain `.csv` (comma-separated), one row per **all 254** addresses |
| Header | `IP,状态,主机名,MAC,备注` |
| Row | `NetworkInfo.ToString()` → `IP + "," + Status + "," + HostName + "," + MAC + "," + Memo` (`NetworkInfo.cs:47-50`) |
| Status values | `正常` / `不通` / `待检测` (un-scanned blocks are exported too) |
| Memo | `GetMemoByMacOrIp(MAC, IP)` with `\r\n` removed |
| Encoding | `Encoding.UTF8` = **UTF-8 *with* BOM** (contrast: `IP批量扫描` writes with no encoding → UTF-8 *without* BOM; see doc 03 §A.7) |
| Line endings | `StringBuilder.AppendLine` → `\r\n`, trailing newline present |
| Escaping | none — commas/quotes/newlines inside host names or memos corrupt the file |
| File name | `IP扫描结果-yyyyMMddHHmmss.csv`, e.g. `IP扫描结果-20240115093012.csv` |
| Location | `Application.StartupPath` (next to the exe). No Save-As dialog, no overwrite prompt (timestamp granularity = 1 s) |
| Confirmation | `IP扫描结果导出成功。是否要打开Excel文档？` `YesNo` + `Question`, caption = the **versioned** window title (`局域网IP扫描工具  Ver: 1.28.2`); `Yes` → `Process.Start(csv)` |
| Failure | raw exception message, `MessageBoxIcon.Hand` |

---

## 9. The IP-change monitor (IP异动监测, v1.26)

### 9.1 UI

`flowLayoutPanel1` inside `tableTop` (row 0 of `tableLayoutPanel1`), left→right:

| Field | Type | `Text` | Decoration | Handler |
| --- | --- | --- | --- | --- |
| `lblMonitor` | Label | `基准监测` | `Cursor = Hand`, 宋体 9pt, `ForeColor = SystemColors.Highlight`, tooltip `以当前视图结果为基准，开始监测IP在线变化\r\n点击更新基准监测数据` | `lblMonitor_Click` → **re-baseline** |
| `chkOnline` | CheckBox | `上线` | `FlatStyle = Flat`, `FlatAppearance.BorderColor = Gray`, `CheckedBackColor = FromArgb(230,240,255)`, tooltip `勾选时，刷新基准监测数据` | `chkOnline_CheckedChanged` → **re-baseline** |
| `chkOffline` | CheckBox | `下线` | same styling, same tooltip | `chkOnline_CheckedChanged` → **re-baseline** |
| `lblOnoffRes` | Label | `未监测` | `Cursor = Hand`, `ForeColor = Gray` | `lblOnoffRes_Click` → show the diff list |

`IsMonitorOnline => chkOnline.Checked || chkOffline.Checked` (`FormMain.cs:259`).

### 9.2 Baseline (`chkOnline_CheckedChanged` / `lblMonitor_Click`, `FormMain.cs:1433-1478`)

```csharp
if (IsMonitorOnline)
{
    lastIPList = GetAllComps(isAll: false).Where(r => r.IsOnline).ToList();   // snapshot of the CURRENT online set
    lblMonitor.Text  = $"已监测{lastIPList.Count}个";
    lblOnoffRes.Text = "监测中";
    lblOnoffRes.ForeColor = Color.Blue;
}
else
{
    lastIPList = new List<NetworkInfo>();
    lblMonitor.Text  = "基准监测";
    lblOnoffRes.Text = "未监测";
    lblOnoffRes.ForeColor = Color.Gray;
}
```

* Checking **either** box takes a fresh baseline of the currently-在线 hosts (so checking 下线 alone still baselines on the
  online set). Clicking `基准监测` re-takes the baseline without touching the checkboxes.
* `lblMonitor` shows `已监测{N}个` while monitoring, `基准监测` when both boxes are clear. In the “no monitor” branch of
  `lblMonitor_Click` the label text is **not** reset (only `lblOnoffRes` is).

### 9.3 Diffing (`MonitorIPChangedStatus`, `FormMain.cs:862-904`)

```csharp
private List<NetworkInfo> MonitorIPChangedStatus()
{
    List<NetworkInfo> list = new List<NetworkInfo>();
    if (IsMonitorOnline)
    {
        if (chkOnline.Checked)                                        // 上线 = now online AND not in the baseline
        {
            List<NetworkInfo> list2 = GetAllComps(false).Where(r => r.IsOnline).ToList();
            IEnumerable<string> list3 = list2.Select(r => r.IP).Except(lastIPList.Select(r => r.IP));
            list2.ForEach(r => r.Status = "上线");                     // status rewritten to 上线 for the diff rows
            list.AddRange(list2.Where(r => list3.Contains(r.IP)));
        }
        if (chkOffline.Checked)                                       // 下线 = now offline AND WAS online in the baseline
        {
            List<NetworkInfo> list4 = GetAllComps(false).Where(r => r.IsOffline).ToList();
            IEnumerable<string> list5 = list4.Select(r => r.IP).Intersect(lastIPList.Select(r => r.IP));
            list4.ForEach(r => r.Status = "下线");
            list.AddRange(list4.Where(r => list5.Contains(r.IP)));
        }
        if (list.Count > 0) { lblOnoffRes.Text = $"差异{list.Count}个"; tip.SetToolTip(lblOnoffRes, "点击查看详细"); }
        else                { lblOnoffRes.Text = "监测中";             tip.SetToolTip(lblOnoffRes, null); }
        lblOnoffRes.ForeColor = Color.Blue;
    }
    return list;
}
```

* **上线 row** = `状态 = "上线"`, plus host name / MAC / memo as collected. **下线 row** = `状态 = "下线"`, host name and MAC are
  empty (an offline host has no fresh data), memo still resolved by IP.
* The baseline is **never updated automatically** — `lastIPList` only changes when a checkbox is toggled or 基准监测 is clicked.
  Therefore the diff keeps reporting the same 上线 hosts on every sweep until the user re-baselines.
* The check ignores whether the current sweep has finished: `GetAllComps(false)` only contains blocks already coloured.

### 9.4 When the diff runs — the 1-second debounce

Every `Ping_PingCompleted` (and the cancel path in `ScanIP`) calls:

```csharp
public void CallMonitorIPChangedStatus()                 // FormMain.cs:838-850
{
    lock (_lock)
    {
        if (_timer != null) { _timer.Change(-1, -1); _timer.Dispose(); _timer = null; }
        _timer = new System.Threading.Timer(ExecuteDoWork, null, 1000, -1);   // one-shot, 1 s from now
    }
}
private void ExecuteDoWork(object state)
{
    MonitorIPChangedStatus();
    lock (_lock) { _timer.Dispose(); _timer = null; }
}
```

So each completion **cancels and re-arms a 1 s one-shot timer**; the diff runs once, 1 second after the *last* result of the
burst. (`_lock` is a `static readonly object`.) With 254 blocks the diff therefore executes once per sweep, not 254 times.

### 9.5 Where the result is surfaced

1. `lblOnoffRes.Text` = `差异{N}个` (blue) or `监测中`; `lblMonitor.Text` = `已监测{N}个`.
2. Tooltip on `lblOnoffRes` = `点击查看详细` when there is a diff.
3. Clicking `lblOnoffRes_Click` recomputes the diff and shows it modally:
   `if (list.Count > 0) new FormIPDetail(list).ShowDialog();` — the same list view (§7) with 状态 = `上线`/`下线`
   (those two values get **no** background colour, because only `正常`/`不通` are handled in `FormIPDetail`).
4. Nothing is written to disk and no toast/notification is raised — the monitor is purely visual.

---

## 10. Tray icon / minimize behaviour

Designer (`FormMain.cs:2160-2188`):

| Property | Value |
| --- | --- |
| `notifyIcon1.Visible` | `true` (from the very first moment — the icon is always in the tray) |
| `notifyIcon1.Text` | `notifyIcon1` — **the tooltip of the tray icon is the literal string `notifyIcon1`**, not the product name (bug; a WinUI port should use `局域网IP扫描工具`) |
| `notifyIcon1.Icon` | `resources."notifyIcon1.Icon"` |
| `notifyIcon1.BalloonTipTitle` | `局域网IP扫描工具` |
| `notifyIcon1.BalloonTipText` | `程序最小化到托盘，双击显示主界面` |
| `notifyIcon1.MouseDoubleClick` | `notifyIcon1_MouseDoubleClick` → `ShowMainForm()` |
| `notifyIcon1.ContextMenuStrip` | `contextMenuNotify` |

### 10.1 Hiding (`HideMainEnabled`, v1.28 item 3)

```csharp
private void FormMain_SizeChanged(object sender, EventArgs e)      // FormMain.cs:401-407
{
    if (!isClosing && WindowState == FormWindowState.Minimized && Global.ConfigInfo.HideMainEnabled)
        HideMainForm();
}
private void HideMainForm()                                        // FormMain.cs:409-413
{
    ShowInTaskbar = false;
    notifyIcon1.ShowBalloonTip(1000, notifyIcon1.BalloonTipTitle, notifyIcon1.BalloonTipText, ToolTipIcon.Info);
}
private void ShowMainForm()                                        // FormMain.cs:415-422
{
    SuspendLayout(); Show(); WindowState = FormWindowState.Normal; ShowInTaskbar = true; ResumeLayout();
}
```

* Gated by the option `最小化时隐藏到托盘` (`chkHideMain`, tab 外观, default **off**, tooltip identical to the label).
* Hiding = `ShowInTaskbar = false` + a 1 s balloon tip; the window itself is already `Minimized` (not `Hide()`n), and it also
  fires a balloon tip every time the window is minimised, not only the first time.
* Restore = double-click the tray icon, or `显示主界面(&S)`.
* `mnuHideMainForm` (`隐藏(&H)`) simply sets `WindowState = FormWindowState.Minimized` (which, with the option on, loops back
  through `SizeChanged` → `HideMainForm`).

### 10.2 Tray context menu (`contextMenuNotify`, 5 items)

| # | Field | `Text` | Action |
| --- | --- | --- | --- |
| 0 | `mnuShowMainForm` | `显示主界面(&S)` | `ShowMainForm()` |
| 1 | `mnuHideMainForm` | `隐藏(&H)` | `WindowState = Minimized` |
| 2 | `toolStripMenuItem11` | — | **separator** |
| 3 | `关于ToolStripMenuItem` | `关于(&A)` | `mnuAbout_Click` → `AboutBox` |
| 4 | `mnuExitInNotify` | `退出(&X)` | `MessageBox.Show("是否确认退出程序？", "退出", OKCancel, Question)`; on OK → `Dispose(); Close();` |

### 10.3 Window close (`FormMain_FormClosing`, `FormMain.cs:1393-1412`)

```csharp
CanScanIp = false;
isClosing = true;                                   // suppresses the minimize->tray handler
log.Log("用户关闭程序");
if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.ApplicationExitCall) { e.Cancel = false; return; }
if (MessageBox.Show("你确定要退出吗？", "关闭询问", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) { e.Cancel = false; return; }
e.Cancel = true;                                    // "No" -> cancel the close and minimise instead
WindowState = FormWindowState.Minimized;
```

So “No” never really refuses: it cancels the close and minimises the window (with `isClosing = true`, so the tray-hide handler
does **not** run for that transition — the taskbar button stays).

---

## 11. `MenuAutoOpen` and the pinned "固定快捷" menu (v1.26)

### 11.1 Hover-to-open (`ToolStripMenuItem_MouseEnter`, `FormMain.cs:1223-1230`)

```csharp
private void ToolStripMenuItem_MouseEnter(object sender, EventArgs e)
{
    if (Global.ConfigInfo.MenuAutoOpen) ((ToolStripMenuItem)sender).ShowDropDown();
}
```

* Wired **only** to two controls: `mnuCommon` (`快捷工具(&T)`) and `mnuWindows` (`windows命令(&C)`) — the drop-downs inside them
  (e.g. `IP批量扫描`, `我的命令`) do **not** auto-open on hover.
* `MenuAutoOpen` is the option `自动弹出菜单` (`cmbMenuAutoOpen`, items `是`/`否`, default **否**), tooltip
  `鼠标划过菜单时，是否自动弹出菜单`.
* Saving the option (`mnuOptionSet_Click` / `statusHostName_Click`) writes the XML, calls `UpdateControlByCfgInfo()` and
  `DislayDesktopOverlay()` — the hover behaviour changes immediately, no restart.

### 11.2 Right-click a submenu item to pin it (v1.26 item 4)

At the end of `FormMain_Load` (`FormMain.cs:303`):

```csharp
AttachRightClickEvent(new ToolStripItem[2] { mnuCommon, mnuWindows });   // FormMain.cs:306-323
```

```csharp
private void AttachRightClickEvent(ToolStripItem[] toolStripItems)
{
    foreach (ToolStripItem toolStripItem in toolStripItems)
    {
        if (!(toolStripItem is ToolStripMenuItem toolStripMenuItem)) continue;
        foreach (ToolStripItem dropDownItem in toolStripMenuItem.DropDownItems)      // ONE level only
        {
            if (dropDownItem is ToolStripMenuItem toolStripMenuItem2 && toolStripMenuItem2 != mnuStar)
            {
                toolStripMenuItem2.MouseDown -= menuRight_MouseDown;
                toolStripMenuItem2.MouseDown += menuRight_MouseDown;
            }
        }
    }
}
private void menuRight_MouseDown(object sender, MouseEventArgs e)                // FormMain.cs:382-394
{
    if (e.Button == MouseButtons.Right)
    {
        ToolStripMenuItem mnu = sender as ToolStripMenuItem;
        ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
        contextMenuStrip.Items.Add("设为固定操作", null, (s, args) => { SetStarTarget(mnu); });
        contextMenuStrip.Show(Control.MousePosition);
    }
}
```

* Only the **direct children** of `快捷工具(&T)` and `windows命令(&C)` are covered: `修改本地IP`, `IP批量扫描`,
  `IP地址计算器`, `端口扫描`, `系统端口查看`, `WiFi密码`, `我的命令`, and every Windows tool listed in §1.1.2.
  Items **inside** `我的命令` (DIY commands, 功能说明) and the top-level items themselves are **not** pinnable — a one-level
  iteration. Separators are skipped by the `is ToolStripMenuItem` test.
* Right-clicking shows a throw-away menu whose single item is `设为固定操作` (the help text says `设置固定` — inconsistency in
  the original).

### 11.3 Pinning + persistence (`SetStarTarget` / `GetMenuPath` / `FindMenuItemByPath` / `LoadStarMnu`)

```csharp
private void SetStarTarget(ToolStripMenuItem target)          // FormMain.cs:359-368
{
    _currentTarget = target;
    mnuStar.Text  = target.Text ?? "";                       // includes the "(&P)" mnemonic, e.g. "端口扫描(&P)"
    Global.ConfigInfo.StarMenu = GetMenuPath(target);        // e.g. "mnuCommon.mnuPortScan"
    XmlUtility.SaveXml(Global.ConfigInfo, CfgPath);          // persisted immediately to IPScaner.cfg
    mnuStar.Click -= mnuStar_Click;
    mnuStar.Click -= ExecuteStarAction;
    mnuStar.Click += ExecuteStarAction;
}
private string GetMenuPath(ToolStripMenuItem item)           // FormMain.cs:325-334
{
    List<string> list = new List<string>();
    while (item != null) { list.Insert(0, item.Name); item = item.OwnerItem as ToolStripMenuItem; }
    return string.Join(".", list);                           // dot-joined *Name* path, root = top-level menu name
}
private void ExecuteStarAction(object sender, EventArgs e)   // FormMain.cs:370-380
{
    if (_currentTarget != null) _currentTarget.PerformClick();
    else MessageBox.Show("请先设置快捷操作目标", Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
}
private void LoadStarMnu()                                   // FormMain.cs:460-470, called from LoadConfig()
{
    if (!string.IsNullOrEmpty(Global.ConfigInfo.StarMenu))
    {
        ToolStripMenuItem toolStripMenuItem = FindMenuItemByPath(Global.ConfigInfo.StarMenu);
        if (toolStripMenuItem != null) SetStarTarget(toolStripMenuItem);
    }
}
private ToolStripMenuItem FindMenuItemByPath(string path)     // FormMain.cs:336-357
{
    try
    {
        string[] array = path.Split('.');
        ToolStripItem toolStripItem = menuStrip1.Items[array[0]];              // lookup by Name
        for (int i = 1; i < array.Length; i++)
        {
            if (toolStripItem is ToolStripMenuItem toolStripMenuItem) { toolStripItem = toolStripMenuItem.DropDownItems[array[i]]; continue; }
            return null;
        }
        return toolStripItem as ToolStripMenuItem;
    }
    catch (Exception) { return null; }                                        // any bad path -> null, silently unpinned
}
```

Facts to preserve:

| Aspect | Behaviour |
| --- | --- |
| Persistence | `ConfigInfo.StarMenu` attribute in `IPScaner.cfg`, e.g. `StarMenu="mnuCommon.mnuPortScan"`; written on **every** pin, and reloaded by `LoadStarMnu()` from `LoadConfig()` |
| Path syntax | dot-joined WinForms **`Name`** values, root first. Indexer lookup on the collection supports both index and name |
| Label | the pinned item's `Text` is copied verbatim onto the top-level item, so `固定快捷` becomes e.g. `端口扫描(&P)` (mnemonic included → `Alt+P` now also triggers it) |
| Invocation | never a copy: `ExecuteStarAction` calls `_currentTarget.PerformClick()`, so the pinned item keeps its own behaviour and state |
| Re-pin | allowed at any time; the previous `_currentTarget`/path is simply overwritten |
| Unpin | **there is no unpin command** — the path can only be cleared by editing `IPScaner.cfg` (or deleting it) |
| Help text | shown only while nothing is pinned (`mnuStar_Click`): `【快捷工具】或【windows命令】下的所有菜单，都可以设置为固定功能。\r\n方法为：右击某一个具体菜单，点击【设置固定】即可` |
| Broken config | an unknown/invalid path makes `FindMenuItemByPath` return `null` → `固定快捷` keeps its help behaviour, no error |

---

## 12. Cross-cutting fidelity notes (things a port must decide about consciously)

1. **Elevation.** The original always runs as administrator. WinUI 3 desktop apps cannot self-elevate via the packaging
   identity — either ship an elevated-launcher stub or drop `arp -d *` / 修改本地IP functionality.
2. **No work queue / unbounded fan-out.** Everything is fire-and-forget (`Ping.SendAsync` per IP, `Task.Factory.StartNew` per
   TCP probe, `ThreadPool.QueueUserWorkItem` per hostname, `arp -a` process per uncached IP). A WinUI port needs explicit
   bounded concurrency, plus a **single** `arp -a` read per sweep shared by all hosts (the current design spawns up to 254
   processes).
3. **Cross-thread UI writes** are legal because `Control.CheckForIllegalCrossThreadCalls = false`; in WinUI every model update
   must be marshalled through the `DispatcherQueue` (or confined to it).
4. **`txtIPPart` is re-read at completion time** (`txtIPPart.Text + "." + ipNum`), and it is re-enabled when 停止 is pressed —
   a classic stale-result bug worth fixing rather than porting.
5. **`statusTip`/`lblStat` are O(254) recounts** per completion; WinUI should keep incremental counters.
6. **The tooltip string is state**: the right-click menu parses `IP：` / `名称：` / `Mac：` out of it. Any refactor should keep an
   explicit model (`IPBtnInfo.HostName` / `.MAC`) as the source for the menu instead.
7. **Encoding policy differs per file**: config XML (UTF-8 via `XmlSerializer`), `IPScanerMemo.dat` (UTF-8 **with** BOM),
   `IPScaner.cfg`, `command.txt` and `IPScanerMemo.dat` reads use `Utility.GetFileEncoding` (BOM sniff → UTF-8 / UTF-16 LE/BE,
   else GBK when a byte in `0xA1…0xF7` is seen, else `Encoding.Default` = ANSI), main-window CSV export (UTF-8 **with** BOM),
   batch-scan CSV export (UTF-8 **without** BOM).
8. **Dead / vestigial code** found while reading: `Blink()`, `statusFecthName_Click()`, `toolStripMenuItem1`, the
   `lblStat.Text == "---"` guard in `btnLegend_Click`, `if (hostName == "bogon") { }`, the `toolStripMenuItem2 != mnuStar`
   comparison, `Utility.GetWorkgroup()`, `Utility.GetComputerManufacturer()` (private, unused), `AdapterInfoCollection` (used only
   by `FormLocalIP`), and the unused `AdapterInfo adapterInfo` local in `GetAllAdapterIP`.
9. **Copy-paste bugs worth *fixing* rather than porting** (flag them in review): the `TCP超时毫秒` tooltip says `Ping时长超过此值…`;
   the memo-dialog default for `DesktopOverlayPre` differs from `ConfigInfo`’s default; `请先点击IP色块` / `等待查询IP信息` /
   `再查看右键菜单` are assigned shifted in `Btn_MouseDown`; `剪贴版` should be `剪贴板`; the end-IP validation message in
   `FormIPSegment` says `请输入正确的开始IP地址`; `contactMenuStrip.Text = "网络信息"` is never displayed.
10. **Behaviour that looks like a bug but is load-bearing**: the initial ARP-table flush (`arp -d *` on the first lookup) is
    what guarantees a *fresh* ARP resolution for the 1.28 fallback — removing it silently degrades 禁PING detection.

---

## Open questions / ambiguities

1. **“Filter ping duration > 2 s”.** No `2000` ms constant exists in V1.28.2; the surviving implementation is the configurable
   `PingTimeout` (default 500 ms) passed to `Ping.SendAsync`, and `RoundtripTime` is never inspected. Confirm with the product
   owner whether the WinUI port should keep timeout-only semantics or add a literal “RTT > 2 s ⇒ 异常” filter (a behaviour
   change).
2. **`PingCount` scope.** It is exposed as `Ping请求次数` (default 4) and used *only* in the `ping -n {PingCount}` command lines;
   the ICMP sweep always sends exactly one echo request per address. Is the port expected to add real retries?
3. **`e.Error` / `e.Cancelled` handling.** `Ping_PingCompleted` dereferences `e.Reply` unconditionally. In .NET, `Reply` is
   `null` when the asynchronous attempt itself failed (unresolvable host name, …) — which, with an unvalidated prefix such as
   `abc.def.ghi`, would throw on a thread-pool thread. Not reproduced here (needs a live run to confirm the exact failure mode);
   the port should validate octets and handle `PingException` explicitly.
4. **Ordering guarantee of `ipBtnDict.Values`.** `Dictionary<int, IPBtnInfo>` is filled 1→254 and never mutated afterwards, so
   enumeration is in practice insertion-ordered, but the export/list order is not contractually sorted. A port should sort
   explicitly by octet.
5. **`GetAllAdapterIP` failure modes.** `IPInterfaceProperties.GetIPv4Properties()` is called unconditionally and throws for
   adapters without IPv4 (TAP/VPN/loopback-ish). Not observed; the user-visible effect would be an unhandled exception during
   load (blank global handler). Needs a live check on a machine with such adapters.
6. **ARP substring match.** `line.Contains(ipAddress)` can match a *different* host whose IP contains the queried string
   (`192.168.1.5` vs `192.168.1.50`). Whether this ever produced wrong MACs in practice is unknown; the port should match the
   first column exactly.
7. **`arp -d *` policy.** The original flushes the whole neighbour cache on the first ARP query and then hourly. On modern
   Windows this can briefly disrupt other applications’ connections; decide whether to keep it (needed for freshness) or switch
   to per-entry `arp -d <ip>`.
8. **Tray icon text.** The literal `notifyIcon1` tooltip is clearly unintentional; the port should use the product name.
   Confirm this is an accepted (visible) deviation.
9. **Monitor semantics.** The baseline is user-driven and never auto-refreshed, so 上线 hosts are reported repeatedly until
   基准监测 is clicked. Confirm whether the port should keep that (faithful) or auto-rebaseline after each notification.
10. **Unpin path.** There is no UI to remove a pinned `固定快捷` item other than editing `IPScaner.cfg`. The port likely needs an
    explicit 取消固定 affordance — a deliberate addition.
11. **Window/geometry fidelity.** The designer’s `AutoScaleDimensions = (11,21)` means the literal pixel sizes (1531x850 form,
    39x24 blocks) do not correspond to a 100 %-scale machine; only the relative layout (toolbar row → title row → block grid,
    status bar with 7 cells) should be treated as the specification.
