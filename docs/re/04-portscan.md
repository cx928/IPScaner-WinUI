# 04 — 目标端口扫描 (`FormPortScan`) + 本机端口占用查看 (`FormPortByPid`)

Reverse-engineering spec for rebuilding **IPScaner V1.28.2** (WinForms, `net40`) as a WinUI 3 app.

| Source (decompiled) | Lines | Form class |
|---|---|---|
| `ipscaner-re\src\IPScaner\FormPortScan.cs` | 921 (task brief said 859 — the checked-out file is 921) | `IPScaner.FormPortScan : Form` |
| `ipscaner-re\src\IPScaner\FormPortByPid.cs` | 485 (task brief said 450) | `IPScaner.FormPortByPid : Form` |
| `ipscaner-re\src\IPScaner\ProcessInfo.cs` | 19 | `IPScaner.ProcessInfo` |
| `ipscaner-re\src\IPScaner\Utility.cs` (L140–223) | — | `TcpPortTest`, `TcpPortTestMuli`, `GetIPRange` |
| `ipscaner-re\src\IPScaner\ConfigInfo.cs`, `Global.cs` | — | `ConfigInfo.PortTimeout` (default `50`) |
| `ipscaner-re\src\IPScaner\NetworkCalculator.cs` | 208 | `CalNBFL` (CIDR expansion) |
| `ipscaner-re\src\IPScaner\FormMain.cs` (L1077, L1248, L1931, L1937) | — | launch points |
| `ipscaner-re\src\IPScaner\Program.cs` | 40 | elevation gate, global exception handlers |
| `ipscaner-re\src\IPScaner.readme.txt` | — | feature wording, changelog |

Feature wording comes from `IPScaner.readme.txt`:

```
5. 目标端口扫描，支持IP网段地址扫描若干个端口、支持单IP扫描全部端口
6. 本机端口占用查看，快速查看系统中被占用的TCP、UDP端口，快速定位指定端口的进程目录，快速杀死PID进程
```

> These two phrases are **readme capability descriptions, not UI strings**. There is no control labelled `IP网段地址扫描若干个端口` / `单IP扫描全部端口` in either form; they map onto four scan buttons (see §A.3).

Launch points (main window 快捷工具 menu):

* `mnuPortScan.Text = "端口扫描(&P)"` → `new FormPortScan().Show()` (main-form L1077–1080)
* `mnuProcessPort.Text = "系统端口查看(&V)"` → `new FormPortByPid().Show()` (L1248–1251)
* IP colour-block context menu `mnuScan.Text = "端口扫描(&S)"` → `new FormPortScan(ipAddressByMnuCmd).Show()` (L1102–1105) — the 1-arg ctor only pre-fills `txtIP`.
* Both are **modeless** (`.Show()`), so any number of copies can be open at once. This matters: `FormPortScan`'s semaphore and cancellation token are `static` and therefore **shared by every open instance** (§A.6, §A.10).

---

# Part A — 目标端口扫描 (`FormPortScan`)

## A.1 Window shell

| Property | Value (from `InitializeComponent`) |
|---|---|
| `Text` | `端口扫描` |
| `Name` | `FormPortScan` |
| `ClientSize` | `1228 × 1060` |
| `Padding` | `15, 16, 15, 16` |
| `StartPosition` | `CenterScreen` |
| `AutoScaleDimensions` / `AutoScaleMode` | `11F, 21F` / `Font` |
| `Icon` | `resources.GetObject("$this.Icon")` (`IPScaner.FormPortScan.resx`, icon only — the .resx holds no strings) |
| Events | `Load → FormPortScan_Load`, `FormClosing → FormPortScan_FormClosing` |

Vertical stacking (docked children; the *last* `Controls.Add`ed docked control is placed outermost — verified against the design `Location`s):

```
y   16 … 128   groupBox3   "单个IP批量端口扫描"            (Dock = Top, 1198 × 112)
y  128 … 250   groupBox2   "IP段多个端口扫描 (结束值可为空)" (Dock = Top, 1198 × 122)
y  250 … 1044  gbxResult   "端口扫描结果"                  (Dock = Fill, 1198 × 794)
```

## A.2 Complete control inventory (exact Chinese text)

Fonts: `宋体` (SimSun) **10 pt** for labels/inputs, `宋体` **9 pt** for the scan/stop buttons. Every button has `UseVisualStyleBackColor = false`, 扫描 buttons `BackColor = Color.LimeGreen` (`#32CD32`), 停止 buttons `BackColor = Color.IndianRed` (`#CD5C5C`) with `ForeColor = White`. Button size is `112 × 35` everywhere in this window.

### groupBox3 — `单个IP批量端口扫描` (single IP, batch ports)

| # | Control | Type | Text / value | Geometry (x, y, w, h) | Notes |
|---|---|---|---|---|---|
| 1 | `label1` | Label | `IP或域名` | 13, 35, 106, 24 | 宋体 10 |
| 2 | `txtIP` | TextBox | `127.0.0.1` | 130, 30, 196, 34 | 宋体 10; `Tag = "填写完整IP，比如127.0.0.1"` (**never read anywhere — dead placeholder metadata**) |
| 3 | `label2` | Label | `端口：` (full-width colon) | 334, 35, 82, 24 | |
| 4 | `txtStartPort` | NumericUpDown | `1` | 405, 31, 95, 31 | `Minimum = 1`, `Maximum = 65535`; tooltip `开始端口号，从1开始` |
| 5 | `label3` | Label | `-` (ASCII hyphen) | 522, 35, 22, 24 | separator between the two port spinners |
| 6 | `txtEndPort` | NumericUpDown | `65535` | 566, 31, 95, 31 | `Minimum = 1`, `Maximum = 65535`; tooltip `最大端口号，不大于65535，可为空值` |
| 7 | `btnScanSingle` | Button | `扫描` | 691, 30, 112, 35 | LimeGreen; **shape 1** |
| 8 | `btnStopSingle` | Button | `停止` | 812, 30, 112, 35 | IndianRed/White |
| 9 | `lblTimeout` | Label | `TCP超时毫秒：` + `Global.ConfigInfo.PortTimeout` | 957, 35, ~166, 24 | 宋体 10; design text is `TCP超时毫秒：`, the value is appended in `Load` |
| 10 | `label9` | Label | `多个端口范围:` (half-width colon) | 334, 75, 166, 24 | tooltip `多个端口之间用逗号分隔` |
| 11 | `txtIPPorts` | TextBox | `80,443` | 496, 71, 165, 31 | **no tooltip, no KeyDown filter**; **shape 2** |
| 12 | `btnScanSingle2` | Button | `扫描` | 691, 70, 112, 35 | LimeGreen |
| 13 | `btnStopSingle2` | Button | `停止` | 812, 70, 112, 35 | IndianRed/White |

### groupBox2 — `IP段多个端口扫描 (结束值可为空)` (IP segment × port list)

| # | Control | Type | Text / value | Geometry | Notes |
|---|---|---|---|---|---|
| 1 | `label6` | Label | `IP网段：` | 11, 44, 106, 24 | |
| 2 | `txtIPPart` | TextBox | `127.0.0.` | 130, 38, 196, 34 | 宋体 10; `Tag = "填写IP段，比如127.0.0."` (dead) |
| 3 | `label5` | Label | `开始：` | 334, 44, 82, 24 | |
| 4 | `txtStartIP` | NumericUpDown | `1` | 405, 40, 77, 31 | Min 1 / Max 255 (last octet); tooltip `起始IP地址` |
| 5 | `label4` | Label | `结束：` | 504, 44, 82, 24 | |
| 6 | `txtEndIP` | NumericUpDown | `254` | 585, 40, 77, 31 | Min 1 / Max 255; tooltip `最大IP地址，可为空值` |
| 7 | `label7` | Label | `端口：` | 688, 44, 82, 24 | tooltip `多个端口之间用逗号分隔` |
| 8 | `txtPorts` | TextBox | `25,80,443` | 759, 38, 165, 31 | tooltip `自动加载上次扫描的端口`; `KeyDown → txtPort_KeyDown`; overwritten at `Load` by the temp history file |
| 9 | `btnScanBatch` | Button | `扫描` | 935, 38, 112, 35 | LimeGreen; **shape 3** |
| 10 | `btnStopBatch` | Button | `停止` | 1054, 38, 112, 35 | IndianRed/White |
| 11 | `label11` | Label | `IP/掩码位:` | 12, 84, 130, 24 | 宋体 10, `ForeColor = Black` |
| 12 | `txtIP1` | NumericUpDown | `192` | 130, 80, 83, 34 | Min 1 / Max 999 |
| 13 | `txtIP2` | NumericUpDown | `168` | 225, 80, 83, 34 | Min 0 / Max 999 |
| 14 | `txtIP3` | NumericUpDown | `0` | 320, 80, 83, 34 | Min 0 / Max 999 |
| 15 | `txtIP4` | NumericUpDown | `1` | 416, 80, 83, 34 | Min 0 / Max 999 |
| 16 | `label10` | Label | `/` | 527, 88, 21, 21 | |
| 17 | `txtBit` | NumericUpDown | `24` | 578, 80, 83, 34 | `Minimum = 1`, `Maximum = 30`; tooltip `子网掩码的位数，值在1~24之间` (**tooltip contradicts the control's own max of 30**) |
| 18 | `btnScanBatch2` | Button | `扫描` | 935, 76, 112, 35 | LimeGreen; **shape 4** |
| 19 | `btnStopBatch2` | Button | `停止` | 1054, 76, 112, 35 | IndianRed/White |

The `/` group shows the literal text of `label10`; the four spinners and `label10` are laid out as `192 . 168 . 0 . 1 / 24` (default `192.168.0.1/24`).

### gbxResult — `端口扫描结果` (results)

| # | Control | Type | Text | Geometry | Notes |
|---|---|---|---|---|---|
| 1 | `gbxResult` | GroupBox | `端口扫描结果` (mutated at runtime, §A.8) | 15, 250, 1198, 794; Dock Fill; Padding 15,16,15,16 | |
| 2 | `lbxResult` | ListBox | — | 15, 40, 1168, 738; Dock Fill | `FormattingEnabled = true`, `ItemHeight = 21`; default `SelectionMode = One`; horizontal scrollbar **not** enabled |
| 3 | `lblCopyResult` | Label | `复制` | 155, 4, 58, 24 | `Anchor = Top｜Right`, `AutoSize = true`, `Cursor = Cursors.Hand`, 宋体 10, `ForeColor = Color.Blue`; `Click → lblCopyResult_Click` |

`lblCopyResult` is the "复制到剪贴板" affordance (added in v1.27: *"端口扫描结果可复制到剪贴板，方便批量复制到其他工具中。"*). It renders as blue hand-cursor text **inside the group-box caption band** (y = 4, i.e. above the list, next to the caption `端口扫描结果`), not as a button. `Anchor = Top|Right` shows the intent was a right-aligned header action; the stored x = 155 reflects a smaller design-time container.

### Input filtering on `txtPorts` only (`txtPort_KeyDown`)

```csharp
bool flag = (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9) || (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9)
         || e.KeyCode == Keys.Oemcomma || e.KeyCode == Keys.Space || e.KeyCode == Keys.Back
         || e.KeyCode == Keys.Delete || e.KeyCode == Keys.Left || e.KeyCode == Keys.Right
         || e.KeyCode == Keys.Home || e.KeyCode == Keys.End || e.KeyCode == Keys.Return;
if (e.Control && (e.KeyCode == Keys.C || e.KeyCode == Keys.V)) { return; }   // <-- makes the block below dead code
if (e.Control && e.KeyCode == Keys.V) { /* digit/dot clipboard validation — UNREACHABLE */ }
if (!flag) { e.SuppressKeyPress = true; }
```

* Allowed keys: digits (top row + numpad), `,` (`Keys.Oemcomma`), space, Backspace, Delete, ←/→, Home, End, Enter, and Ctrl+C/Ctrl+V.
* The pasted-text validation block is **dead code** (the earlier `return` already lets Ctrl+V through), so any pasted junk is accepted into `txtPorts` and then silently dropped by the parser.
* Notably **not** allowed: `-` (so a range like `1000-2000` cannot even be typed), `.`, `;`, tab. Full-width `，` cannot be typed either (it is accepted when pasted).
* Only `txtPorts` is filtered. `txtIP`, `txtIPPart`, `txtIPPorts` accept arbitrary text.

## A.3 The four scan shapes

| Button | Section | Input columns | IP set | Port set | Corresponds to readme |
|---|---|---|---|---|---|
| `btnScanSingle` | groupBox3 row 1 | `txtIP` + `txtStartPort` … `txtEndPort` | exactly one string (IP **or hostname**) | inclusive integer range from the two spinners | **"单IP扫描全部端口"** when start = 1 / end = 65535 (the defaults), or an arbitrary single-host port range |
| `btnScanSingle2` | groupBox3 row 2 | `txtIP` + `txtIPPorts` | exactly one string | parsed list from `txtIPPorts` | "单IP指定端口扫描" (v1.26 changelog: `端口扫描页面支持掩码位IP、单IP指定端口扫描。`) |
| `btnScanBatch` | groupBox2 row 1 | `txtIPPart` + `txtStartIP`/`txtEndIP` + `txtPorts` | `<3-octet prefix>.<start..end>` | parsed list from `txtPorts` | **"IP网段地址扫描若干个端口"** |
| `btnScanBatch2` | groupBox2 row 2 | `txtIP1..4` + `txtBit` + `txtPorts` | CIDR-expanded network range | parsed list from `txtPorts` | "IP网段地址扫描若干个端口" (CIDR variant) |

`btnScanSingle_Click` (shape 1), verbatim:

```csharp
string text = txtIP.Text;
int num  = (int)txtStartPort.Value;
int num2 = (int)txtEndPort.Value;
if (num2 < num) { num2 = num; }                       // "结束值可为空 / 可为空值" fallback
ButtunEnableChange(enable: true);
ListBoxAddItem($"开始扫描【{text}】 端口范围 {num}-{num2}");
ScanPort(new string[1] { text }, Enumerable.Range(num, num2 - num + 1).ToArray(), () => { ... });
```

* "All ports" is therefore `Enumerable.Range(1, 65535)` = **65 535 probes for one host**. There is no `all` / `全部端口` keyword anywhere in the code base (verified: no occurrence of `全部端口` in any `.cs`).
* No IP validation on `txtIP`: `127.0.0.1`, `192.168.1.5` and `www.example.com` are all passed straight to `TcpClient.BeginConnect(string host, …)`.

## A.4 IP-set parsing

### Shape 3 — 3-octet prefix + last-octet range (`btnScanBatch_Click`, L155–216)

```csharp
string input = txtIPPart.Text;
Regex regex = new Regex("^(\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})(\\.\\d{1,3})?");
List<string> list = new List<string>();
if (txtStartIP.Text == string.Empty) {
    MessageBox.Show("请填写开始IP", Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation); return; }
if (txtEndIP.Text == string.Empty) { txtEndIP.Value = txtStartIP.Value; }
if (regex.IsMatch(input)) {
    input = regex.Match(input).Groups[1].Value;
    int num  = (int)txtStartIP.Value;
    int num2 = (int)txtEndIP.Value;
    if (num <= num2) {
        for (int i = num; i < num2 + 1; i++) { list.Add($"{input}.{i}"); }
        ...
    } else { MessageBox.Show("开始IP不得大于结束IP", Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation); }
} else { MessageBox.Show("请输入正确的IP扫描段", Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation); }
```

* Regex is **anchored at the start only** and the optional group 2 is **discarded**, so `192.168.1.50` → scans `192.168.1.<start..end>`; `127.0.0.` matches with group 1 = `127.0.0` (trailing dot not required, and not consumed).
* Octet values are *not* range-checked (`999.1.1` passes); the produced strings are handed to DNS/connect and simply fail per-probe.
* `txtStartIP`/`txtEndIP` are NumericUpDowns (Min 1, Max 255) so the "empty" tests only fire when the user has cleared the edit text; the spinner value survives, which is why the code compares `Text` (not `Value`). The `结束值可为空` caption refers to exactly this: clear the end box → end = start (one address).
* Order of gates: 请填写开始IP → end-empty fixup → regex (请输入正确的IP扫描段) → start ≤ end (开始IP不得大于结束IP) → ports (请填写1~65535之间的端口) → run. All use `MessageBoxIcon.Exclamation`, `MessageBoxButtons.OK`, caption = `Text` = `端口扫描`.

### Shape 4 — IP/掩码位 (`btnScanBatch2_Click`, L406–454)

```csharp
NetworkCalculator networkCalculator = new NetworkCalculator();
NetworkCalculator.AddressFields addressFields = new NetworkCalculator.AddressFields {
    ip_1 = txtIP1.Value.ToString(), ip_2 = txtIP2.Value.ToString(),
    ip_3 = txtIP3.Value.ToString(), ip_4 = txtIP4.Value.ToString(),
    bits = txtBit.Value.ToString() };
if (!networkCalculator.CalNBFL(addressFields)) {
    MessageBox.Show("掩码位异常，请重新输入", Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation); return; }
string startIP = addressFields.firstadr_1 + "." + addressFields.firstadr_2 + "." + addressFields.firstadr_3 + "." + addressFields.firstadr_4;
string endIP   = addressFields.lastadr_1  + "." + addressFields.lastadr_2  + "." + addressFields.lastadr_3  + "." + addressFields.lastadr_4;
List<string> iPRange = Utility.GetIPRange(startIP, endIP);
if (iPRange.Count > 1000 && MessageBox.Show("当前生成的IP地址已超过1000个，你确认要继续吗？", Text,
        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.Cancel) { return; }
```

`NetworkCalculator.CalNBFL` semantics that matter here:

* Returns `false` (⇒ message `掩码位异常，请重新输入`) for bits `31` and `32` (they set `numofaddr = "two hosts"` / `"one host"` and return false) and for bits outside `0..32`. Because `txtBit` is Min 1 / Max 30, the reachable rejection is effectively only the cleared-text case.
* Otherwise: `firstadr = network address with last octet + 1`, `lastadr = broadcast with last octet − 1`. With the defaults (`192.168.0.1`, `/24`) → `192.168.0.1 … 192.168.0.254` = 254 addresses.
* `Utility.GetIPRange(startIP, endIP, isSkipCast = true)` walks the range as a `uint` and **skips every address whose last octet is 0 or 255**:

```csharp
for (uint num3 = num; num3 <= num2; num3++)
    if (!isSkipCast || ((num3 & 0xFF) != 255 && (num3 & 0xFF) != 0)) list.Add(UIntToIp(num3));
```

* `>1000 addresses` ⇒ modal confirm `当前生成的IP地址已超过1000个，你确认要继续吗？` (OK/Cancel, Question). This is the only rate-limiting guard in the app.

## A.5 Port-set parsing (three identical copies)

Identical logic appears in `btnScanBatch_Click` (L182–198), `btnScanSingle2_Click` (L354–371) and `btnScanBatch2_Click` (L429–446):

```csharp
string[] array = txtPorts.Text.Split(new char[2] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
List<int> list2 = new List<int>();
foreach (string s in array) {
    int result = 0;
    if (int.TryParse(s, out result) && result > 0 && result <= 65535 && !list2.Contains(result)) {
        list2.Add(result);
    }
}
if (list2.Count == 0) {
    MessageBox.Show("请填写1~65535之间的端口", Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
    return;
}
txtPorts.Text = string.Join(",", list2.OrderBy((int r) => r));
```

Exact behaviour:

| Input | Result |
|---|---|
| Separators | ASCII `,` and full-width `，` **only**. No spaces-as-separators, no `;`, no `-`, no newlines, no `:` |
| Range syntax `1000-2000` | **NOT supported.** `int.TryParse("1000-2000")` fails → token silently discarded. `80,443,1000-2000` therefore scans exactly `80,443` |
| Tokens with surrounding spaces (`" 80 "`) | Accepted — `int.TryParse` uses `NumberStyles.Integer`, which tolerates leading/trailing whitespace per token |
| `"80 443"` | One token → parse fails → discarded (both ports lost) |
| Invalid tokens (`abc`, `-1`, `0`, `65536`, `99999`) | **Silently dropped**, no message, as long as ≥ 1 valid token remains |
| Duplicates | Dropped (`!list2.Contains(result)`) |
| All tokens invalid / empty box | `请填写1~65535之间的端口` (Exclamation, OK, caption `端口扫描`) |
| Normalisation | After validation the text box is rewritten as **ascending, comma-separated, deduplicated** ASCII (`string.Join(",", … .OrderBy(r => r))`), and the *same sorted array* is passed to `ScanPort` |
| Port `0` | Rejected (`result > 0`) |
| Port range for shape 1 | Bypasses this parser entirely: `Enumerable.Range(start, end-start+1)`, spinners already clamp to `1..65535` |

History persistence (`ReadHisPorts` / `SaveHisPorts`, called only from the two `btnScanBatch*` paths):

```csharp
string path = Path.Combine(Path.GetTempPath(), MethodBase.GetCurrentMethod().DeclaringType.FullName + ".txt");
// ⇒ %TEMP%\IPScaner.FormPortScan.txt   (UTF-8, raw text of txtPorts)
```

* `FormPortScan_Load` reads it and, when non-empty, overwrites the designer default `25,80,443`.
* `SaveHisPorts` swallows all IO exceptions (`catch { }`).
* The single-IP paths (shapes 1, 2) neither read nor write this file.

## A.6 Scan engine — fan-out, concurrency, probe

### Fan-out (`ScanPort`, L240–261), verbatim

```csharp
private static readonly int MaxConcurrentConnections = 1000;
private static readonly Semaphore semaphore = new Semaphore(MaxConcurrentConnections, MaxConcurrentConnections);
private static CancellationTokenSource cancellationTokenSource;

private void ScanPort(string[] ipAddress, int[] portArray, Action completedAction = null)
{
    cancellationTokenSource = new CancellationTokenSource();
    Task<bool>[] tasks = new Task<bool>[portArray.Length * ipAddress.Length];
    for (int i = 0; i < ipAddress.Length; i++) {
        for (int j = 0; j < portArray.Length; j++) {
            int currentPort = portArray[j];
            string ip = ipAddress[i];
            tasks[j + i * portArray.Length] = Task.Factory.StartNew(() => CheckPort(ip, currentPort, cancellationTokenSource.Token));
        }
    }
    Task.Factory.StartNew(() => {
        Task[] tasks2 = tasks;
        Task.WaitAll(tasks2);
        ListBoxAddItem("端口扫描完毕");
        gbxResult.Text = "端口扫描结果";
        completedAction?.Invoke();
    });
}
```

* **One `Task<bool>` per (IP, port) pair, all created up-front** = `ips × ports` tasks (`Task.Factory.StartNew`, i.e. the default scheduler/ThreadPool).
* Matrix index: `tasks[j + i * portArray.Length]`; probe order is port-major within each IP.
* `ip` / `currentPort` are loop-local copies → correct closure capture.
* Concurrency is bounded by a **`System.Threading.Semaphore` with 1000 permits, declared `static`** — the limit is per *process*, shared by every open `FormPortScan` window. At most 1000 `TcpClient` sockets can be mid-connect at any instant, per process.
* Each parked task **blocks a ThreadPool thread inside `semaphore.WaitOne()`**, so the ThreadPool's thread-injection ramp (≈1 thread/0.5 s past the minimum) — not the semaphore — governs how fast large scans ramp up to 1000 in-flight probes. `65 535` tasks for one host therefore start slowly; the completion watcher (`Task.WaitAll`) also occupies a pool thread.
* No retry: each pair is probed **exactly once**. There is no re-scan of closed ports, no per-host grouping, no adaptive timeout.
* `ScanPort` never awaits anything on the UI thread (fire-and-forget), so the WinForms UI stays responsive.

### The probe (`Utility.TcpPortTest`, Utility.cs L158–180), verbatim

```csharp
public static bool TcpPortTest(string ipAddress, int port)
{
    using (TcpClient tcpClient = new TcpClient())
    {
        IAsyncResult asyncResult = tcpClient.BeginConnect(ipAddress, port, null, null);
        if (!asyncResult.AsyncWaitHandle.WaitOne(Global.ConfigInfo.PortTimeout))
        {
            tcpClient.Close();
        }
        else
        {
            try
            {
                tcpClient.EndConnect(asyncResult);
                return true;
            }
            catch
            {
            }
        }
    }
    return false;
}
```

* Mechanism: **`TcpClient.BeginConnect` (APM) + `IAsyncResult.AsyncWaitHandle.WaitOne(timeout)`**, then `EndConnect` on completion. It is *not* `Socket.BeginConnect` directly, *not* `ConnectAsync(...).Wait(...)`, and *not* a raw SYN / half-open scan. It is a classic **TCP connect scan**: full three-way handshake, then the socket is closed immediately (the `using` block) without sending or reading a byte.
* Timeout source: `Global.ConfigInfo.PortTimeout` — read **at probe time** from the static `Global.ConfigInfo`, which is deserialised from `<exe dir>\IPScaner.cfg` (`FormMain.CfgPath = Path.Combine(Application.StartupPath, "IPScaner.cfg")`, XML via `XmlUtility.ReadXml<ConfigInfo>`, attribute `PortTimeout`).
  * Compiled-in default: `PortTimeout = 50` (ms) — `ConfigInfo()` ctor.
  * Editable in 选项配置 (tab `tabPage2`): label `TCP超时毫秒`, `NumericUpDown` `Minimum = 10`, `Maximum = 2000`, `Increment = 50`, design `Value = 100`. (Its tooltip — `Ping时长超过此值，会认为网络异常` — is a copy/paste mistake from the Ping timeout row.)
  * `FormPortScan_Load` renders it once as `lblTimeout.Text = "TCP超时毫秒：" + Global.ConfigInfo.PortTimeout;` — stale until the window is reopened.
* Address family: `new TcpClient()` creates an **IPv4** socket, so the probe is IPv4-only even when given a hostname that resolves solely to IPv6.
* On timeout: `tcpClient.Close()` aborts the pending connect, `EndConnect` is never called, and the method returns `false`. The null callback means no unobserved exception.
* All exceptions (connection refused, unreachable, DNS failure, `SocketException`) land in the empty `catch` → `false`.

### `CheckPort` (L263–293), verbatim

```csharp
private bool CheckPort(string ipAddress, int port, CancellationToken cancellationToken)
{
    try { cancellationToken.ThrowIfCancellationRequested(); }
    catch (Exception) { return false; }
    semaphore.WaitOne();
    try
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Utility.TcpPortTest(ipAddress, port))
        {
            gbxResult.Text = $"端口扫描结果 - 正在扫描IP {ipAddress}的端口：{port}";
            ListBoxAddItem($"ip:【{ipAddress}】 发现开放的端口：{port}");
            return true;
        }
        return false;
    }
    catch { return false; }
    finally { semaphore.Release(); }
}
```

* Two cancellation checkpoints (before acquiring the permit and immediately after). The token is read from the **static field inside the task body at execution time**, not captured per scan (§A.10).
* The permit is acquired **before** the connect and released in `finally`; `WaitOne()` sits outside the `try`, which is safe because the semaphore is never disposed.
* **Open ports are the only thing reported.** Both UI writes happen on the worker thread (no `Invoke`, and this form never sets `CheckForIllegalCrossThreadCalls = false` — it relies on the .NET Framework default of not checking unless a debugger is attached).

## A.7 How open / closed / filtered are distinguished

**They are not.** The probe is a boolean:

| Wire-level outcome | Probe result | UI |
|---|---|---|
| SYN → SYN/ACK (handshake completes) | `true` | `ip:【<ip>】 发现开放的端口：<port>` line + group-box caption update |
| SYN → RST (**closed**) | `false` (`EndConnect` throws) | nothing |
| SYN → no reply (**filtered/dropped**) | `false` (`WaitOne(PortTimeout)` expires → `Close()`) | nothing |
| DNS failure / bad host string | `false` | nothing |
| No route / ARP failure | `false` | nothing |

There is no "filtered" state, no state column, no per-host summary, and **no trailing summary of closed ports**. Rows/items appear only for open ports.

## A.8 Results UI

### `lbxResult` item format (`ListBoxAddItem`, L295–307), verbatim

```csharp
private void ListBoxAddItem(string text)
{
    if (lbxResult.Items.Count > 500) { lbxResult.Items.Clear(); }
    if (!string.IsNullOrEmpty(text)) { text = DateTime.Now.ToString("HH:mm:ss ") + text; }
    lbxResult.Items.Add(text);
    lbxResult.TopIndex = lbxResult.Items.Count - 1;
}
```

* Timestamp prefix `HH:mm:ss ` (24-hour, local time, trailing space) on every **non-empty** line. Empty strings are added as genuine blank lines.
* **Ring-buffer-ish cap**: the check runs *before* adding, so the list can reach 501 items; the 502nd add clears the whole list (so the copy output can silently lose earlier results).
* Auto-scroll via `TopIndex = Items.Count - 1`.
* Single-column list; no columns/headers. Long lines are visually clipped (no horizontal scrollbar) but the item text — and therefore the clipboard text — is complete.

### Exact result-line vocabulary (all four shapes)

| Event | Exact string |
|---|---|
| shape 1 start | `开始扫描【{txtIP.Text}】 端口范围 {num}-{num2}` — one ASCII space before `端口范围`, hyphen between the numbers, no space around it |
| shape 2 start | `"开始扫描【" + txtIP.Text + "】的指定端口 " + txtIPPorts.Text` — no space before `的`, one ASCII space after `端口` |
| shapes 3/4 start | `开始扫描【{ipCount}】个IP的指定端口 {portsCsv}` |
| open port found | `ip:【{ipAddress}】 发现开放的端口：{port}` — lowercase `ip`, ASCII colon after `ip`, full-width `：` before the port number |
| scan finished | `端口扫描完毕` |
| blank separator | `""` — added once by the completion callback (so the last line of the list/copy output is typically empty) |

### Group-box caption as progress indicator

`gbxResult.Text` (the group-box caption) is the only progress display — there is **no progress bar, no percentage, no "scanned N/M" counter**. It is written from two places:

| State | `gbxResult.Text` |
|---|---|
| designer / after finish | `端口扫描结果` |
| on **each open port** (worker thread) | `端口扫描结果 - 正在扫描IP {ipAddress}的端口：{port}` |
| on finish | reset to `端口扫描结果` (immediately before the blank separator line) |

Consequence: while scanning, the caption only changes when something is found, and it keeps the last hit's values; the window never shows which pair is currently probing.

## A.9 复制到剪贴板 (copy results)

```csharp
private void lblCopyResult_Click(object sender, EventArgs e)
{
    if (lbxResult.Items.Count > 0)
    {
        string text = string.Join(Environment.NewLine, lbxResult.Items.Cast<string>());
        Clipboard.SetText(text);
        MessageBox.Show("内容已复制到剪贴板！");
    }
}
```

* Trigger: click on the blue `复制` label (no button, no keyboard shortcut, no context menu; there is no copy-selected-line feature).
* Output format: **every list item joined with `Environment.NewLine` (`\r\n` on Windows)** — i.e. the raw *display* lines, timestamps included, blank separator lines included, so the payload is a plain log, not a CSV/TSV:

```
09:31:02 开始扫描【192.168.0.1】的指定端口 80,443
09:31:02 ip:【192.168.0.1】 发现开放的端口：80
09:31:03 ip:【192.168.0.1】 发现开放的端口：443
09:31:03 端口扫描完毕
09:31:03 
```
(the final line is the trailing blank item; the text therefore normally ends with `\r\n`)

* Nothing is copied (and no message shown) when the list is empty.
* No `try/catch` around `Clipboard.SetText` (a locked clipboard throws `ExternalException`; swallowed by the empty `Application.ThreadException` handler in `Program.Main`).
* Success dialog: `MessageBox.Show("内容已复制到剪贴板！")` — plain text, no caption argument, no owner, default OK button.

## A.10 Cancellation, stop buttons, cleanup

All four 停止 buttons share one handler, `btnStopSingle_Click` (L309–323):

```csharp
private void btnStopSingle_Click(object sender, EventArgs e)
{
    btnScanSingle.Enabled = true;  btnScanSingle2.Enabled = true;
    btnScanBatch.Enabled  = true;  btnScanBatch2.Enabled  = true;
    if (cancellationTokenSource != null) { cancellationTokenSource.Cancel(); }
    btnStopSingle.Enabled = false; btnStopSingle2.Enabled = false;
    btnStopBatch.Enabled  = false; btnStopBatch2.Enabled  = false;
}
```

* **Stop is cooperative and non-blocking.** `Cancel()` only affects tasks that have not yet passed one of the two `ThrowIfCancellationRequested()` checkpoints; a task already inside `TcpPortTest` runs to completion (≤ `PortTimeout` ms). In-flight `BeginConnect`s are *not* aborted.
* `Task.WaitAll(tasks)` therefore still completes, so **the completion sequence still runs after Stop**: the list gets `端口扫描完毕`, the caption is reset to `端口扫描结果`, the callback adds a blank line and calls `ButtunEnableChange(false)`.
* Stop buttons are enabled only while a scan is in flight — set by `ButtunEnableChange(true/false)` (L380–404), which toggles all eight buttons at once:

```csharp
enable == true  : all four 扫描 Disabled, all four 停止 Enabled
enable == false : all four 扫描 Enabled,  all four 停止 Disabled
```

* `FormClosing` cancels and does **not** wait:

```csharp
private void FormPortScan_FormClosing(object sender, FormClosingEventArgs e)
{
    if (cancellationTokenSource != null) { cancellationTokenSource.Cancel(); }
}
```

  Tasks keep running (they just return `false` at their next checkpoint) and may still write into the disposed `ListBox`/`GroupBox` — a real `ObjectDisposedException`/cross-thread hazard in the original.
* **Static-state hazards (must be decided explicitly in the rebuild):**
  1. `cancellationTokenSource` is `static`; `ScanPort` overwrites it. Because the lambda dereferences the *field* (`cancellationTokenSource.Token`) when each task *starts*, tasks queued by window A can receive the token created by window B. **Stop (or closing) in any one window cancels the pending work of every other open window.**
  2. `semaphore` is `static` → the 1000-connection budget is shared process-wide. A second window's scan cannot exceed 1000 in-flight probes *in total*.
  3. `ScanPort` never disposes older `CancellationTokenSource` instances (leak-free but never reclaimed; `Semaphore` is never disposed).
  4. `ButtunEnableChange`/Stop only track the *last* scan; the GUI has no notion of "scan in progress" beyond button state.
  5. **Known bug worth fixing:** in `btnScanBatch_Click`, `ButtunEnableChange(enable: true)` runs *before* port validation, so the `请填写1~65535之间的端口` early `return` (L193–197) leaves every 扫描 button disabled and every 停止 button enabled although nothing is scanning — the user must press 停止 to recover. The other three buttons validate first and are unaffected.

## A.11 Summary of `FormPortScan` behaviours to preserve vs. change

**Preserve (user-visible):** window title `端口扫描`; the three captions incl. the trailing `(结束值可为空)`; all four 扫描/停止 pairs with green/red colours; spinner defaults `1`/`65535`, `1`/`254`, `192.168.0.1/24`, port defaults `25,80,443` (batch) and `80,443` (single); the `,`/`，` port grammar with silent token dropping and ascending normalisation; the `%TEMP%\IPScaner.FormPortScan.txt` history; the `HH:mm:ss ` prefix; the exact result-line wording; the 500-item clear; the `复制` label + `内容已复制到剪贴板！`; timeout label text.

**Fix / modernise:** no range syntax (`1000-2000`) is accepted today — if the rebuild adds it, it is a new feature; async `Socket.ConnectAsync`/`TcpClient.ConnectAsync(host, port, ct)` with `SemaphoreSlim` instead of blocking 1000 pool threads; per-window (not static) cancellation + semaphore, or one shared engine with reference counting; marshal list/caption updates through `DispatcherQueue`; report progress (x/y) and distinguish 关闭 vs 过滤 if the product wants it; abort in-flight connects on Stop; guard UI writes after close.

---

# Part B — 本机端口占用查看 (`FormPortByPid`)

## B.1 Window shell

| Property | Value |
|---|---|
| `Text` | `系统端口查看工具` |
| `Name` | `FormPortByPid` |
| `ClientSize` | `1358 × 807`; `Padding = 15,16,15,16`; `StartPosition = CenterScreen` |
| `AcceptButton` | `btnFilter` → **Enter** in either filter box runs 筛选 |
| Ctor side-effect | `Control.CheckForIllegalCrossThreadCalls = false;` (line 63) — explicitly disables cross-thread checks so the background reader may touch controls |
| Events | `Load → FormPortByPid_Load`; `dgvProcess.CellDoubleClick`, `Dock`-order stacking (verified against design `Location`s): `groupBox1` (Top, y 16…116) → `gbxResult` (Fill, y 116…750) → `statusStrip1` (Bottom, y 750…791) |

## B.2 Control inventory

### groupBox1 — `查询（筛选条件：端口，PID，进程名，路径）` (full-width commas inside the caption)

| # | Control | Type | Text / value | Geometry | Notes |
|---|---|---|---|---|---|
| 1 | `label1` | Label | `筛选条件:` | 6, 42, 118, 24 | 宋体 10 |
| 2 | `txtFilter` | TextBox | (empty) | 141, 35, 305, 34 | 宋体 10; no `KeyDown`/`MaxLength` |
| 3 | `label2` | Label | `反向筛选:` | 478, 42, 118, 24 | 宋体 10 |
| 4 | `txtExceptFilter` | TextBox | (empty) | 614, 35, 305, 34 | 宋体 10 |
| 5 | `btnFilter` | Button | `筛选` | 930, 32, 136, 46 | standard visual styles; `AcceptButton` |
| 6 | `btnReload` | Button | `刷新端口列表` | 1072, 32, 174, 46 | standard visual styles |

### gbxResult — `系统端口列表` (caption is rewritten with statistics, §B.9)

| # | Control | Type | Geometry | Notes |
|---|---|---|---|---|
| 1 | `dgvProcess` | DataGridView | 4, 28, 1320, 602; Dock Fill inside a GroupBox with `Padding = 4` | see §B.5 |

`dgvProcess` properties: `AllowUserToAddRows = false`, `AllowUserToDeleteRows = false`, `ReadOnly = true`, `BackgroundColor = SystemColors.Control`, `ColumnHeadersHeightSizeMode = AutoSize`, `RowHeadersWidth = 50`, `RowTemplate.Height = 23`, `AutoGenerateColumns = false` (**assigned in `Load`, after the reader task is started**), `Dock = Fill`. Row-header default style: `Alignment = MiddleLeft`, `BackColor = SystemColors.Control`, `Font = 宋体 9pt Regular` (GDI charset 134 = GB2312), `ForeColor = SystemColors.WindowText`, `SelectionBackColor = SystemColors.Highlight`, `SelectionForeColor = SystemColors.HighlightText`.

### statusStrip1 (three items, left→right in `Items.AddRange(new[] { statusTip, toolStripStatusLabel3, toolStripStatusLabel2 })`)

| Order | Name | Text | Style |
|---|---|---|---|
| 1 | `statusTip` | `程序已就绪` (design) → `正在读取端口数据` (Load) → `端口数据加载完毕` (after read) | default |
| 2 | `toolStripStatusLabel3` | `双击【进程所在路径】可打开文件目录` | `ForeColor = SystemColors.Highlight`, `BorderSides = Left` |
| 3 | `toolStripStatusLabel2` | `双击【PID】可杀死进程` | `ForeColor = Color.DarkOrange`, `BorderSides = Left`, `LinkColor = Blue` (but **`IsLink` is never set** — it renders as plain orange text, not a hyperlink) |

Strip: `ImageScalingSize = 28,28`, `Padding = 2,0,15,0`, `Size = 1328 × 41`.

## B.3 Enumeration mechanism — **no P/Invoke at all**

The TCP/UDP tables are obtained by **shelling out to `netstat -ano` through `cmd.exe` and regex-parsing stdout**. There is **no** `GetExtendedTcpTable`, `GetExtendedUdpTable`, `GetTcpTable`, `iphlpapi.dll`, `MIB_TCPROW*` or any IP Helper P/Invoke anywhere in the decompiled sources (verified project-wide: the only `DllImport`s are `user32.dll` in `DesktopOverlayForm` and `Netapi32.dll` in `NetAPIUtility`). There is likewise **no TCP state enum and no state column** (§B.7).

`GetActivePorts` (L115–154), verbatim:

```csharp
private List<ProcessInfo> GetActivePorts(ProtocolType protocolType)
{
    string text = protocolType.ToString().ToUpper();          // "TCP" or "UDP"
    List<ProcessInfo> list = new List<ProcessInfo>();
    string arguments = "/c netstat -ano | findstr " + text;
    ProcessStartInfo startInfo = new ProcessStartInfo("cmd.exe", arguments)
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    using (Process process = Process.Start(startInfo))
    {
        using StreamReader streamReader = process.StandardOutput;
        string input = streamReader.ReadToEnd();
        string pattern = ((text == "TCP")
            ? "\\s+(\\d+\\.\\d+\\.\\d+\\.\\d+):(\\d+)\\s+\\S+\\s+\\S+\\s+(\\d+)"
            : "\\s+(\\d+\\.\\d+\\.\\d+\\.\\d+):(\\d+)\\s+\\*\\s*:\\s*\\*\\s+(\\d+)");
        MatchCollection matchCollection = Regex.Matches(input, pattern);
        foreach (Match item in matchCollection)
        {
            try
            {
                string value  = item.Groups[1].Value;   // local IPv4 address
                int num       = int.Parse(item.Groups[2].Value);  // local port
                string value2 = item.Groups[3].Value;   // PID
                if (num > 0)
                {
                    ProcessInfo processInfo = GetProcessDetails(value2, text, num);
                    if (processInfo != null && !list.Any((ProcessInfo r) => r.Port == processInfo.Port && r.PID == processInfo.PID))
                    {
                        list.Add(processInfo);
                    }
                }
            }
            catch { }
        }
    }
    return list;
}
```

Exact regexes (with the C# escapes resolved):

| Protocol | Regex | netstat line shape it matches |
|---|---|---|
| TCP | `\s+(\d+\.\d+\.\d+\.\d+):(\d+)\s+\S+\s+\S+\s+(\d+)` | `  TCP    0.0.0.0:135     0.0.0.0:0     LISTENING     1234` → group 1 local addr, group 2 local port, group 3 PID; **the state token is matched by `\S+` and thrown away** |
| UDP | `\s+(\d+\.\d+\.\d+\.\d+):(\d+)\s+\*\s*:\s*\*\s+(\d+)` | `  UDP    0.0.0.0:500     *:*     1234` → requires the literal `*:*` foreign address |

Behavioural consequences of this approach:

* **IPv6 rows are invisible** — both patterns require a dotted-quad local address, so `[::]:135`, `[::1]:…` etc. never match.
* Only local (listening/local-endpoint) address+port is captured; the **remote endpoint is discarded** for TCP (2nd `\S+`).
* `findstr TCP` is case-sensitive and matches the substring anywhere in the line; netstat prints uppercase `TCP`/`UDP`, so the filter works, but the TCP pass runs first and the UDP pass second, each with its own `cmd.exe` + `netstat` process.
* Both protocols run **sequentially** inside one background task (`GetActivePorts(Tcp)` then `GetActivePorts(Udp)`), i.e. two `netstat` invocations per refresh.
* Port `0` is dropped (`if (num > 0)`).
* Duplicate suppression is **per protocol list** and keyed on `(Port, PID)` — with the state discarded, a listener and its established connections on the same port+PID collapse into one row, while the same port+PID can legitimately appear twice overall (one TCP row + one UDP row).
* Rows whose owning process cannot be queried are **dropped entirely** (see §B.4) — silently, without a placeholder.
* If `netstat` is missing/localised or `cmd.exe` cannot start, the whole list ends up empty (no diagnostic).

### `GetProcessDetails` (L156–177), verbatim

```csharp
private ProcessInfo GetProcessDetails(string pid, string protocol, int port)
{
    try
    {
        int processId = int.Parse(pid);
        Process processById = Process.GetProcessById(processId);
        string processName = processById.ProcessName;
        string fileName = processById.MainModule.FileName;
        return new ProcessInfo { PID = pid, Name = processName, Path = fileName, Protocol = protocol, Port = port };
    }
    catch (Exception) { return null; }
}
```

* Executable path comes from `Process.MainModule.FileName` (internally `CreateToolhelp32Snapshot`/`Module32First` + `GetModuleFileNameEx`). This throws (`Win32Exception`, access denied) for protected/elevated/32-vs-64 mismatches and for already-exited PIDs, and `Process.GetProcessById` throws `ArgumentException` for a stale PID → the row is discarded.
* `PID` is stored as the **original string** from netstat (not the parsed int).
* Because this access is exactly what elevation is for, `Program.Main` forces the whole app to run elevated (§B.10).

## B.4 `ProcessInfo` model (`ProcessInfo.cs`)

```csharp
public class ProcessInfo
{
    public string Protocol { get; set; }   // "TCP" | "UDP"
    public int    Port     { get; set; }   // 1..65535
    public string PID      { get; set; }   // string, as parsed from netstat
    public string Name     { get; set; }   // Process.ProcessName (no ".exe")
    public string Path     { get; set; }   // Process.MainModule.FileName (full path incl. .exe)

    public override string ToString()
        => $"Port:{Port}，PID: {PID}, Name: {Name}, Path: {Path}";   // note the full-width ，after the port
}
```

The `ToString()` is not used by the grid (all five columns bind by `DataPropertyName`); it exists for debugging/`ListBox`-style display. There is **no `State`, `RemoteAddress`, `RemotePort`, or `LocalAddress` member** — nothing about TCP state survives enumeration.

## B.5 `dgvProcess` columns (exact)

| Display index | `Name` | `DataPropertyName` | `HeaderText` | `Width` | `AutoSizeMode` | `MinimumWidth` | `ReadOnly` | `SortMode` | `DefaultCellStyle` |
|---|---|---|---|---|---|---|---|---|---|
| 0 | `Column1` | `Protocol` | `协议` | 40 | `NotSet` | 9 | true | `NotSortable` | — |
| 1 | `Column2` | `Port` | `端口` | 50 | `NotSet` | 9 | true | `NotSortable` | — |
| 2 | `Column3` | `PID` | `PID` | 50 | `NotSet` | 9 | true | `NotSortable` | `ForeColor = Color.DarkOrange` |
| 3 | `Column4` | `Name` | `进程名` | 130 | `NotSet` | 9 | true | `NotSortable` | — |
| 4 | `Column5` | `Path` | `进程所在路径` | — | **`Fill`** | 9 | true | `NotSortable` | — |

Cell values come straight from the bound properties: 协议 = `TCP`/`UDP`, 端口 = integer, PID = *string*, 进程名 = `ProcessName`, 进程所在路径 = full executable path. `协议`/`PID` are the only columns with custom colour (PID = DarkOrange); there is no per-state colouring because there is no state.

**Row headers** are used as 1-based row numbers (`DgvService_RowStateChanged`, wired to `RowStateChanged`):

```csharp
private void DgvService_RowStateChanged(object sender, DataGridViewRowStateChangedEventArgs e)
{
    if (e.Row.Index >= 0) { e.Row.HeaderCell.Value = $"{e.Row.Index + 1}"; }
}
```

`DgvService_DataError` is an **empty handler** (`private void DgvService_DataError(object sender, DataGridViewDataErrorEventArgs e) { }`) that exists purely to swallow binding exceptions.

**Row order** = `AllPortProcessList` = all TCP rows (in netstat output order) followed by all UDP rows (in netstat output order). No sorting is applied anywhere (§B.11).

## B.6 (Question 7) TCP state rendering — **there is none**

* The TCP regex consumes netstat's state column with `\S+` and never captures it: `…\s+(\d+\.\d+\.\d+\.\d+):(\d+)\s+\S+\s+\S+\s+(\d+)` (foreign address = first `\S+`, **state = second `\S+`**).
* There is no `MIB_TCP_STATE`/`TcpState` enum, no `LISTENING`/`ESTABLISHED`/`TIME_WAIT` string mapping, no state column, no state filter, no state glyph anywhere in `FormPortByPid.cs`, `ProcessInfo.cs`, or the rest of the project.
* Consequently a listener and every established connection sharing (port, PID) produce exactly one row, and the grid cannot answer "who is connected to me".

If the WinUI rebuild keeps the netstat approach, a faithful build must keep discarding state. If it moves to `GetExtendedTcpTable` (recommended, §B.13), state becomes available — that would be a **feature addition**, and any state column/state filter must be introduced deliberately (see Open questions).

## B.7 Filter (`筛选`) semantics — `btnFilter_Click` (L179–204)

Both predicates, verbatim (note `ToUpper()` culture-sensitive comparisons, and that only `txtFilter`/`txtExceptFilter` are `Trim()`ed):

```csharp
// positive filter (only when txtFilter.Text is non-empty)
string filter = txtFilter.Text.Trim().ToUpper();
List<ProcessInfo> list2 = AllPortProcessList.Where((ProcessInfo r) =>
        r.Port.ToString() == filter ||
        r.PID.ToString()  == filter ||
        r.Name.ToUpper().Contains(filter) ||
        r.Path.ToUpper().Contains(filter)).ToList();

// reverse filter / exclusion (applied on top, when txtExceptFilter.Text.Trim() is non-empty)
string exceptFilter2 = txtExceptFilter.Text.Trim().ToUpper();
list2 = list2.Where((ProcessInfo r) =>
        r.Port.ToString() != exceptFilter2 &&
        r.PID.ToString()  != exceptFilter2 &&
        !r.Name.ToUpper().Contains(exceptFilter2) &&
        !r.Path.ToUpper().Contains(exceptFilter2)).ToList();
```

Exact matching semantics:

| Field | Positive `筛选` | 反向筛选 (exclusion) |
|---|---|---|
| 端口 (`Port`) | `Port.ToString() == filter` → **exact string equality** on the integer's decimal text | `Port.ToString() != except` → row **dropped** on exact equality |
| PID (`PID`) | `PID.ToString() == filter` → **exact string equality** | `PID.ToString() != except` → dropped on exact equality |
| 进程名 (`Name`) | case-insensitive **substring** (`Name.ToUpper().Contains`) | case-insensitive substring → dropped if contained |
| 进程所在路径 (`Path`) | case-insensitive **substring** | case-insensitive substring → dropped if contained |

Consequences:

* The two text boxes are **ANDed with an OR inside each box**: a row is kept iff `(port==f ‖ pid==f ‖ name⊃f ‖ path⊃f)` **and** `(port!=e ∧ pid!=e ∧ name⊅e ∧ path⊅e)`.
* Port/PID never match by prefix or substring (`"80"` matches port 80, not 8080 or PID 80? — it *does* match PID 80, because all four fields are ORed: `"80"` keeps every row whose port is 80 **or** whose PID is `"80"` **or** whose name/path contains `80`).
* Because matching happens against `Port.ToString()`, a filter of `"080"` or `"80.0"` matches nothing.
* Empty `txtFilter` ⇒ no positive filter (all rows); the reverse filter still applies.
* Empty `txtExceptFilter` ⇒ no exclusion.
* Rebinding always does `dgvProcess.DataSource = null; dgvProcess.DataSource = …;` (full reset, selection cleared implicitly).
* **Statistics bug to preserve or fix:** in the empty-`txtFilter` branch the code calls `RefreshStat(AllPortProcessList)` — the **unfiltered** list — while the non-empty branch calls `RefreshStat(list2)`. So a reverse-only filter changes the grid but *not* the `(TCP: n, UDP: m, 合计: k)` caption, whereas a combined filter does.
* Typing in either box does nothing until `筛选` is clicked (or Enter, via `AcceptButton`). There is no live/instant filtering and no debounce. There is no "reset/clear" button — the user clears the boxes and clicks 筛选 again.

## B.8 Group-box statistics (`RefreshStat`, L206–220)

```csharp
List<string> list2 = new List<string>();
if (list.Count > 0)
{
    IEnumerable<IGrouping<string, ProcessInfo>> enumerable = from r in list group r by r.Protocol;
    foreach (IGrouping<string, ProcessInfo> item in enumerable) { list2.Add(item.Key + ": " + item.Count()); }
    list2.Add("合计: " + list.Count);
}
gbxResult.Text = "系统端口列表(" + string.Join(", ", list2) + ")";
```

* Caption format: `系统端口列表(TCP: {tcpCount}, UDP: {udpCount}, 合计: {total})` — group order is first-appearance order (TCP block first, then UDP), separator is `, ` (ASCII comma + space), and 合计 counts rows (not distinct ports).
* Empty list ⇒ `系统端口列表()` (empty parentheses).
* Caption before the first read finishes: designer text `系统端口列表`.
* Called from `ReadAllPort`'s completion (with the full list) and from `btnFilter_Click`.

## B.9 Double-click flows — kill PID and open the folder (`dgvProcess_CellDoubleClick`, L237–274)

```csharp
private void dgvProcess_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
{
    if (e.RowIndex < 0 || e.ColumnIndex < 0) { return; }
    object value = dgvProcess.CurrentCell.Value;
    List<ProcessInfo> list = dgvProcess.DataSource as List<ProcessInfo>;
    string headerText = dgvProcess.Columns[e.ColumnIndex].HeaderText;
    string text = headerText;
    int result;
    if (!(text == "PID"))
    {
        if (!(text == "进程所在路径") || dgvProcess.CurrentCell.Value == null) { return; }
        string text2 = dgvProcess.CurrentCell.Value.ToString();
        if (!string.IsNullOrEmpty(text2))
        {
            string directoryName = Path.GetDirectoryName(text2);
            if (Directory.Exists(directoryName)) { Process.Start(directoryName); }
        }
    }
    else if (MessageBox.Show($"确定要杀死进程【{dgvProcess.CurrentRow.Cells[e.ColumnIndex + 1].Value}】吗?\r\n请注意：杀死进程可能会导致应用异常，请谨慎操作。",
                 Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK
             && int.TryParse(dgvProcess.CurrentCell.Value.ToString(), out result))
    {
        Process processById = Process.GetProcessById(result);
        if (processById != null)
        {
            processById.Kill();
            processById.WaitForExit(3000);
        }
        btnReload.PerformClick();
    }
}
```

**Dispatch is by column header text**, so renaming `PID` or `进程所在路径` silently breaks both features (an i18n/localisation hazard). Double-clicking 协议/端口/进程名 does nothing. The switch uses `CurrentCell`, not the double-clicked cell (normally identical).

### Kill-PID flow

1. **Confirmation** (exact text, `\r\n` line break; caption = form title `系统端口查看工具`; `OKCancel`; `MessageBoxIcon.Question`):

```
确定要杀死进程【{进程名}】吗?
请注意：杀死进程可能会导致应用异常，请谨慎操作。
```

   The `【…】` placeholder is `CurrentRow.Cells[ColumnIndex + 1].Value`, i.e. the **进程名** of the row (column 3), not the PID.
2. On Cancel → nothing. On OK → `int.TryParse(CurrentCell.Value.ToString())` (PID cell). Unparsable ⇒ no action (silent).
3. `Process.GetProcessById(pid)` → `.Kill()` (no `entireProcessTree` variant, so child processes survive) → `.WaitForExit(3000)`.
4. Then `btnReload.PerformClick()` — an unconditional full re-read of both tables.

**Privileges:** the entire app is forced to run elevated, so most kills succeed:

```csharp
// Program.Main
WindowsIdentity current = WindowsIdentity.GetCurrent();
Application.ThreadException += Application_ThreadException;
AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
WindowsPrincipal windowsPrincipal = new WindowsPrincipal(current);
if (windowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator)) { Application.Run(new FormMain()); return; }
ProcessStartInfo psi = new ProcessStartInfo();
psi.FileName = Application.ExecutablePath;
psi.Verb = "runas";                    // UAC re-launch, not an app.manifest
Process.Start(psi);
Application.Exit();
```

There is **no** `app.manifest`/`requestedExecutionLevel` in the project (`.csproj` has no manifest item); elevation is achieved by restarting itself with `Verb = "runas"`. The WinUI rebuild needs `<requestedExecutionLevel level="requireAdministrator">` (unpackaged) or a documented "run elevated" affordance, otherwise `MainModule.FileName` for other processes and `Kill()` on elevated/protected processes will fail.

**Error handling (or the lack of it):** `GetProcessById`/`Kill()`/`WaitForExit` are **not** wrapped in `try/catch`. Failures (`Win32Exception` access denied, `ArgumentException` no such process, `InvalidOperationException` already exited, `NotSupportedException`) escape the handler and are swallowed by the empty global handlers in `Program.Main`:

```csharp
private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e) { }
private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e) { }
```

Net effect: a failed kill produces **no message box, no status-bar text, no reload** — it silently does nothing. (Because `Application.ThreadException` has a handler, the WinForms default "unhandled exception" dialog is suppressed and the app keeps running.) A WinUI rebuild should surface a real error instead of silently swallowing it.

### Open-folder flow (双击 进程所在路径)

`Path.GetDirectoryName(fullExePath)` → `Directory.Exists(...)` → `Process.Start(directoryName)` = opens Explorer at the containing folder via the shell. No selection/highlight of the file itself, no error message if the folder is gone. Failure inside `Process.Start` is likewise swallowed globally.

## B.10 Refresh lifecycle & threading (`ReadAllPort`, `Load`, `btnReload_Click`)

```csharp
private void FormPortByPid_Load(object sender, EventArgs e)
{
    statusTip.Text = "正在读取端口数据";
    Show();
    Application.DoEvents();                 // force a paint before the (async) read
    ReadAllPort();
    dgvProcess.AutoGenerateColumns = false;
    dgvProcess.DataError          += DgvService_DataError;
    dgvProcess.RowStateChanged    += DgvService_RowStateChanged;
}

private void ReadAllPort()
{
    dgvProcess.DataSource = null;
    ReadCompleted = false;
    Task.Factory.StartNew(() =>
    {
        AllPortProcessList = new List<ProcessInfo>();
        AllPortProcessList.AddRange(GetActivePorts(ProtocolType.Tcp));
        AllPortProcessList.AddRange(GetActivePorts(ProtocolType.Udp));
        ReadCompleted = true;
        if (!dgvProcess.IsDisposed)
        {
            dgvProcess.BeginInvoke((Action)(() =>
            {
                statusTip.Text = "端口数据加载完毕";
                dgvProcess.DataSource = AllPortProcessList;
                RefreshStat(AllPortProcessList);
                dgvProcess.ClearSelection();
            }));
        }
    });
}

private void btnReload_Click(object sender, EventArgs e)
{
    btnReload.Enabled = false;
    ReadAllPort();
    statusTip.Text = "正在读取端口数据";
    ThreadPool.QueueUserWorkItem((object obj) =>
    {
        while (!ReadCompleted) { Thread.Sleep(50); }
        btnReload.Enabled = true;
    });
}
```

* Load sequence: status → `正在读取端口数据`; grid cleared (`DataSource = null`); background task enumerates TCP then UDP; on completion the UI thread (via `BeginInvoke`) sets `端口数据加载完毕`, binds `List<ProcessInfo>`, refreshes the caption, clears selection.
* 刷新端口列表 = full re-enumeration (two new `netstat` processes), disabled while in flight; the re-enable is a 50 ms polling `ThreadPool` loop on the `ReadCompleted` flag — no `CancellationToken`, no cancellation of an in-flight refresh, no progress UI.
* There is no auto-refresh timer; the list shows a snapshot until 刷新端口列表 is pressed.
* Threading quirks: `Control.CheckForIllegalCrossThreadCalls = false` (ctor) plus `BeginInvoke` for the binding; `ReadCompleted` is a plain `bool` (no `volatile`/`Interlocked`); `AllPortProcessList` is assigned inside the background task, so clicking `筛选` **before** the first read completes can throw `NullReferenceException` (swallowed globally).

## B.11 Sorting behaviour — **none**

* Every column has `SortMode = DataGridViewColumnSortMode.NotSortable`; clicking a header does nothing (no sort glyph, no reorder).
* The data source is a plain `List<ProcessInfo>` (not a `SortableBindingList`/`DataView`), so no implicit sorting.
* Rows are always in enumeration order: **all TCP rows first (netstat order), then all UDP rows (netstat order)**; a filter/refresh preserves that relative order, and 1-based row numbers in the header follow the current display order.
* The v1.23 changelog entry "IP批量扫描，点击列头进行排序" refers to the *batch IP scan* window (`FormIPSegment`), **not** this viewer — do not carry that expectation here.

## B.12 Implementation notes for a WinUI 3 rebuild

* **Enumeration.** Keep `netstat`-compatible output only if bug-for-bug fidelity is required (IPv6 blind, state discarded, `MainModule` per row, two `cmd.exe` spawns). The better native route (network access from a packaged app needs no special capability, but spawning `cmd.exe` does require full trust):
  * `GetExtendedTcpTable(pTcpTable, ref dwSize, false, AF_INET /*2*/, TCP_TABLE_OWNER_PID_ALL /*5*/, 0)` → `MIB_TCPTABLE_OWNER_PID { DWORD dwNumEntries; MIB_TCPROW_OWNER_PID table[]; }` with `MIB_TCPROW_OWNER_PID { DWORD dwState; DWORD dwLocalAddr; DWORD dwLocalPort; DWORD dwRemoteAddr; DWORD dwRemotePort; DWORD dwOwningPid; }` (`dwLocalPort`/`dwRemotePort` are network byte order in the low 16 bits — `ntohs((u_short)(x & 0xFFFF))`).
  * `GetExtendedUdpTable(pUdpTable, ref dwSize, false, AF_INET, UDP_TABLE_OWNER_PID /*1*/, 0)` → `MIB_UDPTABLE_OWNER_PID { DWORD dwNumEntries; MIB_UDPROW_OWNER_PID table[]; }` with `MIB_UDPROW_OWNER_PID { DWORD dwLocalAddr; DWORD dwLocalPort; DWORD dwOwningPid; }`.
  * IPv6 (which the original cannot see): `AF_INET6 /*23*/` with `MIB_TCP6ROW_OWNER_PID { DWORD dwState; UCHAR ucLocalAddr[16]; DWORD dwLocalScopeId; DWORD dwLocalPort; UCHAR ucRemoteAddr[16]; DWORD dwRemoteScopeId; DWORD dwRemotePort; DWORD dwOwningPid; }` and `MIB_UDP6ROW_OWNER_PID`.
  * State text (only if a state column is added): `MIB_TCP_STATE` 1 `CLOSED`, 2 `LISTEN`, 3 `SYN_SENT`, 4 `SYN_RCVD`, 5 `ESTAB`, 6 `FIN_WAIT1`, 7 `FIN_WAIT2`, 8 `CLOSE_WAIT`, 9 `CLOSING`, 10 `LAST_ACK`, 11 `TIME_WAIT`, 12 `DELETE_TCB`.
  * Process path without `MainModule`: `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, …)` + `QueryFullProcessImageName` (kernel32) — works for more processes and can be done for the *whole table* in one pass, grouped by PID, instead of per row.
* **UI.** `DataGridView` → `CommunityToolkit.WinUI.UI.Controls.DataGrid` (or a `ListView` with a grid layout). If the toolkit grid is used, set `CanUserSortColumns = false` on every column to reproduce §B.11, keep the same five headers (including the DarkOrange PID column), and use a row-header template / a leading index column for the `1..N` row numbers. The group-box caption `${…}` statistics belong in a header `TextBlock`; the status-strip hints become a footer `InfoBar`/`TextBlock` pair preserving `双击【PID】可杀死进程` and `双击【进程所在路径】可打开文件目录`.
* **Data model.** A WinUI-friendly `ProcessInfo` record with `Protocol`, `Port`, `Pid` (string), `Name`, `Path` — plus, if state is added, `State`/`StateText` and `RemoteEndPoint`.
* **Threading.** Enumeration on a background `Task`; marshal all UI updates through `DispatcherQueue.TryEnqueue`; the original's `CheckForIllegalCrossThreadCalls = false` + `BeginInvoke` + `DoEvents` hacks disappear.
* **Elevation.** `Process.Kill` and `QueryFullProcessImageName` degrade gracefully without admin; decide between a `requireAdministrator` manifest (matches the original) or a "relaunch elevated" button.

---

# Open questions / ambiguities

1. **`"80,443,1000-2000"` range syntax does not exist in V1.28.2.** All three port parsers only split on `,`/`，` and `int.TryParse` each token; `1000-2000` (and any other range notation) is silently discarded, and `-` cannot even be typed into `txtPorts` (the `KeyDown` filter blocks it). If the WinUI product brief promises ranges, that is a **new feature**; confirm the intended syntax (`-`, `:`, `..`?) and whether a range expansion is allowed to be huge (e.g. `1-65535` = 65 535 tokens).
2. **No `all` / `全部端口` keyword.** "单IP扫描全部端口" is realised purely by the 开始端口/结束端口 spinners at their defaults `1`/`65535`. Should the rebuild add an explicit "全部端口" checkbox/preset (and confirm the 65 535-probe cost), or keep the spinner-only affordance?
3. **TCP state (question 7) is unavailable in the original.** The state column of `netstat -ano` is matched by `\S+` and discarded, and `ProcessInfo` has no state member, so there is no "state enum rendered as text" to be faithful to. Decide whether the rebuild adds a 状态 column (and then whether it should map `MIB_TCP_STATE` to the English netstat words — `LISTENING`, `ESTABLISHED`, `TIME_WAIT`, … — or to Chinese).
4. **Copy-payload format.** `复制` currently copies the on-screen log verbatim (timestamps + blank lines + `端口扫描完毕`), which is not machine-parseable and can be truncated by the 500-item clear. Should the rebuild keep it byte-identical (a fidelity requirement for the parent spec?) or add a structured export (CSV/TSV of `IP,port`)?
5. **Cross-window static state.** The semaphore (1000) and the `CancellationTokenSource` are `static`; a Stop/close in one `FormPortScan` cancels every other window, and the token is read from the static field *inside* each task. Intended (a deliberate global throttle) or a bug to fix in the rebuild? Same question for the shared ThreadPool-blocking design (1000 parked threads vs. async `ConnectAsync`).
6. **`Task.WaitAll` on the UI-adjacent path + no progress.** There is no "scanned x/y" counter and no progress bar, and the caption only updates on hits. Does the WinUI build need real progress (and does the spec need a defined behaviour for the 65 535 × /24-scale worst case, plus the >1000-address confirmation which currently exists only in shape 4)?
7. **`txtBit` inconsistency.** Tooltip says `值在1~24之间`, the control allows `1..30`, and `CalNBFL` rejects 31/32 with `掩码位异常，请重新输入`. Which range should the rebuild enforce, and should bits 31/32 (valid CIDR) be supported?
8. **`lblCopyResult` placement.** The blue `复制` label is anchored Top|Right but stored at (155, 4), so it renders inside the caption band next to `端口扫描结果`, not at the right edge. Is the intended design a caption-row action (as built) or a right-aligned header button (as the anchor suggests)?
9. **Row-drop semantics of `GetProcessDetails`.** Rows whose process cannot be queried (protected PIDs, exited PIDs, IPv6-only listeners) are silently absent — the user sees an incomplete port list with no indication. Should the rebuild show such rows with an "unknown"/`无权限` process name instead?
10. **`RefreshStat` inconsistency.** With only 反向筛选 filled, the caption still shows the unfiltered totals; with 筛选 filled it shows the filtered totals. Bug to fix (always show filtered totals) or behaviour to replicate?
11. **Failed kill is completely silent** (`try/catch`-free code path + empty `Application.ThreadException` handler ⇒ no dialog, no status text, no reload). The rebuild presumably needs an error dialog/`InfoBar`, but confirm the expected Chinese wording (`杀进程失败` etc. does not exist anywhere in the source).
12. **Elevation model.** The original relaunches itself with `Verb = "runas"` (no manifest) and exits if the user declines. Which elevation model should the WinUI app use (requireAdministrator manifest vs. on-demand elevated helper), and what should the port viewer degrade to without admin?
13. **Localisation/`findstr` fragility.** Enumeration depends on an English `cmd.exe`/`netstat` layout (`TCP`/`UDP` tokens, `*:*`, dotted-quad addresses). Nothing is version- or locale-guarded. Confirm whether the rebuild keeps the shell-out at all, or moves to `GetExtendedTcpTable`/`GetExtendedUdpTable` (which would also add IPv6 and state as side effects — see items 3, 9).
14. **Line counts differ from the task brief** (`FormPortScan.cs` 921 vs 859, `FormPortByPid.cs` 485 vs 450). If another agent is working from a different decompiler output/version, cross-check the strings above before reconciling.
