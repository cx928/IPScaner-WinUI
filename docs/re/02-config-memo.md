# 02 — Configuration screen (`FormConfig`) and Memo system (备注)

Reverse-engineered specification for the WinUI 3 rebuild of **IPScaner V1.28.2** (assembly version `1.28.2.0`,
`src/Properties/AssemblyInfo.cs:17`). Everything below is taken from the decompiled sources; `file:line` references
point at the decompiled originals under `ipscaner-re/src/`.

Primary sources:

| Concern | File |
| --- | --- |
| Options dialog | `ipscaner-re/src/IPScaner/FormConfig.cs` (877 lines) |
| Config model / XML | `ipscaner-re/src/IPScaner/ConfigInfo.cs` (215 lines) |
| Config persistence, memo file I/O, menu wiring | `ipscaner-re/src/IPScaner/FormMain.cs` |
| Memo editor dialog | `ipscaner-re/src/IPScaner/FormIPMemo.cs` (193 lines) |
| Memo manager dialog | `ipscaner-re/src/IPScaner/FormMemoManager.cs` (345 lines) |
| Memo lookup precedence | `ipscaner-re/src/IPScaner/Global.cs` (25 lines) |
| Memo row model | `ipscaner-re/src/IPScaner/NetworkInfo.cs` (51 lines) |
| Enum used by the double-click setting | `ipscaner-re/src/IPScaner/EventName.cs` |

Exact string literals are quoted verbatim (including the ideographic full stop `。`, the full-width comma `，` and
the full-width colon `：`). Strings that carry a trailing space are called out explicitly.

---

## 1. Runtime facts that both features depend on

| Fact | Value | Evidence |
| --- | --- | --- |
| Config file path | `Path.Combine(Application.StartupPath, "IPScaner.cfg")` — i.e. next to the EXE, **not** `%APPDATA%` | `FormMain.cs:31` |
| Config file format | `XmlSerializer` XML, root element `root`, all settings as **XML attributes**, no namespace (`XmlSerializerNamespaces.Add("", "")`) | `ConfigInfo.cs:6`, `XmlUtility.cs:8-15` |
| Config is written | Only when `FormConfig.ShowDialog()` returns `DialogResult.OK` (`FormMain.cs:1269`, `:1285`), plus when the user sets a 固定快捷 menu target (`FormMain.cs:364`). **Not** on application exit. | |
| Config is read | `FormMain_Load` → `LoadConfig()` → `File.Exists(CfgPath)` check → `XmlUtility.ReadXml<ConfigInfo>` → `UpdateControlByCfgInfo()` (`FormMain.cs:288`, `:450-458`). No try/catch. | |
| In-memory config singleton | `Global.ConfigInfo` (`static` property, initialised to `new ConfigInfo()`) | `Global.cs:9` |
| Memo file path | `Path.Combine(Application.StartupPath, "IPScanerMemo.dat")` | `FormMain.cs:29` |
| Legacy memo file | `IPScaner.dat` in the same folder is `File.Move`d to `IPScanerMemo.dat` on startup (best-effort, exceptions swallowed) | `FormMain.cs:474-484` |
| Memo in-memory store | `Global.MemoDict` — `Dictionary<string,string>`, **case-sensitive ordinal** key comparison, key is either an IP or a MAC | `Global.cs:7` |
| Memo file encoding | Written as UTF-8 **with BOM** (`File.WriteAllText(..., Encoding.UTF8)`); read with a BOM/heuristic detector that can return UTF-8, Unicode (UTF-16 LE/BE) or GBK | `FormMain.cs:521`, `Utility.cs:389-416` |
| Memo file read timing | Once, at startup (`FormMain_Load` line 300), after `LoadConfig()` | `FormMain.cs:299-300` |
| Memo file write timing | After a successful memo edit from the tray/right-click menu (`FormMain.cs:1154`) and after the memo manager window closes (`FormMain.cs:1261`) | |
| Process privilege | The app relaunches itself elevated (`runas`) if not already administrator (`Program.cs:19-30`). Both files therefore live in a directory the process can write. | |

### 1.1 `IPScaner.cfg` — concrete serialized shape

Property declaration order determines attribute order. `[XmlIgnore]` members (`DefaultColor`, `NetworkOKColor`,
`NetworkNGColor`, `MemoColor`, `DesktopForeColor`, `DesktopBgColor`) are never persisted — only their `…Argb` int
twins are.

```xml
<?xml version="1.0" encoding="utf-8"?>
<root Version="1.0" QueryHostNameEnabled="false" PingTimeout="500" PingCount="4"
      ARPInsteadPingEnabled="false" PortInsteadPingEnabled="false" PrePortArray="80,135,445,500"
      PortTimeout="50" DoubleClickTime="200" BtnFontSize="9" MenuAutoOpen="false"
      DefaultColorArgb="-7876885" NetworkOKColorArgb="-13447886" NetworkNGColorArgb="-3318692"
      MemoColorArgb="-16776961" DoubleEvent="Ping" DesktopOverlayEnabled="false"
      DesktopOverlayPre="本地IP地址：" DesktopOverlayOffsetX="100" DesktopOverlayOffsetY="100"
      DesktopOverlayOpacity="70" DesktopOverlayLocation="2" DesktopForeColorArgb="-16777216"
      DesktopBgColorArgb="-256" LogEnabled="false" StarMenu="mnuCommon.mnuLocalIP"
      HideMainEnabled="false" />
```

Notes for the port:

* `bool` attributes serialize as lowercase `true` / `false`.
* `DoubleEvent` (enum `EventName`) serializes as the **member name**: `Ping`, `ViewWeb`, `Tracert`, `Telnet`,
  `Netstat`, `ARP`, `Share` (`EventName.cs:3-12`). An unknown string in a hand-edited file makes
  `XmlSerializer.Deserialize` throw `InvalidOperationException`, which propagates out of `LoadConfig()`
  (no try/catch) — see §9.
* Missing attributes keep the constructor defaults, so a partial/older file degrades gracefully
  (e.g. a file without `MemoColorArgb` yields `Color.Blue`).
* `Version` is a legacy attribute, hard-coded to `"1.0"` and never updated (`ConfigInfo.cs:188`).
* `StarMenu` stores a dot-joined menu path built from `Control.Name` values
  (`FormMain.GetMenuPath`, `FormMain.cs:325-334`, e.g. `mnuCommon.mnuLocalIP`).

---

## 2. `ConfigInfo` — full property table

`ConfigInfo.cs:7-215`. "Default" = value assigned in the parameterless constructor (`ConfigInfo.cs:186-214`).
"UI" = the `FormConfig` control that edits it (`n/a` = no UI, value only survives round-trips).

| # | Property (XML attribute name is identical) | Type | Default | UI control | Consumed by |
| --- | --- | --- | --- | --- | --- |
| 1 | `Version` | string | `"1.0"` | n/a | nothing |
| 2 | `QueryHostNameEnabled` | bool | `false` | `cmbQueryHostName` (启用/禁用) | `FormMain` (status link, ARP/hostname thread, `UpdateTooltip`), `FormIPSegment` (column visibility, hostname resolution) |
| 3 | `PingTimeout` | int | `500` | `txtPingTimeout` | `Ping.SendAsync(..., PingTimeout, ...)` in `FormMain`/`FormIPSegment`; tooltip says “Ping时长超过此值，会认为网络异常” |
| 4 | `PingCount` | int | `4` | `txtPingCount` | `ping {ip} -n {PingCount}` in `FormMain.cs:701`, `:1108` |
| 5 | `ARPInsteadPingEnabled` | bool | `false` | `chkARPScanEnabled` | `FormMain.cs:918-924`: on Ping failure, ARP-table lookup decides 正常/不通 |
| 6 | `PortInsteadPingEnabled` | bool | `false` | `chkPortScanEnabled` | `FormMain.cs:925-975`: on Ping failure, TCP port probe decides 正常/不通 |
| 7 | `PrePortArray` | string | `"80,135,445,500"` | `txtTcpPorts` | `Utility.TcpPortTestMuli(ip, PrePortArray, …)` → `int.Parse` per token |
| 8 | `PortTimeout` | int | `50` | `txtPortTimeout` | `Utility.TcpPortTest` wait time (`Utility.cs:163`), `FormPortScan` label 显示 |
| 9 | `DoubleClickTime` | int | `200` | `txtDoubleTime` | `clickTimer.Interval` (`FormMain.cs:622`, `:1295`) |
| 10 | `BtnFontSize` | int | `9` | `cmbFontSize` (index = value − 8) | `FormMain.BtnFontSize/BtnHeight/BtnWidth` (`FormMain.cs:1307-1319`) |
| 11 | `MenuAutoOpen` | bool | `false` | `cmbMenuAutoOpen` (是/否) | `ToolStripMenuItem_MouseEnter` → `ShowDropDown()` (`FormMain.cs:1223-1230`) |
| 12 | `DefaultColorArgb` | int | `Color.SkyBlue.ToArgb()` = **−7876885** | `pnlDefaultColor` | background of every small IP block (初始色块颜色) |
| 13 | `NetworkOKColorArgb` | int | `Color.LimeGreen.ToArgb()` = **−13447886** | `pnlOKColor` | block background for 正常; also `btnOK` legend, `FormIPDetail` cell colour |
| 14 | `NetworkNGColorArgb` | int | `Color.IndianRed.ToArgb()` = **−3318692** | `pnlNGColor` | block background for 不通; also `btnNG` legend, `FormIPDetail` cell colour |
| 15 | `MemoColorArgb` | int | `Color.Blue.ToArgb()` = **−16776961** | `pnlMemoColor` | foreground (text) colour of an IP block that has a memo (`FormMain.cs:794`) |
| 16 | `DoubleEvent` | `EventName` | `EventName.Ping` | `cmbDoubleEvent` | action performed on double-click of a block (`FormMain.cs:698-721`) |
| 17 | `DesktopOverlayEnabled` | bool | `false` | `chkDesktopOver` | show/hide `DesktopOverlayForm` |
| 18 | `DesktopOverlayPre` | string | `"本地IP地址："` (full-width colon U+FF1A) | `txtDesktopPre` | prefix text of the desktop overlay |
| 19 | `DesktopOverlayOffsetX` | int | `100` | `txtDesktopOffsetX` | overlay position |
| 20 | `DesktopOverlayOffsetY` | int | `100` | `txtDesktopOffsetY` | overlay position |
| 21 | `DesktopOverlayOpacity` | int | `70` | `txtDesktopOpacity` | `Opacity = value / 100.0` (`DesktopOverlayForm.cs:37`) |
| 22 | `DesktopOverlayLocation` | int | `2` | `cmbDesktopLocation` | 0=左上角 1=右上角 2=右下角 3=左下角 (`DesktopOverlayForm.cs:46-64`) |
| 23 | `DesktopForeColorArgb` | int | `Color.Black.ToArgb()` = **−16777216** | `pnlDesktopForecolor` | overlay label foreground |
| 24 | `DesktopBgColorArgb` | int | `Color.Yellow.ToArgb()` = **−256** | `pnlDesktopBackground` | overlay label background |
| 25 | `LogEnabled` | bool | `false` | `chkLogEnabled` | `LogTool` writes into `Logs` under the program directory (`LogTool.cs:53`) |
| 26 | `StarMenu` | string | `null` | n/a (set from the 固定快捷 right-click menu) | `LoadStarMnu()` |
| 27 | `HideMainEnabled` | bool | `false` | `chkHideMain` | minimise-to-tray (`FormMain.cs:403`) |

Read-only computed colour properties (`[XmlIgnore]`): each does
`try { return Color.FromArgb(<int>); } catch { return <fallback>; }`. The fallbacks are `SkyBlue`, `LimeGreen`,
`IndianRed`, `Blue`, `Black`, `Yellow` respectively. **`Color.FromArgb(int)` never throws for any `int32`** (verified:
`FromArgb(-7876885)` → `ff87ceeb` SkyBlue, `FromArgb(0)` → A=0/RGB=0, `FromArgb(-2147483648)` → A=128), so those
`catch` blocks are dead code — a WinUI port can treat the `…Argb` int as authoritative and never needs a fallback
path for range reasons (only for "attribute absent").

---

## 3. Configuration screen — `FormConfig`

### 3.1 Window / chrome

| Aspect | Value | Evidence |
| --- | --- | --- |
| Title (`Text`) | `选项配置` | `FormConfig.cs:860` |
| Client size | 491 × 616 (designer units, `AutoScaleMode.Font`, `AutoScaleDimensions = 11 × 21`) | `:846-849` |
| Border | `FormBorderStyle.FixedSingle`, `MaximizeBox = false`, `MinimizeBox = false` | `:853-857` |
| Start position | `FormStartPosition.CenterScreen` | `:859` |
| Accept / Cancel | `AcceptButton = btnSave` (Enter saves), `CancelButton = btnClose` (Esc closes) | `:845`, `:848` |
| Icon | application icon from resources | `:854` |
| Default tab | `tabControl1.SelectedIndex = 0` → 外观 | `:681` |
| Opened from | menu `选项配置(&O)` (`FormMain.cs:1958`) and the status-bar link `statusHostName` (tooltip `点击可打开【选项】`) | `FormMain.cs:1264-1273`, `:1280-1289` |

Layout: one `TabControl` (`tabControl1`, 436 × 506 at (22, 21)) holding three tabs, plus two buttons at the bottom
(`btnSave` 138 × 40 at (72, 537) with text `保存`, `btnClose` 138 × 40 at (257, 537) with text `关闭`).
All labels are 宋体 10 pt unless noted. Left column ≈ x=31…70 (labels), right column ≈ x=222…248 (editors).

### 3.2 Tab `外观` (`tabPage1`, index 0)

| Control | Type | Chinese label / text | Binds to | Designer details |
| --- | --- | --- | --- | --- |
| `label5` | Label | `小色块字号` | — | (57, 26) |
| `cmbFontSize` | ComboBox, `DropDownStyle = DropDownList`, items `小`,`中`,`大`,`超大`,`特大` | — | `BtnFontSize` = `SelectedIndex + 8` | (222, 24), 134 × 29, TabIndex 1 |
| `label6` | Label, 宋体 9 pt, `ForeColor = SystemColors.Highlight` | `注：变更小色块字号需要重启` | — | (31, 426) |
| `label7` | Label, `ForeColor = SystemColors.ControlText` | `初始色块颜色` | — | (31, 79) |
| `pnlDefaultColor` | Panel swatch, `BorderStyle.FixedSingle`, `Cursor.Hand` | — | `DefaultColorArgb` | (222, 77), 31 × 32, TabIndex 10 |
| `label8` | Label, `ForeColor = Color.LimeGreen` | `通讯正常颜色` | — | (31, 131) |
| `pnlOKColor` | Panel swatch | — | `NetworkOKColorArgb` | (222, 130), 31 × 32 |
| `label9` | Label, `ForeColor = Color.IndianRed` | `通讯异常颜色` | — | (31, 184) |
| `pnlNGColor` | Panel swatch | — | `NetworkNGColorArgb` | (222, 180), 31 × 32 |
| `label11` | Label | `备注色块颜色` | — | (31, 236) |
| `pnlMemoColor` | Panel swatch | — | `MemoColorArgb` | (222, 231), 31 × 32 |
| `label12` | Label | `小色块双击事件` | — | (6, 289) |
| `cmbDoubleEvent` | ComboBox, `DropDownList`, items `Ping`,`浏览网页`,`Tracert`,`Telnet`,`Netstat`,`ARP`,`共享目录` | — | `DoubleEvent` = `(EventName)SelectedIndex` | (222, 284), 134 × 29, TabIndex 10, tooltip `用右键菜单中的某一项代替双击事件` |
| `label10` | Label | `自动弹出菜单` | — | (31, 334) |
| `cmbMenuAutoOpen` | ComboBox, `DropDownList`, items `是`,`否` | — | `MenuAutoOpen` = `Text == "是"` | (222, 329), 134 × 29, tooltip `鼠标划过菜单时，是否自动弹出菜单` |
| `chkHideMain` | CheckBox | `最小化时隐藏到托盘` | `HideMainEnabled` | (35, 381), 252 × 28, tooltip identical to the label |

The four swatch panels are plain 31 × 32 panels with no text; they render the currently selected colour and open the
colour dialog on click (§3.6). The label colours (LimeGreen/IndianRed) are **static decoration**, not bound to the
configured colours.

Enum index mapping for `cmbDoubleEvent` (`EventName.cs`): 0 `Ping`, 1 `ViewWeb` (label `浏览网页`),
2 `Tracert`, 3 `Telnet`, 4 `Netstat`, 5 `ARP`, 6 `Share` (label `共享目录`).

### 3.3 Tab `参数` (`tabPage2`, index 1)

| Control | Type | Chinese label / text | Binds to | Designer details |
| --- | --- | --- | --- | --- |
| `label1` | Label | `启用主机名查询` | — | (31, 21) |
| `cmbQueryHostName` | ComboBox, `DropDownList`, items `启用`,`禁用` | — | `QueryHostNameEnabled` = `Text == "启用"` | (248, 19), 134 × 29, TabIndex 0 |
| `label2` | Label | `Ping超时毫秒` | — | (57, 74) |
| `txtPingTimeout` | NumericUpDown | — | `PingTimeout` | (248, 70), 138 × 31, Min **10**, Max **5000**, Increment **100**, designer `Value` 100, tooltip `Ping时长超过此值，会认为网络异常` |
| `label4` | Label | `Ping请求次数` | — | (57, 126) |
| `txtPingCount` | NumericUpDown | — | `PingCount` | (248, 124), 138 × 31, Min **1**, Max **100** (WinForms default — never set explicitly), Increment 1 (default), designer `Value` 4, tooltip `一次Ping发送的包数量` |
| `label3` | Label | `双击间隔毫秒` | — | (57, 180) |
| `txtDoubleTime` | NumericUpDown | — | `DoubleClickTime` | (248, 177), 138 × 31, Min **100**, Max **500**, Increment **50**, designer `Value` 200 |
| `chkARPScanEnabled` | CheckBox | `启用Ping失败时检测ARP表` | `ARPInsteadPingEnabled` | (64, 221), 312 × 28, tooltip `Ping失败时检查ARP路由表，判断设备是否在线` |
| `chkPortScanEnabled` | CheckBox | `启用Ping失败时侦测端口` | `PortInsteadPingEnabled` (+ enables `txtTcpPorts`) | (62, 272), 300 × 28, tooltip `Ping失败时侦测TCP端口，判断设备是否在线` |
| `label13` | Label | `TCP侦测端口` | — | (70, 319), tooltip `Ping失败时，可通过此端口判断设备状态。多个端口之间用逗号分隔` |
| `txtTcpPorts` | TextBox, `Enabled = false` in the designer | — | `PrePortArray` | (248, 314), 134 × 31, TabIndex 13, tooltip `Ping失败时，可通过此端口判断设备状态`, `Leave` handler |
| `label14` | Label | `TCP超时毫秒` | — | (57, 366) |
| `txtPortTimeout` | NumericUpDown | — | `PortTimeout` | (248, 361), 138 × 31, Min **10**, Max **2000**, Increment **50**, designer `Value` 100, tooltip `Ping时长超过此值，会认为网络异常` (copy/paste of the Ping tooltip) |
| `chkLogEnabled` | CheckBox, `ForeColor = Color.DodgerBlue` | `启用调试日志` | `LogEnabled` | (62, 408), 180 × 28, tooltip `当前程序目录下生成Logs目录` |

`chkPortScanEnabled.CheckedChanged` (`FormConfig.cs:225-235`) mirrors the checkbox into the text box:
`txtTcpPorts.Enabled = chkPortScanEnabled.Checked;` and, when it becomes checked, `txtTcpPorts.Focus()`.
Because the constructor assigns `chkPortScanEnabled.Checked` (`:144`), a saved `PortInsteadPingEnabled = true`
results in the text box being enabled (and focused) as soon as the dialog is constructed.

### 3.4 Tab `桌面` (`tabPage3`, index 2)

| Control | Type | Chinese label / text | Binds to | Designer details |
| --- | --- | --- | --- | --- |
| `chkDesktopOver` | CheckBox | `启用桌面显示本机IP标签` | `DesktopOverlayEnabled` | (42, 28), 300 × 28 |
| `label15` | Label | `显示位置` | — | (37, 79) |
| `cmbDesktopLocation` | ComboBox, `DropDownList`, items `左上角`,`右上角`,`右下角`,`左下角` | — | `DesktopOverlayLocation` = `SelectedIndex` | (169, 77), 173 × 29, TabIndex 15 |
| `label18` | Label | `偏移量x,y` | — | (37, 131) |
| `txtDesktopOffsetX` | NumericUpDown | — | `DesktopOverlayOffsetX` | (169, 130), 82 × 31, Min **−500**, Max **5000**, Increment **10**, designer `Value` 100, tooltip `距离横向边缘的偏移量` |
| `txtDesktopOffsetY` | NumericUpDown | — | `DesktopOverlayOffsetY` | (262, 130), 82 × 31, same range, tooltip `距离纵向边缘的偏移量` |
| `label20` | Label | `透明度` | — | (37, 178) |
| `txtDesktopOpacity` | NumericUpDown | — | `DesktopOverlayOpacity` | (169, 182), 176 × 31, Min **0**, Max **100** (WinForms defaults), Increment **10**, designer `Value` 70, tooltip `窗体透明度，0完全透明，100不透明` |
| `label16` | Label | `IP前缀` | — | (37, 231), tooltip `Ping失败时，可通过此端口判断设备状态。多个端口之间用逗号分隔` (copy/paste bug — the tooltip is the TCP one) |
| `txtDesktopPre` | TextBox, designer `Text = "本地IP地址为"` (overwritten at runtime by `DesktopOverlayPre`) | — | `DesktopOverlayPre` | (169, 229), 173 × 31, TabIndex 22, tooltip `例如：本地IP地址为` |
| `label19` | Label, `ForeColor = Color.Black` | `文字颜色` | — | (37, 285) |
| `pnlDesktopForecolor` | Panel swatch | — | `DesktopForeColorArgb` | (174, 285), 31 × 32 |
| `label21` | Label, `ForeColor = Color.Black` | `背景色` | — | (246, 289) |
| `pnlDesktopBackground` | Panel swatch | — | `DesktopBgColorArgb` | (345, 285), 31 × 32 |

### 3.5 Load mapping (constructor, `FormConfig.cs:116-158`)

| Order | Statement | Notes |
| --- | --- | --- |
| 1 | `cmbQueryHostName.SelectedIndex = QueryHostNameEnabled ? 0 : 1` | 0 = 启用 |
| 2 | `cmbFontSize.SelectedIndex = BtnFontSize - 8` | **throws** `ArgumentOutOfRangeException` unless the stored value is in \[7, 12\] (valid indices −1…4) |
| 3 | `txtPingTimeout.Value = PingTimeout` | **throws** unless 10 ≤ value ≤ 5000 |
| 4 | `txtPingCount.Value = PingCount` | **throws** unless 1 ≤ value ≤ 100 |
| 5 | `txtDoubleTime.Value = DoubleClickTime` | **throws** unless 100 ≤ value ≤ 500 |
| 6 | `cmbMenuAutoOpen.SelectedIndex = MenuAutoOpen ? 0 : 1` | 0 = 是 |
| 7 | `pnlDefaultColor.BackColor = DefaultColor` | `Color.FromArgb(DefaultColorArgb)` |
| 8 | `pnlOKColor.BackColor = NetworkOKColor` | |
| 9 | `pnlNGColor.BackColor = NetworkNGColor` | |
| 10 | `pnlMemoColor.BackColor = MemoColor` | |
| 11 | `cmbDoubleEvent.SelectedIndex = (int)DoubleEvent` | **throws** unless 0 ≤ value ≤ 6 |
| 12 | `chkPortScanEnabled.Checked = PortInsteadPingEnabled` | fires `CheckedChanged` → enables/focuses `txtTcpPorts` |
| 13 | `chkARPScanEnabled.Checked = ARPInsteadPingEnabled` | |
| 14 | `txtTcpPorts.Text = PrePortArray` | raw string, no normalisation on load |
| 15 | `txtPortTimeout.Value = PortTimeout` | **throws** unless 10 ≤ value ≤ 2000 |
| 16 | `chkDesktopOver.Checked = DesktopOverlayEnabled` | |
| 17 | `txtDesktopOffsetX.Value = DesktopOverlayOffsetX` | **throws** unless −500 ≤ value ≤ 5000 |
| 18 | `txtDesktopOffsetY.Value = DesktopOverlayOffsetY` | same range |
| 19 | `cmbDesktopLocation.SelectedIndex = DesktopOverlayLocation` | **throws** unless −1 ≤ value ≤ 3 |
| 20 | `txtDesktopPre.Text = DesktopOverlayPre` | |
| 21 | `txtDesktopOpacity.Value = DesktopOverlayOpacity` | **throws** unless 0 ≤ value ≤ 100 |
| 22 | `pnlDesktopForecolor.BackColor = DesktopForeColor` | |
| 23 | `pnlDesktopBackground.BackColor = DesktopBgColor` | |
| 24 | `chkLogEnabled.Checked = LogEnabled` | |
| 25 | `chkHideMain.Checked = HideMainEnabled` | |

Verified WinForms semantics that the above depends on (measured against .NET, same as .NET Framework):

* `NumericUpDown.Value = <out of [Minimum, Maximum]>` **throws `ArgumentOutOfRangeException`**
  (`"'99999' is not a valid value for 'Value'. 'Value' should be between 'Minimum' and 'Maximum'."`).
  It does **not** clamp.
* Typing into a `NumericUpDown` and leaving the field **does** clamp silently (`99999` → `5000`, `3` → `10`,
  `-5` → `10`); non-numeric or empty text reverts to the current value; grouping separators are accepted
  (`1,234` → `1234`).
* `ComboBox.SelectedIndex = i` throws unless `-1 <= i < Items.Count`.

⇒ A hand-edited `IPScaner.cfg` with any out-of-range numeric value makes `new FormConfig()` throw. The callers
(`mnuOptionSet_Click`, `statusHostName_Click`) have no try/catch, and `Program.cs:37-39` installs an **empty**
`Application.ThreadException` handler, so the observable symptom is that the options dialog simply never appears.
**The WinUI port must clamp (or reject with a message) instead of throwing.**

### 3.6 Save mapping (`btnSave_Click`, `FormConfig.cs:165-212`)

Executed top-to-bottom; the first block has already mutated `Global.ConfigInfo` before the only validation check runs.

| Order | Statement |
| --- | --- |
| 1 | `ConfigInfo.PingTimeout = (int)txtPingTimeout.Value` |
| 2 | `ConfigInfo.PingCount = (int)txtPingCount.Value` |
| 3 | `ConfigInfo.QueryHostNameEnabled = (cmbQueryHostName.Text == "启用")` |
| 4 | `ConfigInfo.DoubleClickTime = (int)txtDoubleTime.Value` |
| 5 | `ConfigInfo.BtnFontSize = cmbFontSize.SelectedIndex + 8` |
| 6 | `ConfigInfo.MenuAutoOpen = (cmbMenuAutoOpen.Text == "是")` |
| 7 | `ConfigInfo.DefaultColorArgb = pnlDefaultColor.BackColor.ToArgb()` |
| 8 | `ConfigInfo.NetworkOKColorArgb = pnlOKColor.BackColor.ToArgb()` |
| 9 | `ConfigInfo.NetworkNGColorArgb = pnlNGColor.BackColor.ToArgb()` |
| 10 | `ConfigInfo.MemoColorArgb = pnlMemoColor.BackColor.ToArgb()` |
| 11 | `ConfigInfo.DoubleEvent = (EventName)cmbDoubleEvent.SelectedIndex` |
| 12 | **validation:** `if (chkPortScanEnabled.Checked && txtTcpPorts.Text == string.Empty)` → `MessageBox.Show("请输入要侦测的TCP端口", Text, OK, Exclamation)` then `return` (dialog stays open, `DialogResult` remains `None`, nothing is persisted to disk — but items 1-11 are already mutated in memory) |
| 13 | `ConfigInfo.PortInsteadPingEnabled = chkPortScanEnabled.Checked` |
| 14 | `ConfigInfo.ARPInsteadPingEnabled = chkARPScanEnabled.Checked` |
| 15 | `ConfigInfo.PrePortArray = txtTcpPorts.Text` (raw, post-`Leave`-normalised text) |
| 16 | `ConfigInfo.PortTimeout = (int)txtPortTimeout.Value` |
| 17 | `ConfigInfo.DesktopOverlayEnabled = chkDesktopOver.Checked` |
| 18 | `ConfigInfo.DesktopOverlayOffsetX = (int)txtDesktopOffsetX.Value` |
| 19 | `ConfigInfo.DesktopOverlayOffsetY = (int)txtDesktopOffsetY.Value` |
| 20 | `ConfigInfo.DesktopOverlayLocation = cmbDesktopLocation.SelectedIndex` |
| 21 | `ConfigInfo.DesktopOverlayPre = txtDesktopPre.Text` |
| 22 | `ConfigInfo.LogEnabled = chkLogEnabled.Checked` |
| 23 | `ConfigInfo.DesktopOverlayOpacity = (int)txtDesktopOpacity.Value` |
| 24 | `ConfigInfo.DesktopBgColorArgb = pnlDesktopBackground.BackColor.ToArgb()` |
| 25 | `ConfigInfo.DesktopForeColorArgb = pnlDesktopForecolor.BackColor.ToArgb()` |
| 26 | `ConfigInfo.HideMainEnabled = chkHideMain.Checked` |
| 27 | `DialogResult = DialogResult.OK` |

`btnClose_Click` sets `DialogResult = DialogResult.Cancel` (`:160-163`); the X button of a modal form also returns
`Cancel`. Closing with Cancel/OK/Esc never writes the file — the caller does that only for `OK`
(`FormMain.cs:1266-1272`, `:1282-1288`), and the caller's post-save work is:
`XmlUtility.SaveXml(Global.ConfigInfo, CfgPath)` → `UpdateControlByCfgInfo()` → `DislayDesktopOverlay()`.

Side effects of `UpdateControlByCfgInfo()` (`FormMain.cs:1291-1323`) that a rebuild must keep:

* `clickTimer.Interval = DoubleClickTime`.
* Status-bar link text becomes `已启用主机名查询` (link colour Blue) or `已禁用主机名查询` (link colour Red).
* If `BtnFontSize` differs from the live value: `num = |BtnFontSize − 8|`, `BtnFontSize = 8 + num`,
  `BtnHeight = 22 + num * 2`, `BtnWidth = 35 + num * 4`, then a prompt
  `字号已变更，是否立即重启程序？` (Yes/No) → Yes relaunches the EXE from `Application.StartupPath` and exits.
* Legend buttons: `btnNG.BackColor = NetworkNGColor`, `btnOK.BackColor = NetworkOKColor`.
* Note: 初始色块颜色 / 备注色块颜色 are **not** applied retroactively — they take effect on the next scan
  (`FormMain.cs:788-799`).

### 3.7 TCP port validation (唯一 input validation on this form)

Two separate code paths:

1. **`txtTcpPorts_Leave`** (`FormConfig.cs:237-256`) — runs when the box loses focus (which happens before
   `btnSave.Click` when the user clicks 保存):

   ```
   text = text.Trim().Replace("，", ",")          // full-width comma U+FF0C → ASCII comma
   tokens = text.Split(',', RemoveEmptyEntries)   // empty tokens dropped (trailing comma OK)
   if tokens.Length == 0 → return (no error)
   for each token:
       if !int.TryParse(token) || value <= 0 || value > 65535:
           MessageBox.Show("[" + token + "] 输入错误，请输入正确的端口号", Text /* 选项配置 */, OK, Hand)
           txtTcpPorts.Focus(); txtTcpPorts.SelectionLength = 0; break
   ```

   Consequences: the normalised text is **kept** (the invalid content is *not* reverted or removed); only the
   **first** offending token is reported; only the ASCII/full-width comma is treated as a separator (spaces are not
   split, but `int.TryParse` tolerates surrounding whitespace, so `80, 135` is accepted); the valid range is
   **1 … 65535** inclusive.

2. **`btnSave_Click` step 12** — only the *empty string* case is rejected
   (`请输入要侦测的TCP端口`). Invalid-but-non-empty content such as `abc` or `99999` **is saved** to
   `PrePortArray`; the scanner thread later calls `int.Parse` on each token (`Utility.TcpPortTestMuli`,
   `Utility.cs:140-156`), which would throw. A rebuild should validate on save (recommended) or reproduce this
   quirk deliberately.

Suggested canonical form for `PrePortArray`: ASCII comma separated, no spaces, e.g. `80,135,445,500`.
`FormPortScan` only displays `TCP超时毫秒：{PortTimeout}` (`FormPortScan.cs:125`) — the port list itself is not
shown there.

### 3.8 Colour pickers

All six swatch panels share one handler, `pnlDefaultColor_Click` (`FormConfig.cs:214-223`):

```csharp
Panel panel = sender as Panel;
using ColorDialog colorDialog = new ColorDialog();
colorDialog.Color = panel.BackColor;          // seed with the current colour
if (colorDialog.ShowDialog() == DialogResult.OK)
    panel.BackColor = colorDialog.Color;      // mutate the swatch only
```

* Nothing is written to `Global.ConfigInfo` until 保存 is pressed — a cancelled options dialog discards the picks.
* `ColorDialog` is used with its defaults (`FullOpen = false`, `AnyColor = true`, `SolidColorOnly = false`,
  `ShowHelp = false`), so it opens in the collapsed "basic colours" state; the user must click
  `规定自定义颜色` to reach the RGB/HSL editor.
* The dialog always yields **A = 255**; alpha is therefore never user-editable, even though the stored value is a
  full ARGB int.
* Swatch panels carry no accessibility text; the sibling Label is the only visible caption.

### 3.9 ARGB integer convention (critical for file compatibility)

`Color.ToArgb()` returns a **signed** `int32` with `A` in the high byte:
`unchecked((int)(0xFF000000 | (R << 16) | (G << 8) | B))`. `ConfigInfo` stores exactly that value, and
`Color.FromArgb(int)` (verified) round-trips it unchanged for every `int32`.

| Colour | Hex | Stored int |
| --- | --- | --- |
| `Color.SkyBlue` (default 初始色块颜色) | `#FF87CEEB` | **−7876885** |
| `Color.LimeGreen` (default 通讯正常颜色) | `#FF32CD32` | **−13447886** |
| `Color.IndianRed` (default 通讯异常颜色) | `#FFCD5C5C` | **−3318692** |
| `Color.Blue` (default 备注色块颜色) | `#FF0000FF` | **−16776961** |
| `Color.Black` (default 桌面文字颜色) | `#FF000000` | **−16777216** |
| `Color.Yellow` (default 桌面背景色) | `#FFFFFF00` | **−256** |
| `Color.White` (designer seed for every swatch panel) | `#FFFFFFFF` | −1 |
| `Color.CornflowerBlue` (仅 导入剪贴板数据 button background) | `#FF6495ED` | −10185235 (not persisted) |

WinUI mapping: keep the `int` in the model; convert with
`Windows.UI.Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v)` and back with
`unchecked((int)((uint)a << 24 | (uint)r << 16 | (uint)g << 8 | b))`. There is no WinUI `ColorDialog`; a custom
flyout/`ColorPicker` is required, and it must not silently produce a non-opaque colour if you want to stay
byte-compatible with the existing `.cfg` files.

---

## 4. Memo (备注) system

### 4.1 Data model

```csharp
// Global.cs:7
public static Dictionary<string, string> MemoDict = new Dictionary<string, string>();   // key = IP or MAC, value = memo text

// NetworkInfo.cs:3-19 (the row type used by the manager window)
class NetworkInfo { string IP; string HostName; string Status; bool IsOnline; bool IsOffline;
                    bool IsPinged; string MAC; string Memo; }
```

* The dictionary is the **single source of truth**; the manager window works on a `List<NetworkInfo>` snapshot that
  is re-flattened into the dictionary when the window closes (§4.7).
* Key = a string that is *either* an IPv4 dotted quad *or* a MAC string. There is **no** separate key-type field and
  **no** normalisation on lookup: keys are compared with the dictionary's default ordinal comparer, so
  `AA-BB-CC-DD-EE-FF` and `aa-bb-cc-dd-ee-ff` are different keys, as are `1.2.3.4` and ` 1.2.3.4`.
* Value = free-form multi-line text. Trailing whitespace is stripped by the editor (`TrimEnd`), leading whitespace is
  kept. Newlines are stored as the literal token `^v^` in the file (§4.2).

### 4.2 Storage: `IPScanerMemo.dat`

**Load** — `FormMain.LoadMemoInfo()` (`FormMain.cs:472-505`):

1. If `<exeDir>\IPScaner.dat` exists, try `File.Move` it to `IPScanerMemo.dat` (exceptions ignored) — legacy
   migration from an older release.
2. If `IPScanerMemo.dat` does not exist → return (empty dictionary, no error).
3. Detect encoding with `Utility.GetFileEncoding`: UTF-8 BOM (`EF BB BF`) → UTF-8; `FF FE` or `FE FF` → Unicode;
   otherwise any byte in 128…247 → `Encoding.GetEncoding("GBK")`; else `Encoding.Default`.
   **Porting note:** in .NET 5+ `Encoding.GetEncoding("GBK")` throws `ArgumentException` unless
   `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` is called first.
4. Read all lines, then for each line:
   * skip `null`/empty lines and lines whose **first character** is `#` (comment) — the test is on the raw,
     untrimmed line, so `" # x"` is *not* a comment;
   * `parts = line.Split('=')`; if `parts.Length >= 2`:
     `key = parts[0]` (**not trimmed**), `value = string.Join("=", parts.Skip(1)).Replace("^v^", Environment.NewLine)`;
   * `MemoDict[key] = value` (overwrites on duplicate keys; not trimmed).
5. The dictionary is **not** cleared first; loading is additive.

**Save** — `FormMain.SaveMemoInfo()` (`FormMain.cs:507-522`):

1. `if (MemoDict.Count == 0) return;` — **the file is left untouched**, so deleting every memo in the manager and
   closing it silently resurrects the old memos on the next start.
2. For each entry with a non-empty value: `AppendLine(key + "=" + value.Replace("\r\n", "^v^"))`.
   Only the CRLF sequence is escaped; a lone `\n` or `\r` inside a memo is written literally and would corrupt the
   line-oriented format on reload.
3. `File.WriteAllText(path, sb.ToString(), Encoding.UTF8)` → UTF-8 **with BOM**, CRLF line endings, trailing newline.

**Canonical file content**

```
# 备注文件，IP或MAC=备注内容
192.168.0.1=监控服务器
AA-BB-CC-DD-EE-FF=三楼机房交换机^v^负责人：张三
```

**Round-trip cautions:** keys are written verbatim, values are never trimmed on load; a value that legitimately
contains the text `^v^` is corrupted on the next load.

### 4.3 Key precedence — “若同时填写了IP地址和MAC地址，则优先使用MAC地址”

Implemented once, in `Global.GetMemoByMacOrIp(string mac, string ipAddress = "")` (`Global.cs:13-24`):

```csharp
if (!string.IsNullOrEmpty(mac) && MemoDict.ContainsKey(mac)) return MemoDict[mac];   // MAC wins
if (!string.IsNullOrEmpty(ipAddress) && MemoDict.ContainsKey(ipAddress)) return MemoDict[ipAddress];
return string.Empty;
```

* A lookup with an empty `mac` (or a MAC that has no entry) falls through to the IP.
* Both keys can exist for the same device with **different** memo texts; the MAC one always wins wherever this
  helper is used.
* The editor writes **both** keys when both fields are filled (§4.4/§5.1), so the two entries normally hold the same
  text — but nothing enforces that, and an import/edit can make them diverge.

Call-site fidelity matrix (this is where the stated rule is only partially honoured):

| Call site | Arguments | Effective rule |
| --- | --- | --- |
| `FormMain.UpdateTooltip` (`:1177`) | `(ipBtnDict[key].MAC, ipAddress)` | MAC first ✔ |
| `FormMain` ARP/hostname worker (`:1010`) | `(macAddressFromARP, text)` | MAC first ✔ |
| `FormMain.GetAllComps` (`:1360`, used by 列表展示/导出) | `(value.MAC, text)` | MAC first ✔ |
| `PingReplyInfo.Memo` (`PingReplyInfo.cs:21`, used by IP批量扫描 grid) | `(MAC, IP)` | MAC first ✔ |
| `FormMain.Btn_Click` (`:682`) | `(text)` — only the IP | **IP only** |
| `FormMain.btnStart_Click` (`:790`) | `(text)` — only the IP | **IP only** (this is what colours the blocks) |
| `FormMain.btnLegend_Click` (`:1051`) | `(text3)` — only the IP | **IP only** |
| `FormMain.mnuCommand_Click` memo item (`:1141`) | `(ipAddressByMnuCmd)` — only the IP | **IP only** (pre-fill of the editor) |

No MAC normalisation is performed anywhere: `Utility.GetMacAddressFromARP` returns `ToUpper()` of the token found in
`arp -a` (Windows prints hyphens, e.g. `AA-BB-CC-DD-EE-FF`) or, for a local adapter IP,
`adapterInfo.MAC.Replace(":", "-")` (also uppercase); adapter MACs built by `GetAllAdapterIP` use
colon-separated uppercase hex (`AA:BB:CC:DD:EE:FF`). Both shapes can coexist in the memo file, so a rebuild should
normalise MAC keys (e.g. uppercase, `-`/`:` unified) or at least preserve whatever the ARP layer produces.

### 4.4 Memo editor — `FormIPMemo`

| Aspect | Value | Evidence |
| --- | --- | --- |
| Title | `"备注 "` (base, note the trailing space) + the IP appended by the 4-arg constructor → `备注 192.168.0.1`; the parameterless constructor leaves the title as `备注 ` | `FormIPMemo.cs:44`, `:189` |
| Client size / border | 317 × 235 (scaled units, `AutoScaleDimensions = 6 × 12`, `AutoScaleMode.Font`), `FixedSingle`, no maximise/minimise, `CenterScreen` | `:169-188` |
| Cancel button | `btnClose` (`DialogResult.Cancel`) | `:171` |

| Control | Type | Chinese text | Meaning | Designer details |
| --- | --- | --- | --- | --- |
| `label2` | Label 宋体 10 pt | `IP地址：` | — | (15, 9) |
| `txtIP` | TextBox | — | IP key; prefilled with the block's IP | (89, 6), 187 × 21, TabIndex 0 |
| `label3` | Label 宋体 10 pt | `MAC地址：` | — | (15, 39) |
| `txtMAC` | TextBox | — | MAC key; prefilled from the ARP value of the block (empty when called from 新增) | (89, 36), 187 × 21, TabIndex 1 |
| `label1` | Label 宋体 10 pt | `备注内容：` | — | (15, 74) |
| `txtMemo` | TextBox, `Multiline = true`, 宋体 10 pt, **no** scrollbars, no `AcceptsReturn` change | memo text | (89, 70), 189 × 88, TabIndex 2 |
| `btnSave` | Button | `保存` | OK path | (89, 171), 70 × 24, TabIndex 3 |
| `btnClose` | Button | `关闭` | Cancel | (206, 171), 70 × 24, TabIndex 4 |
| `label4` | Label 宋体 9 pt, `ForeColor = SystemColors.Highlight`, two lines | `IP或MAC只填一个即可。` CRLF `若同时存在，以MAC为主` | hint | (87, 202) |

Public results: `public string IP { get; private set; }`, `public string MAC { get; private set; }`,
`public string Memo { get; private set; }` (`:30-34`) — all `null` until a successful save.

Constructor semantics (`:41-57`):

* `Text += ip` (title), `txtIP.Text = ip`, `txtMAC.Text = mac`, then `txtIP.Select(0, 0)` / `txtMAC.Select(0, 0)`
  (caret at position 0, no selection).
* `txtMemo.Text = memo` when `memo` is non-empty, **otherwise `txtMemo.Text = hostName`** — i.e. the 4th
  constructor argument is a *fallback pre-fill* used as the memo text when no memo exists yet.

Save semantics (`btnSave_Click`, `:64-80`), in order:

1. `if (string.IsNullOrEmpty(txtIP.Text) && string.IsNullOrEmpty(txtMAC.Text))` →
   `MessageBox.Show("IP地址或MAC地址不能同时为空", Text /* 备注 <ip> */, OK, Exclamation)`, stay open.
   (This test is on the **untrimmed** text, so a whitespace-only IP passes it.)
2. `if (txtIP.Text.Trim() != string.Empty && !Utility.ValidIP(txtIP.Text))` →
   `MessageBox.Show("请填写正确的IP地址", Text, OK, Exclamation)`, stay open.
3. `IP = txtIP.Text.Trim(); MAC = txtMAC.Text.Trim(); Memo = txtMemo.Text.TrimEnd();`
   `DialogResult = DialogResult.OK;`

Validation details worth reproducing (or deliberately fixing):

* `Utility.ValidIP` is `new Regex("(^\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})")` and is **not end-anchored**
  (`Utility.cs:182-186`): `10.0.0.1` ✔, `999.999.999.999` ✔ (accepted!), `10.0.0.1abc` ✔, `10.0.0.1.5` ✔,
  ` 10.0.0.1` ✘ (leading space → the regex is applied to the untrimmed text even though the emptiness test trims),
  `abc` ✘, `10.0.0` ✘.
* A whitespace-only IP field passes validation but yields `IP = ""`; callers guard with
  `!string.IsNullOrEmpty(...)` so nothing is stored for it.
* **MAC is never validated** — any string is accepted as a MAC key.
* `Memo` is `TrimEnd()`-ed only: leading spaces/newlines survive, internal newlines survive, a memo consisting purely
  of whitespace becomes `""` (an empty memo is therefore indistinguishable from "no memo"; `GetMemoByMacOrIp` returns
  the empty string and the UI treats it as absent).
* The memo text box has no length limit and no newline normalisation; CRLF is what WinForms inserts for Enter.

### 4.5 Memo manager — `FormMemoManager`

| Aspect | Value | Evidence |
| --- | --- | --- |
| Title | `备注信息管理` | `FormMemoManager.cs:338` |
| Client size | 1085 × 691, `Padding = 18` on all sides, `AutoScaleDimensions = 11 × 21`, `CenterScreen` | `:330-337` |
| Entry point | main menu `备注管理(&M)` (`FormMain.cs:1952`) → `ShowDialog()` | `FormMain.cs:1253-1262` |
| Constructor | `FormMemoManager()` (wires `Load`), and `FormMemoManager(List<NetworkInfo> list)` which stores the **same list instance** in `public List<NetworkInfo> MemoList` | `:42-52` |

**Top group box** `groupBox2`, `Text = "查询条件"`, `Dock = Top`, height 79:

| Control | Type | Chinese text | Behaviour | Details |
| --- | --- | --- | --- | --- |
| `txtFilter` | TextBox | (no label; tooltip `查询IP或备注信息`) | filter input | (11, 32), 195 × 31, TabIndex 0 |
| `btnQuery` | Button | `查询` | apply filter | (220, 28), 138 × 40, TabIndex 1 |
| `btnNew` | Button | `新增` | create memo | (368, 28), 138 × 40, TabIndex 2 |
| `btnEdit` | Button | `修改` | edit selected row | (517, 28), 138 × 40, TabIndex 3 |
| `btnDel` | Button | `删除` | delete selected row | (666, 28), 138 × 40, TabIndex 4 |
| `btnImport` | Button, `BackColor = CornflowerBlue`, `ForeColor = White`, `UseVisualStyleBackColor = false` | `导入剪贴板数据` | clipboard import (§4.6) | (814, 28), 224 × 40, TabIndex 5, tooltip `导入剪贴板数据，格式为192.168.0.1=监控服务器` |

**Bottom group box** `groupBox1`, `Text = "数据列表"`, `Dock = Fill`, containing `dgvMemo` (`Dock = Fill`):

* `ReadOnly = true`, `AllowUserToAddRows = false`, `AllowUserToDeleteRows = false`,
  `AllowUserToOrderColumns = true`, `MultiSelect = false`, `SelectionMode = FullRowSelect`,
  `BackgroundColor = SystemColors.Control`, `RowHeadersWidth = 60`, `ColumnHeadersHeightSizeMode = AutoSize`,
  `RowTemplate.Height = 25`, row/cell font 宋体 9 pt (`:283-307`).
* `AutoGenerateColumns = false` is set in the `Load` handler, together with
  `DataError += DgvService_DataError` (**empty handler**), `RowStateChanged += DgvService_RowStateChanged`,
  `CellDoubleClick += DgvChzWord_CellDoubleClick`, then `DataSource = MemoList; Refresh();` (`:54-62`).

| Column | `DataPropertyName` | `HeaderText` | Width | Sort mode |
| --- | --- | --- | --- | --- |
| `Column1` | `IP` | `IP地址` | 125 (`MinimumWidth = 9`) | `NotSortable` |
| `Column2` | `MAC` | `MAC地址` | 175 (`MinimumWidth = 9`) | not set → default `Automatic` (the only sortable column) |
| `Column4` | `Memo` | `备注信息` | `AutoSizeMode = Fill`, `MinimumWidth = 100` | `NotSortable` |

Row-header / zebra-striping behaviour (`DgvService_RowStateChanged`, `:74-84`): every row's header cell text is the
**1-based index** (`e.Row.Index + 1`), and rows where `(index + 1) % 2 == 0` get
`DefaultCellStyle.BackColor = Color.LightBlue`. Double-clicking any cell performs `btnEdit.PerformClick()` (`:64-68`).

**查询 (`btnQuery_Click`, `:86-97`)**

* Empty filter → `DataSource = null; DataSource = MemoList;` (full list restored).
* Otherwise `MemoList.Where(r => r.IP.Contains(txtFilter.Text) || r.Memo.Contains(txtFilter.Text)).ToList()`.
  * `MAC` is **not** searched.
  * Matching is ordinal/case-sensitive `string.Contains` (no `StringComparison`, no trim, no wildcards, no regex).
  * The filtered result is a **new list of the same row objects**, so subsequent 修改/删除 still mutate
    `MemoList` correctly while a filter is active.
  * A row with a `null` `Memo` would throw `NullReferenceException` here (not reachable through the app's own paths).

**新增 (`btnNew_Click`, `:99-108`)**

```csharp
var f = new FormIPMemo();                                  // completely empty dialog
if (f.ShowDialog() == DialogResult.OK) {
    MemoList.Add(new NetworkInfo(f.IP, f.MAC, f.Memo));    // IP, MAC, Memo
    dgvMemo.DataSource = null; dgvMemo.DataSource = MemoList;   // filter is dropped
}
```

No duplicate-key check, no IP/MAC format check beyond the editor's own rules.

**修改 (`btnEdit_Click`, `:110-122`)**

```csharp
if (dgvMemo.CurrentRow != null) {
    NetworkInfo ni = dgvMemo.CurrentRow.DataBoundItem as NetworkInfo;
    var f = new FormIPMemo(ni.IP, ni.MAC, ni.Memo, ni.Memo);   // 4th arg = Memo (hostname fallback)
    if (f.ShowDialog() == DialogResult.OK) {
        ni.Memo = f.Memo;          // ONLY the memo is written back
        dgvMemo.Refresh();
    }
}
```

**Quirk to decide on:** changes the user makes to the IP or MAC fields inside the 修改 dialog are **discarded** —
only `Memo` is copied back. (The fields are editable, and there is no indication that they are ignored.)

**删除 (`btnDel_Click`, `:124-136`)**

```csharp
if (dgvMemo.CurrentRow != null) {
    NetworkInfo ni = dgvMemo.CurrentRow.DataBoundItem as NetworkInfo;
    if (MessageBox.Show("确定要删除【" + ni.IP + "】?", Text /* 备注信息管理 */,
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) {
        MemoList.Remove(ni);
        dgvMemo.DataSource = null; dgvMemo.DataSource = MemoList;
    }
}
```

The confirmation text interpolates **`ni.IP`** even when the row is MAC-only (it will read `确定要删除【】?`).

**Window lifetime:** the manager has no OK/Cancel buttons; `ShowDialog()`'s result is ignored by the caller, so
closing the window with X (or Enter/Esc) applies every change made in the list.

### 4.6 从剪贴板导入 (`btnImport_Click`) — exact parsing rules

Source: `FormMemoManager.cs:138-176`.

```
1  text = Clipboard.GetText()
2  if (string.IsNullOrEmpty(text)) return;                       // silent no-op, no message box
3  lines = text.Split(new[] { "\r", "\n" }, RemoveEmptyEntries)  // split on CR *and* LF separately
4  newRows = new List<NetworkInfo>()
5  for each line:
6      if (line.Trim() == "") continue
7      parts = line.Split(new[] { "=" }, RemoveEmptyEntries)
8      if (parts.Length < 2) continue
9      ip   = parts[0].Trim()
10     memo = string.Join("=", parts.Skip(1)).Trim()
11     if (MemoList.All(r => r.IP != ip))                       // skip if the IP already exists
12         newRows.Add(new NetworkInfo(ip, memo))               // 2-arg ctor → MAC stays null
13 if (newRows.Count > 0):
14     MemoList.AddRange(newRows)
15     dgvMemo.DataSource = null; dgvMemo.DataSource = MemoList
16     MessageBox.Show($"成功导入{list.Count}条备注信息", Text, OK, Asterisk)
17 else:
18     MessageBox.Show("未识别到有效的IP备注信息。格式参照如下：\r\nIP在前，备注信息在后，例如：\r\n192.168.0.1 = 监控服务器",
                      Text, OK, Asterisk)
```

Accepted formats and edge cases:

| Clipboard text | Result |
| --- | --- |
| `192.168.0.1 = 监控服务器` | row `IP=192.168.0.1`, `Memo=监控服务器` (both sides trimmed) |
| `192.168.0.1=监控服务器` | same — spaces around `=` are optional |
| `192.168.0.1=核心交换机=机房A` | `Memo` = `核心交换机=机房A` (only the **first** `=` splits; the rest are re-joined) |
| `192.168.0.1` (no `=`) | ignored (line contributes nothing) |
| `192.168.0.1=` | **ignored** — `RemoveEmptyEntries` drops the empty right-hand part, so `parts.Length == 1` |
| `=备注` | ignored for the same reason |
| `abc=备注` | **accepted** — the IP is not validated; the row is added with `IP = "abc"` |
| `# 注释` | not treated as a comment in the import path (only the file loader honours `#`) and is ignored anyway because there is no `=` |
| CRLF, LF or CR line endings | all accepted (`"\r"` and `"\n"` are separate split tokens) |
| Leading/trailing blank lines | removed by `RemoveEmptyEntries` + the `Trim()` guard |
| Tab-separated `IP<TAB>memo` | **not** accepted (only `=` is a separator) |
| Existing IP (already in `MemoList`, including rows added earlier in the same import) | **skipped, not updated** — deduplication uses the full `MemoList`, not the filtered view |
| Same IP twice in the clipboard | the first occurrence wins |
| MAC-keyed rows from the clipboard | impossible — the import always creates IP-keyed rows (`MAC = null`) |
| Import while a 查询 filter is active | allowed; the grid is rebound to the full list afterwards |

Success message is interpolated with the **number of newly added rows**:
`成功导入{N}条备注信息`. Failure message (verbatim, CRLF between the three lines):

```
未识别到有效的IP备注信息。格式参照如下：
IP在前，备注信息在后，例如：
192.168.0.1 = 监控服务器
```

Note the asymmetry with the file loader: the import trims the IP, the loader does not; the import rejects empty
right-hand sides, the loader accepts them (`a=` → memo `""`).

### 4.7 Flattening back into `Global.MemoDict`

`mnuMemoManage_Click` (`FormMain.cs:1253-1262`), executed when the manager window closes (any close reason):

```csharp
List<NetworkInfo> list = Global.MemoDict.Select(r => new NetworkInfo(r.Key, r.Value)).ToList();
var mgr = new FormMemoManager(list);
mgr.ShowDialog();
Global.MemoDict = mgr.MemoList.Where(r => !string.IsNullOrEmpty(r.IP))
                              .ToDictionary(r => r.IP, q => q.Memo);
Dictionary<string,string> second = mgr.MemoList.Where(r => !string.IsNullOrEmpty(r.MAC))
                                               .ToDictionary(r => r.MAC, q => q.Memo);
Global.MemoDict = Global.MemoDict.Union(second).ToDictionary(r => r.Key, q => q.Value);
SaveMemoInfo();
```

Semantics to reproduce/decide:

* A single row with both IP and MAC produces **two** dictionary entries with the same memo text (this is how a
  "device memo" survives an IP change).
* Rows with an empty IP *and* an empty MAC are dropped. Rows with an empty `Memo` are kept as entries with an empty
  value — but `SaveMemoInfo` then skips writing them, so they silently disappear from the file on the next save.
* `Enumerable.Union` compares whole `KeyValuePair<string,string>` values (key **and** text). Because the second
  dictionary is built independently, an IP key that also exists as a MAC key with a *different* text survives both
  entries and then makes the final `ToDictionary` throw
  `ArgumentException: An item with the same key has already been added` (a latent crash). The first `ToDictionary`
  throws the same way if two rows share an IP, and the second if two rows share a MAC.
* Because `MemoList` rows are keyed nowhere, the **file's grouping is lost**: on the next start every entry is loaded
  as a separate `NetworkInfo(r.Key, r.Value)` row, so a device that had both keys reappears as two rows.
* `MemoList` ordering follows `MemoDict` enumeration order (insertion order in practice) plus appended rows.

### 4.8 Where memos surface in the UI (behaviour the rebuild must keep)

| Surface | Behaviour | Evidence |
| --- | --- | --- |
| IP block tooltip | lines in this order: `名称：{host}` (only when known), `IP：{ip}`, `Mac：{mac}` (only when known), `备注：{memo}` | `FormMain.cs:1165-1183`, `:800`, `:687`, `:1003-1017` |
| IP block text colour | at the start of a scan, blocks whose IP has a memo get `ForeColor = ConfigInfo.MemoColor`, others `Color.Black` | `FormMain.cs:788-799` |
| Legend copy to clipboard | `IP段: {part}, 状态【{不通|正常}】地址共有{n}个` + per line `{ip}  {memo}` (normal) or `{ip}  {host} {mac} {memo}` (OK); memo CRLFs are stripped with `Replace("\r\n", "")` | `FormMain.cs:1030-1064` |
| 列表展示 dialog (`FormIPDetail`) | column `备注` (`DataPropertyName = "Memo"`), rows coloured by `NetworkOKColor`/`NetworkNGColor`; data comes from `GetAllComps()` → `GetMemoByMacOrIp(MAC, IP)` with `\r\n` stripped | `FormMain.cs:1354-1381`, `FormIPDetail.cs:62-66`, `:149` |
| CSV export | `IP扫描结果-yyyyMMddHHmmss.csv` (UTF-8) with header `IP,状态,主机名,MAC,备注`; row = `NetworkInfo.ToString()` → `IP,Status,HostName,MAC,Memo` | `FormMain.cs:1325-1352`, `NetworkInfo.cs:47-50` |
| IP批量扫描 (`FormIPSegment`) | column `备注` bound to the computed `PingReplyInfo.Memo`; export line `{IP},{HostName},{MAC},{Memo},{Status},{Time}` | `FormIPSegment.cs:854-859`, `:416`, `PingReplyInfo.cs:21` |
| Right-click menu item | `备注信息(&M)` on an IP block opens `FormIPMemo(ip, ipBtnDict[key].MAC, memo, ipBtnDict[key].HostName)`; on OK it writes `MemoDict[IP]` and (when non-empty) `MemoDict[MAC]`, calls `SaveMemoInfo()` and `UpdateTooltip(ip)` | `FormMain.cs:1136-1157`, `:2158` |

---

## 5. Inter-form coupling summary

```
FormConfig  --(DialogResult.OK)-->  FormMain: XmlUtility.SaveXml(Global.ConfigInfo, <exe>\IPScaner.cfg)
                                     FormMain.UpdateControlByCfgInfo()  -> clickTimer, status link, font/block metrics,
                                                                           legend colours, (optional relaunch prompt)
                                     FormMain.DislayDesktopOverlay()    -> DesktopOverlayForm
```

| Producer | Consumer | Data |
| --- | --- | --- |
| `FormConfig` | `FormMain` | all `ConfigInfo` fields; re-read on every save |
| `FormConfig` | `DesktopOverlayForm` | `DesktopOverlayEnabled/Pre/OffsetX/OffsetY/Opacity/Location`, `DesktopForeColor`, `DesktopBgColor` (recreated on each save) |
| `FormConfig` | `LogTool` | `LogEnabled` (checked per log write) |
| `FormConfig` | `FormPortScan` | `PortTimeout` (label `TCP超时毫秒：{n}`) |
| `FormConfig` | `FormIPSegment` | `QueryHostNameEnabled` (hides `colHostName`/`colMAC`), `PingTimeout` |
| `FormConfig` | `Utility.TcpPortTest` / `TcpPortTestMuli` | `PortTimeout`, `PrePortArray` |
| `FormMain` | `FormConfig` | seeds every control from `Global.ConfigInfo`; **writes back into `Global.ConfigInfo` on 保存 even if a later validation fails** (§3.6 step 12) |
| `FormMain` | `FormIPMemo` | `(ip, mac, memo, hostName)`; memo pre-filled from `GetMemoByMacOrIp(ip)` (IP-only) |
| `FormIPMemo` | `FormMain` | `IP`, `MAC`, `Memo` read-only properties; `FormMain` writes `MemoDict` and calls `SaveMemoInfo()` |
| `FormMemoManager` | `FormIPMemo` | `新增` → parameterless; `修改` → `(IP, MAC, Memo, Memo)`; writes back only `Memo` |
| `FormMain` | `FormMemoManager` | builds `List<NetworkInfo>` from `MemoDict`; after close re-flattens (`IP` first, then `MAC`, union) and calls `SaveMemoInfo()` |
| `Global.MemoDict` | `FormMain`, `FormIPDetail`, `FormIPSegment` | all memo lookups via `Global.GetMemoByMacOrIp` |
| — | any other form | `FormConfig` is the **only** writer of `IPScaner.cfg`; `FormMain` is the only reader/writer of `IPScanerMemo.dat` |

---

## 6. WinUI 3 rebuild checklist

| WinForms | WinUI 3 | Notes |
| --- | --- | --- |
| `Form` (`FixedSingle`, fixed size, `CenterScreen`) | `Window` with `AppWindow` sizing + a custom title bar, or a `ContentDialog` for a smaller layout | The 3-tab layout (436 × 506) fits a `TabView`/`NavigationView` inside a fixed-size dialog window. |
| `TabControl` + 3 `TabPage` (`外观` / `参数` / `桌面`) | `TabView` (or `Pivot`) with the same three headers | Keep the exact header order; 外观 is the default. |
| `NumericUpDown` (Min/Max/Increment) | `NumberBox` + `SpinButtonPlacementMode="Inline"` | **Clamp explicitly** on load and on save; do not let an out-of-range `IPScaner.cfg` value throw. Replicate the typed-input clamp (out-of-range → nearest bound, non-numeric → revert) and the save-time `(int)Value` cast. |
| `ComboBox` (`DropDownList`) | `ComboBox` with `IsEditable="False"` | Persist by index for `cmbDoubleEvent`/`cmbDesktopLocation`, by string comparison (`启用`, `是`) for the two Yes/No-ish boxes — or normalise both to indices in the new code and migrate on read. |
| `CheckBox` | `CheckBox` | Same captions; `chkPortScanEnabled` must drive `txtTcpPorts.IsEnabled` and focus behaviour. |
| `Panel` swatch + `ColorDialog` | `Button`/`Border` swatch + `ColorPicker` in a `Flyout`, or a custom RGB dialog | Store the signed ARGB int (§3.9) so existing `.cfg` files stay valid. There is no built-in colour dialog in WinUI 3. |
| `TextBox` (memo, multiline) | `TextBox` with `AcceptsReturn="True"`, `TextWrapping="Wrap"` | Preserve `TrimEnd()`-only semantics; there is no built-in scrollbar-free multiline box — match `ScrollViewer` settings as closely as possible. |
| `DataGridView` | `ListView` with a header row (`GridView`-style columns) or `CommunityToolkit.WinUI.Controls.DataGrid` | Required behaviours: read-only, full-row selection, 1-based row-number column, LightBlue striping on even rows (`#ADD8E6` = −5383962), double-click = 修改, IP/MAC/备注 column order and widths 125/175/Fill. |
| `MessageBox.Show(text, Text, buttons, icon)` | `ContentDialog` (must be shown with a `XamlRoot`; only one at a time) | Keep the exact Chinese text and the icon/caption mapping: `Exclamation` for validation, `Hand` for the port error, `Question` for delete, `Asterisk` for import results. |
| `ToolTip` (`toolTip1`) | `ToolTipService.SetToolTip` | Port every tooltip string in §3 (including the two copy/paste mistakes, unless you choose to fix them). |
| `Clipboard.GetText()` | `Clipboard.GetContent().GetTextAsync()` (async!) | The parser in §4.6 is pure string work and ports 1:1. |
| `Application.StartupPath` | `AppContext.BaseDirectory` (unpackaged) / `ApplicationData.Current.LocalFolder` (packaged) | A packaged MSIX cannot write next to the EXE. For byte-compatible migration either ship unpackaged or add a one-time copy of `IPScaner.cfg` / `IPScanerMemo.dat` from the install directory into the writable location. |
| `Encoding.GetEncoding("GBK")` (legacy memo files) | same call **plus** `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` at startup | Without the provider registration the GBK branch throws `ArgumentException` on .NET 5+. |
| 宋体 10 pt / 9 pt | `FontFamily="SimSun"` with 13.33 px / 12 px (1 pt = 1.333 px at 96 DPI) | WinUI font sizes are in effective pixels; the designer values are in points under `AutoScaleMode.Font`. |
| `System.Drawing.Color` | `Windows.UI.Color` / `Microsoft.UI.Colors` | Only the persisted `int` matters for compatibility; use the conversion helper in §3.9. |

Recommended implementation shape: a `ConfigService` that (a) loads/merges `IPScaner.cfg` with per-attribute defaults,
(b) clamps every numeric/selection field instead of throwing, and (c) exposes the same 27 properties; plus a
`MemoStore` that owns `IPScanerMemo.dat`, implements `GetByMacOrIp(mac, ip)` with MAC-first precedence, and keeps the
`^v^` newline escaping so old files keep working.

---

## 7. Acceptance test vectors

Configuration:

1. Fresh install, no `IPScaner.cfg` → `Global.ConfigInfo` equals the `ConfigInfo` constructor defaults; the options
   dialog shows `禁用`, `中`, `100`, `4`, `200`, `否`, `White`-seeded swatches replaced by SkyBlue/LimeGreen/IndianRed/
   Blue, `Ping`/`浏览网页`… default index 0, unchecked port/ARP/log/hide/desktop boxes,
   `IP前缀 = 本地IP地址：`, `透明度 = 70`, `偏移量x,y = 100,100`, `显示位置 = 右下角`.
2. Save immediately, without touching anything → `IPScaner.cfg` is written; every attribute matches the sample in
   §1.1 except `StarMenu` (absent/empty) and the colours must round-trip to the same ints.
3. Enable 启用Ping失败时侦测端口, clear the port box, press 保存 → `MessageBox` `请输入要侦测的TCP端口`, dialog stays
   open, **no** file write; the already-applied fields are visible in memory (e.g. changing 双击间隔 to 300 before
   saving then cancelling leaves `Global.ConfigInfo.DoubleClickTime == 300`).
4. Type `80，135, 445 ,` and leave the box → text becomes `80,135, 445 ,` (full-width comma converted, trimmed);
   no error. Type `80,abc,445` → `[abc] 输入错误，请输入正确的端口号` and focus returns to the box while the text stays.
5. Type `70000` in Ping超时毫秒 and leave → the control clamps to `5000`. Write `PingTimeout="99999"` into the cfg by
   hand → opening the dialog must not crash in the rebuild (the original throws).

Memos:

6. Right-click an online block → 备注信息(&M) → title `备注 192.168.0.7`, MAC pre-filled from ARP, memo box pre-filled
   with the existing memo or the hostname; save → `IPScanerMemo.dat` contains
   `192.168.0.7=<memo>` (CRLF escaped as `^v^`) and the block tooltip shows `备注：<memo>` plus the MEMO colour
   at the next scan.
7. Clear both IP and MAC and save → `IP地址或MAC地址不能同时为空`. Enter `10.0.0` → `请填写正确的IP地址`.
   Enter `10.0.0.1` with an empty memo → an entry with an empty value that is skipped on write.
8. 备注管理 → grid shows one row per dictionary key with 1-based numbering and LightBlue striping on rows 2, 4, …;
   type `监控` into the filter + 查询 → only rows whose IP **or** memo contains `监控` (MAC-only matches are excluded).
9. Clipboard `192.168.0.1 = 监控服务器\n192.168.0.1=重复\n192.168.0.2=核心=机房A\n#x\n` + 导入剪贴板数据 →
   `成功导入2条备注信息`; the list gains `192.168.0.1/监控服务器` and `192.168.0.2/核心=机房A`.
10. Clipboard `hello world` + 导入 → the three-line explanatory message appears.
11. 修改 a row, change IP **and** memo, save → only the memo changed in the grid (IP/MAC edits are discarded —
    fix this deliberately or keep the quirk).
12. Delete every row → close the manager → the file on disk is **unchanged** (stale) because `SaveMemoInfo` returns
    early on an empty dictionary.

---

## 8. Open questions / ambiguities

1. **Out-of-range config values.** The original *throws* `ArgumentOutOfRangeException` from `FormConfig`'s
   constructor when a hand-edited `IPScaner.cfg` holds values outside the designer ranges (and for `BtnFontSize`
   outside \[7, 12\], `DoubleEvent` outside \[0, 6\], `DesktopOverlayLocation` outside \[−1, 3\]). Decide: clamp
   silently (recommended), clamp + toast, or reject the file and fall back to defaults. Same question for an unknown
   `DoubleEvent` string, which makes `XmlSerializer` throw inside `LoadConfig()` (`FormMain_Load`, no try/catch).
2. **Partial save on validation failure.** `btnSave_Click` mutates 11 config fields before the TCP-port check and then
   returns without a `DialogResult`, leaving `Global.ConfigInfo` half-updated in memory (not persisted unless the user
   then saves successfully). Keep the "all-or-nothing" fix in the rebuild, or reproduce the partial mutation?
3. **`txtTcpPorts` invalid-but-non-empty values are persisted** (`abc`, `99999`), which later makes
   `int.Parse` throw in the scanner thread. Should the rebuild validate the whole list at save time (and reject
   `>65535`/non-numeric), given that the Leave handler already reports them but does not block saving?
4. **`修改` in the memo manager ignores IP/MAC edits** (only `Memo` is written back). Intentional or bug?
5. **Duplicate-key crash when flattening**: `mgr.MemoList.Where(MAC non-empty).ToDictionary(r => r.MAC, …)` plus
   `Union` + `ToDictionary` throws if the same key is produced twice with different texts. Fix by merging with a
   defined precedence (e.g. MAC rows override IP rows, later rows win)?
6. **Precedence inconsistency at call sites**: `Btn_Click`, `btnStart_Click`, `btnLegend_Click` and the memo-item
   pre-fill pass only the IP, so a MAC-keyed memo is invisible in the block tooltip, in the block colouring and in the
   legend clipboard export, while `UpdateTooltip` (hover) does find it. Should the rebuild honour "MAC 优先"
   everywhere (recommended) or mirror each call site?
7. **MAC key normalisation**: `arp -a` yields hyphen-separated uppercase; adapter enumeration yields
   colon-separated uppercase (`GetMacAddressFromARP` converts `AA:BB:…` → `AA-BB-…` only for local adapter IPs).
   No case/separator folding exists on lookup. Should the new store fold MAC keys (uppercase + one separator) and
   migrate existing keys?
8. **Stale memo file**: `SaveMemoInfo` returns early when the dictionary is empty, so "delete all memos" does not
   persist, and entries with empty values are dropped from the file even though they exist in memory. Required
   behaviour for the rebuild (delete the file vs. write an empty file)?
9. **Empty vs. whitespace memo**: `Memo = txtMemo.Text.TrimEnd()` makes `"   "` equal to `""`, losing the
   distinction between "no memo" (no colour, no tooltip line) and "memo that is only spaces". Acceptable?
10. **`^v^` escaping**: values containing a literal `^v^` are corrupted, only `\r\n` is escaped (a lone `\n` breaks the
    format). Keep the legacy token for file compatibility, or introduce version-2 escaping with a migration?
11. **`IPScaner.dat` legacy move**: the migration is best-effort with a swallowed exception; no retry and no message.
    Keep it in the new app?
12. **`ColorDialog` real-world alpha**: it always returns A=255, but the model accepts any `int32`, so a hand-edited
    `.cfg` can specify a translucent block colour (e.g. `DefaultColorArgb="0"` → fully transparent). Should the
    WinUI picker expose alpha, clamp it to 255, or keep whatever the file says?
13. **Memo editor validation strictness**: `Utility.ValidIP` is not end-anchored and accepts `999.999.999.999` and
    `10.0.0.1xyz`; the MAC field accepts anything. Tighten in the rebuild (and how to handle already-stored invalid
    keys)?
14. **Memo text length / row height**: the memo cell is `Fill` with `RowTemplate.Height = 25`, so multi-line memos are
    clipped visually but the tooltip and CSV export show the raw text with `\r\n` (CSV strips them, the manager grid
    does not). Confirm the desired WinUI rendering (single line + tooltip vs. wrapped rows).
15. **Colour label copy/paste bugs**: `label16` (IP前缀) carries the TCP-port tooltip and `txtPortTimeout` carries the
    Ping tooltip. Reproduce verbatim for parity or fix?
16. **Where to store the files in WinUI**: packaged MSIX cannot write next to the EXE; migrating `IPScaner.cfg` /
    `IPScanerMemo.dat` into `ApplicationData.Current.LocalFolder` changes the documented layout of this tool.
    Decide before implementing the config/memo services.
