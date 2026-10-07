# 03 — IP Batch Scan (IP批量扫描) & IP Address Calculator (IP地址计算器)

Reverse-engineering spec for rebuilding IPScaner V1.28.2 in WinUI 3.

**Sources of truth**

| Artefact | Path |
| --- | --- |
| Batch-scan form | `ipscaner-re/src/IPScaner/FormIPSegment.cs` (1105 lines) |
| Address-calculator form | `ipscaner-re/src/IPScaner/FormAddressCalc.cs` (622 lines) |
| Subnet math engine | `ipscaner-re/src/IPScaner/NetworkCalculator.cs` (208 lines) |
| Grid row model | `ipscaner-re/src/IPScaner/PingReplyInfo.cs` |
| Helpers (IP range, DNS, ARP) | `ipscaner-re/src/IPScaner/Utility.cs` |
| Runtime config | `ipscaner-re/src/IPScaner/ConfigInfo.cs`, `Global.cs`, `MemoryCacheManager.cs` |
| Memo store (selected model file) | `ipscaner-re/src/IPScaner.Models/IPBtnInfo.cs` |

**Menu entry points** (`FormMain.cs`)

| Menu item field | `Text` | Handler | Line |
| --- | --- | --- | --- |
| `mnuIPSegment` | `IP批量扫描(&S)` | `mnuIPSegment_Click` → `new FormIPSegment(txtIPPart.Text).Show()` | 1917, 1084 |
| `mnuAddressCalc` | `IP地址计算器(&C)` | `mnuAddressCalc_Click` → `new FormAddressCalc().Show()` | 1923, 1245 |

Both are non-modal (`Show()`), so several copies can coexist. `FormIPSegment` receives the main window's current `txtIPPart.Text` (an `A.B.C` prefix, possibly empty).

---

# PART A — IP Batch Scan (`FormIPSegment`)

`Text = "IP批量扫描"`, `ClientSize = 713 x 547`, `AutoScaleDimensions = 6F, 12F` / `AutoScaleMode.Font`, `StartPosition = CenterScreen`, `Padding = (5,6,5,6)`, `Icon = resources."$this.Icon"`.

## A.1 Constructor and one-time setup

```csharp
public FormIPSegment(string ipPart)          // FormIPSegment.cs:124
{
    InitializeComponent();
    Control.CheckForIllegalCrossThreadCalls = false;   // line 127 — cross-thread UI writes are intentionally allowed
    if (!string.IsNullOrEmpty(ipPart))
    {
        txtIPPart.Text  = ipPart;                       // e.g. "192.168.1"
        txtStartIP.Text = ipPart + ".1";
        txtEndIP.Text   = ipPart + ".254";
        string[] array = ipPart.Split('.');
        txtIP1.Value = int.Parse(array[0]);             // throws if the prefix is not 3 numeric parts
        txtIP2.Value = int.Parse(array[1]);
        txtIP3.Value = int.Parse(array[2]);
    }
    Load += FormIPSegment_Load;
}
```

`FormIPSegment_Load` (line 141):

```csharp
txtIPPart.SelectionStart = 0;
txtStartIP.SelectionStart = 0;
pingReplyList = new List<PingReplyInfo>();

dgvResult.Columns["colHostName"].Visible = Global.ConfigInfo.QueryHostNameEnabled;
dgvResult.Columns["colMAC"].Visible      = Global.ConfigInfo.QueryHostNameEnabled;
if (!Global.ConfigInfo.QueryHostNameEnabled)
    Width -= 100;                                   // window is 100 px narrower

dgvResult.AutoGenerateColumns = false;
dgvResult.DataError          += DgvService_DataError;          // empty handler, swallows everything
dgvResult.RowStateChanged    += DgvService_RowStateChanged;
dgvResult.ColumnHeaderMouseClick += DgvResult_ColumnHeaderMouseClick;
```

`AutoGenerateColumns = false` is set *after* the columns were added in `InitializeComponent`, so the six designer columns are the only columns. Default `Global.ConfigInfo.QueryHostNameEnabled == false` (`ConfigInfo.cs:189`), so **by default 主机名 and MAC are hidden and the window opens 100 px narrower**.

## A.2 Complete UI inventory

### A.2.1 `groupBox1` — `Text = "设置IP扫描范围"` (Dock=Top, 703 x 117)

| Control | Type | Exact `Text` | Bounds / size | Default value | Notes |
| --- | --- | --- | --- | --- | --- |
| `label1` | Label | `输入IP扫描段：` | (6,25), 宋体 10pt | — | |
| `txtIPPart` | TextBox | — | (96,22) 101x23, 宋体 10pt | empty | `KeyDown += txtSetIP_KeyDown` (digit filter) |
| `label3` | Label | `开始：` | (208,25) | — | prefix of the 单网段 range |
| `txtStart` | NumericUpDown | — | (247,23) 42x23 | `1` | `Minimum=1`, `Maximum=255` |
| `label4` | Label | `结束：` | (301,25) | — | |
| `txtEnd` | NumericUpDown | — | (345,23) 42x23 | `254` | `Minimum=1`, `Maximum=255` |
| `btnGeneral` | Button | `生成` | (412,18) 74x26 | — | `BackColor=LightBlue`, `ForeColor=Black`, 宋体 9pt |
| `toolTip1` on `btnGeneral` | ToolTip | `快速生成单网段所有IP地址` | — | — | |
| `toolTip1` on `txtIPSegment` | ToolTip | `可以手动将IP地址列表复制到此处` | — | — | |

`txtSetIP_KeyDown` (line 434) allows only: `D0–D9`, `NumPad0–NumPad9`, `OemPeriod`, `Decimal`, `Back`, `Delete`, `Left`, `Right`, `Home`, `End`, `Return`, and `Ctrl+C`. Paste is only inspected by a dead branch (`if (e.Control && e.KeyCode == Keys.V) return;` precedes the real `Ctrl+V` handler at line 441, so **plain Ctrl+V passes through unchecked**); only `Ctrl+Shift+V`-style distinct paths would reach the clipboard scan. Reproduce as: numeric/dot/editing keys only, Ctrl+C always allowed, Ctrl+V effectively unvalidated.

### A.2.2 `panel9` — mask-bits generator (5,52) 545x30

| Control | Type | Exact `Text` | Bounds | Default | Constraints |
| --- | --- | --- | --- | --- | --- |
| `label5` | Label | `IP/掩码位:` | (23,6), 宋体 10pt, `ForeColor=Black` | — | |
| `txtIP1` | NumericUpDown | — | (97,3) 45x23 | `192` | `Minimum=1`, `Maximum=999` |
| `txtIP2` | NumericUpDown | — | (149,3) 45x23 | `168` | `Maximum=999` |
| `txtIP3` | NumericUpDown | — | (201,3) 45x23 | `0` | `Maximum=999` |
| `txtIP4` | NumericUpDown | — | (253,3) 45x23 | `1` | `Maximum=999` |
| `label10` | Label | `/` | (314,7) | — | separator |
| `txtBit` | NumericUpDown | — | (342,3) 45x23 | `24` | `Minimum=1`, `Maximum=30` |
| `btnCalc` | Button | `生成` | (412,2) 74x26 | — | `BackColor=CornflowerBlue`, `ForeColor=Black` |
| tooltip `txtBit` | ToolTip | `子网掩码的位数，值在1~24之间` | — | — | tooltip is **stale**; real clamp is 1–30 |
| tooltip `btnCalc` | ToolTip | `批量生成多网段中的连续IP地址` | — | — | |

**Critical fidelity notes**
* `txtBit.Maximum = 30`, `Minimum = 1` — the UI can never produce `bits = 31` or `bits = 32`, so `NetworkCalculator`'s `"two hosts"` / `"one host"` branches are **unreachable from this dialog**.
* `txtIP1..txtIP4` allow up to **999**; values > 255 make `CalNBFL` fail (`ip>255`) and the dialog shows `掩码位异常，请重新输入`.
* The four counters are *independent*: the values are concatenated into `ip_1..ip_4`, no octet carry logic.

### A.2.3 `panel3` — contiguous IP range (5,85) 545x32

| Control | Type | Exact `Text` | Bounds | Default | Tooltip |
| --- | --- | --- | --- | --- | --- |
| `label6` | Label | `开始IP:` | (36,9), 宋体 10pt | — | — |
| `txtStartIP` | TextBox | — | (96,6) 118x23, 宋体 10pt | seeded `<prefix>.1` | `开始ip，例如192.168.0.1` |
| `label7` | Label | `结束IP:` | (218,9), 宋体 10pt | — | — |
| `txtEndIP` | TextBox | — | (272,6) 116x23, 宋体 10pt | seeded `<prefix>.254` | `结束IP, 例如192.168.1.254` |
| `btnGeneralBatch` | Button | `生成` | (411,3) 74x26 | — | `批量生成连续的IP地址`; `BackColor=CornflowerBlue`, `ForeColor=Black` |

`txtStartIP`/`txtEndIP` are plain TextBoxes with **no key filter** (unlike `txtIPPart`).

### A.2.4 `panel1` (Dock=Fill, padding 5,6,5,6) — the two work areas

`gbxIPArr` — `Dock=Left`, `Size = 144 x 338`, `Text = "IP扫描范围 (0)"`

| Control | Type | Exact `Text` | Notes |
| --- | --- | --- | --- |
| `txtIPSegment` | TextBox | — | `Dock=Fill`, `Multiline=true`, `ScrollBars=Vertical`, 宋体 10pt, `TabIndex=4`; tooltip `可以手动将IP地址列表复制到此处`; `TextChanged += txtIPSegment_TextChanged` |

The group caption is recomputed on every keystroke (line 506): if the text is non-empty, `gbxIPArr.Text = $"IP扫描范围 ({allIPArray.Length})"` where `allIPArray = txtIPSegment.Text.Split(['\r','\n'], RemoveEmptyEntries)`. **If the text becomes empty the caption is left at its previous value** (it is not reset to `IP扫描范围 (0)`).

`gbxResult` — `Dock=Fill`, `Text = "网络扫描结果"` (rewritten after each result), tooltip on the group: `点各个列头，可排序`.

`dgvResult` (`DataGridView`), `Dock=Fill`, `ReadOnly=true`, `AllowUserToAddRows=false`, `AllowUserToDeleteRows=false`, `BackgroundColor=SystemColors.Control`, `ColumnHeadersHeightSizeMode=AutoSize`, `RowHeadersWidth=60`, `RowTemplate.Height=23`, `TabIndex=6`, tooltip `点各个列头，可升序或降序排序`.

`RowHeadersDefaultCellStyle`: `Alignment=MiddleLeft`, `BackColor=Control`, `Font=宋体 9pt` (GdiCharSet 134), `ForeColor=WindowText`, `SelectionBackColor=Highlight`, `SelectionForeColor=HighlightText`.

### A.2.5 `panel2` (Dock=Bottom, 703 x 42) — action bar

| Control | Type | Exact `Text` | Bounds | Notes |
| --- | --- | --- | --- | --- |
| `chkInterval` | CheckBox | `定时扫描` | (20,14) 72x16, `AutoSize` | tooltip `定时执行批量扫描任务`; `CheckedChanged += chkInterval_CheckedChanged` |
| `txtMins` | NumericUpDown | — | (88,13) 33x21 | `Minimum=1`, `Maximum=59`, `Value=1`; tooltip `定时执行批量扫描任务的间隔时长` |
| `label2` | Label | `分` | (125,15) | unit suffix for `txtMins` |
| `btnStart` | Button | `开始扫描` | (173,8) 83x27, `TabIndex=6` | |
| `btnReset` | Button | `清空日志` | (272,8) 83x27, `TabIndex=7` | tooltip `清空IP扫描范围和结果数据` |
| `btnCopy` | Button | `导出结果` | (369,8) 83x27, `TabIndex=8` | `Image = resources."btnCopy.Image"`, `ImageAlign=MiddleLeft`, `TextAlign=MiddleRight` |

### A.2.6 `statusStrip1` (Dock bottom of the form interior, 26 px)

| Item | Name | Initial `Text` | Decoration |
| --- | --- | --- | --- |
| 1 | `statuslabel` | `状态栏` | none |
| 2 | `status` | `准备就绪` | `BorderSides = Left` |
| 3 | `statusNext` | `-` | `BorderSides = Left` |

`ImageScalingSize = 28 x 28`, `Padding = (1,0,8,0)`.

**Z-order / docking** (`Controls.Add` order in `InitializeComponent`, line 1067): `panel3`, `panel1`, `panel9`, `panel2`, `statusStrip1`, `groupBox1`; `Padding = (5,6,5,6)`.

## A.3 The three input modes — exact list generation

`txtIPSegment` is always the single source of truth for *what gets scanned*. All three generators append into it; nothing is scanned until `开始扫描` is pressed.

### Mode 1 — 单网段 (single segment): `btnGeneral` / `btnGeneral_Click` (line 460)

```csharp
string input = txtIPPart.Text;
Regex regex = new Regex(@"^(\d{1,3}\.\d{1,3}\.\d{1,3})(\.\d{1,3})?");
if (regex.IsMatch(input))
{
    input = regex.Match(input).Groups[1].Value;   // keep only the first three octets
    int num  = (int)txtStart.Value;
    int num2 = (int)txtEnd.Value;
    if (num <= num2)
    {
        string startIP = $"{input}.{num}";
        string endIP   = $"{input}.{num2}";
        AppendIPPart(Utility.GetIPRange(startIP, endIP));
    }
    else
    {
        MessageBox.Show("开始IP不得大于结束IP", Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
    }
}
else
{
    MessageBox.Show("请输入正确的IP扫描段", Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
}
```

Edge cases and exact behaviour:
* The regex is **anchored at `^` but not at `$`**, and the 4th octet group is optional and **discarded**. `192.168.1.77` yields prefix `192.168.1`. Leading zeros are accepted (`001.002.003` → `001.002.003`, and `Utility.IpToUInt` uses `byte.Parse`, so `001.002.003.x` parses fine).
* Octets are **not range-checked** here: `999.999.999` matches the regex, then `Utility.IpToUInt` calls `byte.Parse("999")` and throws `OverflowException`, which propagates out of the click handler (unhandled). Only `\d{1,3}` length is enforced.
* The list is `prefix . start .. prefix . end` inclusive, generated by `Utility.GetIPRange(startIP, endIP)`.
* `num <= num2` compares the two NumericUpDowns; equal values are allowed and produce a one-entry list.
* Errors go to a modal `MessageBox` with caption = the form's `Text` (`IP批量扫描`) and `MessageBoxIcon.Exclamation`.

### Mode 2 — 连续IP范围 (contiguous IP range): `btnGeneralBatch` / `btnGeneralBatch_Click` (line 581)

```csharp
string startIP = txtStartIP.Text;
if (!Utility.ValidIP(startIP)) {
    MessageBox.Show("请输入正确的开始IP地址", Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
    return;
}
string endIP = txtEndIP.Text;
if (!Utility.ValidIP(endIP)) {
    MessageBox.Show("请输入正确的开始IP地址", Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation); // same string as above
    return;
}
AppendIPPart(Utility.GetIPRange(startIP, endIP));
```

* `Utility.ValidIP` is `new Regex(@"(^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})").IsMatch(text)` — prefix match, **no `$` anchor**, so `192.168.1.5garbage` passes; octets are still not range-checked → `999.999.999.999` passes the regex and then throws in `byte.Parse`.
* **Both branches show the identical string** `请输入正确的开始IP地址` (the end-IP branch is a copy/paste bug in the original; reproduce faithfully or fix deliberately — flag it).
* No `start <= end` check: if start > end, `GetIPRange`'s `for (uint i = start; i <= end; i++)` never runs and **an empty list is appended silently** — no message, no error.
* Trailing/leading whitespace is not trimmed; a padded string fails the regex.

### Mode 3 — 掩码位 (mask bits): `btnCalc` / `btnCalc_Click` (line 556)

```csharp
var networkCalculator = new NetworkCalculator();
var addressFields = new NetworkCalculator.AddressFields {
    ip_1 = txtIP1.Value.ToString(), ip_2 = txtIP2.Value.ToString(),
    ip_3 = txtIP3.Value.ToString(), ip_4 = txtIP4.Value.ToString(),
    bits = txtBit.Value.ToString()
};
if (!networkCalculator.CalNBFL(addressFields))
{
    MessageBox.Show("掩码位异常，请重新输入", Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
    return;
}
string startIP = $"{addressFields.firstadr_1}.{addressFields.firstadr_2}.{addressFields.firstadr_3}.{addressFields.firstadr_4}";
string endIP   = $"{addressFields.lastadr_1}.{addressFields.lastadr_2}.{addressFields.lastadr_3}.{addressFields.lastadr_4}";
List<string> iPRange = Utility.GetIPRange(startIP, endIP);
if (iPRange.Count <= 1000 ||
    MessageBox.Show("当前生成的IP地址已超过1000个，你确认要继续吗？", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.Cancel)
{
    AppendIPPart(iPRange);
}
```

* The generated range is **`firstadr .. lastadr`** from `NetworkCalculator`, i.e. the usable-host range, and it is then passed **again** through `Utility.GetIPRange(..., isSkipCast = true)`, which drops any address whose last octet is `0` or `255`. For `/24` the first/last usable are `.1`/`.254` so nothing extra is dropped; for e.g. `/23` on `192.168.0.0` the boundary `192.168.1.0` and `192.168.1.255` are silently dropped.
* Failure (`CalNBFL` returns `false`) covers: any octet > 255, `bits` unparsable/out of 0..32, **and also the `bits == 31` / `bits == 32` special cases** — so even if the UI were widened to 31/32, the user gets `掩码位异常，请重新输入` and **nothing is appended**.
* Duplicate-IP de-duplication happens in `AppendIPPart`.
* Threshold prompt is `> 1000`; `OK` or window-close continues, `Cancel` aborts.

### Appending (`AppendIPPart`, line 487) and the range helper

```csharp
private void AppendIPPart(List<string> newIPs)
{
    string[] allIPArray = GetAllIPArray();               // current textbox lines
    StringBuilder sb = new StringBuilder();
    foreach (string newIP in newIPs)
        if (!allIPArray.Contains(newIP)) sb.AppendLine(newIP);
    txtIPSegment.AppendText(sb.ToString());
}

private string[] GetAllIPArray() =>
    txtIPSegment.Text.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
```

* Deduplication compares against the **pre-append** snapshot only, so two entries inside the *same* generated batch are never de-duplicated (they cannot occur anyway).
* `AppendText` uses `\r\n` per line; the last appended line ends with a newline.
* Therefore the textbox is a plain **one-IP-per-line list**, and a user may type/paste arbitrary text; `开始扫描` accepts anything non-empty and passes each line to `Ping.SendAsync` (invalid strings simply produce a `PingException` inside the completion callback).

### `Utility.GetIPRange` semantics (Utility.cs:188)

```csharp
public static List<string> GetIPRange(string startIP, string endIP, bool isSkipCast = true)
{
    List<string> list = new List<string>();
    uint num  = IpToUInt(startIP);
    uint num2 = IpToUInt(endIP);
    for (uint num3 = num; num3 <= num2; num3++)
        if (!isSkipCast || ((num3 & 0xFF) != 255 && (num3 & 0xFF) != 0))
            list.Add(UIntToIp(num3));
    return list;
}
```

* Ascending, inclusive of both ends, skipping every address ending in `.0` or `.255`.
* `isSkipCast` is never passed `false` anywhere in these two forms.
* Both helpers are `private static`; `IpToUInt` uses `byte.Parse` per octet (throws `OverflowException`/`FormatException` on out-of-range or non-numeric octets — the only guard is the loose regexes above).
* The loop is a `uint` counter; if `endIP` were `255.255.255.255` and start `0.0.0.0`, `num3++` wraps and the loop terminates (theoretical only).

## A.4 Results grid — columns and cell provenance

`dgvResult.Columns.AddRange(colIP, colHostName, colMAC, colMemo, colStatus, colTime)` — order matters and is the display order.

| # | Field name | `HeaderText` | `DataPropertyName` | `SortMode` (designer) | `AutoSizeMode` | Width / Min | Source of the value |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 0 | `colIP` | `IP` | `IP` | `NotSortable` | `Fill` | `MinimumWidth = 9` | `e.UserState.ToString()` — the exact string that was sent to `Ping.SendAsync`, i.e. the line from `txtIPSegment` |
| 1 | `colHostName` | `主机名` | `HostName` | (default `Automatic`) | — | 80, `MinimumWidth = 9` | `Utility.GetHostName(ip)` = `Dns.GetHostEntry(ip).HostName`; on exception the literal `未知`; `.local`/`domain` suffixes stripped case-sensitively in a loop |
| 2 | `colMAC` | `MAC` | `MAC` | (default `Automatic`) | — | 80, `MinimumWidth = 9` | `Utility.GetMacAddressFromARP(ip)` (ARP table scrape); `null` on failure → empty cell |
| 3 | `colMemo` | `备注` | `Memo` | `NotSortable` | `Fill` | `MinimumWidth = 9` | computed property `PingReplyInfo.Memo` → `Global.GetMemoByMacOrIp(MAC, IP)`, MAC-keyed first then IP-keyed, else `string.Empty` |
| 4 | `colStatus` | `状态` | `Status` | `NotSortable` | — | 40, `MinimumWidth = 9` | computed property `PingReplyInfo.Status` → `Success ? "OK" : "NG"` |
| 5 | `colTime` | `时间` | `Time` | `NotSortable` | — | 60, `MinimumWidth = 9` | `PingReplyInfo.Time`: `RoundtripTime < 1 → "<1ms"`, else `$"{time}ms"`, else `"Timeout"` |

**Row header** (`DgvService_RowStateChanged`, line 243): `e.Row.HeaderCell.Value = (e.Row.Index + 1).ToString()` — 1-based row number, so re-sorting renumbers rows.

**Row background colour** (same handler):

```csharp
string text = e.Row.Cells["colStatus"].Value?.ToString();
if      (text == "OK") e.Row.DefaultCellStyle.BackColor = Color.LightGreen;
else if (text == "NG") e.Row.DefaultCellStyle.BackColor = Color.LightCoral;
else                   e.Row.DefaultCellStyle.BackColor = Color.White;
```

Note these are **hard-coded** `LightGreen` / `LightCoral` / `White` — **not** `Global.ConfigInfo.NetworkOKColor` (`LimeGreen`) / `NetworkNGColor` (`IndianRed`), which the main window uses. A WinUI rebuild should decide whether to keep the hard-coded batch-scan colours or unify with config.

**Underlying model** `PingReplyInfo` (`PingReplyInfo.cs`):

```csharp
public class PingReplyInfo
{
    public string IP { get; set; }
    public string HostName { get; set; }
    public string MAC { get; set; }
    public string Time { get; set; }
    public long   RoundtripTime { get; set; }
    public bool   Success { get; set; }
    public string Status => Success ? "OK" : "NG";
    public string Memo   => Global.GetMemoByMacOrIp(MAC, IP);

    public PingReplyInfo(string ip, IPStatus status, long time = -1L)
    {
        IP = ip;
        Success = status == IPStatus.Success;
        if (Success && time >= 0)
        {
            RoundtripTime = time;
            Time = (time < 1) ? "<1ms" : time + "ms";
        }
        else
        {
            Time = "Timeout";              // also used for non-Success non-timeout statuses
            RoundtripTime = 2147483647L;   // int.MaxValue sentinel for sorting
        }
    }
}
```

* `Time` says `Timeout` even for `DestinationHostUnreachable`, `TimedOut`, etc. — the raw `IPStatus` is **not** surfaced anywhere in the grid.
* There is **no column for `IPStatus`**, no column for `Success`, and no `PingCount` retry in this dialog (`Global.ConfigInfo.PingCount` is unused here; the default is 4).

**Hostname / MAC enrichment** (`GetHostNameAndMac`, line 370) — only when `Global.ConfigInfo.QueryHostNameEnabled` is true:

```csharp
if (!Global.ConfigInfo.QueryHostNameEnabled) return;
ThreadPool.QueueUserWorkItem(obj => {
    var info = obj as PingReplyInfo;
    info.HostName = "查询中";       // placeholder strings shown while resolving
    info.MAC      = "查询中";
    statusNext.Text = "正在查询" + info.IP + " 主机名";
    string hostName = Utility.GetHostName(info.IP);
    info.HostName = hostName;
    statusNext.Text = "正在查询" + info.IP + " MAC";
    string mac = Utility.GetMacAddressFromARP(info.IP);
    info.MAC = mac;
    statusNext.Text = string.Empty;
    if (!dgvResult.Disposing && !dgvResult.IsDisposed)
        dgvResult.BeginInvoke((Action)(() => dgvResult.Refresh()));
}, info);
```

`Utility.GetMacAddressFromARP` details worth preserving:
1. If the IP belongs to a local adapter (`Global.AdapterList`), return that adapter's MAC with `:` replaced by `-` (after re-enumerating adapters and a bogus `ValidIP(...)` sanity check).
2. Otherwise consult `MemoryCacheManager` (1-hour absolute expiration, key `"{DeclaringType.FullName}.{MethodName} - {ip}"`).
3. Otherwise `DeleteARPTable()` — which runs `arp -d *` at most once per hour (`lastExecutionTime` guard) — then runs `arp -a` with `RedirectStandardOutput`, scanning lines containing the IP for a token matching `([0-9A-Fa-f]{2}[-:]){5}([0-9A-Fa-f]{2})`, returning it `.ToUpper()` and caching it.
4. Returns `null` if nothing matched.

`Utility.GetHostName` caches `未知` too, and strips trailing `domain` / `.local` (case-sensitive `EndsWith`, `string.Replace`) from the resolved name.

## A.5 Sorting when a column header is clicked

```csharp
private void DgvResult_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
{
    int columnIndex = e.ColumnIndex;
    SortData(dgvResult.Columns[columnIndex].Name);   // name-based dispatch
    isAscending = !isAscending;                      // toggled AFTER the sort
}
```

* `isAscending` is initialised to `true` (line 18) and flipped *after* sorting, so the **first click on any column sorts DESCENDING**; the second click sorts ascending; alternating thereafter.
* The toggle is **global, not per column** — clicking a different column continues the same alternation.
* `SortData` (line 165) starts with `List<PingReplyInfo> list = (List<PingReplyInfo>)dgvResult.DataSource;` — a hard cast, so it throws if `DataSource` is `null` (i.e. **clicking any header before the first scan result arrives throws `NullReferenceException`/`InvalidCastException`**).
* `SortData` clears and immediately restores `DataSource` before re-assigning the sorted list.
* Sort keys per column:

| Column | Ascending | Descending |
| --- | --- | --- |
| `colIP` | `orderby ParseIPAddress(p.IP)[0], [1], [2], [3]` | same, all four `descending` |
| `colHostName` | `list.OrderBy(p => p.HostName)` | `OrderByDescending(p => p.HostName)` |
| `colMAC` | `list.OrderBy(p => p.MAC)` | `OrderByDescending(p => p.MAC)` |
| `colMemo` | `list.OrderBy(p => p.Memo)` | `OrderByDescending(p => p.Memo)` |
| `colStatus` | `list.OrderBy(p => p.Status)` | `OrderByDescending(p => p.Status)` — **string sort on `"OK"`/`"NG"`**, so ascending puts all `NG` before all `OK` |
| `colTime` | `list.OrderBy(p => p.RoundtripTime)` | `OrderByDescending(p => p.RoundtripTime)` — numeric, so timeouts (`int.MaxValue`) always sort last ascending |

* `Utility.ParseIPAddress(p.IP)` = `ip.Split('.').Select(int.Parse).ToArray()` — throws if `IP` is not four numeric octets.
* **Sorting is cosmetic only and is destroyed by the next result**: `Ping_PingCompleted` reassigns `dgvResult.DataSource = pingReplyList` on every completion, restoring insertion order. It also does not touch `pingReplyList` itself, so the sorted view and the exported order diverge.
* Headers show **no sort glyph**; the click handler drives ordering directly.
* Columns whose designer `SortMode` is `NotSortable` (`colIP`, `colMemo`, `colStatus`, `colTime`) still sort, because the handler is a raw mouse-click handler, not the built-in sorting path. `colHostName` and `colMAC` keep the default `Automatic`, which in WinForms only affects glyph rendering.

## A.6 Scheduled / timed auto-scan (定时扫描)

**Interval unit: minutes.** `txtMins` (`NumericUpDown`, `Minimum=1`, `Maximum=59`, default `1`) with the literal suffix label `分`. There is no seconds/hours option.

**Arming:** checking `chkInterval` does **not** start a scan. `chkInterval_CheckedChanged` (line 515) only:

```csharp
if (Disposing || IsDisposed) return;
if (chkInterval.Checked)
{
    if (nextTimer == null)
    {
        nextTimer = new System.Windows.Forms.Timer();
        nextTimer.Interval = 1000;        // 1 s UI ticker
        nextTimer.Tick += NextTimer_Tick;
        nextTimer.Start();
    }
    btnGeneral.Enabled   = false;         // Mode 1 blocked
    txtIPSegment.ReadOnly = true;         // list frozen
}
else
{
    btnGeneral.Enabled   = true;
    txtIPSegment.ReadOnly = false;
}
```

* `nextTimer` is created **once, lazily, and never disposed** (`Dispose(bool)` only disposes `components`). Unchecking the box **does not stop the timer** — it keeps ticking every second forever, but `NextTimer_Tick` does nothing when `timerEnabled == false`.
* Unchecking does **not** stop a running timed loop either; the loop re-reads `chkInterval.Checked` (see below) and exits at the next check point.
* Note the asymmetry: `btnCalc`/`btnGeneralBatch` (modes 2 and 3) are **not** disabled while 定时扫描 is on, but they append via `txtIPSegment.AppendText`, which still works even with `ReadOnly = true` (programmatic writes bypass `ReadOnly`), mutating the frozen list.

**Scan loop** (`ScanIP`, line 299 — runs on a background thread started by `StartScanIP`):

```csharp
if (chkInterval.Checked)
{
    while (chkInterval.Checked)
    {
        pingReplyList.Clear();
        dgvResult.DataSource = null;
        dgvResult.Refresh();
        foreach (string ip in ipAddressList)
        {
            Ping ping = new Ping();
            ping.PingCompleted += Ping_PingCompleted;
            ping.SendAsync(ip, Global.ConfigInfo.PingTimeout, ip);
            status.Text = "正在扫描IP: " + ip;
            Thread.Sleep(10);
        }
        if (!chkInterval.Checked) break;
        status.Text     = $"等待{txtMins.Value}分钟后，进行下一轮扫描";
        statusNext.Text = $"剩余时间{txtMins.Value * 60m} 秒";
        lastRefreshTime = DateTime.Now;
        timerEnabled    = true;
        Thread.Sleep((int)txtMins.Value * 1000 * 60);   // whole wait inside the worker thread
        timerEnabled    = false;
    }
}
```

* **Start** = `chkInterval` checked *and* `开始扫描` pressed. **Stop** = uncheck `chkInterval`; the wait is a plain `Thread.Sleep` that cannot be interrupted, so the loop only exits after the full sleep elapses (up to 59 minutes later). There is no `CancellationToken`, no `Thread.Interrupt`, no `Abort`.
* **Re-entry**: `btnStart.Enabled = false` is set in `btnStart_Click` and restored to `true` only after the worker leaves the loop — but `ScanIP` also sets `btnStart.Enabled = true; status.Text = "批量扫描完成";` after the loop, which in the timed case is also after the final wait. `btnStart` is never re-enabled *between* rounds. However, `Control.CheckForIllegalCrossThreadCalls = false` plus the fact that nothing prevents a second `btnStart_Click` (e.g. after the button is re-enabled) means a second worker can be spawned; **there is no guard against two concurrent `ScanIP` workers**, and `pingReplyList.Clear()` in either worker discards the other's results.
* Each round resets the list/grid first, then re-pings every address; results accumulate for the whole round via the `PingCompleted` callback.
* **Countdown display** is driven by the 1-second WinForms timer (`NextTimer_Tick`, line 540) which runs on the UI thread:

```csharp
if (timerEnabled)
{
    TimeSpan ts = lastRefreshTime.AddMinutes((int)txtMins.Value).Subtract(DateTime.Now);
    if (ts.TotalSeconds > 0) statusNext.Text = "剩余时间" + ts.ToString("m\\分s") + "秒";
    else                     statusNext.Text = "正在批量扫描中...";
}
```

  * Note the format string `"m\分s"` plus a **literal trailing `秒`**, so a rendered value looks like `剩余时间0分37秒` — and note that the initial one-shot string set by the worker is `剩余时间{minutes*60} 秒` (with a space, no `分`), so the two messages are inconsistent by design in the original.
  * The countdown recomputes from `txtMins.Value` at tick time; if the user edits `txtMins` mid-wait, the displayed countdown desynchronises from the actual `Thread.Sleep` (which captured the value once).
  * While the loop is actively pinging, `timerEnabled` is `false`, so `statusNext` keeps whatever the enrichment code last wrote (often `string.Empty`).
* `lastRefreshTime` is only used for this countdown; it is re-set at the start of every wait.
* Timed rounds never clear `MemoryCacheManager` entries (1-hour TTL for DNS/ARP), so repeat rounds may report cached hostnames/MACs for up to an hour.

## A.7 Export to Excel (`btnCopy` — 导出结果)

```csharp
private void btnCopy_Click(object sender, EventArgs e)
{
    if (pingReplyList.Count == 0) return;

    StringBuilder stringBuilder = new StringBuilder();
    stringBuilder.AppendLine("IP,主机名,MAC,备注,状态,时间");
    foreach (PingReplyInfo r in pingReplyList)
        stringBuilder.AppendLine(r.IP + "," + r.HostName + "," + r.MAC + "," + r.Memo + "," + r.Status + "," + r.Time);

    string text = Path.Combine(Application.StartupPath,
                               "IP批量扫描-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".csv");
    File.WriteAllText(text, stringBuilder.ToString());

    if (!File.Exists(text) ||
        MessageBox.Show("IP批量扫描导出成功。是否要打开Excel文档？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        return;
    try { Process.Start(text); }
    catch (Exception ex) { MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Hand); }
}
```

Answers to the specific questions:

* **Format: a real comma-separated `.csv` file** — *not* an `.xlsx`, *not* an HTML table, *not* tab-separated. Plain text lines.
* **Encoding: `File.WriteAllText(path, text)` with no `Encoding` argument → .NET's `UTF8Encoding(encoderShouldEmitUTF8Identifier: false)`**, i.e. **UTF-8 without BOM**. Consequence: double-clicking the file in Chinese-locale Excel typically renders the Chinese header/values as mojibake, because Excel assumes the ANSI code page. A faithful-but-corrected WinUI port should write **UTF-8 *with* BOM** (or `Encoding.UTF8` / `new UTF8Encoding(true)`) — call this out as an intentional deviation.
* **Line endings:** `StringBuilder.AppendLine` uses `Environment.NewLine` (`\r\n` on Windows). The final line also ends with a newline.
* **No CSV escaping whatsoever**: fields containing `,` `"` or newlines break the file. Hostnames and memos are the realistic offenders.
* **Header row:** `IP,主机名,MAC,备注,状态,时间` — i.e. the Chinese grid headers, in grid column order.
* **Row order = insertion order of `pingReplyList`**, *not* the current grid sort order, and *not* filtered by anything.
* **File naming:** `IP批量扫描-yyyyMMddHHmmss.csv`, e.g. `IP批量扫描-20240115093012.csv` (17 Chinese chars + hyphen + 14 digits + `.csv`).
* **Location:** `Application.StartupPath` — the folder containing the executable, **not** the working directory and **not** a user-chosen folder. There is no Save-As dialog and no overwrite prompt; the timestamp gives de-facto uniqueness only down to the second.
* **Post-export:** on success a `MessageBox` with `IP批量扫描导出成功。是否要打开Excel文档？` and `YesNo` + `Question` icon; `Yes` calls `Process.Start(path)` (shell-open with the default `.csv` handler). Failure to open shows the raw exception message with `MessageBoxIcon.Hand`.
* Nothing is exported (silent no-op) when `pingReplyList.Count == 0`.

## A.8 Concurrency, timeouts, cancellation, UI marshalling

* **Dispatch loop**: `btnStart_Click` (line 264) clears `ipAddressList`/`pingReplyList`, nulls the `DataSource`, calls `Refresh()`, computes `GetAllIPArray()`, bails out if it is empty, fills `ipAddressList` with non-empty lines, sets `status.Text = $"准备批量扫描 共有{ipAddressList.Count}个IP"`, disables `btnStart`, then `StartScanIP()`.
* **One background thread for the whole batch** (`StartScanIP`, line 292): `new Thread(ScanIP) { IsBackground = true }.Start()` — a new thread per scan, never joined, never cancelled.
* **Concurrency model: fire-and-forget, no explicit parallelism cap.**

```csharp
foreach (string ipAddress in ipAddressList)
{
    Ping ping = new Ping();                            // one Ping instance PER ADDRESS, never reused
    ping.PingCompleted += Ping_PingCompleted;
    ping.SendAsync(ipAddress, Global.ConfigInfo.PingTimeout, ipAddress);   // userState = the IP string
    status.Text = "正在扫描IP: " + ipAddress;
    Thread.Sleep(10);                                  // 10 ms pacing between dispatch calls
}
```

  * So the effective pacing is **one new ICMP request every 10 ms ≈ 100/s**, with all outstanding requests overlapping. `Ping.SendAsync` completes on an IOCP/thread-pool callback, so the practical in-flight ceiling is imposed by the thread pool (historically ~`ProcessorCount * 64` completions before queuing), not by application code. **There is no `SemaphoreSlim`, no `Parallel.ForEach`, no `MaxDegreeOfParallelism`.**
  * 1000 IPs ≈ 10 s of dispatch + however long the tail of queued pings takes.
* **Timeout:** `Global.ConfigInfo.PingTimeout` (default **500 ms**, `ConfigInfo.cs:190`) is passed as the per-ping timeout: `ping.SendAsync(address, timeout, userState)`. There is exactly **one** ping attempt per address (no `PingCount` retries in this dialog).
* **Cancellation: none.** Closing the form, pressing 清空日志, or unchecking 定时扫描 does not stop dispatch or in-flight pings. The `try { } catch { }` around the whole `Ping_PingCompleted` body (line 365) swallows every exception, which is what makes this survivable — including the `ObjectDisposedException` from `dgvResult.BeginInvoke` after the form closes.
* **UI-thread marshalling — two different mechanisms, inconsistently used:**
  1. `Control.CheckForIllegalCrossThreadCalls = false` in the constructor disables WinForms' cross-thread guard, so direct writes like `status.Text = ...` from the worker thread are allowed (and are the original's de-facto marshalling).
  2. `dgvResult.BeginInvoke((Action)(...))` — asynchronous post to the UI thread — for the grid rebind and for `dgxResult.Refresh()` after enrichment.
  There is a race window: `Ping_PingCompleted` reads `pingReplyList` from the worker's completion thread and rebinds the grid, while the ScanIP thread may `Clear()` the same list for the next timed round. Reproduce in WinUI with an `ObservableCollection` bound on the dispatcher + an explicit lock or a per-round local list.
* **Result handling per completion** (`Ping_PingCompleted`, line 343):

```csharp
Ping ping = sender as Ping;
ping.Dispose();                                   // the per-address Ping is disposed here
PingReply reply = e.Reply;
string ip = e.UserState.ToString();
PingReplyInfo info = new PingReplyInfo(ip, reply.Status, reply.RoundtripTime);
if (reply.Status == IPStatus.Success) GetHostNameAndMac(info);   // queues a ThreadPool work item per OK host
pingReplyList.Add(info);
dgvResult.BeginInvoke(() => {
    dgvResult.DataSource = null;                  // full rebind on EVERY result (no incremental row add)
    dgvResult.DataSource = pingReplyList;
    dgvResult.ClearSelection();
    gbxResult.Text = $"网络扫描结果 (OK:{pingReplyList.Count(r => r.Success)}, NG:{pingReplyList.Count(r => !r.Success)})";
});
```

  * Enrichment adds a **second layer of unbounded concurrency**: one `ThreadPool.QueueUserWorkItem` per successful host, each doing a blocking DNS lookup plus an `arp -a` process launch. The `arp -d *` purge is rate-limited to once per hour globally.
  * The grid is **rebound from scratch on every single result** — with *n* hosts that is *n* full rebinds, each resetting selection. A WinUI port should use a bound `ObservableCollection<PingRowViewModel>` instead.
  * `gbxResult.Text` becomes `网络扫描结果 (OK:x, NG:y)`, e.g. `网络扫描结果 (OK:12, NG:254)`. Note the space before the parenthesis and the `OK:`/`NG:` labels.
  * `dgvResult.DataError += DgvService_DataError` is an empty handler — all binding/data errors are silently ignored.

## A.9 Progress / status reporting

Three status surfaces plus the group caption:

| Surface | Message | When |
| --- | --- | --- |
| `status` (middle cell) | `准备就绪` | initial designer value |
| `status` | `准备批量扫描 共有{N}个IP` | `btnStart_Click`, after the list is built, before the worker starts. Note: no punctuation, a space before `共有`, and the literal `个IP` |
| `status` | `正在扫描IP: {ip}` | set synchronously in the dispatch loop after each `SendAsync` (so it shows the most recently *dispatched*, not the most recently *completed*, address) |
| `status` | `等待{M}分钟后，进行下一轮扫描` | timed mode, start of each wait (`M` = `txtMins.Value`) |
| `status` | `批量扫描完成` | end of `ScanIP` (both immediate and timed mode) |
| `statusNext` (right cell) | `-` | initial designer value |
| `statusNext` | `剩余时间{M*60} 秒` | timed mode, set once when the wait begins (note the space before `秒`) |
| `statusNext` | `剩余时间{m分s}秒` | every 1 s tick via `NextTimer_Tick` (`"m\\分s"` format + literal `秒`) |
| `statusNext` | `正在批量扫描中...` | tick when the computed remaining time is ≤ 0 |
| `statusNext` | `正在查询{ip} 主机名` / `正在查询{ip} MAC` / `string.Empty` | enrichment worker, only when `QueryHostNameEnabled` |
| `gbxResult.Text` | `网络扫描结果` → `网络扫描结果 (OK:{ok}, NG:{ng})` | group box caption, recomputed on every completed ping |
| `gbxIPArr.Text` | `IP扫描范围 ({count})` | on every `txtIPSegment.TextChanged` with non-empty text |
| Row header cells | `1`, `2`, `3`, … | on every `RowStateChanged` |
| Row background | `LightGreen` / `LightCoral` / `White` | by `colStatus` value |

There is **no progress bar, no percentage, and no per-row spinner**. The only per-item progress is `status.Text` overwriting itself in the dispatch loop.

## A.10 Other handlers

* `btnReset_Click` (line 398) — `清空日志`: `txtIPSegment.Clear(); pingReplyList.Clear(); dgvResult.DataSource = null; dgvResult.Refresh();`. It **does not** reset `txtIPPart`/`txtStart`/`txtEnd`/`txtStartIP`/`txtEndIP`/`txtIP1..4`/`txtBit`/`txtMins`/`chkInterval`, does **not** stop a running scan or the timed loop, and does **not** reset `gbxIPArr.Text`/`gbxResult.Text`/`status`/`statusNext`.
* `DgvService_DataError` — intentionally empty.
* `btnCalc` (mask mode) shows **no** result preview; it only appends to the list. The subnet details are only visible in `FormAddressCalc`.

---

# PART B — IP Address Calculator (`FormAddressCalc`)

`Text = "IP地址计算器"`, `BackColor = Color.White`, `ForeColor = Color.Black`, `FormBorderStyle = FixedSingle`, `MaximizeBox = false`, `MinimizeBox = false`, `StartPosition = CenterScreen`, `ClientSize = 452 x 331`, `AutoScaleDimensions = 6F,12F` / `AutoScaleMode.Font`, `AcceptButton = btnCalc` (Enter triggers 计算). Layout root: `tableLayoutPanel1` at (12,12), `Size 431 x 313`, `ColumnCount = 1` (100 %), `RowCount = 10` (all `RowStyle` default = `AutoSize`).

## B.1 Complete UI inventory (row by row in `tableLayoutPanel1`)

| Row | Panel | Child controls — exact `Text` / field names |
| --- | --- | --- |
| 0 | `panel1` (408x19) | `label1` — Label, `Text = 网络和IP地址计算器`, font **黑体 13pt Bold**, `ForeColor=Black`, `Dock=Fill`. (This is the in-form title; the window caption is `IP地址计算器`.) |
| 1 | `panel10` (408x24) | `label2` — Label, `Text = 显示网络，广播，第一个和最后一个给定的网络地址`, `Dock=Fill`, `TextAlign=MiddleLeft` |
| 2 | `panel9` (408x32) | `label3` = `IP/掩码位:`, `txtIP1`, `txtIP2`, `txtIP3`, `txtIP4`, `label10` = `/`, `txtBit`, `btnCalc` = `计算` |
| 3 | `panel8` (408x24) | `label4` = `可用地址:`, `txtNumofaddr` (read-only) |
| 4 | `panel7` (408x24) | `label5` = `掩码:`, `txtSnm1`, `txtSnm2`, `txtSnm3`, `txtSnm4` (read-only) |
| 5 | `panel6` (408x24) | `label6` = `网络:`, `txtNwadr1`, `txtNwadr2`, `txtNwadr3`, `txtNwadr4` (read-only) |
| 6 | `panel5` (408x24) | `label7` = `首个可用:`, `txtFirstadr1..4` (read-only) |
| 7 | `panel4` (408x24) | `label8` = `末个可用：`, `txtLastadr1..4` (read-only) — note this label uses a **full-width colon `：`** while all others use the ASCII `:` |
| 8 | `panel2` (408x24) | `label9` = `广播:`, `txtBcast1..4` (read-only) |
| 9 | `panel3` (408x24) | `label11` = `网络掩码“位格式”也被称为CIDR格式(CIDR=无类别域间路由选择)。` — footnote, uses curly quotes `“”` |

### B.1.1 Input row (`panel9`) in detail

| Field name | Type | `Text` | Location | Size | `Minimum` | `Maximum` | Default `Value` | Font |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `label3` | Label | `IP/掩码位:` | (4,10) | 65x12 | — | — | — | default (宋体 9pt) |
| `txtIP1` | NumericUpDown | — | (78,3) | 45x26 | **1** | **999** | `192` | 宋体 12pt (GdiCharSet 134) |
| `txtIP2` | NumericUpDown | — | (130,3) | 45x26 | 0 | **999** | `168` | 宋体 12pt |
| `txtIP3` | NumericUpDown | — | (182,3) | 45x26 | 0 | **999** | `0` | 宋体 12pt |
| `txtIP4` | NumericUpDown | — | (234,3) | 45x26 | 0 | **999** | `1` | 宋体 12pt |
| `label10` | Label | `/` | (280,10) | 11x12 | — | — | — | default |
| `txtBit` | NumericUpDown | — | (291,3) | 45x26 | **1** | **30** | `24` | 宋体 12pt |
| `btnCalc` | Button | `计算` | (342,1) | 46x31 | — | — | — | `BackColor=CornflowerBlue`, `ForeColor=White`, `UseVisualStyleBackColor=false` |

**There are no tooltips anywhere on this form** (`ToolTip` is not declared in `FormAddressCalc`). `btnCalc` has **no `Image`** (unlike the batch-scan export button).

**Output width warning:** every output textbox is **45 x 23 / 45 x 26 px** except `txtNumofaddr` which is **59 x 23**. 宋体 10pt fits ~5 digits; the 9-digit value for `/0` (`4294967294`) and the 8-digit values for `/1`..`/3` are wider than 59 px and will be visually clipped (`TextBox` does not shrink text). `txtNumofaddr` is the only output box using font 宋体 10pt; the other output boxes are also 宋体 10pt. Grid positions of the 4-octet groups: 78, 130, 182, 234 (52 px pitch).

### B.1.2 Output rows in detail (all `ReadOnly = true`)

| Label `Text` | Field names (octet 1→4) | Locations x | Panel |
| --- | --- | --- | --- |
| `可用地址:` | `txtNumofaddr` | 78 | `panel8` |
| `掩码:` | `txtSnm1` = 78, `txtSnm2` = 130, `txtSnm3` = 182, `txtSnm4` = 234 | — | `panel7` |
| `网络:` | `txtNwadr1` = 78, `txtNwadr2` = 130, `txtNwadr3` = 182, `txtNwadr4` = 234 | — | `panel6` |
| `首个可用:` | `txtFirstadr1` = 78, `txtFirstadr2` = 130, `txtFirstadr3` = 182, `txtFirstadr4` = 234 | — | `panel5` |
| `末个可用：` | `txtLastadr1` = 78, `txtLastadr2` = 130, `txtLastadr3` = 182, `txtLastadr4` = 234 | — | `panel4` |
| `广播:` | `txtBcast1` = 78, `txtBcast2` = 130, `txtBcast3` = 182, `txtBcast4` = 234 | — | `panel2` |

Field-name mapping to the engine is 1:1 (`snm_N`, `nwadr_N`, `firstadr_N`, `lastadr_N`, `bcast_N`, `numofaddr`), so the WinUI view-model can mirror `NetworkCalculator.AddressFields` directly.

## B.2 `btnCalc_Click` — exact flow (FormAddressCalc.cs:115)

```csharp
var nc = new NetworkCalculator();
var a = new NetworkCalculator.AddressFields {
    ip_1 = txtIP1.Value.ToString(), ip_2 = txtIP2.Value.ToString(),
    ip_3 = txtIP3.Value.ToString(), ip_4 = txtIP4.Value.ToString(),
    bits = txtBit.Value.ToString()
};
bool ok = nc.CalNBFL(a);            // return value is IGNORED here

txtNumofaddr.Text = a.numofaddr;
txtSnm1.Text = a.snm_1; txtSnm2.Text = a.snm_2; txtSnm3.Text = a.snm_3; txtSnm4.Text = a.snm_4;
txtNwadr1.Text = a.nwadr_1; ... txtNwadr4.Text = a.nwadr_4;
txtFirstadr1.Text = a.firstadr_1; ... txtFirstadr4.Text = a.firstadr_4;
txtLastadr1.Text = a.lastadr_1; ... txtLastadr4.Text = a.lastadr_4;
txtBcast1.Text = a.bcast_1; ... txtBcast4.Text = a.bcast_4;
```

* The `bool` result is **discarded**: this dialog *always* renders whatever the engine left in the fields — including partial results and the `错误` / `two hosts` / `one host` strings. It never shows a `MessageBox`.
* **Every field is unconditionally assigned**, so an all-empty engine result clears all boxes (no stale values from a previous computation except `ip_*`/`bits`, which are inputs and are never overwritten).
* Contrast with the batch-scan `btnCalc`, which *does* test the return value and shows `掩码位异常，请重新输入` and appends nothing.
* No exception handling: any exception thrown inside `CalNBFL` (see the `bit.Parse` / `Convert.ToInt32` hazards below) escapes to the WinForms unhandled-exception path.

## B.3 `NetworkCalculator` — exact subnet computation

### B.3.1 `AddressFields` (all `string`, all `{ get; set; }`)

```
bcast_1..4, nwadr_1..4, firstadr_1..4, lastadr_1..4,
snm_1..4, numofaddr, bits, ip_1..4
```

`ResetRestFrom4(a)` (line 62) clears `bcast_*`, `nwadr_*`, `firstadr_*`, `lastadr_*`, `snm_*` and `numofaddr` to `""` (it does **not** touch `bits` or `ip_*`). It is called as the **first** statement of `CalNBFL`.

### B.3.2 `CalNBFL(AddressFields b)` — full algorithm

```csharp
public bool CalNBFL(AddressFields b)
{
    int num = 0;
    ResetRestFrom4(b);                                     // 1. wipe all output fields
    int result = -1;
    for (int i = 1; i <= 4; i++)                           // 2. validate octets
    {
        if (!int.TryParse(b.GetType().GetProperty($"ip_{i}").GetValue(b, null).ToString(), out result)
            || result > 255 || result < 0)
        {
            b.numofaddr = "错误";
            return false;
        }
    }
    num = CalcNWMask(b);                                   // 3. fill snm_*
    if (num != 0) return false;                            //    (CalcNWMask already wrote 错误 / "" )
    int result2 = -1;                                      // 4. re-parse bits
    if (!int.TryParse(b.bits, out result2) || result2 < 0 || result2 > 32)
    {
        b.numofaddr = "错误";
        return false;
    }
    switch (result2)
    {
    case 31:                                               // 5a. two hosts
        b.numofaddr = "two hosts";
        for (int l = 1; l <= 4; l++)
        {
            b.firstadr_l = ip_l & snm_l;                                             // network address
            b.lastadr_l  = ip_l | (~Convert.ToInt32(snm_l) & 0xFF);                  // broadcast address
        }
        return false;
    case 32:                                               // 5b. one host
        b.numofaddr = "one host";
        for (int k = 1; k <= 4; k++)
            b.firstadr_k = ip_k;                                                     // echo the input IP
        return false;
    default:                                               // 5c. normal /0../30
        b.numofaddr = (Math.Pow(2.0, 32 - result2) - 2.0).ToString();
        for (int j = 1; j <= 4; j++)
        {
            b.bcast_j = (ip_j | (~Convert.ToInt32(snm_j) & 0xFF)).ToString();
            b.nwadr_j = (ip_j &  Convert.ToInt32(snm_j)).ToString();
        }
        b.firstadr_1 = b.nwadr_1; b.firstadr_2 = b.nwadr_2; b.firstadr_3 = b.nwadr_3;
        b.firstadr_4 = (Convert.ToInt32(b.nwadr_4) + 1).ToString();
        b.lastadr_1 = b.bcast_1; b.lastadr_2 = b.bcast_2; b.lastadr_3 = b.bcast_3;
        b.lastadr_4 = (Convert.ToInt32(b.bcast_4) - 1).ToString();
        return true;
    }
}
```

Order of validation matters: **octets first, then mask, then bits again**.

### B.3.3 `CalcNWMask` (line 102) — mask octets, and the only place `snm_1` becomes `错误`

```csharp
private int CalcNWMask(AddressFields a)
{
    int result = -1;
    if (!int.TryParse(a.bits, out result) || result > 32 || result < 0)
    {
        a.snm_1 = "错误"; a.snm_2 = ""; a.snm_3 = ""; a.snm_4 = "";
        return 1;                                          // -> CalNBFL returns false with numofaddr == ""
    }
    a.snm_1 = "0"; a.snm_2 = "0"; a.snm_3 = "0"; a.snm_4 = "0";
    if (result >= 8) {
        a.snm_1 = "255"; result -= 8;
        if (result >= 8) {
            a.snm_2 = "255"; result -= 8;
            if (result >= 8) {
                a.snm_3 = "255"; result -= 8;
                a.snm_4 = HFillBitsFromLeft(result).ToString();   // note: no reset if result==0
                return 0;
            }
            a.snm_3 = HFillBitsFromLeft(result).ToString();
            return 0;
        }
        a.snm_2 = HFillBitsFromLeft(result).ToString();
        return 0;
    }
    a.snm_1 = HFillBitsFromLeft(result).ToString();
    return 0;
}
```

`HFillBitsFromLeft` (line 87):

```csharp
public int HFillBitsFromLeft(int a)
{
    if (a >= 8) return 255;
    int num = 65280;            // 0xFF00
    while (a > 0) { num >>= 1; a--; }
    return num & 0xFF;          // a==0 -> 0x00 ; a==1 -> 128 ; ... ; a==7 -> 254
}
```

Exact mask octets by `bits`:

| `bits` | `snm_1` | `snm_2` | `snm_3` | `snm_4` | dotted mask |
| --- | --- | --- | --- | --- | --- |
| 0 | `0` | `0` | `0` | `0` | `0.0.0.0` |
| 1 | `128` | `0` | `0` | `0` | `128.0.0.0` |
| 2 | `192` | `0` | `0` | `0` | `192.0.0.0` |
| 3 | `224` | `0` | `0` | `0` | `224.0.0.0` |
| 4 | `240` | `0` | `0` | `0` | `240.0.0.0` |
| 5 | `248` | `0` | `0` | `0` | `248.0.0.0` |
| 6 | `252` | `0` | `0` | `0` | `252.0.0.0` |
| 7 | `254` | `0` | `0` | `0` | `254.0.0.0` |
| 8 | `255` | `0` | `0` | `0` | `255.0.0.0` |
| 9..15 | `255` | `128/192/224/240/248/252/254` | `0` | `0` | e.g. /12 → `255.240.0.0` |
| 16 | `255` | `255` | `0` | `0` | `255.255.0.0` |
| 17..23 | `255` | `255` | `128..254` | `0` | e.g. /20 → `255.255.240.0` |
| 24 | `255` | `255` | `255` | `0` | `255.255.255.0` |
| 25..30 | `255` | `255` | `255` | `128/192/224/240/248/252` | e.g. /26 → `255.255.255.192` |
| 31 | `255` | `255` | `255` | `254` | `255.255.255.254` |
| 32 | `255` | `255` | `255` | `255` | `255.255.255.255` |

Important implementation detail: because every branch writes `"0"` or `"255"` explicitly (and `HFillBitsFromLeft(0)` returns `0`), all four `snm_*` fields are **always populated with a decimal string** for `bits` in `0..32` — there is no `""` for valid bits. `snm_1 = "错误"` happens **only** when `bits` is non-numeric or outside `0..32` (unreachable from the UI, whose `txtBit` is clamped to `1..30`).

### B.3.4 Per-`bits` result table

`ip_j` = the four input octets (validated `0..255`). All arithmetic is per-octet with `& 0xFF`; there is **no 32-bit carry between octets**, which is correct for `firstadr_4 = nwadr_4 + 1` and `lastadr_4 = bcast_4 - 1` only because those are the low octets.

| `bits` | `numofaddr` (`可用地址:`) | `nwadr_*` (`网络:`) | `bcast_*` (`广播:`) | `firstadr_*` (`首个可用:`) | `lastadr_*` (`末个可用：`) | return |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | `"4294967294"` (`2^32-2`) | `ip_j & 0` = `0.0.0.0` | `ip_j \| 255` = `255.255.255.255` (because `~0 & 0xFF == 255`) | `0.0.0.1` | `255.255.255.254` | `true` |
| 1 | `"2147483646"` (`2^31-2`) | `ip_1 & 128` + `.0.0.0` | `ip_1 \| 127` + `.255.255.255` | nwadr with last octet +1 | bcast with last octet −1 | `true` |
| … | `(2^(32-bits) - 2).ToString()` | per-octet AND | per-octet OR of `~snm & 0xFF` | `nwadr_1..3` + `nwadr_4+1` | `bcast_1..3` + `bcast_4−1` | `true` |
| 24 | `"254"` | `ip_1.ip_2.ip_3.0` | `ip_1.ip_2.ip_3.255` | `…​.1` | `…​.254` | `true` |
| 30 | `"2"` | `…​.252` style | `…​.255` style | `nwadr_4+1` | `bcast_4−1` | `true` |
| **31** | **`"two hosts"`** | **left `""` (cleared by `ResetRestFrom4`)** | **left `""`** | `ip_j & snm_j` = **the network address** | `ip_j \| (~snm_j & 0xFF)` = **the broadcast address** | **`false`** |
| **32** | **`"one host"`** | **left `""`** | **left `""`** | `firstadr_1..4 = ip_1..4` = **exactly the input IP** | **left `""`** | **`false`** |
| invalid `bits` | `"错误"` (set by `CalNBFL` step 4) — but if `bits` fails inside `CalcNWMask` first, `numofaddr` stays `""` and `snm_1 == "错误"` | `""` | `""` | `""` | `""` | `false` |
| octet > 255 or unparsable | `"错误"` (set by step 2, **overwriting** the cleared `""`) | `""` | `""` | `""` | `""` | `false` |

Specific quirks to reproduce (or deliberately fix) in WinUI:

1. **`bits == 31` ("two hosts")**: the *label says* two hosts, and `firstadr_*`/`lastadr_*` are filled with the **network** and **broadcast** addresses of the /31 (which for a /31 are exactly the two usable addresses) — but `bcast_*` and `nwadr_*` are left empty, and the engine returns `false`. `可用地址:` shows the English literal `two hosts`, **not** a number and **not** Chinese.
2. **`bits == 32` ("one host")**: only `firstadr_*` is filled (with the input IP verbatim); `lastadr_*`, `nwadr_*`, `bcast_*` stay empty; `可用地址:` shows `one host`; returns `false`.
3. Because both 31 and 32 return `false`, the batch-scan dialog treats them as errors (`掩码位异常，请重新输入`), and `txtBit.Maximum = 30` in **both** dialogs makes them unreachable from the UI anyway. A WinUI port that widens the `bits` range must decide the intended behaviour rather than inheriting this dead code.
4. `numofaddr` for `/0../3` is 10 digits and is clipped by the 59 px `txtNumofaddr`; the real value is still in `Text`.
5. `Math.Pow` is a `double`, so values are exact only up to `2^53`; `2^32` is fine. `.ToString()` on such a `double` yields plain integer notation here (e.g. `"4294967294"`), not scientific notation.
6. `firstadr_4 = nwadr_4 + 1` and `lastadr_4 = bcast_4 - 1` are computed from the already-stringified octets via `Convert.ToInt32` — no `/31`-style `nwadr_4 == bcast_4` special case; for `bits` 0..30 the network and broadcast never coincide in the low octet, so no overflow can occur (worst case `nwadr_4 = 254 → firstadr_4 = 255`).
7. `~Convert.ToInt32(snm_j) & 0xFF` — `snm_j` is always a valid decimal string here, so this cannot throw for valid input.

## B.4 Input validation and error text

**There is no input validation in `FormAddressCalc` itself.** All validation lives in `NetworkCalculator.CalNBFL` and manifests only as *output text*, never as a dialog:

| Trigger | Output shown | Which boxes |
| --- | --- | --- |
| `bits` non-numeric or `< 0` or `> 32` | `txtSnm1.Text = "错误"`; `numofaddr` stays `""` | `掩码:` row shows `错误 | (empty) | (empty) | (empty)`; every other output row is blank |
| any `ip_j` non-numeric or `< 0` or `> 255` | `txtNumofaddr.Text = "错误"` | `可用地址:` shows `错误`; every other output row is blank |
| `bits` in `0..30` (after octets validated) | numeric values | all six rows populated |
| `bits == 31` | `可用地址: two hosts` | `首个可用:` / `末个可用：` populated with network/broadcast; `网络:` and `广播:` blank |
| `bits == 32` | `可用地址: one host` | `首个可用:` = the input IP; `末个可用：`, `网络:`, `广播:` blank |

**How invalid input is reachable:**
* `txtBit` is a `NumericUpDown` clamped to `1..30`, so `bits` out of `0..32` is unreachable through the UI; it can only occur programmatically.
* `txtIP1..txtIP4` are clamped to `Maximum = 999`, so **`256..999` are typeable** and produce `可用地址: 错误` with all other fields blank. `txtIP1.Minimum = 1` prevents a `0` first octet in the box, but `0` is legal for the engine.
* **Hazard, not handled:** if `bits` (or an `ip_*` field) were `null`, `int.TryParse(null, ...)` returns `false` (so `bits` is safe) but `ip_i`-null hits `...GetValue(b, null).ToString()` → `NullReferenceException` **before** `TryParse`, escaping the method. `txtIP1` etc. are never null in the UI, so this is latent only.
* **`FormatException`/`OverflowException` hazard:** `NetworkCalculator` itself never calls `byte.Parse`, so it is safe; the throwing paths (`Utility.IpToUInt`) are only exercised by the batch-scan dialog's generators.

**Error strings inventory for both windows**

| String | Where shown |
| --- | --- |
| `错误` | `numofaddr` (bad octet, or bad bits reaching step 4) and `snm_1` (bad bits caught in `CalcNWMask`) — **FormAddressCalc output only** |
| `two hosts` | `numofaddr` when `bits == 31` |
| `one host` | `numofaddr` when `bits == 32` |
| `掩码位异常，请重新输入` | `MessageBox` (caption `IP批量扫描`) in `FormIPSegment.btnCalc_Click` when `CalNBFL` returns `false` |
| `请输入正确的IP扫描段` | `MessageBox` (caption `IP批量扫描`) in `FormIPSegment.btnGeneral_Click` |
| `开始IP不得大于结束IP` | `MessageBox` (caption `IP批量扫描`) in `FormIPSegment.btnGeneral_Click` |
| `请输入正确的开始IP地址` | `MessageBox` (caption `IP批量扫描`) in `FormIPSegment.btnGeneralBatch_Click` — **used for both the start-IP and the end-IP failure branches** |
| `当前生成的IP地址已超过1000个，你确认要继续吗？` | `MessageBox` `OKCancel` + `Question` in `FormIPSegment.btnCalc_Click` |
| `IP批量扫描导出成功。是否要打开Excel文档？` | `MessageBox` `YesNo` + `Question` in `FormIPSegment.btnCopy_Click` |
| `查询中` | transient `HostName` / `MAC` cell values while enriching |
| `未知` | `HostName` fallback when DNS resolution throws |
| `OK` / `NG` | `状态` column / row colouring |
| `Timeout` | `时间` column for any non-success reply |
| `<1ms` | `时间` column for sub-millisecond replies |

---

# Open questions / ambiguities

1. **`FormIPSegment` constructor is not defensive.** `int.Parse(array[0..2])` throws if `FormMain.txtIPPart.Text` is not exactly three numeric octets, and the `NumericUpDown.Maximum = 999` clamp throws `ArgumentOutOfRangeException` on assignment for octets ≥ 1000. WinUI should instead seed the fields defensively and skip the prefill on parse failure. Confirm whether the parent port wants bug-for-bug fidelity here.
2. **Form-prefill semantics.** Only `txtIP1..txtIP3` are seeded from the main window's prefix; `txtIP4` keeps its designer default `1` and `txtBit` keeps `24`. Should the WinUI version carry the full current IP including the 4th octet?
3. **Locale / DPI.** All sizes come from a 6x12 font auto-scale (96 DPI, `AutoScaleMode.Font`, `宋体`/`黑体`). WinUI must pick an explicit layout scale; the 45 px and 59 px output boxes will need re-sizing (see the `numofaddr` clipping above).
4. **Export encoding.** The original writes UTF-8 **without BOM** (Excel-mojibake for Chinese). Recommended deliberate deviation: UTF-8 **with** BOM, plus RFC-4180 quoting. Is a real `.xlsx` (via a library) desired instead of CSV? The export button says `导出结果` and the dialog says `是否要打开Excel文档？`, but the artefact is CSV — confirm the target format for the rebuild.
5. **Export destination.** `Application.StartupPath` is typically not writable from an installed WinUI app (MSIX/Program Files). Recommend a file picker or `KnownFolders.DocumentsLibrary`; confirm.
6. **Cancel/abort semantics.** There is no way to stop a batch scan or a 定时扫描 wait (up to 59 minutes of `Thread.Sleep`). Does the WinUI port add a 停止 button / `CancellationToken`? If yes, it is a behavioural extension, not a port.
7. **`bits` 31/32 are dead code** in the original (both dialogs clamp to 1..30) and both return `false` from `CalNBFL`. Should the WinUI calculator expose 0..32 properly (with correct `/31` two-host and `/32` single-host output), and should `可用地址` show `2` / `1` instead of the English strings `two hosts` / `one host`?
8. **Row colours.** Batch scan hard-codes `LightGreen`/`LightCoral`; the main window uses `ConfigInfo.NetworkOKColor`/`NetworkNGColor`. Unify or keep separate?
9. **Status-bar text is inconsistent** (`剩余时间600 秒` vs `剩余时间0分37秒`, and a stray `秒` suffix on a `"m\分s"` format). Should the WinUI port normalise the countdown to one format?
10. **Sort direction off-by-one.** `isAscending` is flipped *after* sorting and is shared across columns, so the first header click sorts descending. Is that a bug to preserve or to fix (first click ascending, per-column state, sort glyphs in the header)?
11. **`Utility.ValidIP` and the 单网段 regex are both prefix-anchored and octet-unvalidated**, so `999.999.999.999` reaches `byte.Parse` and throws an unhandled exception. The WinUI port should validate octets `0..255` properly — confirm this is an accepted deviation.
12. **Concurrency ceiling.** The original relies on the thread pool with 10 ms dispatch pacing and no cap; measured behaviour on modern .NET (ICMP `Ping` uses `IcmpSendEcho2` on a worker) can exhaust the pool for large ranges. What concurrency limit (e.g. 32–64 in-flight pings) should the WinUI port use, and should timeouts stay at the config default of 500 ms?
13. **Memo lookup key precedence** is MAC-first then IP, and `Memo` is a *computed* property re-evaluated on every grid repaint — so memo edits in the memo manager appear only after a rebind. Confirm the WinUI view-model should raise change notifications for `Memo` instead.
14. **DNS/ARP caches have a 1-hour TTL** and are shared process-wide; repeat 定时扫描 rounds may show stale 主机名/MAC. Should the WinUI port expose a cache-busting option or keep the TTL?
