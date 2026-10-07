using IPScaner.Core.Models;
using IPScaner.Core.Storage;

namespace IPScaner.Core.Commands;

/// <summary>
/// User-defined launchers read from <c>command.txt</c> (我的命令).
/// </summary>
/// <remarks>
/// Format preserved from the original: one command per line, the first
/// whitespace-delimited token is the menu caption and the remainder is the
/// command line; a leading '#' comments the line out. Encoding is sniffed so
/// GBK-saved files keep working.
/// </remarks>
public sealed class DiyCommandStore
{
    public const string FileName = "command.txt";

    public string FilePath { get; }

    public DiyCommandStore(string? filePath = null)
        => FilePath = filePath ?? Path.Combine(Storage.AppPaths.DataDirectory, FileName);

    public List<DiyCommand> Load()
    {
        var list = new List<DiyCommand>();
        if (!File.Exists(FilePath)) return list;

        string[] lines;
        try { lines = TextFileEncoding.ReadAllLines(FilePath); }
        catch { return list; }

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.TrimStart().StartsWith('#')) continue;

            var parts = raw.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            list.Add(new DiyCommand
            {
                Name = parts[0],
                Command = parts.Length > 1 ? parts[1].Trim() : parts[0],
            });
        }

        return list;
    }

    /// <summary>Writes a starter file so users have something to edit.</summary>
    public void WriteTemplate(bool overwrite = false)
    {
        if (File.Exists(FilePath) && !overwrite) return;

        var text = string.Join(Environment.NewLine,
        [
            "# 我的命令 —— 每行一条，第一个空格前是菜单名称，后面是要执行的命令或程序路径。",
            "# 以 # 开头的行会被忽略。",
            "# 示例：",
            "记事本 notepad.exe",
            "计算器 calc.exe",
            @"下载目录 explorer.exe C:\Users\Public\Downloads",
            "本机监听端口 netstat -ano | findstr LISTENING &pause",
        ]);

        try
        {
            File.WriteAllText(FilePath, text + Environment.NewLine, TextFileEncoding.Utf8NoBom);
        }
        catch
        {
            // best effort — the menu simply stays empty
        }
    }
}
