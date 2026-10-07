using System.ComponentModel;
using System.Runtime.CompilerServices;
using IPScaner.Core.Models;
using IPScaner.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace IPScaner.WinUI.ViewModels;

/// <summary>
/// One of the 254 colour blocks in the scan grid. The same instance feeds all four
/// presentations (色块网格 / 详细列表 / 紧凑表格 / 卡片视图), so every view always
/// shows the same facts.
/// </summary>
/// <remarks>
/// The block's background <i>is</i> the status readout, so every visual property
/// is derived from <see cref="Status"/> plus the colours in the user's config.
/// Text colour follows the same precedence as the original: a memo-coloured label
/// when the host has a note, otherwise a colour chosen for contrast against the
/// status colour.
/// </remarks>
public sealed class IpBlock : INotifyPropertyChanged
{
    private HostStatus _status = HostStatus.Pending;
    private LivenessSource _source = LivenessSource.None;
    private string _hostName = string.Empty;
    private string _mac = string.Empty;
    private string _memo = string.Empty;
    private string _segment = "192.168.1";
    private string _timeText = "Timeout";
    private bool _isLocalMachine;
    private bool _showHostNameCell = true;
    private bool _showMacCell = true;
    private bool _showMemoCell = true;
    private bool _showSourceCell = true;
    private SolidColorBrush _background = new(Microsoft.UI.Colors.SkyBlue);
    private SolidColorBrush _foreground = new(Microsoft.UI.Colors.Black);
    private SolidColorBrush _statusBrush = new(Microsoft.UI.Colors.SkyBlue);

    public IpBlock(int lastOctet)
    {
        LastOctet = lastOctet;
        Label = lastOctet.ToString();
    }

    /// <summary>1..254 — fixed for the lifetime of the block, as in the original.</summary>
    public int LastOctet { get; }

    /// <summary>Text drawn on the block (the last octet).</summary>
    public string Label { get; }

    /// <summary>
    /// Alternating-row opacity for the tabular views (1 = tinted stripe).
    /// </summary>
    /// <remarks>
    /// A static function rather than a property on purpose: row parity never
    /// changes, so the zebra stripe needs neither storage nor notification.
    /// </remarks>
    public static double Zebra(int lastOctet) => (lastOctet & 1) == 0 ? 1.0 : 0.0;

    public string Segment
    {
        get => _segment;
        set
        {
            if (_segment == value) return;
            _segment = value;
            Raise();
            Raise(nameof(Ip));
            Raise(nameof(Tooltip));
        }
    }

    public string Ip => $"{Segment}.{LastOctet}";

    /// <summary>Localised status label (正常 / 不通 / 待检测) used by every view.</summary>
    public string StatusText => HostStatusText.Short(Status);

    public HostStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise();
            Raise(nameof(StatusText));
            Raise(nameof(Tooltip));
        }
    }

    /// <summary>Which probe confirmed liveness (Ping / ARP / TCP).</summary>
    public LivenessSource Source
    {
        get => _source;
        set
        {
            if (_source == value) return;
            _source = value;
            Raise();
            Raise(nameof(SourceText));
            Raise(nameof(Tooltip));
        }
    }

    /// <summary>判定来源 column text; empty while the host has not answered.</summary>
    public string SourceText => HostStatusText.Source(Source);

    /// <summary><c>&lt;1ms</c> / <c>12ms</c> / <c>Timeout</c>, as HostResult formats it.</summary>
    public string TimeText
    {
        get => _timeText;
        set
        {
            if (_timeText == value) return;
            _timeText = value;
            Raise();
        }
    }

    public string HostName
    {
        get => _hostName;
        set { if (_hostName == value) return; _hostName = value; Raise(); Raise(nameof(Tooltip)); }
    }

    public string Mac
    {
        get => _mac;
        set { if (_mac == value) return; _mac = value; Raise(); Raise(nameof(Tooltip)); }
    }

    public string Memo
    {
        get => _memo;
        set { if (_memo == value) return; _memo = value; Raise(); Raise(nameof(Tooltip)); }
    }

    /// <summary>The local machine's own address is tinted, as the original did.</summary>
    public bool IsLocalMachine
    {
        get => _isLocalMachine;
        set { if (_isLocalMachine == value) return; _isLocalMachine = value; Raise(); }
    }

    /// <summary>Fill of the colour block — the status colour from the user's config.</summary>
    public SolidColorBrush Background
    {
        get => _background;
        set { _background = value; Raise(); }
    }

    public SolidColorBrush Foreground
    {
        get => _foreground;
        set { _foreground = value; Raise(); }
    }

    /// <summary>
    /// The same status colour as <see cref="Background"/>, used as the small
    /// reachability dot in the list / table / card views, where a full colour fill
    /// would fight the neutral chrome.
    /// </summary>
    public SolidColorBrush StatusBrush
    {
        get => _statusBrush;
        set { _statusBrush = value; Raise(); }
    }

    // ---- which columns this host shows in the tabular views ---------------
    // Driven by AppConfig.Show*Column plus the measured width. Hidden columns are
    // decided here, once per host, which is what keeps the sticky header and every
    // row in step (and lets a collapsed cell give its width back to the row).

    public Visibility HostNameCellVisibility => Vis(_showHostNameCell);
    public Visibility MacCellVisibility => Vis(_showMacCell);
    public Visibility MemoCellVisibility => Vis(_showMemoCell);
    public Visibility SourceCellVisibility => Vis(_showSourceCell);

    /// <summary>Applies the column flags, raising only what actually changed.</summary>
    public void ApplyColumns(bool hostName, bool mac, bool memo, bool source)
    {
        if (_showHostNameCell != hostName)
        {
            _showHostNameCell = hostName;
            Raise(nameof(HostNameCellVisibility));
        }

        if (_showMacCell != mac)
        {
            _showMacCell = mac;
            Raise(nameof(MacCellVisibility));
        }

        if (_showMemoCell != memo)
        {
            _showMemoCell = memo;
            Raise(nameof(MemoCellVisibility));
        }

        if (_showSourceCell != source)
        {
            _showSourceCell = source;
            Raise(nameof(SourceCellVisibility));
        }
    }

    /// <summary>Multi-line tooltip: 名称 / IP / Mac / 备注 / 状态, shared by every view.</summary>
    public string Tooltip
    {
        get
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(HostName)) lines.Add("名称：" + HostName);
            lines.Add("IP：" + Ip);
            if (!string.IsNullOrEmpty(Mac)) lines.Add("Mac：" + Mac);
            if (!string.IsNullOrEmpty(Memo)) lines.Add("备注：" + Memo);
            lines.Add("状态：" + HostStatusText.Short(Status));
            if (Status == HostStatus.Online)
            {
                if (!string.IsNullOrEmpty(SourceText)) lines.Add("来源：" + SourceText);
                lines.Add("延时：" + TimeText);
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Applies a scan result to this block.</summary>
    public void Apply(HostResult result, AppConfigColors colors)
    {
        Status = result.Status;
        Source = result.Source;
        TimeText = result.TimeText;
        if (!string.IsNullOrEmpty(result.HostName)) HostName = result.HostName;
        if (!string.IsNullOrEmpty(result.Mac)) Mac = result.Mac;
        Memo = result.Memo;
        RefreshColors(colors);
    }

    /// <summary>Recolours from configuration (status colour + memo colour rules).</summary>
    public void RefreshColors(AppConfigColors colors)
    {
        var status = UiKit.BrushFromArgb(colors.BackgroundFor(Status));
        Background = status;
        StatusBrush = status;

        if (!string.IsNullOrEmpty(Memo))
        {
            Foreground = UiKit.BrushFromArgb(colors.MemoArgb);
        }
        else if (IsLocalMachine)
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
        }
        else
        {
            Foreground = new SolidColorBrush(UiKit.ContrastingTextColor(Background.Color));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static Visibility Vis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The colour set the scan surfaces need, resolved from <c>AppConfig</c> once per
/// repaint instead of re-reading the config object per block.
/// </summary>
public readonly record struct AppConfigColors(
    int DefaultArgb,
    int OnlineArgb,
    int OfflineArgb,
    int MemoArgb)
{
    public static AppConfigColors From(IPScaner.Core.Configuration.AppConfig config) => new(
        config.DefaultColorArgb,
        config.NetworkOKColorArgb,
        config.NetworkNGColorArgb,
        config.MemoColorArgb);

    public int BackgroundFor(HostStatus status) => status switch
    {
        HostStatus.Online => OnlineArgb,
        HostStatus.Offline => OfflineArgb,
        _ => DefaultArgb,
    };
}
