using System.Xml;
using System.Xml.Serialization;
using IPScaner.Core.Logging;
using IPScaner.Core.Models;
using IPScaner.Core.Storage;

namespace IPScaner.Core.Net;

/// <summary>Persists previously applied static-IP settings to ipScaner_his.xml.</summary>
/// <remarks>
/// <para>
/// The on-disk contract is the original's, byte for byte, so an existing history
/// file keeps loading and a new one is indistinguishable from what the WinForms
/// tool wrote:
/// </para>
/// <code>
/// &lt;?xml version="1.0"?&gt;
/// &lt;root&gt;
///   &lt;array&gt;
///     &lt;AdapterInfo Name="以太网" IP="192.168.1.100" SubnetMask="255.255.255.0" Gateway="192.168.1.1" DNS="223.5.5.5" /&gt;
///   &lt;/array&gt;
/// &lt;/root&gt;
/// </code>
/// <para>
/// UTF-8 without BOM, an empty <see cref="XmlSerializerNamespaces"/> so no
/// <c>xmlns:xsi</c>/<c>xmlns:xsd</c> and no <c>encoding</c> attribute are emitted,
/// and an <see cref="XmlTextWriter"/> constructed with a null encoding — exactly how
/// <c>XmlSerializer.Serialize(Stream, …)</c> built the original document.
/// </para>
/// <para>
/// Only <c>Name, IP, SubnetMask, Gateway, DNS</c> are persisted, because the other
/// <see cref="AdapterInfo"/> members are <c>[XmlIgnore]</c>. Records are unique on
/// <c>(Name, IP)</c> and stored newest-first; the list is capped at
/// <see cref="MaxEntries"/>. A corrupt or unreadable file never throws — it is
/// reported to the log and read as an empty history.
/// </para>
/// </remarks>
public sealed class NetworkHistory
{
    private const string Category = nameof(NetworkHistory);

    /// <summary>File name used when no explicit path is supplied.</summary>
    public const string DefaultFileName = "ipScaner_his.xml";

    /// <summary>Newest entries kept; older ones are dropped on write.</summary>
    public const int MaxEntries = 100;

    private readonly object _gate = new();

    /// <param name="filePath">
    /// History file to use; defaults to <c>ipScaner_his.xml</c> in the writable
    /// data directory (beside the executable for the portable build, otherwise
    /// <c>%APPDATA%\IPScaner</c> — see <see cref="Storage.AppPaths"/>).
    /// the exe-relative location the original used.
    /// </param>
    public NetworkHistory(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(Storage.AppPaths.DataDirectory, DefaultFileName)
            : filePath;
    }

    /// <summary>Absolute or caller-supplied path of the history file.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Reads the stored history in file order (newest first for files this class
    /// wrote). Returns an empty list when the file is missing, empty or corrupt.
    /// </summary>
    public List<AdapterInfo> Load()
    {
        lock (_gate)
        {
            return LoadCore();
        }
    }

    /// <summary>Overwrites the file with <paramref name="entries"/>, keeping at most <see cref="MaxEntries"/>.</summary>
    public void Save(IEnumerable<AdapterInfo> entries)
    {
        lock (_gate)
        {
            SaveCore(entries);
        }
    }

    /// <summary>
    /// Inserts one record at the top, replacing any existing record with the same
    /// <c>(Name, IP)</c> pair, then writes the file.
    /// </summary>
    public void Add(AdapterInfo entry)
    {
        if (entry is null) return;

        lock (_gate)
        {
            var list = LoadCore();
            list.RemoveAll(e => SameKey(e, entry));
            list.Insert(0, entry);
            if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            SaveCore(list);
        }
    }

    /// <summary>
    /// Clears the history. The file is rewritten as an empty (but still valid)
    /// <c>&lt;root&gt;&lt;array /&gt;&lt;/root&gt;</c> document, so the next load returns nothing.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            SaveCore([]);
            AppLog.Instance.Log(Category, "已清空历史记录: " + FilePath);
        }
    }

    // ---- implementation ----------------------------------------------------

    private List<AdapterInfo> LoadCore()
    {
        var list = new List<AdapterInfo>();
        try
        {
            if (!File.Exists(FilePath)) return list;

            var serializer = new XmlSerializer(typeof(AdapterInfoCollection));
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (serializer.Deserialize(stream) is not AdapterInfoCollection collection) return list;

            foreach (var item in collection.AdapterList ?? [])
            {
                if (item is not null) list.Add(item);
            }

            if (list.Count > MaxEntries)
            {
                AppLog.Instance.Log(Category,
                    $"历史记录文件包含 {list.Count} 条记录，超过上限 {MaxEntries}，保存时将被截断");
            }
        }
        catch (Exception ex)
        {
            // A corrupt history must never take the window down (the original let the
            // exception escape into the WinForms thread handler and lost the history).
            AppLog.Instance.Log(Category, $"读取历史记录失败（按空记录处理）: {FilePath} - {ex.Message}");
            return [];
        }

        return list;
    }

    private void SaveCore(IEnumerable<AdapterInfo>? entries)
    {
        try
        {
            var collection = new AdapterInfoCollection();
            if (entries is not null)
            {
                foreach (var entry in entries)
                {
                    if (entry is null) continue;
                    collection.AdapterList.Add(entry);
                    if (collection.AdapterList.Count >= MaxEntries) break;
                }
            }

            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var serializer = new XmlSerializer(typeof(AdapterInfoCollection));
            var namespaces = new XmlSerializerNamespaces();
            namespaces.Add("", ""); // no xmlns:xsi / xmlns:xsd, like XmlUtility.SaveXml

            using var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            // Null encoding == the original's `XmlSerializer.Serialize(Stream, …)`:
            // bytes are UTF-8 without BOM and the declaration stays `<?xml version="1.0"?>`.
            using var writer = new XmlTextWriter(stream, null) { Formatting = Formatting.Indented };
            serializer.Serialize(writer, collection, namespaces);

            AppLog.Instance.Log(Category, $"保存历史记录 {collection.AdapterList.Count} 条: {FilePath}");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(Category, $"保存历史记录失败: {FilePath} - {ex.Message}");
        }
    }

    private static bool SameKey(AdapterInfo a, AdapterInfo b) =>
        string.Equals(a.Name, b.Name, StringComparison.Ordinal) &&
        string.Equals(a.IP, b.IP, StringComparison.Ordinal);
}
