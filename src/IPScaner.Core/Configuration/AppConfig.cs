using System.Xml.Serialization;

namespace IPScaner.Core.Configuration;

/// <summary>
/// Application settings, persisted as <c>IPScaner.cfg</c>.
/// </summary>
/// <remarks>
/// This type is a deliberate, line-for-line structural port of the original
/// <c>IPScaner.ConfigInfo</c> class. The XML attribute order emitted by
/// <see cref="XmlSerializer"/> follows property declaration order, so the
/// declaration order below MUST NOT be reshuffled: doing so would rewrite a
/// user's existing IPScaner.cfg with a different attribute order.
/// <para>
/// Colours are stored as signed 32-bit ARGB values exactly like WinForms
/// <c>Color.ToArgb()</c> (alpha in the high byte), e.g. SkyBlue = -7876885.
/// </para>
/// </remarks>
[XmlRoot("root")]
public class AppConfig
{
    // ---- well-known WinForms colour values, as signed ARGB -------------------
    public const int SkyBlueArgb = -7876885;      // #FF87CEEB
    public const int LimeGreenArgb = -13447886;   // #FF32CD32
    public const int IndianRedArgb = -3318692;    // #FFCD5C5C
    public const int BlueArgb = -16776961;        // #FF0000FF
    public const int BlackArgb = -16777216;       // #FF000000
    public const int YellowArgb = -256;           // #FFFFFF00

    [XmlAttribute] public string Version { get; set; } = "1.0";
    [XmlAttribute] public bool QueryHostNameEnabled { get; set; }
    [XmlAttribute] public int PingTimeout { get; set; }
    [XmlAttribute] public int PingCount { get; set; }
    [XmlAttribute] public bool ARPInsteadPingEnabled { get; set; }
    [XmlAttribute] public bool PortInsteadPingEnabled { get; set; }
    [XmlAttribute] public string PrePortArray { get; set; } = "80,135,445,500";
    [XmlAttribute] public int PortTimeout { get; set; }
    [XmlAttribute] public int DoubleClickTime { get; set; }
    [XmlAttribute] public int BtnFontSize { get; set; }
    [XmlAttribute] public bool MenuAutoOpen { get; set; }
    [XmlAttribute] public int DefaultColorArgb { get; set; }
    [XmlAttribute] public int NetworkOKColorArgb { get; set; }
    [XmlAttribute] public int NetworkNGColorArgb { get; set; }
    [XmlAttribute] public int MemoColorArgb { get; set; }
    [XmlAttribute] public EventName DoubleEvent { get; set; }
    [XmlAttribute] public bool DesktopOverlayEnabled { get; set; }
    [XmlAttribute] public string DesktopOverlayPre { get; set; } = "本地IP地址：";
    [XmlAttribute] public int DesktopOverlayOffsetX { get; set; }
    [XmlAttribute] public int DesktopOverlayOffsetY { get; set; }
    [XmlAttribute] public int DesktopOverlayOpacity { get; set; }
    [XmlAttribute] public int DesktopOverlayLocation { get; set; }
    [XmlAttribute] public int DesktopForeColorArgb { get; set; }
    [XmlAttribute] public int DesktopBgColorArgb { get; set; }
    [XmlAttribute] public bool LogEnabled { get; set; }

    /// <summary>
    /// Path of a menu item pinned to the top-level "star" menu (v1.26 feature).
    /// Left null by default so the attribute is omitted from the file, matching
    /// the original writer.
    /// </summary>
    [XmlAttribute] public string? StarMenu { get; set; }

    /// <summary>最小化时隐藏到托盘 (v1.28 option; the config UI labels this chkHideMain).</summary>
    [XmlAttribute] public bool HideMainEnabled { get; set; }

    public AppConfig() => ResetToDefaults();

    /// <summary>Restores every setting to the original application's factory default.</summary>
    public void ResetToDefaults()
    {
        Version = "1.0";
        QueryHostNameEnabled = false;
        PingTimeout = 500;
        PingCount = 4;
        ARPInsteadPingEnabled = false;
        PortInsteadPingEnabled = false;
        PrePortArray = "80,135,445,500";
        PortTimeout = 50;
        DoubleClickTime = 200;
        BtnFontSize = 9;
        MenuAutoOpen = false;
        DefaultColorArgb = SkyBlueArgb;
        NetworkOKColorArgb = LimeGreenArgb;
        NetworkNGColorArgb = IndianRedArgb;
        MemoColorArgb = BlueArgb;
        DoubleEvent = EventName.Ping;
        DesktopOverlayEnabled = false;
        DesktopOverlayPre = "本地IP地址：";
        DesktopOverlayOffsetX = 100;
        DesktopOverlayOffsetY = 100;
        DesktopOverlayOpacity = 70;
        DesktopOverlayLocation = 2;
        DesktopForeColorArgb = BlackArgb;
        DesktopBgColorArgb = YellowArgb;
        LogEnabled = false;
        StarMenu = null;
        HideMainEnabled = false;
    }

    /// <summary>Creates a detached deep copy (the config window edits a clone, then commits).</summary>
    public AppConfig Clone()
    {
        var c = new AppConfig();
        c.Version = Version;
        c.QueryHostNameEnabled = QueryHostNameEnabled;
        c.PingTimeout = PingTimeout;
        c.PingCount = PingCount;
        c.ARPInsteadPingEnabled = ARPInsteadPingEnabled;
        c.PortInsteadPingEnabled = PortInsteadPingEnabled;
        c.PrePortArray = PrePortArray;
        c.PortTimeout = PortTimeout;
        c.DoubleClickTime = DoubleClickTime;
        c.BtnFontSize = BtnFontSize;
        c.MenuAutoOpen = MenuAutoOpen;
        c.DefaultColorArgb = DefaultColorArgb;
        c.NetworkOKColorArgb = NetworkOKColorArgb;
        c.NetworkNGColorArgb = NetworkNGColorArgb;
        c.MemoColorArgb = MemoColorArgb;
        c.DoubleEvent = DoubleEvent;
        c.DesktopOverlayEnabled = DesktopOverlayEnabled;
        c.DesktopOverlayPre = DesktopOverlayPre;
        c.DesktopOverlayOffsetX = DesktopOverlayOffsetX;
        c.DesktopOverlayOffsetY = DesktopOverlayOffsetY;
        c.DesktopOverlayOpacity = DesktopOverlayOpacity;
        c.DesktopOverlayLocation = DesktopOverlayLocation;
        c.DesktopForeColorArgb = DesktopForeColorArgb;
        c.DesktopBgColorArgb = DesktopBgColorArgb;
        c.LogEnabled = LogEnabled;
        c.StarMenu = StarMenu;
        c.HideMainEnabled = HideMainEnabled;
        return c;
    }

    // ---- convenience -------------------------------------------------------

    /// <summary>Splits <see cref="PrePortArray"/> into ports, ignoring empty tokens.</summary>
    public IReadOnlyList<int> GetPrePorts()
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(PrePortArray)) return list;
        foreach (var token in PrePortArray.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token.Trim(), out var p) && p is > 0 and <= 65535) list.Add(p);
        }
        return list;
    }

    /// <summary>True when name lookup is on — the UI warns that this is slow.</summary>
    public bool IsHostNameLookupEnabled => QueryHostNameEnabled;
}
