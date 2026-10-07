# IPScaner V1.28.2 — RE Spec 05: 修改本地IP, WiFi password viewer, infrastructure

Source of truth: decompiled C# under `ipscaner-re/src/`.
Files covered: `FormLocalIP.cs` (635), `FormWiFiViewer.cs` (274), `WiFiInfo.cs` (15), `NetAPIUtility.cs` (47),
`AdapterInfo.cs` (29), `AdapterInfoCollection.cs` (9), `NetworkInfo.cs` (38), `Utility.cs` (394), `LogTool.cs` (121),
`XmlUtility.cs` (20), `MemoryCacheManager.cs` (35), `Program.cs` (36), `Global.cs` (20), `EventName.cs` (11),
`DesktopOverlayForm.cs` (107), `AboutBox.cs` (230). Supporting evidence also taken from `ConfigInfo.cs`,
`PingReplyInfo.cs`, `FormMain.cs`, `FormIPSegment.cs`, `FormConfig.cs`, `IPScaner.readme.txt`, `Properties/AssemblyInfo.cs`.

**Target platform facts** (needed before any port):
* `IPScaner V1.28.2.csproj` → `OutputType=WinExe`, `UseWindowsForms=True`, **`TargetFramework=net40`**, references
  `System.Runtime.Caching`, `System.Xml`, `System.Core`, `System.Management`; `ApplicationIcon=app.ico`;
  embedded resource `IPScaner.readme.txt` with `LogicalName=IPScaner.readme.txt`.
* `Properties/AssemblyInfo.cs`: `AssemblyTitle("IPScaner")`, `AssemblyProduct("局域网IP扫描工具")`,
  `AssemblyDescription("局域网IP扫描工具")`, `AssemblyCopyright("Copyright © chenhuxiang@qq.com 2019-2025")`,
  `AssemblyFileVersion("1.28.2")`, `AssemblyVersion("1.28.2.0")`, `Guid("0dc21ab7-14ce-4443-994a-dd85087d7916")`.
* Everything is .NET Framework WinForms; there is **no** IPHelper/WMI-based IP configuration, **no** `wlanapi`
  P/Invoke, and **no** single-instance mutex anywhere (see C.7). The Chinese messages are literal UTF-8 strings.

---

# A. 修改本地IP window (修改本地IP → `FormLocalIP`, title `一键设置IP`)

Entry point: `FormMain` menu `快捷工具(&T)` → item `修改本地IP(&L)` (`FormMain.cs:1911`), handler
`mnuLocalIP_Click` → `new FormLocalIP().Show()` (`FormMain.cs:1072-1075`). **Non-modal, no instance tracking** — the
user can open several copies. `FormWiFiViewer` is launched the same way from `WiFi密码(&W)` (`FormMain.cs:1945`).

## A.1 Complete control inventory (exact Chinese strings)

`Form` (`FormLocalIP.InitializeComponent`, `FormLocalIP.cs:405-683`):

| Property | Value |
|---|---|
| `Text` | `一键设置IP` (`:671`) |
| `ClientSize` | `576, 673` (`:655`) |
| `FormBorderStyle` | `FixedSingle` (`:664`) |
| `MaximizeBox` / `MinimizeBox` | `false` / `false` (`:667-668`) |
| `StartPosition` | `CenterScreen` (`:670`) |
| `AcceptButton` | `btnSetIP` (`:652`) |
| `Icon` | from `FormLocalIP.resx` (`:665`) |
| `AutoScaleMode` / `AutoScaleDimensions` | `Font` / `SizeF(11F, 21F)` (`:653-654`) |
| `Name` | `FormLocalIP` (`:669`) |

> **Scaling note (verify during layout):** all other forms in this app declare `AutoScaleDimensions = (6F, 12F)`
> (e.g. `FormWiFiViewer.cs:277`, `AboutBox.cs:231`), i.e. the 96-dpi designer baseline. `FormLocalIP` declares
> `(11F, 21F)` ≈ the same layout captured at 175 % DPI, so **every coordinate/size below is ≈1.75× the logical
> 96-dpi value** (ClientSize 576×673 ≈ 329×384 logical; `cmbAdapter` 328×29 ≈ 187×17). For the WinUI rebuild it is
> safest to divide positions/sizes by 1.75 and re-lay-out, rather than copy pixels verbatim. Fonts in this form are
> declared as `宋体 11f` for labels/textboxes and `宋体 10f` for radios — the effective sizes are ≈9 pt/8 pt at
> 96 dpi. The raw numbers are given below so the layout can be reproduced either way.

| Control | Type | Text (exact) | Location | Size | Font / colour | Notes |
|---|---|---|---|---|---|---|
| `label1` | Label | `当前IP：` | 57, 38 | autosize (116×26) | 宋体 11 | header row of the form |
| `lblCurrent` | Label | *(empty at design time)* | 224, 38 | autosize | 宋体 11, `ForeColor=Blue` | shows `cmbAdapter.SelectedValue` (the adapter IP); **Click → `RefreshAdapter()`** (re-enumerate adapters) `:289-292` |
| `label2` | Label | `适配器：` | 59, 93 | autosize | 宋体 11 | |
| `cmbAdapter` | ComboBox | *(data-bound)* | 222, 88 | 328×29 | `DropDownList` | `DisplayMember="Name"`, `ValueMember="IP"`; tooltip `切换多个网卡` `:488` |
| `panel1` | Panel | — | 12, 140 | 557×309 | | container for the IP group |
| ├ `rbtnDHCP` | RadioButton | `自动获取 IP地址` | 38, 3 | 215×28 | 宋体 10 | sits **above** `groupBox1` |
| ├ `groupBox1` | GroupBox | *(empty caption)* | 12, 36 | 540×270 | | |
| │ ├ `rbtnIPStatic` | RadioButton | `使用下面的 IP地址` | 26, 0 | 239×28 | 宋体 10 | drawn on the groupbox caption line — visually pairs with `rbtnDHCP` as one radio row |
| │ ├ `label3` | Label | `IP地址：` | 33, 51 | autosize | 宋体 11 | |
| │ ├ `txtSetIP` | TextBox | *(empty)* | 198, 42 | 272×37 | 宋体 11 | TabIndex 0, gets focus on Load |
| │ ├ `label4` | Label | `子网掩码：` | 7, 105 | autosize | 宋体 11 | |
| │ ├ `txtMask` | TextBox | `255.255.255.0` | 198, 94 | 272×37 | 宋体 11 | design-time default text |
| │ ├ `label5` | Label | `网关：` | 62, 160 | autosize | 宋体 11 | |
| │ ├ `txtRoute` | TextBox | *(empty)* | 196, 149 | 274×37 | 宋体 11 | |
| │ ├ `label7` | Label | `历史地址：` | 7, 210 | autosize | 宋体 11, `ForeColor=MediumBlue` | |
| │ └ `cmbHisIp` | ComboBox | *(history IPs)* | 196, 207 | 274×29 | `DropDownList` | tooltip `历史修改记录都在这里` `:544` |
| `panel2` | Panel | — | 10, 455 | 557×138 | | container for the DNS group |
| ├ `rbtnDNS` | RadioButton | `自动获取 DNS服务器地址` | 38, 3 | 299×28 | 宋体 10 | above `groupBox2` |
| ├ `groupBox2` | GroupBox | *(empty caption)* | 12, 36 | 540×93 | | |
| │ ├ `rbtnDNSStatic` | RadioButton | `使用下面的 DNS服务器地址` | 26, 0 | 323×28 | 宋体 10 | on the groupbox caption line |
| │ ├ `label6` | Label | `DNS 服务器：` | 9, 51 | autosize | 宋体 11 | note the space in `DNS 服务器：` |
| │ ├ `txtDNS` | TextBox | *(empty)* | 198, 48 | 242×37 | 宋体 11 | |
| │ └ `cmbDNS` | ComboBox | items `阿里` `腾讯` `百度` `114` | 452, 48 | 84×29 | `DropDownList` | tooltip (copy-paste from `cmbHisIp`) `历史修改记录都在这里` `:535` |
| `btnSetIP` | Button | `保存设置` | 220, 620 | 138×40 | 宋体 11 | also the form's AcceptButton (Enter applies) |
| `btnClose` | Button | `关闭` | 388, 620 | 138×40 | 宋体 11 | `Close()` `:320-323` |
| `toolTip1` | ToolTip | — | | | | only the 3 tooltips listed above |

Design-time defaults applied on load (`FormLocalIP_Load`, `FormLocalIP.cs:78-94`):
`txtSetIP.Focus(); Show(); Application.DoEvents(); rbtnDHCP.Checked = true; rbtnDNS.Checked = true;` then the adapter
event is hooked, `RefreshAdapter()` runs, then the history event is hooked, then
`suggestDNSList = { "223.5.5.5", "119.29.29.29", "180.76.76.76", "114.114.114.114" }` is created and `cmbDNS` gets
`阿里 / 腾讯 / 百度 / 114`. Selecting a provider copies the matching IP into `txtDNS`
(`CmbDNS_SelectedIndexChanged`, `:96-102`).

The window has **no** refresh button, no Apply/OK split, no "打开网络连接" shortcut (that lives in the main menu as
`网络连接(&N)` with `Tag = "control Ncpa.cpl"`, `FormMain.cs:2014`); `RefreshAdapter` is triggered by clicking the
blue `lblCurrent` label.

## A.2 Adapter enumeration and filtering ("增加无线网卡")

`FormLocalIP.RefreshAdapter()` (`:147-159`):

```csharp
Global.AdapterList = Utility.GetAllAdapterIP();
if (Global.AdapterList.Count == 0)
{
    Global.AdapterList = Utility.GetAllAdapterIP();
}
cmbAdapter.DataSource = null;
cmbAdapter.Items.Clear();
cmbAdapter.DisplayMember = "Name";
cmbAdapter.ValueMember = "IP";
cmbAdapter.DataSource = Global.AdapterList;
```

The list is the **shared** `Global.AdapterList` (also used by `FormMain`). Enumeration is
`System.Net.NetworkInformation`, not WMI (`Utility.GetAllAdapterIP()`, `Utility.cs:268-340`):

* `NetworkInterface.GetAllNetworkInterfaces()`.
* **Filter:** keep only `NetworkInterfaceType.Ethernet` **or** `NetworkInterfaceType.Wireless80211` — this is the
  v1.8 feature 增加无线网卡 — and additionally skip any interface whose `Name` contains the literal `"VMware"`. All
  other types (Loopback, Tunnel, PPP…) are dropped.
* Per interface: `Name = networkInterface.Name` (the Windows connection name, e.g. `以太网`, `WLAN` used verbatim in
  netsh commands); `Status = (int)networkInterface.OperationalStatus` (`Up == 1`);
  `MAC = string.Join(":", bytes.Select(b => b.ToString("X2")))` → `AA:BB:CC:DD:EE:FF`.
* Per unicast IPv4 address, skipped when it is APIPA (`169.254.*`), loopback (`127.*`) **or ends with `".0"`** (the
  last rule wrongly hides legitimate `.0` hosts): one `AdapterInfo` row per IP with `IP`, `SubnetMask` (from
  `unicastAddress.IPv4Mask`), `IsDhcpEnabled = iPv4Properties?.IsDhcpEnabled ?? false`, `Gateway` = last IPv4
  gateway in `iPProperties.GatewayAddresses`, `DnsServers` = all IPv4 `iPProperties.DnsAddresses`.
  Every gateway/DNS/adapter read is written to the log (`读取{ip} 网关: {gw}`, `读取{ip} DNS服务器: {dns}`,
  `读取本地网卡信息 {AdapterInfo}`).
* Result is `orderby r.Name, r.Status` and then **de-duplicated by display name**: while a name already exists in the
  output list, the new one becomes `{Name}_{n}` starting at `_1` (`Utility.cs:323-338`). This is what allows a
  single NIC with several IPs to appear as several combo rows.
* Consequence for the UI: the combo shows **one row per (adapter, IP)**; `cmbAdapter.Text` is the *interface name*
  (fed to netsh) while `cmbAdapter.SelectedValue` is the *IP* (shown in blue in `lblCurrent`).
* Wiring on selection (`CmbAdapter_SelectedIndexChanged`, `:104-125`): `rbtnDHCP.Checked = IsDhcpEnabled`,
  `rbtnIPStatic.Checked = !IsDhcpEnabled`, then `txtSetIP = IP`, `txtMask = SubnetMask`, `txtRoute = Gateway`,
  `lblCurrent.Text = cmbAdapter.SelectedValue.ToString()`, and if `DnsServers.Count > 0` → `txtDNS = DnsServers[0]`;
  finally `RefreshHisCmbData()`.
* `Global.AdapterList` assignment means the main window's adapter list is silently refreshed whenever this dialog
  refreshes.

WinUI note: use `Windows.Networking.Connectivity.NetworkInformation.GetNetworkInterfacesAsync()` (or Win32
`GetAdaptersAddresses`), keep the same filter (`Ethernet` + `Wi-Fi`, exclude VMware) and the same one-row-per-IP
projection; the name that netsh needs is the *interface alias* (`NetworkInterfaceInfo.Name`), not the description.

## A.3 Static vs DHCP switching — exact calls

**There is no WMI/netsh API call in managed code for setting addresses.** The only mechanism is spawning `cmd.exe`
and writing a `netsh` command line to its standard input (`FormLocalIP.btnSetIP_Click`, `:174-238`):

```csharp
if (rbtnIPStatic.Checked)
{
    string command = "netsh interface ip set address \"" + cmbAdapter.Text + "\" \"static\" \"" + txtSetIP.Text + "\" \"" + txtMask.Text + "\" \"" + txtRoute.Text + "\"";
    ExceCmd(command);
    SaveHisXml(cmbAdapter.Text);
}
else
{
    string command2 = "netsh interface ip set address \"" + cmbAdapter.Text + "\" \"dhcp\" ";
    ExceCmd(command2);
}
if (rbtnDNSStatic.Checked)
{
    string command3 = "netsh interface ip set dns \"" + cmbAdapter.Text + "\" \"static\" \"" + txtDNS.Text + "\" ";
    ExceCmd(command3);
}
else
{
    string command4 = "netsh interface ip set dns \"" + cmbAdapter.Text + "\" \"dhcp\"";
    ExceCmd(command4);
}
MessageBox.Show("本地IP地址修改成功", Text, MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
RefreshAdapter();
RefreshHisCmbData();
```

Rendered commands (adapter name `以太网`, IP `192.168.1.55`):

| Case | Command line written to cmd.exe stdin |
|---|---|
| static IP | `netsh interface ip set address "以太网" "static" "192.168.1.55" "255.255.255.0" "192.168.1.1"` |
| DHCP IP | `netsh interface ip set address "以太网" "dhcp" ` (trailing space) |
| static DNS | `netsh interface ip set dns "以太网" "static" "223.5.5.5" ` (trailing space) |
| DHCP DNS | `netsh interface ip set dns "以太网" "dhcp"` |

Note the legacy `netsh interface ip …` namespace (not `interface ipv4 …`); notes:
* The IP command and the DNS command are **independent** — a DHCP address with a static DNS is allowed
  (see the radio matrix in A.7), and both branches always run, so one click may issue two netsh commands.
* No IPv6, no secondary/alternate IP, no `add address` (multi-IP is *read*, never *written*).

Command execution helper (`ExceCmd`, `:274-287`) — quoted in full because its fire-and-forget semantics are part of
the behaviour:

```csharp
private void ExceCmd(string command)
{
    using Process process = new Process();
    process.StartInfo.FileName = "cmd.exe";
    process.StartInfo.RedirectStandardInput = true;
    process.StartInfo.UseShellExecute = false;
    process.StartInfo.CreateNoWindow = true;
    process.Start();
    using StreamWriter streamWriter = process.StandardInput;
    if (streamWriter.BaseStream.CanWrite)
    {
        streamWriter.WriteLine(command);
    }
}
```

Consequences to preserve/replace:
* `cmd.exe` is started **without** `/c`; the command text is written as one line to stdin. Disposing the
  `StreamWriter` closes the pipe, cmd executes the line and then exits. `netsh` inherits the hidden console.
* **No `WaitForExit`, no exit-code check, no stdout/stderr capture.** The success MessageBox and `RefreshAdapter()`
  therefore run *before* netsh has applied anything; a wrong adapter name or a rejected value fails silently (netsh
  error text goes to a hidden console that is disposed with the parent).
* The `Process` object is disposed without waiting; the child keeps running detached.

WinUI replacement: use the IP Helper API (`CreateIpForwardEntry` for routes is unrelated) — concretely
`SetIpInterfaceEntry` / `InitializeUnicastIpAddressEntry` + `CreateUnicastIpAddressEntry` + `DeleteUnicastIpAddressEntry`
and `SetInterfaceDnsSettings` (Windows 10 1809+) or `netsh` executed with `Process` **and** awaited
(`RedirectStandardOutput/Error`, `WaitForExit`, non-zero exit ⇒ surface `stderr`). If netsh is kept for fidelity,
keep the exact argument strings above so error text and behaviour match, but report failures.
* Sibling dead code: `Utility.IsDnsAutoConfigured()` (`Utility.cs:342-362`) is the only WMI path in the app
  (`SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = 'TRUE'`, reads `DNSServerSearchOrder`) and is
  **never called**; `Utility.GetComputerManufacturer` (`\\{name}\root\cimv2`, `Win32_ComputerSystem.Manufacturer`,
  `:364-387`) and `Utility.GetWorkgroup()` (`:225-227`, empty body) are also dead.

## A.4 History feature (历史修改记录)

* **Storage:** `ipScaner_his.xml` — `private string ipHisXmlPath = Path.Combine(Application.StartupPath, "ipScaner_his.xml");`
  (`FormLocalIP.cs:15`). Sibling of the exe, **not** in `%APPDATA%`, and a different file from the main `IPScaner.cfg`.
* **What is saved** — `SaveHisXml(string adapterName)` (`:240-263`) is called **only in the static-IP branch**, and
  per the 1.26 changelog ("保存IP、掩码和网关，并增加历史修改记录") stores IP + mask + gateway, plus DNS (added with the
  1.21 DNS feature):
  ```csharp
  AdapterInfo adapterInfo = adapterInfoCollection.AdapterList.FirstOrDefault(r => r.Name == adapterName && r.IP == txtSetIP.Text);
  if (adapterInfo == null)
  {
      adapterInfo = new AdapterInfo { IP = txtSetIP.Text, SubnetMask = txtMask.Text, Gateway = txtRoute.Text, Name = cmbAdapter.Text, DNS = txtDNS.Text };
      adapterInfoCollection.AdapterList.Add(adapterInfo);
  }
  else { adapterInfo.SubnetMask = txtMask.Text; adapterInfo.Gateway = txtRoute.Text; adapterInfo.DNS = txtDNS.Text; }
  XmlUtility.SaveXml(adapterInfoCollection, ipHisXmlPath);
  ```
  Uniqueness key = `(Name, IP)`; re-applying the same IP updates the mask/gateway/DNS in place. `Global.AdapterList`
  entries also carry these fields but are never written here.
* **Persistence format:** UTF-8 (no BOM) XML written by `XmlUtility.SaveXml` — see C.5 for the measured file; every
  record is an attribute-only `<AdapterInfo … />` element. Because `AdapterInfo.Status/MAC/IsDhcpEnabled/DnsServers`
  are `[XmlIgnore]`, only `Name, IP, SubnetMask, Gateway, DNS` are persisted. DHCP-applied adapters leave no trace.
* **Read:** `LoadHisXml()` (`:265-272`) returns a fresh empty `AdapterInfoCollection` when the file is missing,
  otherwise `XmlUtility.ReadXml<AdapterInfoCollection>(ipHisXmlPath)`. A corrupt file throws out of
  `RefreshHisCmbData` and out of the `Load` handler; `Program.Main` installs a no-op `Application.ThreadException`
  handler (`Program.cs:37-39`), so the dialog silently ends up with an empty history combo.
* **Quick switching** (`RefreshHisCmbData` `:127-145` + `cmbHisIp_SelectedIndexChanged` `:379-394`):
  * Items = the history records whose `Name == cmbAdapter.Text` (exact string compare with the interface name),
    displayed as the raw IP; the previously selected index is restored if it is still valid.
  * Picking an entry fills `txtSetIP`, `txtMask`, `txtRoute`, `txtDNS` from the record (`FirstOrDefault(r => r.Name == cmbAdapter.Text && r.IP == text)`).
  * It only *fills the form* — the user must still press `保存设置` to apply. `cmbHisIp` is disabled while
    `自动获取 IP地址` (DHCP) is selected (see A.7), and it is refreshed after every successful save and on every
    adapter change.
  * The label `历史地址：` is drawn in `MediumBlue`; the tooltip repeats `历史修改记录都在这里`.

WinUI note: keep the exact file name/format for fidelity (`ipScaner_his.xml` next to the exe) or migrate to
`ApplicationData.Current.LocalFolder` with a one-time import; keep the `(Name, IP)` uniqueness and the
"fill-only, never auto-apply" interaction.

## A.5 Validation, error handling, required privileges

Validation runs first, in this order (`btnSetIP_Click`, `:174-238`); every failure is a `throw new Exception(...)`
caught by the method's own `catch` and shown with `MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Hand)`
(title = the window title `一键设置IP`):

| Order | Condition | Message (exact) | Extra effect |
|---|---|---|---|
| 1 | static IP and (`txtSetIP` or `txtMask` or `txtRoute` empty) | `请输入完整的IP信息` | — |
| 2 | static IP and `txtDNS` empty | `请输入DNS服务器地址` | — |
| 3 | `!Utility.ValidIP(txtSetIP.Text)` | `请输入正确的IP地址` | `txtSetIP.Focus()` |
| 4 | `!Utility.ValidIP(txtMask.Text)` | `请输入正确的子网掩码` | `txtMask.Focus()` |
| 5 | `!Utility.ValidIP(txtRoute.Text)` | `请输入正确的网关地址` | `txtRoute.Focus()` |
| 6 | `rbtnDNSStatic.Checked && !Utility.ValidIP(txtDNS.Text)` | `请输入正确的DNS地址` | `txtDNS.Focus()` |

`Utility.ValidIP` (`Utility.cs:182-186`) is a *loose* check — `new Regex("(^\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})").IsMatch(text)`:
no `$` anchor, no 0-255 range check, so `999.1.1.1`, `1.2.3.4.5` and `1.2.3.4abc` all pass; empty/whitespace fails.
The WinUI rebuild should keep this permissiveness only if bug-compatibility is wanted; otherwise validate properly
and keep the same messages.

Success path: `MessageBox.Show("本地IP地址修改成功", Text, MessageBoxButtons.OK, MessageBoxIcon.Asterisk)` **always**
fires when no managed exception was thrown — netsh failures are invisible (see A.3). Failures of `Process.Start`
inside `ExceCmd` would propagate into the same catch.

Required privileges: **administrator, enforced by the process itself** (`Program.cs:11-31`):
`WindowsIdentity.GetCurrent()` → `new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator)`;
if true → `Application.Run(new FormMain())`; if false →
`new ProcessStartInfo { FileName = Application.ExecutablePath, Verb = "runas" }` + `Process.Start` + `Application.Exit()`.
There is no `app.manifest` `requestedExecutionLevel` in the decompiled sources, so elevation is purely the
self-relaunch. If the user declines UAC, `Process.Start` throws `Win32Exception` out of `Main` (no try/catch) and the
process dies. netsh `set address/dns` itself requires an elevated token, which the app therefore always has.
(This is also why the WiFi viewer can read cleartext keys — see B.)

WinUI note: a packaged WinUI 3 app cannot self-elevate; either ship unpackaged with `runas`-equivalent
(`ShellExecute` with `runas` verb / a manifest requiring admin) or move IP/DNS configuration into a helper that
requests elevation, and mirror the checked-before-launch behaviour (in that case the UAC-decline path must be
handled gracefully instead of crashing).

## A.6 Input filtering and the auto-gateway helper

* `txtSetIP_TextChanged` (`:161-172`): on **every keystroke**, if the text matches `(^\d{1,3}\.\d{1,3}\.\d{1,3}\.)`,
  `txtRoute.Text` is overwritten with the matched prefix + `"254"`. Typing `192.168.1.55` therefore rewrites the
  gateway to `192.168.1.254` continuously (and trashes a manually typed gateway as soon as the IP starts matching).
* `txtSetIP_KeyDown` (`:294-318`): a whitelist `flag` allows `D0-D9`, `NumPad0-NumPad9`, `OemPeriod`, `Decimal`,
  `Back`, `Delete`, `Left`, `Right`, `Home`, `End`, `Return`; anything else gets `e.SuppressKeyPress = true`.
  Accepted keystrokes are *not* further validated (letters are blocked, but `1...2` is still possible).
* Dead code worth noting for the port: the first branch is
  `if (e.Control && (e.KeyCode == Keys.C || e.KeyCode == Keys.V)) return;` which returns for **both** Ctrl+C and
  Ctrl+V, making the following clipboard-paste validation (`Clipboard.GetText()` scanned for non-digit/non-`.` chars)
  **unreachable**. Same duplicate pattern in `FormIPSegment.txtSetIP_KeyDown` (`FormIPSegment.cs:434-444`).

## A.7 Radio-button state machine (must be reproduced exactly)

`rbtnDHCP`/`rbtnIPStatic` share `rbtnDHCP_CheckedChanged` (`:325-348`); `rbtnDNS`/`rbtnDNSStatic` share
`rbtnDNS_CheckedChanged` (`:350-377`). Both handlers ignore the *uncheck* event (`if (!radioButton.Checked) return;`
— the DHCP handler embeds that in `if (radioButton.Checked)`), because WinForms also raises `CheckedChanged` when a
peer radio unchecks.

```
On adapter change:  rbtnDHCP.Checked := adapter.IsDhcpEnabled ; rbtnIPStatic.Checked := !IsDhcpEnabled

rbtnDHCP_CheckedChanged(sender):
  if sender.Checked:
    if sender == rbtnDHCP:                       # 自动获取 IP地址
        rbtnIPStatic.Checked = false
        txtSetIP.Enabled = txtMask.Enabled = txtRoute.Enabled = cmbHisIp.Enabled = false
    else:                                        # 使用下面的 IP地址
        rbtnDHCP.Checked = false
        txtSetIP.Enabled = txtMask.Enabled = txtRoute.Enabled = cmbHisIp.Enabled = true
        rbtnDNSStatic.Checked = true             # forces DNS to static (which enables txtDNS/cmbDNS)

rbtnDNS_CheckedChanged(sender):
  if sender.Checked:
    if sender == rbtnDNS:                        # 自动获取 DNS服务器地址
        if rbtnIPStatic.Checked: rbtnDNS.Checked = false; rbtnDNSStatic.Checked = true
        else: rbtnDNSStatic.Checked = false; txtDNS.Enabled = false; cmbDNS.Enabled = false
    else:                                        # 使用下面的 DNS服务器地址
        rbtnDNS.Checked = false
        txtDNS.Enabled = true; cmbDNS.Enabled = true
```

Resulting legal combinations: (DHCP IP + auto DNS), (DHCP IP + static DNS), (static IP + static DNS).
"static IP + auto DNS" is impossible — the UI forces DNS static. Initial state at Load is `rbtnDHCP.Checked = true`
then `rbtnDNS.Checked = true`, immediately overwritten by the first adapter selection. `txtDNS`/`cmbDNS` keep
`Enabled = true` from the designer until an adapter is selected.

---

# B. WiFi password viewer (`FormWiFiViewer`, title `WiFi密码查看器`)

## B.1 UI

| Control | Type | Text (exact) | Notes |
|---|---|---|---|
| Form | — | `WiFi密码查看器` (`:285`) | `ClientSize = 545×422`, `AutoScaleDimensions = (6F, 12F)`, `StartPosition = CenterScreen`, icon from resx |
| `gbxResult` | GroupBox | `WiFi列表` | `Dock = Fill`; **disabled** (`Enabled = false`) while loading, re-enabled at the end |
| `dgvWiFi` | DataGridView | — | `Dock = Fill`, `ReadOnly`, `AllowUserToAddRows = false`, `AllowUserToDeleteRows = false`, `AutoGenerateColumns = false`, `RowHeadersWidth = 50`, `RowTemplate.Height = 23`, `BackgroundColor = SystemColors.Control`; header style `宋体 9pt` (`FontStyle.Regular`, `GraphicsUnit.Point`, char set 134) |
| `Column1` | DataGridViewTextBoxColumn | header `WiFi 名称` | `DataPropertyName = "SSID"`, `SortMode = NotSortable`, `Width = 200` |
| `Column2` | DataGridViewTextBoxColumn | header `保存的密码` | `DataPropertyName = "Password"`, `AutoSizeMode = Fill`, `SortMode = NotSortable` |
| `statusStrip1` | StatusStrip | — | `ImageScalingSize = 28×28` |
| `statusTip` | ToolStripStatusLabel | `程序已就绪` (design) | progress text, see B.6 |
| `mnuExportPwd` | ToolStripStatusLabel | `导出保存的密码` | `IsLink = true`, `BorderSides = Left`, `Click → mnuExportPwd_Click` |

Row decoration (`DgvService_RowStateChanged`, `:54-64`): every realized row gets `HeaderCell.Value = (index + 1)`
and even rows get `DefaultCellStyle.BackColor = Color.LightSkyBlue`. `DataError` is swallowed (`:50-52`).

## B.2 Mechanism: `netsh wlan`, driven from a background task

**No `wlanapi.dll` P/Invoke, no XML profile parsing, no DPAPI.** The viewer shells out to `netsh wlan` twice per
profile and scrapes the console text. `ReadWiFiProfile` (`:66-119`) runs inside `Task.Factory.StartNew`:

```csharp
string text = ReadProfiles(Encoding.GetEncoding("gbk")) ?? ReadProfiles(Encoding.UTF8) ?? ReadProfiles(Encoding.Default);
if (text == null)
{
    BeginInvoke((Action)(() => { statusTip.Text = "读取WiFi保存密码失败，可能是未知的编码格式"; }));
    return;
}
MatchCollection matchCollection = Regex.Matches(text, "所有用户配置文件\\s*:\\s*(.+)");
foreach (Match item in matchCollection)
{
    string text2 = item.Groups[1].Value.Trim();
    string text3 = ReadKeyByProfile(text2, Encoding.GetEncoding("gbk")) ?? ReadKeyByProfile(text2, Encoding.UTF8) ?? ReadKeyByProfile(text2, Encoding.Default);
    if (text3 != null)
    {
        Match match2 = Regex.Match(text3, "关键内容\\s*:\\s*(.+)");
        if (match2.Success)
        {
            string password = match2.Groups[1].Value.Trim();
            list.Add(new WiFiInfo(text2, password));
            dgvWiFi.BeginInvoke((Action)(() => { dgvWiFi.DataSource = null; dgvWiFi.DataSource = list; }));
        }
    }
}
```

List step (`ReadProfiles`, `:121-141`):

```csharp
ProcessStartInfo startInfo = new ProcessStartInfo("netsh", "wlan show profiles")
{
    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = encoding
};
... text = streamReader.ReadToEnd();
if (Regex.IsMatch(text, "所有用户配置文件")) return text;
return null;
```

Key step (`ReadKeyByProfile`, `:143-163`):

```csharp
ProcessStartInfo startInfo = new ProcessStartInfo("netsh", "wlan show profile name=\"" + profileName + "\" key=clear")
{
    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = encoding
};
... text = streamReader.ReadToEnd();
if (Regex.IsMatch(text, "关键内容")) return text;
return null;
```

Exact commands on the wire:
* `netsh wlan show profiles`
* `netsh wlan show profile name="<SSID>" key=clear`   (SSID quoted with `"`; no escaping of `"` inside the SSID)

Extraction contract (Chinese-locale Windows only):
* Profile lines: `所有用户配置文件\s*:\s*(.+)` → capture group 1 trimmed = SSID. `Regex.Matches` iterates all
  matches so *multiple wireless interfaces* are handled (the Chinese `netsh` prints one `所有用户配置文件 :` block per
  interface). The list step additionally requires the raw output to contain `所有用户配置文件`.
* Password: `关键内容\s*:\s*(.+)` in the `key=clear` output → trimmed group 1 = **cleartext key**
  (`关键内容` = "Key Content"). Profiles without a key, or open networks, produce no match → no row is added.
* `Encoding.GetEncoding("gbk")` is resolved from the system; on a system where GBK is unavailable this would throw
  inside the task (→ `MessageBox.Show(ex.StackTrace)`), which is a latent bug.
* Aggregation/de-duplication: none — duplicates by SSID are impossible from netsh, and the grid is simply rebound to
  the growing `List<WiFiInfo>` after every profile (progressive rendering, `DataSource = null` then `= list`).

**Localization fragility (reproduced live on this machine):** on an en-US Windows the same commands emit
`User profiles` / `All User Profile     : cx-WIFI` / `Key Content            : …`, so `ReadProfiles` returns `null` for
all three encodings and the UI shows `读取WiFi保存密码失败，可能是未知的编码格式` — nothing is listed. Any faithful
rebuild must decide whether to keep the Chinese-only regexes (bug-compatible) or to add locale-tolerant matching
(e.g. regex on the `:\s*(.+)$` tail plus the English/Chinese header names). Same class of problem for the
"Failed to read" status message.

## B.3 The Windows 11 24H2 workaround (changelog v1.20 item 1)

Changelog: `版本1.20更新说明 1. 修复windows 11(24H2版本)不可查看WIFI密码的问题` (`IPScaner.readme.txt:58`), and the
feature list still advertises `支持windows 11(24H2版本)` (`:11`). There is **no version-conditional code path, no
`Environment.OSVersion` check, no alternate API** anywhere in `FormWiFiViewer.cs`. What the decompiled 1.28.2 code
*does* contain, and what the pre-1.20 code did not need to contain, is the triple-encoding fallback on both netsh
readers plus the encoding-specific error message:

```csharp
ReadProfiles(Encoding.GetEncoding("gbk")) ?? ReadProfiles(Encoding.UTF8) ?? ReadProfiles(Encoding.Default)
ReadKeyByProfile(text2, Encoding.GetEncoding("gbk")) ?? ReadKeyByProfile(text2, Encoding.UTF8) ?? ReadKeyByProfile(text2, Encoding.Default)
...
statusTip.Text = "读取WiFi保存密码失败，可能是未知的编码格式";   // "may be an unknown encoding format"
```

and the fact that console decoding is forced per attempt via `StandardOutputEncoding = encoding` (without it,
`Process.StandardOutput` uses `Console.OutputEncoding`, i.e. the OEM code page).

Inference (flagged in Open questions): on 24H2 the WLAN/console output for Chinese Windows began arriving as UTF-8
rather than GBK/CP936, so a single fixed `StandardOutputEncoding` produced mojibake in which `所有用户配置文件` /
`关键内容` no longer matched and no password was shown; 1.20 fixed it by trying GBK → UTF-8 → `Encoding.Default`
(and by reporting an encoding-specific error when all three fail). Behaviourally the "24H2 fix" is therefore
*"retry the netsh scrape with several output encodings and require a marker string to accept the result"* — not a new
API. A robust rebuild can drop the guessing entirely: run netsh with `StandardOutputEncoding = Encoding.UTF8`
(24H2+ behaviour) and fall back to `Encoding.GetEncoding(936)`, or better, read profiles through
`WlanGetProfile`/`WlanGetProfileList` (wlanapi) and parse the profile XML `<keyMaterial>` after `WlanGetProfile` with
`WLAN_PROFILE_GET_PLAINTEXT_KEY`, which is locale-independent and returns the same cleartext key.

## B.4 Export to Excel and copy behaviour

`mnuExportPwd_Click` (`:165-192`):

```csharp
if (dgvWiFi.DataSource == null) return;
List<WiFiInfo> list = dgvWiFi.DataSource as List<WiFiInfo>;
StringBuilder stringBuilder = new StringBuilder();
stringBuilder.AppendLine("WiFi 名称  ,保存的密码   ");
foreach (WiFiInfo item in list)
{
    stringBuilder.AppendLine(item.SSID + "," + item.Password);
}
string text = Path.Combine(Application.StartupPath, "WiFi密码.csv");
File.WriteAllText(text, stringBuilder.ToString());
if (!File.Exists(text) || MessageBox.Show("密码导出成功。是否要打开Excel文档？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
try { Process.Start(text); } catch (Exception ex) { MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Hand); }
```

* File: **`<exe dir>\WiFi密码.csv`** — a *fixed* name, silently overwritten on every export (no timestamp, unlike
  the other two CSV exports in the app). `File.WriteAllText(path, text)` with no encoding ⇒ UTF-8 **without BOM**
  (.NET Framework default), so Excel (Chinese locale) usually shows the header mojibake unless imported as UTF-8.
* Layout: header literally `WiFi 名称  ,保存的密码   ` (two trailing spaces before the comma, three after the second
  column name), then one line per profile: `SSID,Password`; line separator = `AppendLine` = CRLF.
* **No CSV escaping/quoting** — a password containing `,` or `"` shifts columns; an SSID with a comma breaks the row.
* Confirmation dialog text: `密码导出成功。是否要打开Excel文档？` with `YesNo` + `Question`; `Yes` →
  `Process.Start(path)` (shell-open, i.e. Excel/WPS or whatever `.csv` is associated with; exceptions shown as a
  message box). Note the ordering quirk: the message appears only after `File.Exists` succeeds.
* Also used by `FormMain.btnExport_Click` (`IP扫描结果-yyyyMMddHHmmss.csv`, UTF-8, header `IP,状态,主机名,MAC,备注`)
  and `FormIPSegment.btnCopy_Click` (`IP批量扫描-yyyyMMddHHmmss.csv`, no encoding ⇒ UTF-8 no BOM, header
  `IP,主机名,MAC,备注,状态,时间`) — see C.1.

**Copy-password behaviour:** the decompiled viewer has **no** explicit copy handler — no `Clipboard.SetText`, no
`CellDoubleClick`/`CellContentClick` handler, no context menu of its own, and no copy on the form (grep for
`Clipboard` finds hits only in `FormMain`, `FormLocalIP`, `FormIPSegment`, `FormPortScan`, `FormMemoManager`). The
readme's `快速复制密码` (`:11`, `:79`) is satisfied by the **DataGridView built-in clipboard support**: with
`ClipboardCopyMode` left at its default (`EnableWithAutoHeaderText`) and `ContextMenuStrip` unset, the grid supplies
its own right-click context menu with a `复制` command, and Ctrl+C copies the selected cell/row text. So: select the
password cell → right-click → 复制 (or Ctrl+C) → password on the clipboard. The rebuild should provide an explicit
"copy password" affordance (right-click menu item / button / double-click) to be safe, but the observable behaviour
to match is "click the password cell, copy it, no message box".

## B.5 Threading and UI states

* `FormWiFiViewer_Load` (`:38-48`): `statusTip.Text = "正在读取WiFi配置..."`, `gbxResult.Enabled = false`, `Show()`,
  `Application.DoEvents()`, `ReadWiFiProfile()`, then `AutoGenerateColumns = false`, `DataError` and
  `RowStateChanged` hooks.
* Work runs on a `Task.Factory.StartNew` thread; every UI touch is marshalled with `BeginInvoke`; the SSID list is
  rebound progressively (`dgvWiFi.DataSource = null; dgvWiFi.DataSource = list;`).
* Completion (`:107-117`): guarded by `!dgvWiFi.IsDisposed`; sets `statusTip.Text = "WiFi保存密码加载完毕"`,
  `gbxResult.Enabled = true`, rebinds the data source again, `dgvWiFi.ClearSelection()`.
* Exceptions are swallowed into `MessageBox.Show(ex.StackTrace)` (no title, no user-friendly text). Closing the form
  mid-scan can therefore throw on `BeginInvoke` (the `IsDisposed` guard covers only the final update).

---

# C. Infrastructure / cross-cutting

## C.1 `Utility.cs` — every helper (394 lines, `internal class Utility`)

Statics: `logTool = new LogTool()`, `lastExecutionTime = DateTime.MinValue`, `ExceptNames = { "domain", ".local" }`,
`CommonCmds = { "ping", "tracert", "telnet", "netstat", "arp", "nslookup", "ipconfig", "route", "netsh" }`.

> **There is no ping helper and no clipboard helper in `Utility.cs`.** Pinging is done inline in the two forms with
> `System.Net.NetworkInformation.Ping` (see C.2); clipboard access is inline `Clipboard.SetText/GetText` in the forms;
> there is no shared Excel/CSV helper (three inline CSV writers, see below).

| Member | Signature | Behaviour (exact) |
|---|---|---|
| `ParseIPAddress` | `static int[] ParseIPAddress(string ip)` | `ip.Split('.').Select(int.Parse).ToArray()` — used only for grid sorting in `FormIPSegment` (`:176-182`). Throws on non-numeric octets. |
| `GetMacAddressFromARP` | `static string GetMacAddressFromARP(string ipAddress)` | Two-tier lookup, detailed below. Returns uppercase MAC (ARP tier) / dash-separated MAC (adapter tier) / `null`. |
| `DeleteARPTable` | `private static void DeleteARPTable()` | Throttled flush: `if ((DateTime.Now - lastExecutionTime).TotalHours >= 1.0) { ProcessStartCommand("arp -d *", cmdVisible: false); lastExecutionTime = DateTime.Now; }` inside `try{}catch{}`. |
| `GetHostName` | `static string GetHostName(string ipAddress)` | Cache-check, `Dns.GetHostEntry(ipAddress).HostName`, then strips the suffixes `domain` and `.local` (loop restarts `i = 0` after each hit, `EndsWith` is case-sensitive); default/fallback value `未知` ("unknown") on any exception; logs `从DNS表中获取{ip}对应的主机名【{name}】`; caches the result **including the `未知` fallback** for 1 h (negative caching). |
| `ValidOnLineByARP` | `static bool ValidOnLineByARP(string ipAddress, Action<bool> actionCompleted = null)` | `bool flag = !string.IsNullOrEmpty(GetMacAddressFromARP(ip)); actionCompleted?.Invoke(flag); return flag;` |
| `TcpPortTestMuli` | `static void TcpPortTestMuli(string ipAddress, string portArray, Action<bool> actionCompleted = null)` | `Task.Factory.StartNew`: split `portArray` on `,` (RemoveEmptyEntries), `TcpPortTest` each port in order, stop at first success, then `actionCompleted?.Invoke(result)`. Used by `FormMain` for the "ping failed → probe ports" fallback. |
| `TcpPortTest` | `static bool TcpPortTest(string ipAddress, int port)` | `TcpClient.BeginConnect(ip, port, null, null)`; `if (!ar.AsyncWaitHandle.WaitOne(Global.ConfigInfo.PortTimeout)) tcpClient.Close(); else try { tcpClient.EndConnect(ar); return true; } catch {}` → `false`. Non-blocking-ish; the `AsyncWaitHandle` is never disposed. |
| `ValidIP` | `static bool ValidIP(string text)` | `new Regex("(^\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})").IsMatch(text)` — prefix match, no upper bound, no end anchor (see A.5). |
| `GetIPRange` | `static List<string> GetIPRange(string startIP, string endIP, bool isSkipCast = true)` | Converts both to `uint` (`IpToUInt`), loops inclusive, and when `isSkipCast` skips values whose low byte is `0` or `255`; `UIntToIp` formats back. `isSkipCast` defaults to `true`; callers: `FormIPSegment` (3×), `FormPortScan` (1×). |
| `IpToUInt` / `UIntToIp` | `private static` | `num = (num << 8) + byte.Parse(octet)` for 4 octets / `string.Join(".", …)` of the 4 shifted bytes. |
| `GetWorkgroup` | `static void GetWorkgroup()` | Empty body — dead. |
| `IsCommonCmd` | `static bool IsCommonCmd(string command)` | `CommonCmds.Contains(command.ToLower().Trim())` → decides whether a DIY command opens a visible console (`FormMain.mnuOutTool_Click`). |
| `ProcessStartCommand` | `static void ProcessStartCommand(string command, bool cmdVisible = true)` | If `File.Exists(command)` → `Process.Start(command)`; otherwise `ProcessStartInfo("cmd.exe", "/c " + command)` with `WindowStyle = Normal` or `Hidden`; rethrows exceptions (`throw ex`). |
| `GetAllAdapterIP` | `static List<AdapterInfo> GetAllAdapterIP()` | See A.2. |
| `IsDnsAutoConfigured` | `static bool IsDnsAutoConfigured()` | WMI `SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = 'TRUE'`; `true` if any adapter has null/empty `DNSServerSearchOrder`; `catch → false`. **Dead code.** |
| `GetComputerManufacturer` | `private static string GetComputerManufacturer(string computerName)` | `ManagementObjectSearcher("\\\\" + computerName + "\\root\\cimv2", "SELECT * FROM Win32_ComputerSystem")` → `Manufacturer`; `string.Empty` on failure. **Dead code.** |
| `GetFileEncoding` | `static Encoding GetFileEncoding(string filePath)` | Reads all bytes: `EF BB BF` → `Encoding.UTF8`; `FF FE` **or** `FE FF` → `Encoding.Unicode` (both branches return UTF-16LE, so UTF-16BE files are mis-decoded); else any byte in `161..247` (`b >= 128 && b >= 161 && b <= 247`) → `Encoding.GetEncoding("GBK")`; else `Encoding.Default`. Used for `IPScanerMemo.dat` and `command.txt`. |

`GetMacAddressFromARP` in full behaviour (`Utility.cs:32-87`):
1. If the IP is in `Global.AdapterList`: re-enumerate via `GetAllAdapterIP()`, find `r.IP == ipAddress`, take
   `adapterInfo.MAC.Replace(":", "-")` and — after `if (!ValidIP(text.Trim()))`, which is *always* true for a MAC
   string because the regex needs 4 dotted octets, i.e. the guard never fires — log
   `从本地适配器中获取{ip}对应的MAC【{mac}】` and return it (self-MAC short-circuit for the local machine).
2. Cache key = `MemoryCacheManager.CreateCacheKey(MethodBase.GetCurrentMethod(), ipAddress)` →
   `IPScaner.Utility.GetMacAddressFromARP - 192.168.1.5`; a hit returns the cached string.
3. `DeleteARPTable()` — at most one `arp -d *` per hour (flushes stale entries so a fresh ARP resolution happens).
4. `ProcessStartInfo { FileName = "arp", Arguments = "-a", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }`
   — **no `StandardOutputEncoding`**, so the text is decoded with the console/OEM code page (CP936 on Chinese
   Windows). Note: `arp` is invoked as an executable, not through `cmd /c`.
5. Split on `Environment.NewLine` (RemoveEmptyEntries) → keep lines containing the IP → split on `' '`
   (RemoveEmptyEntries) → first token matching `([0-9A-Fa-f]{2}[-:]){5}([0-9A-Fa-f]{2})` wins → cache it
   (`AddObject(mac.ToUpper(), key)`, 1 h), log `从ARP表中获取{ip}对应的MAC【{mac}】`, return uppercased.
6. Otherwise `null` (not cached — the *next* call re-runs `arp -a`, only throttled by the 1-hour table flush).

Clipboard/CSV helpers that exist **outside** `Utility.cs` (inline, no shared abstraction):
* `FormMain.Btn_Click`: `Clipboard.SetText(text)` on every single click of an IP color block (IP copied silently).
* `FormMain.btnLegend_Click`: builds `IP段: {txtIPPart.Text}, 状态【{不通|正常}】地址共有{n}个` + one line per address
  (`ip + "  " + memo` for 不通; `ip + " " + hostName + " " + mac + " " + memo` for 正常), `Clipboard.SetText`, then
  `MessageBox.Show("IP段: {seg}, 状态【{text}】地址共有{n}个，已复制剪贴版", …)` (note the typo `剪贴版`).
* `FormMain.mnuCommand_Click`: copies the value after `：` in the menu item text (`已复制【{value}】`).
* `FormMain.statusIP_Click`: copies the active IP, status `IP已复制到剪贴板`.
* `FormMemoManager.btnImport_Click`: reads the clipboard, splits on `\r`/`\n`, each line `ip=memo`, dedupes by IP.
* `FormPortScan.lblCopyResult_Click`: `string.Join(Environment.NewLine, lbxResult.Items)` → clipboard, message
  `内容已复制到剪贴板！`.
* CSV writers: `FormMain.btnExport_Click` → `<exe>\IP扫描结果-yyyyMMddHHmmss.csv`, UTF-8 **with** BOM
  (`File.WriteAllText(..., Encoding.UTF8)`), header `IP,状态,主机名,MAC,备注`, rows `NetworkInfo.ToString()` =
  `IP,Status,HostName,MAC,Memo`; `FormIPSegment.btnCopy_Click` → `<exe>\IP批量扫描-yyyyMMddHHmmss.csv`, no encoding
  argument (UTF-8 no BOM), header `IP,主机名,MAC,备注,状态,时间`; `FormWiFiViewer` → see B.4. All three are raw
  `string.Join(",", …)` with no quoting.

## C.2 Ping implementation and the "过滤ping时长>2秒" rule

`System.Net.NetworkInformation.Ping`, **async, fire-and-forget, one `Ping` instance per address**, event-driven on a
thread-pool thread. Main-window scan (`FormMain.ScanIP`, `:814-836`) — a dedicated background thread walks `.1`…`.254`
with a 10 ms gap:

```csharp
while (CanScanIp && CurrentIp < 255)
{
    string text = txtIPPart.Text + "." + CurrentIp;
    statusTip.Text = ("开始扫描IP: " + text).PadRight(2);
    Ping ping = new Ping();
    ping.PingCompleted += Ping_PingCompleted;
    ping.SendAsync(text, Global.ConfigInfo.PingTimeout, CurrentIp);
    CurrentIp++;
    Thread.Sleep(10);
}
```

Handler (`FormMain.Ping_PingCompleted`, `:906-982`): `Ping ping = sender as Ping; ping.Dispose(); PingReply reply = e.Reply;`
then `reply.Status == IPStatus.Success` → `NetworkOKColor` + `GetHostNameAndMac(ip)`; else the fallback chain
`ARPInsteadPingEnabled ? ValidOnLineByARP : (PortInsteadPingEnabled ? TcpPortTestMuli(ip, PrePortArray) : NG)` with the
counter label `$"数量合计：正常 {num2}，不通 {num}"`. Single-block click (`Btn_Click`, `:675-693`) pings one address
with `ping.SendAsync(text, Global.ConfigInfo.PingTimeout, btn.Text)` and copies the IP to the clipboard.

`FormIPSegment` does the same for an arbitrary IP list (`:299-341`), with an interval mode (`chkInterval` + `txtMins`,
`System.Windows.Forms.Timer` 1000 ms, `lastRefreshTime.AddMinutes(mins)` countdown) and a 10 ms gap; results are
appended to `List<PingReplyInfo>` and re-bound on every completion (`:343-368`).

Result model (`PingReplyInfo.cs`):

```csharp
public PingReplyInfo(string ip, IPStatus status, long time = -1L)
{
    IP = ip;
    Success = status == IPStatus.Success;
    if (Success && time >= 0)
    {
        RoundtripTime = time;
        if (time < 1) Time = "<1ms"; else Time = time + "ms";
    }
    else { Time = "Timeout"; RoundtripTime = 2147483647L; }
}
// Status => Success ? "OK" : "NG";  Memo => Global.GetMemoByMacOrIp(MAC, IP);
```

**The literal 2-second filter does not exist in 1.28.2.** Changelog v1.5 (`IPScaner.readme.txt:144`) says
`过滤ping时长>2秒的IP`; in the current build the equivalent (and its successor) is the configurable ping timeout that
is passed as `SendAsync`'s timeout parameter: `ConfigInfo.PingTimeout` — default **500 ms** in the `ConfigInfo`
constructor (`ConfigInfo.cs:190`) but **100 ms** in the `FormConfig` designer (`FormConfig.cs:387`,
`Minimum = 10`, `Maximum = 5000`, `Increment = 100`, label `Ping超时毫秒`, tooltip
`Ping时长超过此值，会认为网络异常`). A host whose RTT exceeds the timeout comes back as `IPStatus.TimedOut` and is
treated as not-online; there is no post-hoc comparison of `RoundtripTime > 2000`. `PingCount` (label `Ping请求次数`,
tooltip `一次Ping发送的包数量`, default 4) is used **only** for the spawned console command
`ping {ip} -n {PingCount} &pause`, never for the in-process scan. There is no ICMP P/Invoke and no raw-socket
implementation anywhere in the project.

## C.3 `NetAPIUtility.cs`, `AdapterInfo*`, `NetworkInfo.cs`

`NetAPIUtility` (47 lines) — P/Invoke declarations, **entirely unused** (no call site anywhere):

```csharp
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
public struct SHARE_INFO_1 { public string NetName; public int Type; public string Remark;
    public int Permissions; public int MaxUses; public int CurrentUses; public string Path; public string Password; }

[DllImport("Netapi32.dll", SetLastError = true)]
public static extern int NetShareEnum(string servername, int level, out IntPtr bufptr, int prefmaxlen,
    out int entriesread, out int totalentries, IntPtr resume_handle);

[DllImport("Netapi32.dll", SetLastError = true)]
public static extern int NetApiBufferFree(IntPtr Buffer);
```

`EnumerateShares(string ip)` calls `NetShareEnum(ip, 1, out bufptr, -1, out entriesread, out _, IntPtr.Zero)`, on
`== 0` walks `entriesread` structures with `Marshal.PtrToStructure` advancing by
`Marshal.SizeOf(typeof(SHARE_INFO_1))`, prints `共享名称: {NetName}` to `Console.WriteLine` (invisible — the app is
`WinExe`), and frees the buffer with `NetApiBufferFree`; on failure prints
`无法获取共享目录，错误代码: {code}`. Notes for the rebuild: the level-1 Win32 structure is only
`{ LPWSTR shi1_netname; DWORD shi1_type; LPWSTR shi1_remark; }` — the extra fields belong to levels 2/502, so
`Marshal.SizeOf` overshoots the real stride and only the first entry is parsed correctly; the level-1 buffer holds
*structures*, not pointers. The intended UX exists elsewhere: the share menu item
(`mnuShareFolder` → `explorer.exe \\{ip}`, `FormMain.cs:1128`) and `EventName.Share`. The WinUI port should use
`NetShareEnum` with the correct `SHARE_INFO_1` or `WNetEnumResource` if the feature is implemented.

`AdapterInfo` (`AdapterInfo.cs`) — `[XmlRoot("Adapter")]`, attribute-annotated, used both as the live adapter model
and as the history record:

| Property | XML | Notes |
|---|---|---|
| `string Name` | `[XmlAttribute]` | interface alias, e.g. `以太网` |
| `string IP` | `[XmlAttribute]` | |
| `int Status` | `[XmlIgnore]` | `(int)OperationalStatus`, `1 == Up` |
| `string MAC` | `[XmlIgnore]` | `AA:BB:CC:DD:EE:FF` |
| `string SubnetMask` | `[XmlAttribute]` | |
| `string Gateway` | `[XmlAttribute]` | |
| `bool IsDhcpEnabled` | `[XmlIgnore]` | |
| `List<string> DnsServers` | `[XmlIgnore]` | initialized to `new List<string>()` |
| `string DNS` | `[XmlAttribute("DNS")]` | history-only single DNS value |
| `ToString()` | — | `"名称：" + Name + " IP：" + IP + " MAC：" + MAC` |

`AdapterInfoCollection` (`AdapterInfoCollection.cs`): `[XmlRoot("root")]`, one member
`[XmlArray("array")] public List<AdapterInfo> AdapterList { get; set; } = new List<AdapterInfo>();`.
Measured serialization (see C.5) writes `<root><array><AdapterInfo …/></array></root>` — the `XmlRoot("Adapter")` on
the item type does **not** rename array items.

`NetworkInfo` (`NetworkInfo.cs`) — the scan-result model used by `GetAllComps`, the detail dialog, the memo manager
and the CSV export:

| Member | Notes |
|---|---|
| `IP`, `HostName`, `Status`, `MAC`, `Memo` (`string`) | |
| `IsOnline` | `status == "正常"` |
| `IsOffline` | `status == "不通"` |
| `IsPinged` | `status != "待检测"` |
| ctors | `()`, `(IP, Memo)`, `(IP, mac, Memo)`, `(ip, status, hostName, mac, memo)` |
| `ToString()` | `IP + "," + Status + "," + HostName + "," + MAC + "," + Memo` (the CSV row) |

Status strings produced by `FormMain.GetAllComps` (`:1354-1375`): `正常` (green/NetworkOKColor), `不通`
(red/NetworkNGColor), `待检测` (untouched block, only when `isAll == true`).

## C.4 `LogTool.cs` — file, name pattern, format, call sites

* Directory: `Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs")` (`LogTool.cs:34`) — created lazily.
* File name: `$"{DateTime.Now:yyyy-MM-dd}.log"`, e.g. `Logs\2025-06-14.log` (`:64`, `:95`); the name is switched at
  the first write after midnight (`if (DateTime.Now.Date != lastLogDate)`).
* Line format (`Log(string msg)`, `:83-91`): the caller is resolved with
  `new StackTrace().GetFrame(1).GetMethod()` and the line is
  `$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {method.DeclaringType.Name}.{method.Name}  {msg}"`
  — timestamp, ` - `, `Class.Method`, **two spaces**, message. Example:
  `2025-06-14 09:12:33 - Utility.GetHostName  从DNS表中获取192.168.1.5对应的主机名【PC-01】`.
* Writing: a static background thread (`IsBackground = true`) is started in the static constructor; it sleeps 3000 ms,
  then loops: `Thread.Sleep(1000)`, drain `logQueue` (a `Queue<string>`), and **per line** check
  `Global.ConfigInfo.LogEnabled`; only then `Directory.CreateDirectory(logDirectory)` if needed and
  `using StreamWriter w = new StreamWriter(logFilePath, append: true); w.WriteLine(value);` — i.e. the file is opened
  and closed per line, then `Thread.Sleep(100)`.
* `PauseLog` is set to `true` by *every* `Log()` call; when the worker sees it, it clears it and sleeps 1000 ms —
  so the effective throughput is roughly **one line per second**, and bursts are queued.
* Rotation/cleanup (`DeleteExpiredLogFiles`, `:98-133`): runs once per queued line, but only once per day
  (`DtList.Contains(DateTime.Today)` guard); deletes `Logs\*.log` whose `CreationTime` is
  `>= MaxLogFileCount` (= **30**) days old; deletion errors are re-queued as
  `"{ts} -删除日志文件出错:" + ex.Message` (note: no space after `-`).
* All exceptions inside the writer are swallowed (`catch {}`).
* Logging is **off by default** (`ConfigInfo.LogEnabled = false`); toggled by `FormConfig` checkbox `启用调试日志`.
* Call sites: `FormMain.FormMain_Load` — `**************程序开始启动*******************`, `加载配置文件...`,
  `初始化小色块控件...`, `加载自定义命令文件...`, `加载备注文件...`, `程序启动完成`; `FormMain_FormClosing` —
  `用户关闭程序`; `Utility.GetMacAddressFromARP` (both tiers), `Utility.GetHostName`, `Utility.GetAllAdapterIP`
  (per gateway, per DNS, per adapter `读取本地网卡信息 {AdapterInfo}`). Nothing in `FormLocalIP`,
  `FormWiFiViewer`, `NetAPIUtility` or the other forms logs. Because `LogTool`'s queue/dir members are `static` but
  each form news up its own `LogTool` (`FormMain.log`, `Utility.logTool`), they share one queue and one thread.

## C.5 `XmlUtility.cs` — exact `.cfg` serialization

```csharp
internal class XmlUtility
{
    public static void SaveXml<T>(T cfgInfo, string cfgPath)
    {
        XmlSerializer xmlSerializer = new XmlSerializer(typeof(T));
        XmlSerializerNamespaces xmlSerializerNamespaces = new XmlSerializerNamespaces();
        xmlSerializerNamespaces.Add("", "");
        using FileStream stream = new FileStream(cfgPath, FileMode.Create);
        xmlSerializer.Serialize(stream, cfgInfo, xmlSerializerNamespaces);
    }

    public static T ReadXml<T>(string cfgPath)
    {
        XmlSerializer xmlSerializer = new XmlSerializer(typeof(T));
        using FileStream stream = new FileStream(cfgPath, FileMode.Open);
        return (T)xmlSerializer.Deserialize(stream);
    }
}
```

`FileMode.Create` truncates; `FileMode.Open` throws `FileNotFoundException` when absent (callers always check
`File.Exists` first — `FormMain.LoadConfig` `:452`, `FormLocalIP.LoadHisXml` `:267`). No try/catch, no backup file,
no schema, no namespace (the empty `XmlSerializerNamespaces` suppresses `xmlns:xsi`/`xmlns:xsd`).

**Measured output** (verified by compiling the attribute set with `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
and running it on .NET Framework 4.8):

* Declaration: `<?xml version="1.0"?>` — **no `encoding` attribute** (because `XmlTextWriter(stream, null)` is used),
  no BOM (the first bytes are `60 63 120` = `<?x`), body encoded UTF-8.
* Default `IPScaner.cfg` written by `XmlUtility.SaveXml(Global.ConfigInfo, CfgPath)`:
  ```xml
  <?xml version="1.0"?>
  <root Version="1.0" QueryHostNameEnabled="false" PingTimeout="500" PingCount="4" ARPInsteadPingEnabled="false" PortInsteadPingEnabled="false" PrePortArray="80,135,445,500" PortTimeout="50" DoubleClickTime="200" BtnFontSize="9" MenuAutoOpen="false" DefaultColorArgb="-7876885" NetworkOKColorArgb="-13447886" NetworkNGColorArgb="-3318692" MemoColorArgb="-16776961" DoubleEvent="Ping" DesktopOverlayEnabled="false" DesktopOverlayPre="本地IP地址：" DesktopOverlayOffsetX="100" DesktopOverlayOffsetY="100" DesktopOverlayOpacity="70" DesktopOverlayLocation="2" DesktopForeColorArgb="-16777216" DesktopBgColorArgb="-256" LogEnabled="false" HideMainEnabled="false" />
  ```
  (single line, ` />` self-closing with a space; attribute order = declaration order; `StarMenu` omitted while null;
  the enum `EventName` serializes as its **name**, e.g. `DoubleEvent="Ping"`; `Color` values are stored as signed
  32-bit ARGB ints: SkyBlue `-7876885`, LimeGreen `-13447886`, IndianRed `-3318692`, Blue `-16776961`,
  Black `-16777216`, Yellow `-256`.)
* `ipScaner_his.xml` (history, `AdapterInfoCollection`):
  ```xml
  <?xml version="1.0"?>
  <root>
    <array>
      <AdapterInfo Name="以太网" IP="192.168.1.100" SubnetMask="255.255.255.0" Gateway="192.168.1.1" DNS="223.5.5.5" />
    </array>
  </root>
  ```
  (multi-line with 2-space indent because the collection contains child elements; item element name is `AdapterInfo`,
  **not** the `XmlRoot("Adapter")` name.)
* Writing is **not atomic** (no temp+rename) and is called on every config-option save, on every
  "set as fixed menu" (`FormMain.SetStarTarget` `:364`), and on every static-IP apply.

## C.6 `MemoryCacheManager.cs` — what is cached and the eviction policy

```csharp
public static string CreateCacheKey(MethodBase method, string argsValue)
    => method.DeclaringType.FullName + "." + method.Name + " - " + argsValue;
public static bool HasCacheKey(string cacheKey) => MemoryCache.Default[cacheKey] != null;
public static object GetObject(string cacheKey) => MemoryCache.Default[cacheKey];
public static object AddObject(object data, string cacheKey, double overTimeHours = 1.0)
{
    CacheItemPolicy policy = new CacheItemPolicy { AbsoluteExpiration = DateTime.Now.AddHours(overTimeHours) };
    MemoryCache.Default.Set(cacheKey, data, policy);
    return data;
}
public static void RemoveCache(string cacheKey) { if (HasCacheKey(cacheKey)) MemoryCache.Default.Remove(cacheKey); }
```

* Backing store: `System.Runtime.Caching.MemoryCache.Default` (in-process, `System.Runtime.Caching` reference).
* Key format: `{DeclaringType.FullName}.{MethodName} - {arg}` → the only two producers are
  `IPScaner.Utility.GetMacAddressFromARP - {ip}` and `IPScaner.Utility.GetHostName - {ip}`, so the cache is
  effectively a MAC map and a hostname map keyed by IP.
* Values cached: MAC strings (uppercase, ARP tier only) and hostnames (including the `未知` fallback, i.e. negative
  results are cached). Cache misses for MAC are **not** cached.
* Eviction: `AbsoluteExpiration = DateTime.Now.AddHours(1)` (default `overTimeHours = 1.0`; no caller overrides it).
  No sliding expiration, no `CacheEntryChangeMonitor`, no priority, no explicit memory limit set by the app — besides
  the 1-hour absolute expiry, entries are only removed by the cache's own memory-pressure trimming
  (`MemoryCache.Default` uses host default limits) or by `RemoveCache`, which nothing calls.
* `HasCacheKey`/`GetObject` use the indexer, so a stored `null` would read as "no cache"; nothing stores null.
  `Global.DeleteARPTable`'s own 1-hour throttle is independent of this cache, and `_ = Global.AdapterList` refreshes
  are not invalidating the caches, which is why a MAC/hostname can be stale for up to an hour.

## C.7 `Program.cs` / `Global.cs` / `EventName.cs`

`Program.Main` (the complete startup sequence, `Program.cs:11-31`):

```csharp
[STAThread]
private static void Main()
{
    WindowsIdentity current = WindowsIdentity.GetCurrent();
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(defaultValue: false);
    Application.ThreadException += Application_ThreadException;             // empty body
    AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;  // empty body
    WindowsPrincipal windowsPrincipal = new WindowsPrincipal(current);
    if (windowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator))
    {
        Application.EnableVisualStyles();                                    // called twice in this path
        Application.Run(new FormMain());
        return;
    }
    ProcessStartInfo processStartInfo = new ProcessStartInfo();
    processStartInfo.FileName = Application.ExecutablePath;
    processStartInfo.Verb = "runas";
    Process.Start(processStartInfo);
    Application.Exit();
}
```

Order of business inside the main form (`FormMain_Load`, `:269-304`): log start → `cmbAdapter.Items.Add("加载中...")`
→ `AppTitle = Text` → status `程序准备就绪` → title becomes `{Text}  Ver: {Application.ProductVersion}` →
`Global.AdapterList = Utility.GetAllAdapterIP()` → re-bind `cmbAdapter` (`DisplayMember="Name"`,
`ValueMember="IP"`) → `activeIp = string.Join("; ", adapters.Where(Status == 1).Select(IP))`,
`statusIP.Text = "活动IP:" + activeIp` → `Show(); Application.DoEvents();` → `LoadConfig()` →
`DelayDisplayTableView()` (254 IP buttons on a thread-pool thread) → `InitTipList()` → `ToolTip` + 1 s UI timer →
`LoadDIYCommand()` (`command.txt`) → `LoadMemoInfo()` (`IPScanerMemo.dat`, migrating the legacy `IPScaner.dat`) →
`DislayDesktopOverlay()` → `AttachRightClickEvent`.

**Single-instance handling: none.** There is no `Mutex`, no `Process.GetProcessesByName` guard, no window-message
broadcast, no `Application.Restart` — grep for `Mutex` across the whole decompiled tree returns nothing, and the
readme even advertises the opposite for the tools (`Tip:快捷工具中的所有工具,都可以打开多个哦`). Launching the exe
twice creates two independent elevated instances that both write `IPScaner.cfg`, `IPScanerMemo.dat` and
`ipScaner_his.xml` (last writer wins). Only the font-size change path relaunches: `Process.Start(Application.ExecutablePath)`
with `WorkingDirectory = Application.StartupPath` then `Application.Exit()` (`FormMain.cs:1313-1319`, prompt
`字号已变更，是否立即重启程序？`). The tray icon (`notifyIcon1`, balloon `程序最小化到托盘，双击显示主界面`, tooltip title
`局域网IP扫描工具`) hides/shows the *same* instance: `FormMain_SizeChanged` hides to tray when
`ConfigInfo.HideMainEnabled`, `notifyIcon1_MouseDoubleClick → ShowMainForm()`; closing asks
`你确定要退出吗？` (title `关闭询问`), tray-exit asks `是否确认退出程序？` (title `退出`).

`Global` (`Global.cs`) — the process-wide state, all `public static`:
`Dictionary<string,string> MemoDict` (key = IP **or** MAC in one flat map, deserialized from `IPScanerMemo.dat`,
lines `key=value` with `^v^` as the newline escape, `#` comments, encoding auto-detected);
`ConfigInfo ConfigInfo { get; set; } = new ConfigInfo();`; `List<AdapterInfo> AdapterList { get; set; } = new List<AdapterInfo>();`
(shared by `FormMain` and `FormLocalIP`, re-assigned wholesale on refresh);
`GetMemoByMacOrIp(mac, ipAddress = "")` → MAC key wins, then IP key, else `string.Empty`.

`EventName` (`EventName.cs`) — the double-click action enum; the numeric values are also the index of
`FormConfig.cmbDoubleEvent` items `{ "Ping", "浏览网页", "Tracert", "Telnet", "Netstat", "ARP", "共享目录" }`
(`FormConfig.cs:500`) and are persisted as names in `IPScaner.cfg`:

| Value | Member | `FormConfig` combo label | Action (`FormMain.Btn_DoubleClick`, `:695-722`) |
|---|---|---|---|
| 0 | `Ping` | `Ping` | `Utility.ProcessStartCommand($"ping {ip} -n {ConfigInfo.PingCount} &pause")` |
| 1 | `ViewWeb` | `浏览网页` | `Process.Start("http://" + ip)` |
| 2 | `Tracert` | `Tracert` | `Utility.ProcessStartCommand("tracert " + ip + " &pause")` |
| 3 | `Telnet` | `Telnet` | `Utility.ProcessStartCommand("telnet " + ip + " 23 &pause")` |
| 4 | `Netstat` | `Netstat` | `Utility.ProcessStartCommand("netstat -ano \| findstr " + ip + " &pause")` |
| 5 | `ARP` | `ARP` | `Utility.ProcessStartCommand("arp -a " + ip + " &pause")` |
| 6 | `Share` | `共享目录` | `Process.Start("explorer.exe", "\\\\" + ip)` |

Default is `EventName.Ping`; the same seven actions are also available from the IP-block right-click menu
(`mnuCommand_Click`, `:1087-1163`), which additionally handles `mnuMemo` (per-IP/MAC note) and copy actions.

`AboutBox` (for completeness; opened by `mnuAbout_Click` → `new AboutBox().ShowDialog()`): title
`关于 {AssemblyTitle}` (= `关于 IPScaner`), `labelVersion.Text = 版本 {AssemblyVersion}` (= `版本 1.28.2.0`),
`labelProductName.Text = 局域网IP扫描工具`, `labelCopyright.Text = Copyright © chenhuxiang@qq.com 2019-2025`,
`labelCompanyName.Text = ""`, description = embedded resource `IPScaner.readme.txt` (fallback
`AssemblyDescription` = `局域网IP扫描工具`), `okButton.Text = 确定(&O)` (`DialogResult.Cancel`, so it is the
AcceptButton), size `514×396`, `FormBorderStyle.FixedDialog`, logo `PictureBox` (`StretchImage`) spanning 6 rows of a
`TableLayoutPanel` (33 %/67 % columns, rows 23/23/23/23/100 %/30).

File map (all relative to the exe directory unless stated):

| Path | Written by | Format |
|---|---|---|
| `IPScaner.cfg` | `FormMain` (options, star menu) | XML, `<?xml version="1.0"?>`, `<root …/>`, UTF-8 no BOM |
| `ipScaner_his.xml` | `FormLocalIP.SaveHisXml` | XML `<root><array><AdapterInfo …/></array></root>` |
| `IPScanerMemo.dat` | `FormMain.SaveMemoInfo` | `key=value` lines, `^v^` newline escape, UTF-8 **with** BOM |
| `IPScaner.dat` | legacy input only | migrated to `IPScanerMemo.dat` via `File.Move` on startup |
| `command.txt` | user-authored | `标题 命令…` per line, `#` comments, auto-detected encoding |
| `Logs\yyyy-MM-dd.log` | `LogTool` | see C.4; 30-day retention |
| `IP扫描结果-yyyyMMddHHmmss.csv` | `FormMain.btnExport_Click` | `IP,状态,主机名,MAC,备注`, UTF-8 **with** BOM |
| `IP批量扫描-yyyyMMddHHmmss.csv` | `FormIPSegment.btnCopy_Click` | `IP,主机名,MAC,备注,状态,时间`, UTF-8 no BOM |
| `WiFi密码.csv` | `FormWiFiViewer.mnuExportPwd_Click` | see B.4, UTF-8 no BOM, fixed name |
| `%TEMP%\IPScaner.FormPortScan.txt` | `FormPortScan.SaveHisPorts` | last-used port list, UTF-8 with BOM |

## C.8 `DesktopOverlayForm.cs` — desktop overlay

Content is built by the **main** form (`FormMain.DislayDesktopOverlay`, method name typo included, `:424-448`):

```csharp
AdapterInfo adapterInfo = Global.AdapterList.FirstOrDefault(r => r.IP == activeIp);
string text = Global.ConfigInfo.DesktopOverlayPre + activeIp;
if (adapterInfo != null)
{
    text = text + Environment.NewLine + "名称：" + Utility.GetHostName(activeIp);
    text = text + Environment.NewLine + "MAC：" + adapterInfo.MAC;
    text = text + Environment.NewLine + "掩码：" + adapterInfo.SubnetMask;
    text = text + Environment.NewLine + "网关：" + adapterInfo.Gateway;
    text = text + Environment.NewLine + "DHCP：" + adapterInfo.IsDhcpEnabled;
    text = text + Environment.NewLine + "DNS：" + string.Join("; ", adapterInfo.DnsServers);
}
desktopForm = new DesktopOverlayForm(text);
desktopForm.Show();
```

* `DesktopOverlayPre` (config default `本地IP地址：`; the `FormConfig` designer default for the same field is
  `本地IP地址为`, label `IP前缀`) is concatenated **without separator** to `activeIp`
  (`activeIp = string.Join("; ", adapters.Where(Status == 1).Select(IP))`, `FormMain.cs:282-284` — several NICs ⇒
  `本地IP地址：192.168.1.5; 10.0.0.7`).
* Lines 2-6 are only emitted when `Global.AdapterList` contains an entry whose `IP == activeIp`, i.e. effectively
  always; the hostname line is labelled `名称：` (not `计算机名称：` despite the designer placeholder text), `DHCP：`
  prints the raw .NET bool (`True`/`False`), `DNS：` joins all IPv4 DNS servers with `"; "`.
* The label is a single `Label lbl` with `AutoSize = true`, `Dock = Fill`, `Padding = new Padding(18,18,18,18)`,
  `Font = 宋体 10`, `TextAlign = MiddleLeft`, `BackColor = Yellow`, `ForeColor = Black`, and the designer placeholder
  `本地IP地址：192.168.1.122\r\n\r\n计算机名称：我的电脑`; `\r\n` in the content is honoured (multiline label).
* Recreated (close + dispose + new) on startup and whenever the options dialog is accepted
  (`mnuOptionSet_Click` / `statusHostName_Click` → `XmlUtility.SaveXml` + `UpdateControlByCfgInfo` +
  `DislayDesktopOverlay`); hidden entirely when `DesktopOverlayEnabled == false`.

Window/desktop behaviour (`DesktopOverlayForm`):
* Form chrome: `FormBorderStyle = None`, `ShowIcon = false`, `ShowInTaskbar = false`,
  `StartPosition = Manual`, `BackColor = LightYellow`, `TransparencyKey = LightYellow`, design `Opacity = 0.9`,
  `ClientSize = 368×108`, `AutoScaleDimensions = (11F, 21F)` (again the 175 % designer capture), `Name = DesktopOverlayForm`.
* **The desktop pinning is done purely with extended window styles** — there is **no** `SetParent` to `Progman`/
  `WorkerW`, **no** `SetWindowPos(HWND_BOTTOM)`, **no** `TopMost`, no `WS_EX_NOACTIVATE`:
  ```csharp
  private const int GWL_EXSTYLE = -20;
  private const uint WS_EX_LAYERED = 524288u;      // 0x00080000
  private const uint WS_EX_TRANSPARENT = 32u;      // 0x00000020
  private const uint WS_EX_TOOLWINDOW = 128u;      // 0x00000080
  ...
  IntPtr handle = Handle;
  uint windowLong = GetWindowLong(handle, -20);
  SetWindowLong(handle, -20, windowLong | 0x80000 | 0x20 | 0x80);

  [DllImport("user32.dll")] private static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);
  [DllImport("user32.dll")] private static extern uint GetWindowLong(IntPtr hWnd, int nIndex);
  ```
  So: `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW` (Layered enables `Form.Opacity` and, combined with
  `WS_EX_TRANSPARENT`, makes the window **click-through**; `TOOLWINDOW` keeps it out of Alt+Tab/taskbar). The window
  remains a normal top-level, non-owned window shown with `Show()` — it is *not* parented below the desktop icons, so
  it starts at the top of the z-order and can be covered by other windows (the changelog claim 桌面背景显示 /
  在桌面指定区域显示本机IP describes the intent, not a `WorkerW` parent). A WinUI 3 port that needs true
  "on the desktop" behaviour must add what this code lacks: `SetParent(hwnd, FindWindow("Progman", null))` +
  `WorkerW` discovery, or a `WS_EX_NOACTIVATE`-style `AppWindow` with `OverlappedPresenter`,
  `IsResizable=false`, `IsAlwaysOnTop` per config; keep `WS_EX_TRANSPARENT` for click-through.
* Opacity: `Opacity = (double)Global.ConfigInfo.DesktopOverlayOpacity / 100.0;` — the config value is an int
  0…100 (`FormConfig` `NumericUpDown` label `透明度`, tooltip `窗体透明度，0完全透明，100不透明`, default **70**,
  increment 10). Only the Form's opacity is set (WinForms applies it to the whole layered window).
* Colours: `lbl.ForeColor = Global.ConfigInfo.DesktopForeColor; lbl.BackColor = Global.ConfigInfo.DesktopBgColor;`
  (config keys `DesktopForeColorArgb` default Black `-16777216`, `DesktopBgColorArgb` default Yellow `-256`,
  picked through `ColorDialog` from the swatches labelled `文字颜色` / `背景色`). Quirk to preserve or fix: the form's
  `BackColor`/`TransparencyKey` stay `LightYellow`, so any content painted `LightYellow` would punch through as
  transparent.
* Size: `Width = lbl.Width + 40; Height = lbl.Height + 40;` — content-driven (AutoSize label + 36 px padding + 4),
  which is the v1.25 "支持宽度自适应" behaviour.
* **Four position modes** (`Global.ConfigInfo.DesktopOverlayLocation`, `FormConfig` combo
  `{ "左上角", "右上角", "右下角", "左下角" }` at index 0-3, default **2**; `FormConfig` label `显示位置`, offsets
  `偏移量x,y`, both default 100 and used as padding from the chosen edge):
  ```csharp
  Rectangle workingArea = Screen.GetWorkingArea(this);
  workingArea = Screen.PrimaryScreen.Bounds;          // ← overrides the previous line
  int num = 0, num2 = 0;
  if (location == 0) { num = OffsetX; num2 = OffsetY; }                                   // 左上角
  else if (location == 1) { num = workingArea.Width - Width - OffsetX; num2 = OffsetY; }  // 右上角
  else if (location == 2) { num = workingArea.Width - Width - OffsetX;
                            num2 = workingArea.Height - Height - OffsetY; }                // 右下角
  else if (location == 3) { num = OffsetX;
                            num2 = workingArea.Height - Height - OffsetY; }                // 左下角
  Location = new Point(num, num2);
  ```
  Note the dead `Screen.GetWorkingArea(this)` call and the `Screen.PrimaryScreen.Bounds` replacement: the taskbar
  area is **not** excluded, and multi-monitor setups always place the overlay on the primary monitor. Locations > 3
  fall through to `(0, 0)`. Offsets are pixels, positive inward from the chosen edges/corners.

The matching config surface (`FormConfig` tab `桌面`, `FormConfig.cs:751-841`): checkbox `启用桌面显示本机IP标签`
(default off), `显示位置` combo, `偏移量x,y` (two `NumericUpDown`s), `IP前缀` textbox (bound to
`DesktopOverlayPre`), `透明度` numeric, `文字颜色` / `背景色` colour swatches, plus the general-tab checkbox
`最小化时隐藏到托盘` (`HideMainEnabled`) and `启用调试日志` (`LogEnabled`); the config dialog writes every field in
`btnSave_Click` (`FormConfig.cs:165-212`) and returns `DialogResult.OK`, which is what triggers the overlay rebuild.

---

# Open questions / ambiguities

1. **24H2 fix — mechanism inferred, not proven.** The decompiled 1.28.2 contains no version check and no alternate
   API; the only candidate for the v1.20 fix is the GBK → UTF-8 → `Encoding.Default` retry loop with
   `StandardOutputEncoding` plus the error text `读取WiFi保存密码失败，可能是未知的编码格式`. The exact 24H2 behaviour
   change (console output codepage switching to UTF-8 for `netsh wlan`) is an inference from that code shape; if the
   original 1.19 binary is available, diffing its `ReadProfiles` would confirm it.
2. **WiFi encryption of "open" networks / non-ASCII SSIDs** are not handled specially; profiles whose `key=clear`
   output lacks `关键内容` (open networks, or when elevation is missing) are silently skipped. Whether the original
   lists them with an empty password is unknown.
3. **"快速复制密码" (readme) vs the code**: no copy handler exists in `FormWiFiViewer`; the behaviour is assumed to be
   the `DataGridView` default `复制` context menu / Ctrl+C. If a pre-1.20 build had a dedicated handler, the readme
   text may be describing it.
4. **FormLocalIP designer scaling**: `AutoScaleDimensions = (11F, 21F)` vs `(6F, 12F)` in every other form suggests a
   175 %-DPI designer capture; the true 96-dpi logical layout cannot be recovered from the decompiled file alone.
   All coordinates in A.1 are the raw (scaled) values.
5. **`ExceCmd` correctness**: whether the netsh command is guaranteed to have executed before `RefreshAdapter()` is
   undefined (fire-and-forget, no wait). The original UI may therefore show a stale IP after "修改成功"; the rebuild
   should await the command, but that changes observable timing.
6. **`ipScaner_his.xml` naming** is inconsistent with the other artefacts (`IPScaner.cfg`, `IPScanerMemo.dat`); it is
   stored next to the exe, so an installed copy under `Program Files` would need write permission — no fallback to
   `%APPDATA%` exists.
7. **`NetAPIUtility`/`EnumerateShares`** is dead code with an incorrect level-1 structure layout; the share feature as
   shipped is only `explorer.exe \\{ip}`. Do not port it as-is.
8. **`Utility.ValidIP`** is a loose prefix regex, and `GetMacAddressFromARP` uses it as a MAC sanity check (always
   true). Any stricter validation in the port will reject inputs the original accepted.
9. **`LogTool.PauseLog`** throttling (~1 line/s) and the per-line `StreamWriter` open/close look accidental; a port
   should keep the file naming/format but may legitimately restructure the writer.
10. **`DesktopOverlayForm` is not actually parented to the desktop** (no `Progman`/`WorkerW`), does not set
    `TopMost`, and uses `Screen.PrimaryScreen.Bounds`; the changelog wording ("桌面背景显示", "在桌面指定区域显示")
    describes intent. The WinUI implementation must decide whether to reproduce the literal window styles or the
    intended always-on-desktop behaviour.
11. **Encoding of exported CSVs** differs per export (BOM for `IP扫描结果-*.csv`, no BOM for `WiFi密码.csv` /
    `IP批量扫描-*.csv`); Excel's rendering of the Chinese headers therefore differs between the three files. No CSV
    escaping is performed anywhere.
