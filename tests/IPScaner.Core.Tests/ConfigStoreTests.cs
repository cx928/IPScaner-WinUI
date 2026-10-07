using Xunit;
using System.Text;
using IPScaner.Core.Configuration;

namespace IPScaner.Core.Tests;

/// <summary>
/// Wire-format coverage for <see cref="ConfigStore"/>. This is the highest-value
/// suite in the project: a user must be able to drop the WinUI build next to an
/// existing IPScaner.cfg and keep every setting.
/// </summary>
public class ConfigStoreTests
{
    // ---- serialized shape --------------------------------------------------

    [Fact]
    public void SerializeToXml_RootIsRootAndCarriesNoXmlNamespace()
    {
        var xml = ConfigStore.SerializeToXml(new AppConfig());

        Assert.Contains("<root ", xml);
        Assert.StartsWith("<?xml", xml);
        Assert.DoesNotContain("xmlns", xml);          // empty XmlSerializerNamespaces
        Assert.DoesNotContain("xsd", xml);
        Assert.DoesNotContain("xsi", xml);
        Assert.Contains("Version=\"1.0\"", xml);
        Assert.Contains("DefaultColorArgb=\"-7876885\"", xml);
        Assert.Contains("PingTimeout=\"500\"", xml);
        Assert.Contains("PrePortArray=\"80,135,445,500\"", xml);
    }

    [Fact]
    public void SerializeToXml_RoundTripsThroughDeserialize()
    {
        var xml = ConfigStore.SerializeToXml(new AppConfig { PingTimeout = 750, StarMenu = "mnuHelp" });

        var cfg = Deserialize(xml);

        Assert.Equal(750, cfg.PingTimeout);
        Assert.Equal("mnuHelp", cfg.StarMenu);
    }

    [Fact]
    public void SerializeToXml_OmitsStarMenuWhenNull_AndWritesItWhenSet()
    {
        var cfg = new AppConfig();
        Assert.Null(cfg.StarMenu);

        Assert.DoesNotContain("StarMenu", ConfigStore.SerializeToXml(cfg));

        cfg.StarMenu = "mnuTools";
        Assert.Contains("StarMenu=\"mnuTools\"", ConfigStore.SerializeToXml(cfg));
    }

    [Fact]
    public void SerializeToXml_EmitsAttributesInDeclarationOrder()
    {
        var xml = ConfigStore.SerializeToXml(new AppConfig());

        // Attribute order is part of the on-disk contract: reordering the
        // properties would rewrite every user's file.
        var version = xml.IndexOf("Version=", StringComparison.Ordinal);
        var pingTimeout = xml.IndexOf("PingTimeout=", StringComparison.Ordinal);
        var pingCount = xml.IndexOf("PingCount=", StringComparison.Ordinal);
        var doubleEvent = xml.IndexOf("DoubleEvent=", StringComparison.Ordinal);
        var starMenu = xml.IndexOf("StarMenu=", StringComparison.Ordinal);
        var hideMain = xml.IndexOf("HideMainEnabled=", StringComparison.Ordinal);

        Assert.True(version >= 0 && version < pingTimeout, "Version must precede PingTimeout");
        Assert.True(pingTimeout < pingCount, "PingTimeout must precede PingCount");
        Assert.True(pingCount < doubleEvent, "PingCount must precede DoubleEvent");
        Assert.True(doubleEvent < hideMain, "DoubleEvent must precede HideMainEnabled");
        // StarMenu is omitted while null, so it must not appear at all here.
        Assert.Equal(-1, starMenu);
    }

    // ---- XML declaration ---------------------------------------------------
    // The original ran on .NET Framework, whose XmlTextWriter emitted a declaration
    // with NO encoding pseudo-attribute:
    //     <?xml version="1.0"?>
    // The .NET Core rewrite of XmlSerializer changed that and now emits
    // encoding="utf-8", which would rewrite the first line of every existing
    // IPScaner.cfg. ConfigStore writes the declaration by hand to preserve the
    // original bytes; these tests pin that.

    /// <summary>
    /// A verbatim copy of the IPScaner.cfg shipped with IPScaner V1.28.2, byte for
    /// byte (691 bytes, UTF-8 with no BOM, CRLF after the declaration, no trailing
    /// newline, no xmlns, no StarMenu attribute because it was null).
    /// </summary>
    private const string OriginalCfgFixture =
        "<?xml version=\"1.0\"?>\r\n" +
        "<root Version=\"1.0\" QueryHostNameEnabled=\"true\" PingTimeout=\"1000\" PingCount=\"8\" " +
        "ARPInsteadPingEnabled=\"false\" PortInsteadPingEnabled=\"true\" PrePortArray=\"80,135,445,500,3389\" " +
        "PortTimeout=\"50\" DoubleClickTime=\"200\" BtnFontSize=\"9\" MenuAutoOpen=\"false\" " +
        "DefaultColorArgb=\"-7876885\" NetworkOKColorArgb=\"-13447886\" NetworkNGColorArgb=\"-3318692\" " +
        "MemoColorArgb=\"-16776961\" DoubleEvent=\"Ping\" DesktopOverlayEnabled=\"false\" " +
        "DesktopOverlayPre=\"本地IP地址：\" DesktopOverlayOffsetX=\"100\" DesktopOverlayOffsetY=\"100\" " +
        "DesktopOverlayOpacity=\"70\" DesktopOverlayLocation=\"2\" DesktopForeColorArgb=\"-16777216\" " +
        "DesktopBgColorArgb=\"-256\" LogEnabled=\"false\" HideMainEnabled=\"false\" />";

    [Fact]
    public void SerializeToXml_EmitsTheBareDeclarationWithNoEncodingAttribute()
    {
        var xml = ConfigStore.SerializeToXml(new AppConfig());

        Assert.StartsWith("<?xml version=\"1.0\"?>\r\n<root ", xml);
        Assert.DoesNotContain("encoding=", xml);
    }

    [Fact]
    public void Save_FileDeclarationIsBareUtf8WithoutBom()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor(ConfigStore.FileName);
        new ConfigStore(path).Save(new AppConfig());

        var bytes = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Equal((byte)'<', bytes[0]); // UTF-8 with no BOM, like the original
        Assert.StartsWith("<?xml version=\"1.0\"?>\r\n<root ", text);
        Assert.DoesNotContain("encoding=", text);
        Assert.EndsWith("/>", text);           // no trailing newline
        Assert.DoesNotContain("xmlns", text);
    }

    [Fact]
    public void RoundTripOfTheShippedOriginalConfig_IsByteIdentical_AndPreservesEveryValue()
    {
        using var workspace = new TempWorkspace();

        // 1. Write the exact bytes of the original tool's config file.
        var source = workspace.PathFor("original.cfg");
        File.WriteAllBytes(source, Encoding.UTF8.GetBytes(OriginalCfgFixture));
        var originalBytes = File.ReadAllBytes(source);
        Assert.Equal(691, originalBytes.Length);

        // 2. Load it with the new model.
        var loaded = new ConfigStore(source).Load();

        Assert.True(loaded.QueryHostNameEnabled);
        Assert.Equal(1000, loaded.PingTimeout);
        Assert.Equal(8, loaded.PingCount);
        Assert.False(loaded.ARPInsteadPingEnabled);
        Assert.True(loaded.PortInsteadPingEnabled);
        Assert.Equal("80,135,445,500,3389", loaded.PrePortArray);
        Assert.Equal(50, loaded.PortTimeout);
        Assert.Equal(EventName.Ping, loaded.DoubleEvent);
        Assert.Equal("本地IP地址：", loaded.DesktopOverlayPre);
        Assert.Equal(2, loaded.DesktopOverlayLocation);
        Assert.Null(loaded.StarMenu); // absent attribute -> null, and must stay absent

        // 3. Save it again and require the bytes to match exactly. This is the
        //    compatibility guarantee: dropping the new build beside an existing
        //    installation must not rewrite the user's settings file.
        var rewritten = workspace.PathFor(ConfigStore.FileName);
        new ConfigStore(rewritten).Save(loaded);

        Assert.Equal(originalBytes, File.ReadAllBytes(rewritten));
    }

    // ---- file round trip ---------------------------------------------------

    [Fact]
    public void SaveThenLoad_RoundTripsEverySingleProperty()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor(ConfigStore.FileName);
        var expected = MutatedConfig();

        // Guard: the fixture must actually differ from the defaults, otherwise a
        // round-trip test would pass even if nothing were persisted.
        AssertAllPropertiesDiffer(new AppConfig(), expected);

        var store = new ConfigStore(path);
        store.Save(expected);
        Assert.True(store.Exists);

        var actual = new ConfigStore(path).Load();

        AssertAllPropertiesMatch(expected, actual);
        Assert.True(actual.IsHostNameLookupEnabled);
    }

    [Fact]
    public void SaveThenLoad_SurvivesASecondSaveOverAnExistingFile()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor(ConfigStore.FileName);
        var store = new ConfigStore(path);
        var expected = MutatedConfig();

        store.Save(expected);
        store.Save(expected); // exercises File.Replace on the existing file

        AssertAllPropertiesMatch(expected, new ConfigStore(path).Load());
    }

    [Fact]
    public void SaveThenLoad_KeepsStarMenuAttribute()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor(ConfigStore.FileName);

        new ConfigStore(path).Save(new AppConfig { StarMenu = "mnuTools" });

        Assert.Contains("StarMenu=\"mnuTools\"", File.ReadAllText(path));
        Assert.Equal("mnuTools", new ConfigStore(path).Load().StarMenu);
    }

    [Fact]
    public void Save_CreatesTheDirectory_WhenMissing()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "nested", "deeper", ConfigStore.FileName);

        new ConfigStore(path).Save(new AppConfig());

        Assert.True(File.Exists(path));
    }

    // ---- load tolerance ----------------------------------------------------

    [Fact]
    public void Load_MissingFile_ReturnsFactoryDefaults()
    {
        using var workspace = new TempWorkspace();
        var store = new ConfigStore(workspace.PathFor(ConfigStore.FileName));

        Assert.False(store.Exists);
        var cfg = store.Load();

        Assert.Equal(500, cfg.PingTimeout);
        Assert.Equal("1.0", cfg.Version);
        Assert.Equal("80,135,445,500", cfg.PrePortArray);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaultsAndQuarantinesTheFile()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor(ConfigStore.FileName);
        File.WriteAllText(path, "this is not xml at all <<<", Encoding.UTF8);

        var cfg = new ConfigStore(path).Load();

        Assert.Equal(500, cfg.PingTimeout);
        Assert.Equal(4, cfg.PingCount);
        Assert.True(File.Exists(path + ".broken"), "the damaged file should be copied aside for inspection");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_ReadsAHandWrittenLegacyFile_WithReorderedAndUnknownAttributes()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathFor(ConfigStore.FileName);
        File.WriteAllText(
            path,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <root HideMainEnabled="true" StarMenu="mnuHelp" SomeUnknownFutureSetting="42" PingTimeout="750" Version="1.0" />
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var cfg = new ConfigStore(path).Load();

        Assert.Equal(750, cfg.PingTimeout);
        Assert.Equal("mnuHelp", cfg.StarMenu);
        Assert.True(cfg.HideMainEnabled);
        Assert.Equal(4, cfg.PingCount);   // absent in the file -> factory default
        Assert.Equal("1.0", cfg.Version);
    }

    [Fact]
    public void DefaultFilePath_IsNextToTheApplication()
        => Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, ConfigStore.FileName),
            new ConfigStore().FilePath);

    [Fact]
    public void FileName_MatchesTheOriginalTool()
        => Assert.Equal("IPScaner.cfg", ConfigStore.FileName);

    // ---- helpers -----------------------------------------------------------

    /// <summary>A config where every settable property differs from its default.</summary>
    private static AppConfig MutatedConfig() => new()
    {
        Version = "2.5",
        QueryHostNameEnabled = true,
        PingTimeout = 1234,
        PingCount = 7,
        ARPInsteadPingEnabled = true,
        PortInsteadPingEnabled = true,
        PrePortArray = "22,3389",
        PortTimeout = 987,
        DoubleClickTime = 321,
        BtnFontSize = 12,
        MenuAutoOpen = true,
        DefaultColorArgb = -16711936,
        NetworkOKColorArgb = -65536,
        NetworkNGColorArgb = -16711681,
        MemoColorArgb = -65535,
        DoubleEvent = EventName.Tracert,
        DesktopOverlayEnabled = true,
        DesktopOverlayPre = "当前IP：",
        DesktopOverlayOffsetX = 12,
        DesktopOverlayOffsetY = 34,
        DesktopOverlayOpacity = 55,
        DesktopOverlayLocation = 5,
        DesktopForeColorArgb = -1,
        DesktopBgColorArgb = -16777216,
        LogEnabled = true,
        StarMenu = "mnuTools",
        HideMainEnabled = true,

        // Added by this rewrite. Fully qualified because the property and the enum
        // share a name, which would otherwise be ambiguous inside an initializer.
        ViewMode = IPScaner.Core.Configuration.ViewMode.Cards,
        ThemeMode = IPScaner.Core.Configuration.ThemeMode.Dark,
        BlockSize = 140,
        ShowHostNameColumn = false,
        ShowMacColumn = false,
        ShowMemoColumn = false,
        ShowSourceColumn = false,
    };

    private static System.Reflection.PropertyInfo[] SettableProperties() =>
        [.. typeof(AppConfig)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0)];

    private static void AssertAllPropertiesDiffer(AppConfig left, AppConfig right)
    {
        foreach (var property in SettableProperties())
        {
            Assert.NotEqual(property.GetValue(left), property.GetValue(right));
        }
    }

    private static void AssertAllPropertiesMatch(AppConfig expected, AppConfig actual)
    {
        foreach (var property in SettableProperties())
        {
            var e = property.GetValue(expected);
            var a = property.GetValue(actual);
            Assert.True(Equals(e, a), $"{property.Name}: expected '{e ?? "<null>"}', got '{a ?? "<null>"}'");
        }
    }

    private static AppConfig Deserialize(string xml)
    {
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(AppConfig));
        using var reader = new StringReader(xml);
        return (AppConfig)serializer.Deserialize(reader)!;
    }
}
