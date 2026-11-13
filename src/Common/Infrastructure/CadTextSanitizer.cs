using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ZwcadBatchPlot;

/// <summary>
/// CAD 文字内容清洗：把 MText 的格式控制码与 %% 控制码处理成人能读的纯文本。
/// <para>
/// 图签里的字段常来自多行文字，<c>MText.Contents</c> 自带 <c>\W0.7;</c>、<c>\H2.5x;</c>、
/// <c>\fSimSun|b0|i0|c134|p2;</c>、<c>\C1;</c>、<c>\Q15;</c> 这类标签；
/// 不清洗就会原样带到清单、图纸目录和导出的 CSV 里（图上显示成「\W0.7;图名」）。
/// </para>
/// <para>
/// 只做「去掉控制码」，不做换行保留——目录单元格与 CSV 都要单行文本，
/// 段落符 <c>\P</c> 与换行统一变成空格后合并多余空白。
/// </para>
/// </summary>
public static class CadTextSanitizer
{
    /// <summary>
    /// 字符级格式标签：反斜杠 + 一个控制字母 + 到分号为止的参数。
    /// 覆盖 <c>\W</c>(宽度因子)、<c>\H</c>(字高)、<c>\S</c>(堆叠)、<c>\C</c>/<c>\c</c>(颜色)、
    /// <c>\F</c>/<c>\f</c>(字体)、<c>\Q</c>(倾斜)、<c>\T</c>(字距)、<c>\A</c>(对齐)、<c>\p</c>(段落)。
    /// 参数里限定不含分号/反斜杠/花括号，所以 <c>C:\dwg\a.txt</c> 这种没有分号的文本不会被误删。
    /// </summary>
    private static readonly Regex FormatTagPattern = new(
        @"\\[WwHhSsCcFfQqTtAaPp][^;\\{}]{0,80};", RegexOptions.Compiled);

    /// <summary><c>\U+4E2D</c> 形式的 Unicode 转义，可还原成真字符。</summary>
    private static readonly Regex UnicodeEscapePattern = new(
        @"\\U\+(?<hex>[0-9A-Fa-f]{4})", RegexOptions.Compiled);

    /// <summary><c>\M+xxxxx</c> 双字节字符编码：无法还原，只能丢弃。</summary>
    private static readonly Regex MultibyteEscapePattern = new(
        @"\\M\+[0-9A-Fa-f]{1,5}", RegexOptions.Compiled);

    /// <summary>无参数的单字母控制码：<c>\L \l \O \o \K \k \N</c>（下划线/上划线/删除线/分栏）。</summary>
    private static readonly Regex SingleLetterCodePattern = new(
        @"\\[LlOoKkN]", RegexOptions.Compiled);

    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    /// <summary>返回可直接写进单行文字、表格单元格或 CSV 的纯文本。</summary>
    public static string StripFormatting(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var text = value!;

        // \U+4E2D 先还原成真字符，否则后面的标签规则会把整段当普通文本留下来。
        text = UnicodeEscapePattern.Replace(text, match =>
        {
            var hex = match.Groups["hex"].Value;
            return int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
                && code > 0
                && code <= 0x10FFFF
                ? char.ConvertFromUtf32(code)
                : match.Value;
        });

        text = MultibyteEscapePattern.Replace(text, "");

        // \W0.7; \H2.5x; \fSimSun|b0|i0|c134|p2; \C1; \Q15; \T1.2; \S1^2; \pql; …
        // 必须排在单独处理 \P 之前：\pql; 这类段落属性带分号，若先按单字母替换会留下 "ql;"。
        text = FormatTagPattern.Replace(text, "");

        // 段落符与不断行空格（不带分号）换空格。
        text = text.Replace("\\P", " ").Replace("\\p", " ").Replace("\\~", " ");

        // 残留的无参控制码 \L \O \K \N 等。
        text = SingleLetterCodePattern.Replace(text, "");

        // 单行文字/表格里也常见的 %% 控制码。
        text = text.Replace("%%U", "").Replace("%%u", "")
            .Replace("%%O", "").Replace("%%o", "")
            .Replace("%%C", "Φ").Replace("%%c", "Φ")
            .Replace("%%D", "°").Replace("%%d", "°")
            .Replace("%%P", "±").Replace("%%p", "±")
            .Replace("%%%", "%");

        // MText 的分组花括号（只保留组内内容）。
        text = text.Replace("{", "").Replace("}", "");

        return WhitespacePattern.Replace(text, " ").Trim();
    }
}
