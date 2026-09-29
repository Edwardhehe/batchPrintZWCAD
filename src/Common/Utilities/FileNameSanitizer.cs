using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ZwcadBatchPlot;

public static class FileNameSanitizer
{
    private const int DefaultMaxFileNameLength = 120;
    private const int LegacyMaxPathLength = 240;
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public static string Clean(string value)
    {
        var cleaned = new string((value ?? "").Select(ch => InvalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "未命名" : cleaned;
    }

    public static string MakeUnique(string directory, string fileNameWithoutExtension)
    {
        return MakeUnique(directory, fileNameWithoutExtension, null);
    }

    public static string MakeUnique(string directory, string fileNameWithoutExtension, ISet<string>? reservedPaths)
    {
        return MakeUnique(directory, fileNameWithoutExtension, reservedPaths, true);
    }

    public static string MakeUnique(
        string directory,
        string fileNameWithoutExtension,
        ISet<string>? reservedPaths,
        bool avoidExistingFile,
        string extension = ".pdf",
        bool createDirectory = true)
    {
        if (createDirectory)
        {
            Directory.CreateDirectory(directory);
        }
        var clean = TrimFileNameForPath(Clean(fileNameWithoutExtension), directory, extension);
        var path = Path.Combine(directory, clean + extension);
        var index = 1;
        while ((avoidExistingFile && File.Exists(path)) || reservedPaths?.Contains(path) == true)
        {
            path = BuildSuffixedPath(directory, clean, index, extension);
            index++;
        }

        reservedPaths?.Add(path);
        return path;
    }

    /// <summary>
    /// 批量命名（同一批次一次性计算）：同一批内目录 + 文件名 + 扩展名（不区分大小写）出现多次的，
    /// 全部按清单顺序从 _1 起编号：图号_1、图号_2……图号_10；
    /// 本批只出现一次的名称与 <see cref="MakeUnique(string, string, ISet{string}?, bool, string, bool)"/> 相同：
    /// 默认不加后缀，avoidExistingFile 为 true 且磁盘已有同名文件时再顺延 _1、_2…。
    /// 重复组编号会跳过本批已占用的路径（含其他唯一名称，如单独的 A_1 与两个 A 同批时，A 组取 _2、_3），
    /// avoidExistingFile 为 true 时还会跳过磁盘上已存在的文件。
    /// 返回值与 items 一一对应；所有结果都会写入 reservedPaths（如提供）。
    /// </summary>
    public static IReadOnlyList<string> MakeUniqueBatch(
        IReadOnlyList<BatchFileNameRequest> items,
        ISet<string>? reservedPaths,
        bool avoidExistingFile,
        string extension = ".pdf",
        bool createDirectory = true)
    {
        if (items == null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        var reserved = reservedPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prepared = new PreparedBatchName[items.Count];
        var candidateCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var createdDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 第一遍：按与 MakeUnique 相同的清洗/截断规则得到候选路径，并统计本批重复次数。
        for (var i = 0; i < items.Count; i++)
        {
            var directory = items[i].Directory ?? "";
            if (createDirectory && createdDirectories.Add(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var clean = TrimFileNameForPath(Clean(items[i].FileNameWithoutExtension), directory, extension);
            var candidate = Path.Combine(directory, clean + extension);
            prepared[i] = new PreparedBatchName(directory, clean, candidate);
            candidateCounts.TryGetValue(candidate, out var count);
            candidateCounts[candidate] = count + 1;
        }

        // 本批唯一名称的原始路径先占位，避免被重复组的 _N 或已存在文件的顺延后缀抢走。
        var pendingUniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in prepared)
        {
            if (candidateCounts[item.CandidatePath] == 1 && !reserved.Contains(item.CandidatePath))
            {
                pendingUniquePaths.Add(item.CandidatePath);
            }
        }

        // 第二遍：按清单顺序分配最终路径。
        var nextDuplicateIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>(prepared.Length);
        foreach (var item in prepared)
        {
            string path;
            if (candidateCounts[item.CandidatePath] == 1)
            {
                pendingUniquePaths.Remove(item.CandidatePath);
                path = item.CandidatePath;
                var index = 1;
                while (IsTaken(path))
                {
                    path = BuildSuffixedPath(item.Directory, item.CleanName, index, extension);
                    index++;
                }
            }
            else
            {
                if (!nextDuplicateIndex.TryGetValue(item.CandidatePath, out var index))
                {
                    index = 1;
                }

                do
                {
                    path = BuildSuffixedPath(item.Directory, item.CleanName, index, extension);
                    index++;
                }
                while (IsTaken(path));

                nextDuplicateIndex[item.CandidatePath] = index;
            }

            reserved.Add(path);
            results.Add(path);
        }

        return results;

        bool IsTaken(string path) =>
            (avoidExistingFile && File.Exists(path))
            || reserved.Contains(path)
            || pendingUniquePaths.Contains(path);
    }

    private static string BuildSuffixedPath(string directory, string cleanName, int index, string extension)
    {
        var suffix = "_" + index.ToString(CultureInfo.InvariantCulture);
        var maxNameLength = GetMaxFileNameLength(directory, extension);
        var uniqueName = TrimToLength(cleanName, Math.Max(1, maxNameLength - suffix.Length)) + suffix;
        return Path.Combine(directory, uniqueName + extension);
    }

    private readonly struct PreparedBatchName
    {
        public PreparedBatchName(string directory, string cleanName, string candidatePath)
        {
            Directory = directory;
            CleanName = cleanName;
            CandidatePath = candidatePath;
        }

        public string Directory { get; }

        public string CleanName { get; }

        public string CandidatePath { get; }
    }

    private static string TrimFileNameForPath(string value, string directory, string extension = ".pdf")
    {
        return TrimToLength(value, GetMaxFileNameLength(directory, extension));
    }

    private static int GetMaxFileNameLength(string directory, string extension = ".pdf")
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return DefaultMaxFileNameLength;
        }

        return Math.Min(
            DefaultMaxFileNameLength,
            Math.Max(1, LegacyMaxPathLength - Path.GetFullPath(directory).Length - extension.Length - 1));
    }

    /// <summary>
    /// 把加长图幅名转换为适合文件名的格式：
    /// 配置1（分数）：含"/"的转为"∕"（U+2215）；小数扩展量先尝试还原为1/8模数分数（如0.5→1∕2）；
    ///               无法还原的任意加长保留小数。
    /// 配置2（小数）：含"/"的分数转为小数（如1/4→0.25）；已是小数的保持不变。
    /// 配置3（倍数）：将加长图转换为"图幅x放大倍数"形式（如A1+1/4→A1x1.25，A2+0.5→A2x1.5）。
    /// </summary>
    public static string NormalizeLongPaperFraction(string paperName, LongPaperNameFormat format = LongPaperNameFormat.Fraction)
    {
        if (string.IsNullOrEmpty(paperName)) return paperName ?? "";

        // ── 配置3（倍数）：将加长图转换为"图幅x放大倍数"形式 ──
        if (format == LongPaperNameFormat.Multiplier)
        {
            // 先处理已有 "/" 的分数形式（如 A1+1/2）
            var multiplierResult = LongPaperFractionPattern.Replace(paperName, match =>
            {
                var numerator = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var denominator = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                if (denominator == 0) return match.Value;
                var ext = numerator / (double)denominator;
                return FormatMultiplier(ext);
            });

            // 再处理整数或小数扩展量（如 A1+1、A1+0.25）
            multiplierResult = LongPaperNumberExtPattern.Replace(multiplierResult, match =>
            {
                var ext = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                return ext <= 0d ? match.Value : FormatMultiplier(ext);
            });

            return multiplierResult;
        }

        // ── 处理已有 "/" 的分数形式（如 A1+1/2）──
        var resultDefault = LongPaperFractionPattern.Replace(paperName, match =>
        {
            var numerator = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var denominator = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (denominator == 0) return match.Value;
            if (format == LongPaperNameFormat.Decimal)
            {
                var ext = numerator / (double)denominator;
                return "+" + ext.ToString("0.###", CultureInfo.InvariantCulture);
            }
            // Config1（分数）：仅将 "/" 换为 "∕"（U+2215），文件系统合法
            return "+" + match.Groups[1].Value + "∕" + match.Groups[2].Value;
        });

        // ── 配置1（分数）：把小数扩展量还原为 1/8 模数分数 ──
        // 例：A1+0.5 → A1+1∕2，A2+1.501（任意加长）保持不变
        if (format == LongPaperNameFormat.Fraction)
        {
            resultDefault = LongPaperDecimalExtPattern.Replace(resultDefault, match =>
            {
                var dec = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                if (dec <= 0d) return match.Value;
                var units8 = (int)Math.Round(dec * 8, MidpointRounding.AwayFromZero);
                // 只有精确命中 1/8 步进的才转；任意加长（如 +1.501）偏差超 0.001 保留小数
                if (Math.Abs(dec * 8 - units8) > 0.001) return match.Value;
                var gcd = Gcd(units8, 8);
                return "+" + (units8 / gcd) + "∕" + (8 / gcd);
            });
        }

        return resultDefault;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) { var r = a % b; a = b; b = r; }
        return a;
    }

    /// <summary>把加长扩展量换算为"图幅x总倍数"形式，如 0.25 → x1.25、1 → x2。</summary>
    private static string FormatMultiplier(double extension)
    {
        // 最多3位小数，覆盖 1/8 模数（0.125）而不产生多余尾零。
        return "x" + (1.0 + extension).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static readonly Regex LongPaperFractionPattern =
        new Regex(@"\+(\d+)/(\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 匹配末尾小数扩展量，如 +0.5、+1.501、+1.125
    private static readonly Regex LongPaperDecimalExtPattern =
        new Regex(@"\+(\d+\.\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 匹配末尾整数或小数扩展量，如 +1、+0.5、+1.125
    private static readonly Regex LongPaperNumberExtPattern =
        new Regex(@"\+(\d+(?:\.\d+)?)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 按用户输入的规则生成文件名。占位符区分大小写：
    /// A=图号，B=版次，C=图名，D=日期，E=信息1，F=信息2，G=设计阶段，T=图幅，N=序号。
    /// 反斜杠转义其后的字符，例如 \A 输出字母 A。
    /// </summary>
    public static string FormatFileNamePattern(
        string? pattern,
        PlotJob job,
        int? sequenceNumber = null,
        int sequenceDigits = 0,
        LongPaperNameFormat longPaperNameFormat = LongPaperNameFormat.Fraction,
        double longPaperSnapToleranceMm = 3d)
    {
        var result = new StringBuilder();
        var value = pattern ?? "";
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\\' && index + 1 < value.Length)
            {
                result.Append(value[++index]);
                continue;
            }

            var replacement = character switch
            {
                'A' => job.DrawingNumber,
                'B' => job.Revision,
                'C' => job.Title,
                'D' => job.Date,
                'E' => job.Info1,
                'F' => job.Info2,
                'G' => job.Phase,
                'T' => NormalizeLongPaperFraction(
                    OutputPaperNameResolver.Resolve(job, longPaperSnapToleranceMm),
                    longPaperNameFormat),
                'N' => sequenceNumber.HasValue ? FormatSequenceNumber(sequenceNumber.Value, sequenceDigits) : "",
                _ => null
            };
            if (replacement == null)
            {
                result.Append(character);
            }
            else if (!string.IsNullOrWhiteSpace(replacement))
            {
                result.Append(replacement.Trim());
            }
        }

        var formatted = result.ToString();
        if (string.IsNullOrWhiteSpace(formatted))
        {
            formatted = string.IsNullOrWhiteSpace(job.DrawingNumber) ? "未命名" : job.DrawingNumber;
        }

        return Clean(formatted);
    }

    public static int ResolveSequenceDigits(
        bool autoDigits,
        int configuredDigits,
        int startNumber,
        int totalCount)
    {
        if (!autoDigits)
        {
            return Math.Max(0, Math.Min(10, configuredDigits));
        }

        var lastNumber = (long)Math.Max(0, startNumber) + Math.Max(0, totalCount - 1);
        return Math.Max(1, Math.Min(10, lastNumber.ToString(CultureInfo.InvariantCulture).Length));
    }

    private static string FormatSequenceNumber(int sequenceNumber, int sequenceDigits)
    {
        var digits = Math.Max(0, Math.Min(10, sequenceDigits));
        return digits == 0
            ? sequenceNumber.ToString(CultureInfo.InvariantCulture)
            : sequenceNumber.ToString($"D{digits}", CultureInfo.InvariantCulture);
    }

    private static string TrimToLength(string value, int maxLength)
    {
        value = string.IsNullOrWhiteSpace(value) ? "未命名" : value.Trim();
        return value.Length <= maxLength ? value : value.Substring(0, maxLength).Trim();
    }

    /// <summary>
    /// 根据用户配置的字段键列表，从 PlotJob 中提取对应的值组成文件名片段。
    /// 空值自动跳过，确保最终文件名不含多余分隔符。
    /// sequenceNumber > 0 时 "Sequence" 键生效，按 sequenceDigits 补零。
    /// </summary>
    public static List<string> GetFileNameParts(
        PlotJob job,
        List<string> fieldKeys,
        int sequenceNumber = 0,
        int sequenceDigits = 2,
        LongPaperNameFormat longPaperNameFormat = LongPaperNameFormat.Fraction,
        double longPaperSnapToleranceMm = 3d)
    {
        var parts = new List<string>();
        foreach (var key in fieldKeys)
        {
            var value = key switch
            {
                "DrawingNumber" => job.DrawingNumber,
                "Title" => job.Title,
                "Date" => job.Date,
                "Revision" => job.Revision,
                "Phase" => job.Phase,
                "Info1" => job.Info1,
                "Info2" => job.Info2,
                "PaperName" => NormalizeLongPaperFraction(
                    OutputPaperNameResolver.Resolve(job, longPaperSnapToleranceMm),
                    longPaperNameFormat),
                "Sequence" => sequenceNumber > 0 ? sequenceNumber.ToString($"D{Math.Max(1, Math.Min(10, sequenceDigits))}") : "",
                _ => ""
            };
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value.Trim());
            }
        }

        return parts;
    }
}

/// <summary>批量命名请求：输出目录 + 未清洗的文件名（不含扩展名）。</summary>
public readonly struct BatchFileNameRequest
{
    public BatchFileNameRequest(string directory, string fileNameWithoutExtension)
    {
        Directory = directory ?? "";
        FileNameWithoutExtension = fileNameWithoutExtension ?? "";
    }

    public string Directory { get; }

    public string FileNameWithoutExtension { get; }
}
