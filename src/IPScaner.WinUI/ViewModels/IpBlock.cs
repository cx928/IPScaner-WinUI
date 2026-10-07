using System.ComponentModel;
using System.Runtime.CompilerServices;
using IPScaner.Core.Models;
using IPScaner.WinUI.Services;
using Microsoft.UI.Xaml.Media;

namespace IPScaner.WinUI.ViewModels;

/// <summary>
/// One of the 254 colour blocks in the scan grid.
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
    private string _hostName = string.Empty;
    private string _mac = string.Empty;
    private string _memo = string.Empty;
    private string _segment = "192.168.1";
    private bool _isLocalMachine;
    private SolidColorBrush _background = new(Microsoft.UI.Colors.SkyBlue);
    private SolidColorBrush _foreground = new(Microsoft.UI.Colors.Black);

    public IpBlock(int lastOctet)
    {
        LastOctet = lastOctet;
        Label = lastOctet.ToString();
    }

    /// <summary>1..254 — fixed for the lifetime of the block, as in the original.</summary>
    public int LastOctet { get; }

    /// <summary>Text drawn on the block (the last octet).</summary>
    public string Label { get; }

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

    /// <summary>Localised status label for the list view (正常 / 不通 / 待检测).</summary>
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

    /// <summary>Multi-line tooltip: 名称 / IP / Mac / 备注.</summary>
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
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Applies a scan result to this block.</summary>
    public void Apply(HostResult result, AppConfigColors colors)
    {
        Status = result.Status;
        if (!string.IsNullOrEmpty(result.HostName)) HostName = result.HostName;
        if (!string.IsNullOrEmpty(result.Mac)) Mac = result.Mac;
        Memo = result.Memo;
        RefreshColors(colors);
    }

    /// <summary>Recolours from configuration (status colour + memo colour rules).</summary>
    public void RefreshColors(AppConfigColors colors)
    {
        Background = new SolidColorBrush(UiKit.ColorFromArgb(colors.BackgroundFor(Status)));

        if (!string.IsNullOrEmpty(Memo))
        {
            Foreground = new SolidColorBrush(UiKit.ColorFromArgb(colors.MemoArgb));
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
