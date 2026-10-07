# WinUI page implementation contract

Read this fully before writing code. It is the frozen interface between the
pages built in parallel and the already-finished shell.

## 1. Where you work

The real tree is:

```
E:\Users\Administrator\Documents\deepseek-harness\default-workspace\ipscaner-winui\
  src\IPScaner.Core\            <- FINISHED. Do not modify.
  src\IPScaner.WinUI\           <- the app. You add page files here.
  docs\re\01-mainwindow.md      <- reverse-engineering specs (source of truth)
  docs\re\02-config-memo.md
  docs\re\03-batchscan-calc.md
  docs\re\04-portscan.md
  docs\re\05-localip-wifi-infra.md
```

**Do not build the real tree** — several agents share it. Instead:

```powershell
# 1. copy to your own scratch area (exclude build output)
$src = "E:\Users\Administrator\Documents\deepseek-harness\default-workspace\ipscaner-winui"
$dst = "E:\Users\Administrator\Documents\deepseek-harness\default-workspace\_work\<YOUR-NAME>\ipscaner-winui"
robocopy $src $dst /E /XD bin obj .git _work _verify | Out-Null

# 2. build + iterate there
dotnet build "$dst\src\IPScaner.WinUI\IPScaner.WinUI.csproj" -c Debug --nologo

# 3. run it to check your page renders
& "$dst\src\IPScaner.WinUI\bin\Debug\net8.0-windows10.0.19041.0\win-x64\IPScaner.exe"
```

Screenshot helper (works on a running window, z-order independent):

```powershell
& "C:\Users\Administrator\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe" `
  "$src\tools\grab_window.py" "IPScaner" "$dst\shot.png"
```
Click through to your page first (the app opens on IP段扫描; nav items are on the left).

**When done, copy ONLY your page files back into the real tree.** Never copy
`bin`, `obj`, `MainWindow.*`, `App.*` or anything under `src\IPScaner.Core`.

## 2. Files you own

Each page is a XAML `Page`. The real tree currently contains a **stub** at
`src\IPScaner.WinUI\Views\<Name>.cs` that renders "功能建设中".

To implement your page:
1. **Delete** `Views\<Name>.cs` (the stub).
2. **Add** `Views\<Name>.xaml` + `Views\<Name>.xaml.cs`.

The stub must be deleted — leaving it produces a duplicate class definition.

Exact shape:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page x:Class="IPScaner.WinUI.Views.BatchScanPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
      xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
      mc:Ignorable="d">
    ...
</Page>
```

```csharp
namespace IPScaner.WinUI.Views;

public sealed partial class BatchScanPage : Microsoft.UI.Xaml.Controls.Page
{
    public BatchScanPage() => InitializeComponent();
}
```

## 3. What you must NOT change

`MainWindow.xaml(.cs)`, `App.xaml(.cs)`, `Services\*`, `ViewModels\*`, `Styles\*`,
`Views\ScanPage.*`, `Views\AboutPage.*`, `Views\PlaceholderPage.cs`,
anything under `src\IPScaner.Core\`, `IPScaner.WinUI.csproj`.

If you need a helper, put it **inside your own page class** as a private member,
or as a `private sealed` nested type. Do not add new top-level public types —
two agents could pick the same name.

## 4. Available APIs

### `IPScaner.WinUI.Services.AppServices.Current`

```csharp
AppConfig Config { get; }                 // live config; see §5
void ApplyConfig(AppConfig config);       // save + raise ConfigChanged
void ReloadConfig();
ConfigStore ConfigStore { get; }          // .FilePath
MemoStore  Memo { get; }                  // .Lookup(mac, ip), .Set(key,value), .Save(), .Entries, .Remove()
DiyCommandStore DiyCommands { get; }      // .Load()
NetworkHistory History { get; }           // .Load(), .Add(AdapterInfo), .Save(entries)
ShellLauncher Shell { get; }              // .RunCommand(cmd, visible), .RunHostAction(EventName,ip,pingCount), .OpenUrl, .OpenShare, .OpenFolder, .RunBuiltInTool
AdapterService Adapters { get; }          // .GetAll(), .GetSegments(), .GetPrimarySegment()
ArpTable Arp { get; }
ScanEngine Scanner { get; }               // .RunAsync(...), .ProbeOnceAsync(ip, cfg, ct)
PortScanner PortScanner { get; }          // .RunAsync(PortScanRequest, onResult, progress, ct)
LocalPortTable LocalPorts { get; }        // .QueryAsync(includeTcp, includeUdp, includeIpv6, ct)
WifiService Wifi { get; }                 // .QueryAsync(ct)
NetworkConfigurator NetworkConfig { get; }// .SetStaticAsync / .SetDhcpAsync / .SetDnsStaticAsync / .SetDnsDhcpAsync / .EnableAdapterAsync / .DisableAdapterAsync
bool IsElevated { get; }
IReadOnlyList<AdapterInfo> AdapterList { get; }
IReadOnlyList<AdapterInfo> RefreshAdapters();
void ClearCaches();
string GetDefaultSegment();
event EventHandler<AppConfig>? ConfigChanged;
```

### `IPScaner.WinUI.Services.UiKit` (static)

```csharp
Color ColorFromArgb(int argb);      int ArgbFromColor(Color c);
SolidColorBrush BrushFromArgb(int argb);
Color ContrastingTextColor(Color background);
bool CopyToClipboard(string text);  Task<string> ReadClipboardAsync();
Task InfoAsync(XamlRoot, string title, string message);
Task<bool> ConfirmAsync(XamlRoot, string title, string message, string primary="确定", string close="取消");
Task<ContentDialogResult> ShowContentAsync(XamlRoot, string title, object content, string? primary, string? secondary, string close="关闭");
Task<ContentDialogResult> ShowSafeAsync(ContentDialog dialog);
XamlRoot? MainXamlRoot;   void ActivateMainWindow();   bool IsElevated;   bool RestartElevated();
```

Pass `XamlRoot` (the page property) to the dialog helpers. WinUI has **no
`MessageBox`** — use these for every prompt.

### `IPScaner.WinUI.Services.NavigationArgs` (static)

```csharp
string? PendingPortScanHost { get; set; }   // + TakePortScanHost()
string? PendingBatchSegment { get; set; }   // + TakeBatchSegment()
string? PendingMemoKey { get; set; }        // + TakeMemoKey()
```

Cross-page navigation: `App.MainWindow?.NavigateTo("batch")` — valid tags are
`scan, batch, portscan, localport, localip, wifi, calc, memo, config, about`.
From a page you can also set the status bar via `App.MainWindow?.SetStatus("…")`.

### Core types you will use

Namespace `IPscaner.Core.*` — `AppConfig`, `ConfigStore`, `EventName`,
`HostResult`, `HostStatus`, `HostStatusText`, `LivenessSource`, `AdapterInfo`,
`PortScanResult`, `LocalPortInfo`, `WifiProfile`, `DiyCommand`,
`IpMath`, `SubnetCalculator`, `SubnetResult`, `TcpProbe`, `PortScanner`,
`PortScanRequest`, `ScanEngine`, `ScanProgress`, `LocalPortTable`,
`WifiService`, `NetworkConfigurator`, `NetConfigResult`, `NetworkHistory`,
`MemoStore`, `ShellLauncher`, `TableExporter`, `TextFileEncoding`,
`ServiceNames`, `AppLog.Instance.Log(category, message)`.

`TableExporter` is how you write output:
```csharp
string name = TableExporter.BuildFileName("IP批量扫描", ".csv");  // <prefix>-yyyyMMddHHmmss.csv
TableExporter.WriteCsv(path, headers, rows);      // UTF-8 with BOM, RFC4180 quoted
TableExporter.WriteXlsx(path, "Sheet1", headers, rows);  // real .xlsx
```

Write exports to `Path.Combine(AppContext.BaseDirectory, "导出")` with a
Documents fallback if that throws (see `ScanPage.ExportFolder()` for the pattern).

## 5. Configuration

`AppConfig` mirrors the original `IPScaner.cfg`. **Never mutate
`AppServices.Current.Config` in place** — clone, change, apply:

```csharp
var cfg = AppServices.Current.Config.Clone();
cfg.PingTimeout = 800;
AppServices.Current.ApplyConfig(cfg);
```

Numeric ranges the original UI enforced (clamp, do **not** throw — the original
crashed on a hand-edited cfg):

| Setting | Range | Step | Default |
|---|---|---|---|
| `PingTimeout` | 10–5000 | 100 | 500 |
| `PingCount` | 1–100 | 1 | 4 |
| `DoubleClickTime` | 100–500 | 50 | 200 |
| `PortTimeout` | 10–2000 | 50 | 50 |
| `BtnFontSize` | 7–12 | 1 | 9 |
| `DesktopOverlayOffsetX/Y` | −500–5000 | 10 | 100 |
| `DesktopOverlayOpacity` | 0–100 | 10 | 70 |
| `DesktopOverlayLocation` | 0–3 | 1 | 2 |

Colours are **signed ARGB ints** (WinForms `Color.ToArgb()`), e.g. SkyBlue
`-7876885`, LimeGreen `-13447886`, IndianRed `-3318692`, Blue `-16776961`,
Black `-16777216`, Yellow `-256`. Convert with `UiKit.ColorFromArgb` /
`UiKit.ArgbFromColor`. The original used a `ColorDialog`, which WinUI lacks —
build an inline swatch grid or a small `ColorPicker` from
`Microsoft.UI.Xaml.Controls` (available in WinUI 3) inside a `ContentDialog`.

## 6. Style rules

- All user-facing text is **Simplified Chinese**, matching the exact strings in
  `docs\re\*.md`. Do not invent new wording where the spec quotes one.
- Use the shared styles: `CardStyle`, `SectionHeaderStyle`, `HintTextStyle`,
  `FieldLabelStyle`, `StatusTextStyle`, `LegendSwatchStyle`, `IpBlockButtonStyle`
  (all in `Styles\AppStyles.xaml`, already merged app-wide).
- Long-running work must be `async`, must show progress, and must be
  **cancellable** via a `CancellationTokenSource` cancelled in `Unloaded`.
- Never touch UI from a background thread: `DispatcherQueue.TryEnqueue(...)`.
- Wrap risky I/O in try/catch and log via `AppLog.Instance.Log(nameof(YourPage), msg)`.
- `Nullable` is enabled — no nullable warnings.
- The build must finish with **0 errors and 0 warnings**.

## 7. Fidelity vs. improvement

The goal is a faithful port that also fixes real defects. Where the specs list a
bug (silent failures, unreachable code, locale-dependent parsing, unbounded
concurrency, uncancellable waits), fix it and note it in a short comment. Where
the spec quotes exact Chinese strings, user-visible formats or file formats,
match them exactly — those are compatibility surface, not bugs.

## 8. Report format

When finished, report:
1. the page files you produced (real-tree paths),
2. the exact build command and its result (0 errors / 0 warnings),
3. what you verified by running the app and what you saw,
4. any deviation from the spec and why,
5. anything you could not verify.
