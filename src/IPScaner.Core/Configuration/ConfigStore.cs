using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace IPScaner.Core.Configuration;

/// <summary>
/// Reads and writes <c>IPScaner.cfg</c>.
/// </summary>
/// <remarks>
/// Wire-format compatibility with the original tool is a hard requirement: a user
/// must be able to drop the new build next to an existing IPScaner.cfg and keep
/// every setting. The original called
/// <c>XmlSerializer.Serialize(FileStream, obj, emptyNamespaces)</c> on .NET
/// Framework, whose <c>XmlTextWriter</c> emitted a declaration with <b>no</b>
/// encoding pseudo-attribute:
/// <code>
/// &lt;?xml version="1.0"?&gt;\r\n
/// &lt;root Version="1.0" ... /&gt;
/// </code>
/// UTF-8 with no BOM, no XML namespace, CRLF after the declaration and no
/// trailing newline.
/// <para>
/// The .NET Core rewrite of <c>XmlSerializer</c> changed that: the same overload
/// now emits <c>&lt;?xml version="1.0" encoding="utf-8"?&gt;</c>. Passing the
/// overload straight through would therefore rewrite the first line of every
/// user's config file. The declaration is written by hand instead so the bytes
/// match the original exactly.
/// </para>
/// </remarks>
public sealed class ConfigStore
{
    public const string FileName = "IPScaner.cfg";

    private static readonly XmlSerializer Serializer = new(typeof(AppConfig));
    private static readonly XmlSerializerNamespaces NoNamespaces = CreateEmptyNamespaces();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static XmlSerializerNamespaces CreateEmptyNamespaces()
    {
        var ns = new XmlSerializerNamespaces();
        ns.Add(string.Empty, string.Empty);
        return ns;
    }

    /// <summary>Full path of the config file in use.</summary>
    public string FilePath { get; }

    public ConfigStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(Storage.AppPaths.DataDirectory, FileName);
    }

    /// <summary>True when a config file already exists on disk.</summary>
    public bool Exists => File.Exists(FilePath);

    /// <summary>
    /// Loads the configuration. A missing or unreadable file yields factory
    /// defaults rather than throwing, so a corrupt cfg can never block startup;
    /// the damaged file is renamed aside so it can be inspected.
    /// </summary>
    public AppConfig Load()
    {
        if (!File.Exists(FilePath)) return new AppConfig();

        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var cfg = Serializer.Deserialize(stream) as AppConfig;
            return cfg ?? new AppConfig();
        }
        catch (Exception ex)
        {
            TryQuarantine(ex);
            return new AppConfig();
        }
    }

    /// <summary>
    /// Persists the configuration, creating the directory when needed.
    /// </summary>
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write to a sibling temp file and swap, so an interrupted save cannot
        // truncate the user's settings.
        var temp = FilePath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            WriteDocument(stream, config);
        }

        if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
        else File.Move(temp, FilePath);
    }

    /// <summary>Serialises to a string, for tests and diagnostics.</summary>
    public static string SerializeToXml(AppConfig config)
    {
        using var stream = new MemoryStream();
        WriteDocument(stream, config);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes the exact byte sequence the original tool produced. See the type
    /// remarks for why the declaration cannot come from <see cref="XmlSerializer"/>.
    /// </summary>
    private static void WriteDocument(Stream stream, AppConfig config)
    {
        stream.Write(DeclarationBytes);

        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            Encoding = Utf8NoBom,
            CloseOutput = false,
        };

        using var writer = XmlWriter.Create(stream, settings);
        Serializer.Serialize(writer, config, NoNamespaces);
    }

    private static readonly byte[] DeclarationBytes = Utf8NoBom.GetBytes("<?xml version=\"1.0\"?>\r\n");

    private void TryQuarantine(Exception ex)
    {
        try
        {
            var broken = FilePath + ".broken";
            File.Copy(FilePath, broken, overwrite: true);
            System.Diagnostics.Debug.WriteLine($"[ConfigStore] unreadable config: {ex.Message}");
        }
        catch
        {
            // Diagnostics only — never let quarantine failure mask the default.
        }
    }
}
