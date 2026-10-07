using System.Xml.Serialization;

namespace IPScaner.Core.Configuration;

/// <summary>How the scan results are laid out.</summary>
/// <remarks>
/// The original had exactly two presentations — the 254 colour blocks and a
/// separate modal list window. Both are kept (they are what long-time users
/// expect) and two more are added for denser or more legible views.
/// </remarks>
public enum ViewMode
{
    /// <summary>色块网格 — the original's signature 254-square grid.</summary>
    Blocks = 0,

    /// <summary>详细列表 — one row per host with every field.</summary>
    List = 1,

    /// <summary>紧凑表格 — dense grid, for scanning many rows quickly.</summary>
    Table = 2,

    /// <summary>卡片视图 — large touch-friendly tiles.</summary>
    Cards = 3,
}

/// <summary>Application colour theme.</summary>
public enum ThemeMode
{
    /// <summary>跟随系统.</summary>
    System = 0,

    /// <summary>浅色.</summary>
    Light = 1,

    /// <summary>深色.</summary>
    Dark = 2,
}

/// <summary>User-visible names for <see cref="ViewMode"/>.</summary>
public static class ViewModeText
{
    public static readonly (ViewMode Value, string Text, string Glyph)[] All =
    [
        (ViewMode.Blocks, "色块网格", "\uE80A"),
        (ViewMode.List, "详细列表", "\uE8FD"),
        (ViewMode.Table, "紧凑表格", "\uE80A"),
        (ViewMode.Cards, "卡片视图", "\uE8A9"),
    ];

    public static string Describe(ViewMode value)
    {
        foreach (var (v, t, _) in All)
        {
            if (v == value) return t;
        }
        return value.ToString();
    }
}

/// <summary>User-visible names for <see cref="ThemeMode"/>.</summary>
public static class ThemeModeText
{
    public static readonly (ThemeMode Value, string Text)[] All =
    [
        (ThemeMode.System, "跟随系统"),
        (ThemeMode.Light, "浅色"),
        (ThemeMode.Dark, "深色"),
    ];

    public static string Describe(ThemeMode value)
    {
        foreach (var (v, t) in All)
        {
            if (v == value) return t;
        }
        return value.ToString();
    }
}
