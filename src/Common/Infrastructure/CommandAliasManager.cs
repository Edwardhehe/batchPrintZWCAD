using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
#if ZWCAD
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#elif ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 命令快捷键（简化命令）管理。
/// CAD 原生命令别名机制是 PGP 程序参数文件（acad.pgp / ZWCAD.pgp），
/// .NET API 无法在运行时注册任意命令名，因此用户别名统一写入 PGP 文件末尾的管理块，
/// 由 CAD 在 REINIT（勾选 PGP 文件）或重启后接管。
/// </summary>
internal static class CommandAliasManager
{
    /// <summary>可设置快捷键的命令，与菜单栏主要功能一一对应；顺序即设置界面和 PGP 中的排列顺序。</summary>
    internal static readonly IReadOnlyList<AliasableCommand> AliasableCommands = new[]
    {
        new AliasableCommand("新增图框", "ZBP_ADD_TITLE_BLOCK"),
        new AliasableCommand("图框库管理", "ZBP_MANAGE_LIBRARY"),
        new AliasableCommand("批量打印(选图框块)", "ZBP_SHOW_PANEL"),
        new AliasableCommand("批量打印(选矩形框)", "ZBP_RECTANGLE_BATCH_PLOT"),
        new AliasableCommand("单张打印", "ZBP_SINGLE_PLOT"),
        new AliasableCommand("设置", "ZBP_SETTINGS"),
    };

    // 管理块标记必须保持纯 ASCII：PGP 读写使用 Latin-1 按字节往返，不触碰原文件编码。
    private const string BlockBegin = ";>>> ZBP COMMAND ALIASES >>>";
    private const string BlockEnd = ";<<< ZBP COMMAND ALIASES <<<";

    private static readonly Regex AliasPattern = new(
        "^[A-Za-z][A-Za-z0-9]{0,15}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // PGP 别名行格式：ALIAS,   *COMMAND
    private static readonly Regex PgpAliasLinePattern = new(
        @"^\s*([A-Za-z0-9]+)\s*,\s*\*\s*([^\s;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>Latin-1 与字节一一对应，可原样保留 ANSI/GBK 等单字节编码的 PGP 内容。</summary>
    private static readonly Encoding ByteRoundTripEncoding = Encoding.GetEncoding("ISO-8859-1");

    // 标记的 ASCII 字节形式，用于清除旧版本按单字节追加、在 UTF-16 PGP 中不可解析的管理块。
    private static readonly byte[] AsciiBlockBeginBytes = Encoding.ASCII.GetBytes(BlockBegin);
    private static readonly byte[] AsciiBlockEndBytes = Encoding.ASCII.GetBytes(BlockEnd);

    internal sealed class AliasableCommand
    {
        internal AliasableCommand(string displayName, string commandName)
        {
            DisplayName = displayName;
            CommandName = commandName;
        }

        internal string DisplayName { get; }
        internal string CommandName { get; }
    }

    private sealed class AliasEntry
    {
        internal AliasEntry(string alias, string commandName)
        {
            Alias = alias;
            CommandName = commandName;
        }

        internal string Alias { get; }
        internal string CommandName { get; }
    }

    /// <summary>简化命令格式：字母开头，仅含字母和数字，最长 16 位。</summary>
    internal static bool IsValidAlias(string? alias)
    {
        return alias != null && AliasPattern.IsMatch(alias);
    }

    /// <summary>
    /// 规范化持久化的别名表：去掉未知命令和非法别名；
    /// 同一别名被多个命令占用时按命令表顺序保留靠前的一个。
    /// </summary>
    internal static Dictionary<string, string> NormalizeAliases(Dictionary<string, string>? aliases)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (aliases == null)
        {
            return result;
        }

        var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in AliasableCommands)
        {
            var alias = "";
            foreach (var pair in aliases)
            {
                if (string.Equals(pair.Key, command.CommandName, StringComparison.OrdinalIgnoreCase))
                {
                    alias = (pair.Value ?? "").Trim();
                    break;
                }
            }

            if (alias.Length == 0 || !IsValidAlias(alias) || !usedAliases.Add(alias))
            {
                continue;
            }

            result[command.CommandName] = alias;
        }

        return result;
    }

    /// <summary>
    /// 把别名写入 PGP 文件末尾的管理块；别名全空时只移除旧管理块。
    /// 写入后别名需执行 REINIT（勾选 PGP 文件）或重启 CAD 生效。
    /// </summary>
    internal static bool Apply(IReadOnlyDictionary<string, string> aliases, out string message)
    {
        var pgpPath = LocatePgpFile();
        if (pgpPath == null)
        {
            message = "设置已保存，但未找到 CAD 的 PGP 程序参数文件（acad.pgp / ZWCAD.pgp），简化命令不会生效。";
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(pgpPath);
            // 旧版本曾按单字节 ASCII 追加管理块，在 UTF-16 编码的 PGP（如部分中望版本）中不可解析，先按字节清除。
            bytes = RemoveLegacyAsciiBlock(bytes);

            // 部分中望版本的 ZWCAD.pgp 是 UTF-16（带 BOM），必须按原编码解码后再做文本处理。
            var encoding = DetectPgpEncoding(bytes, out var bomLength);
            var content = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);

            // 首次修改前保留一份原始备份，便于用户手动还原。
            var backupPath = pgpPath + ".zbpbak";
            if (!File.Exists(backupPath))
            {
                File.WriteAllBytes(backupPath, bytes);
            }

            content = RemoveManagedBlock(content);

            var entries = new List<AliasEntry>();
            foreach (var command in AliasableCommands)
            {
                if (aliases.TryGetValue(command.CommandName, out var raw))
                {
                    var alias = (raw ?? "").Trim();
                    if (IsValidAlias(alias))
                    {
                        entries.Add(new AliasEntry(alias.ToUpperInvariant(), command.CommandName));
                    }
                }
            }

            var conflicts = FindConflicts(content, entries);

            if (entries.Count > 0)
            {
                var builder = new StringBuilder(content.TrimEnd('\r', '\n'));
                builder.Append("\r\n\r\n");
                builder.Append(BlockBegin).Append("\r\n");
                foreach (var entry in entries)
                {
                    builder.Append((entry.Alias + ",").PadRight(16)).Append('*').Append(entry.CommandName).Append("\r\n");
                }

                builder.Append(BlockEnd).Append("\r\n");
                content = builder.ToString();
            }

            // 按原编码和原 BOM 写回，保证 UTF-16 编码的 PGP 仍可被宿主正常解析。
            var body = encoding.GetBytes(content);
            var output = new byte[bomLength + body.Length];
            Array.Copy(bytes, 0, output, 0, bomLength);
            Array.Copy(body, 0, output, bomLength, body.Length);
            File.WriteAllBytes(pgpPath, output);

            message = entries.Count > 0
                ? $"已写入 {entries.Count} 个简化命令到 PGP 文件：\n{pgpPath}\n\n请执行 REINIT 命令并勾选 PGP 文件，或重启 CAD 后生效。"
                : "已清除 PGP 文件中的简化命令。\n\n请执行 REINIT 命令并勾选 PGP 文件，或重启 CAD 后生效。";
            if (conflicts.Count > 0)
            {
                message += "\n\n注意：以下简化命令与 PGP 中已有别名重复，将以本次设置为准：\n" + string.Join("、", conflicts);
            }

            return true;
        }
        catch (Exception ex)
        {
            message = "设置已保存，但写入 PGP 文件失败：\n" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 检测 PGP 文件编码：优先按 BOM 识别 UTF-8 / UTF-16；无 BOM 时按 0x00 分布判断 UTF-16；
    /// 否则按 Latin-1 单字节处理，原样保留 ANSI/GBK 内容。
    /// </summary>
    private static Encoding DetectPgpEncoding(byte[] bytes, out int bomLength)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bomLength = 3;
            return Encoding.UTF8;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            bomLength = 2;
            return Encoding.Unicode;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            bomLength = 2;
            return Encoding.BigEndianUnicode;
        }

        // 无 BOM：ASCII 为主的 UTF-16 文件中 0x00 会集中出现在奇数位（LE）或偶数位（BE）。
        var sample = Math.Min(bytes.Length, 2000);
        if (sample >= 4)
        {
            var evenZeros = 0;
            var oddZeros = 0;
            for (var i = 0; i < sample; i++)
            {
                if (bytes[i] != 0)
                {
                    continue;
                }

                if (i % 2 == 0)
                {
                    evenZeros++;
                }
                else
                {
                    oddZeros++;
                }
            }

            if (oddZeros > sample / 4 && evenZeros == 0)
            {
                bomLength = 0;
                return Encoding.Unicode;
            }

            if (evenZeros > sample / 4 && oddZeros == 0)
            {
                bomLength = 0;
                return Encoding.BigEndianUnicode;
            }
        }

        bomLength = 0;
        return ByteRoundTripEncoding;
    }

    /// <summary>
    /// 按字节清除旧版本以单字节 ASCII 形式追加的管理块（在 UTF-16 PGP 中表现为乱码尾巴）。
    /// 标记为纯 ASCII，直接字节匹配不受文件编码影响；未找到时原样返回。
    /// </summary>
    private static byte[] RemoveLegacyAsciiBlock(byte[] bytes)
    {
        var beginIndex = IndexOf(bytes, AsciiBlockBeginBytes, 0);
        if (beginIndex < 0)
        {
            return bytes;
        }

        var removeStart = beginIndex;
        while (removeStart > 0 && bytes[removeStart - 1] != (byte)'\n')
        {
            removeStart--;
        }

        int removeEnd;
        var endIndex = IndexOf(bytes, AsciiBlockEndBytes, beginIndex);
        if (endIndex < 0)
        {
            removeEnd = bytes.Length;
        }
        else
        {
            removeEnd = endIndex + AsciiBlockEndBytes.Length;
            while (removeEnd < bytes.Length && bytes[removeEnd] != (byte)'\n')
            {
                removeEnd++;
            }

            if (removeEnd < bytes.Length)
            {
                removeEnd++;
            }
        }

        var result = new byte[bytes.Length - (removeEnd - removeStart)];
        Array.Copy(bytes, 0, result, 0, removeStart);
        Array.Copy(bytes, removeEnd, result, removeStart, bytes.Length - removeEnd);
        return result;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int startIndex)
    {
        for (var i = startIndex; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>在 CAD 支持文件搜索路径（ACADPREFIX）中查找 acad.pgp / ZWCAD.pgp。</summary>
    private static string? LocatePgpFile()
    {
        try
        {
            var prefix = Convert.ToString(CadApp.GetSystemVariable("ACADPREFIX")) ?? "";
            foreach (var directory in prefix.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var path = FindPgpInDirectory(directory.Trim());
                if (path != null)
                {
                    return path;
                }
            }
        }
        catch
        {
        }

        // ZWCAD 用户级 PGP 在 AppData 目录下，不在 ACADPREFIX（安装目录）列表中。
        // 例如 C:\Users\<用户>\AppData\Roaming\ZWSOFT\ZWCAD\<版本>\zh-CN\Support\ZWCAD.pgp
        try
        {
            var roamableRoot = Convert.ToString(CadApp.GetSystemVariable("ROAMABLEROOTPREFIX")) ?? "";
            foreach (var directory in roamableRoot.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var path = FindPgpInDirectory(directory.Trim());
                if (path != null)
                {
                    return path;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? FindPgpInDirectory(string directory)
    {
        try
        {
            if (directory.Length == 0 || !Directory.Exists(directory))
            {
                return null;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.pgp", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (string.Equals(name, "acad.pgp", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "ZWCAD.pgp", StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>删除 PGP 文本中的旧管理块；结束标记缺失时从起始标记所在行删到文件尾。</summary>
    private static string RemoveManagedBlock(string content)
    {
        var beginIndex = content.IndexOf(BlockBegin, StringComparison.Ordinal);
        if (beginIndex < 0)
        {
            return content;
        }

        // 起始标记所在行的行首。
        var removeStart = beginIndex == 0 ? 0 : content.LastIndexOf('\n', beginIndex - 1) + 1;
        var endIndex = content.IndexOf(BlockEnd, beginIndex, StringComparison.Ordinal);
        int removeEnd;
        if (endIndex < 0)
        {
            removeEnd = content.Length;
        }
        else
        {
            var lineEnd = content.IndexOf('\n', endIndex);
            removeEnd = lineEnd < 0 ? content.Length : lineEnd + 1;
        }

        return content.Remove(removeStart, removeEnd - removeStart);
    }

    /// <summary>找出与 PGP 管理块之外已有别名重复的简化命令（PGP 后定义生效，重复仅作提示）。</summary>
    private static List<string> FindConflicts(string contentWithoutBlock, IReadOnlyList<AliasEntry> entries)
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in PgpAliasLinePattern.Matches(contentWithoutBlock))
        {
            var alias = match.Groups[1].Value;
            if (!existing.ContainsKey(alias))
            {
                existing[alias] = match.Groups[2].Value;
            }
        }

        var conflicts = new List<string>();
        foreach (var entry in entries)
        {
            if (existing.TryGetValue(entry.Alias, out var mappedCommand)
                && !string.Equals(mappedCommand, entry.CommandName, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(entry.Alias);
            }
        }

        return conflicts;
    }
}
