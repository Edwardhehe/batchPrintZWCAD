using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;

/**
 * @file PlotStyleLineweightConverter.cs
 * @description 生成“仅线宽改为使用对象线宽”的打印样式表（CTB/STB）字节副本；纯文件处理，不引用 CAD API。
 *
 * 主要功能：
 * - Convert：把 plot_style 下每个样式的 lineweight 改为 0（使用对象线宽），其余字节原样保留
 * - ExtractPayload：取出样式表正文（压缩格式解压；非压缩格式原样），供校验/单元测试使用
 * - 副本命名：<原名>__objlw.ctb / .stb，并提供识别与还原原名的方法（下拉框据此隐藏副本）
 *
 * 文件格式（与 PiaNO PiaSerializer 读写的 PIAFILE 结构一致）：
 * - 压缩格式：48 字节文本头 "PIAFILEVERSION_2.0,CTBVER1,compress\r\npmzlibcodec"
 *   + 12 字节（adler32、正文长度、压缩长度，小端；AutoCAD 的 adler32 针对压缩数据）+ zlib 数据。
 * - 非压缩格式：首行 "PIAFILEVERSION_2.0,CTBVER1"，其后直接是正文。
 * - plot_style{ N{ ... lineweight=K ... } }：K 为 custom_lineweight_table 下标 + 1，K=0 表示“使用对象线宽”。
 *   依据：AutoCAD/中望自带 acad.ctb、monochrome.ctb（全部“使用对象线宽”）所有样式均为 lineweight=0；
 *   DWF Virtual Pens.ctb 把表项 0 改成 0.254 且所有样式 lineweight=1。表中并无 255 这一取值。
 *
 * 注意：按字节逐行改写而非经 PiaSerializer 反序列化再序列化，避免 Encoding.Default 在 .NET 8 下
 * 变为 UTF-8 导致 GBK 描述乱码、键值顺序/缩进/结尾 NUL 等被改动；其余内容与原文件逐字节一致。
 */

namespace ZwcadBatchPlot;

internal static class PlotStyleLineweightConverter
{
    /** 对象线宽副本文件名后缀（位于扩展名之前）。 */
    public const string CopySuffix = "__objlw";

    /** CTB/STB 中“使用对象线宽”的 lineweight 取值。 */
    public const int UseObjectLineweightValue = 0;

    private const int HeaderLength = 48;
    private const int ChecksumLength = 12;
    private const int PayloadOffset = HeaderLength + ChecksumLength;
    private const string PlotStyleNode = "plot_style";
    private const string LineweightKey = "lineweight";

    /** IsSupportedStyleFile：是否为 .ctb / .stb 文件名。 */
    public static bool IsSupportedStyleFile(string? fileName)
    {
        var extension = SafeExtension(fileName);
        return string.Equals(extension, ".ctb", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".stb", StringComparison.OrdinalIgnoreCase);
    }

    /** IsObjectLineweightCopy：是否为插件生成的 *__objlw.ctb / *__objlw.stb 副本。 */
    public static bool IsObjectLineweightCopy(string? styleName)
    {
        if (!IsSupportedStyleFile(styleName))
        {
            return false;
        }

        var stem = SafeFileNameWithoutExtension(styleName);
        return stem.Length > CopySuffix.Length
               && stem.EndsWith(CopySuffix, StringComparison.OrdinalIgnoreCase);
    }

    /** GetCopyFileName：A3民建院.ctb → A3民建院__objlw.ctb；已是副本时原样返回。 */
    public static string GetCopyFileName(string sourceFileName)
    {
        var fileName = SafeFileName(sourceFileName);
        if (IsObjectLineweightCopy(fileName))
        {
            return fileName;
        }

        return SafeFileNameWithoutExtension(fileName) + CopySuffix + SafeExtension(fileName);
    }

    /** GetSourceFileName：A3民建院__objlw.ctb → A3民建院.ctb；不是副本时原样返回。 */
    public static string GetSourceFileName(string styleName)
    {
        var fileName = SafeFileName(styleName);
        if (!IsObjectLineweightCopy(fileName))
        {
            return fileName;
        }

        var stem = SafeFileNameWithoutExtension(fileName);
        return stem.Substring(0, stem.Length - CopySuffix.Length) + SafeExtension(fileName);
    }

    /**
     * Convert：返回线宽全部改为“使用对象线宽”的新文件字节；source 不会被修改。
     * changedStyles 为原先不是对象线宽、被改写的样式数；totalStyles 为 plot_style 下的样式线宽条目数。
     */
    public static byte[] Convert(byte[] source, out int changedStyles, out int totalStyles)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var layout = ReadLayout(source);
        var payload = layout.Compressed
            ? Inflate(source, PayloadOffset, layout.CompressedLength, layout.RawLength)
            : source;
        var rewritten = RewriteLineweights(payload, out changedStyles, out totalStyles);
        if (totalStyles == 0)
        {
            throw new InvalidDataException("打印样式表中未找到 plot_style 线宽条目。");
        }

        if (!layout.Compressed)
        {
            return rewritten;
        }

        var deflated = Deflate(rewritten);
        var checksum = layout.ChecksumOfCompressedData ? Adler32(deflated, 0, deflated.Length) : Adler32(rewritten, 0, rewritten.Length);
        var trailingStart = PayloadOffset + layout.CompressedLength;
        var trailingLength = source.Length - trailingStart;

        var result = new byte[PayloadOffset + deflated.Length + trailingLength];
        Buffer.BlockCopy(source, 0, result, 0, HeaderLength);
        WriteUInt32(result, HeaderLength, checksum);
        WriteUInt32(result, HeaderLength + 4, (uint)rewritten.Length);
        WriteUInt32(result, HeaderLength + 8, (uint)deflated.Length);
        Buffer.BlockCopy(deflated, 0, result, PayloadOffset, deflated.Length);
        if (trailingLength > 0)
        {
            Buffer.BlockCopy(source, trailingStart, result, PayloadOffset + deflated.Length, trailingLength);
        }

        return result;
    }

    /** ExtractPayload：返回样式表正文字节（压缩格式为解压后的正文，非压缩格式为整个文件）。 */
    public static byte[] ExtractPayload(byte[] file)
    {
        if (file is null)
        {
            throw new ArgumentNullException(nameof(file));
        }

        var layout = ReadLayout(file);
        return layout.Compressed
            ? Inflate(file, PayloadOffset, layout.CompressedLength, layout.RawLength)
            : file;
    }

    /** IsCompressed：文件头是否声明 zlib 压缩。 */
    public static bool IsCompressed(byte[] file) => ReadLayout(file).Compressed;

    private sealed class FileLayout
    {
        public bool Compressed { get; set; }
        public int RawLength { get; set; }
        public int CompressedLength { get; set; }
        public bool ChecksumOfCompressedData { get; set; }
    }

    private static FileLayout ReadLayout(byte[] file)
    {
        var headLength = Math.Min(HeaderLength, file.Length);
        var head = Latin1(file, 0, headLength);
        if (!head.StartsWith("PIAFILEVERSION", StringComparison.Ordinal))
        {
            throw new InvalidDataException("不是有效的打印样式表文件（缺少 PIAFILEVERSION 文件头）。");
        }

        if (head.IndexOf("CTBVER", StringComparison.Ordinal) < 0
            && head.IndexOf("STBVER", StringComparison.Ordinal) < 0)
        {
            throw new InvalidDataException("文件头不是 CTB/STB 打印样式表：" + head.Split('\r', '\n')[0]);
        }

        var firstLine = head.Split('\r', '\n')[0];
        if (firstLine.IndexOf("compress", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return new FileLayout { Compressed = false };
        }

        if (file.Length < PayloadOffset)
        {
            throw new InvalidDataException("压缩打印样式表文件长度不足。");
        }

        var storedChecksum = ReadUInt32(file, HeaderLength);
        var rawLength = (int)ReadUInt32(file, HeaderLength + 4);
        var compressedLength = (int)ReadUInt32(file, HeaderLength + 8);
        if (compressedLength <= 0 || PayloadOffset + compressedLength > file.Length)
        {
            // 个别写入器压缩长度字段不可信时，按文件剩余长度解压。
            compressedLength = file.Length - PayloadOffset;
        }

        var layout = new FileLayout
        {
            Compressed = true,
            RawLength = rawLength,
            CompressedLength = compressedLength
        };

        // AutoCAD 自带 CTB/STB 头部存的是压缩数据（zlib 流）的 adler32；PiaSerializer 写的是正文的 adler32。
        // 源文件明确用正文校验时沿用，否则按 AutoCAD 约定写压缩数据校验和。
        var raw = Inflate(file, PayloadOffset, compressedLength, rawLength);
        layout.ChecksumOfCompressedData = storedChecksum != Adler32(raw, 0, raw.Length);
        return layout;
    }

    /**
     * RewriteLineweights：逐行扫描，只改写 plot_style{ N{ lineweight=... } } 这一层的线宽值，
     * 保留原缩进与换行符；aci_table、custom_lineweight_table、custom_lineweight_display_units 等一律不动。
     */
    private static byte[] RewriteLineweights(byte[] payload, out int changedStyles, out int totalStyles)
    {
        changedStyles = 0;
        totalStyles = 0;
        var output = new MemoryStream(payload.Length);
        var nodeStack = new List<string>();
        var replacementValue = Encoding.ASCII.GetBytes(UseObjectLineweightValue.ToString());

        var lineStart = 0;
        while (lineStart < payload.Length)
        {
            var newline = Array.IndexOf(payload, (byte)'\n', lineStart);
            var lineEnd = newline < 0 ? payload.Length : newline;           // 不含 '\n'
            var contentEnd = lineEnd;
            if (contentEnd > lineStart && payload[contentEnd - 1] == (byte)'\r')
            {
                contentEnd--;
            }

            var indentEnd = lineStart;
            while (indentEnd < contentEnd && (payload[indentEnd] == (byte)' ' || payload[indentEnd] == (byte)'\t'))
            {
                indentEnd++;
            }

            var equalsIndex = IndexOf(payload, (byte)'=', indentEnd, contentEnd);
            var trimmedEnd = contentEnd;
            while (trimmedEnd > indentEnd && (payload[trimmedEnd - 1] == (byte)' ' || payload[trimmedEnd - 1] == (byte)'\t'))
            {
                trimmedEnd--;
            }

            var rewritten = false;
            if (equalsIndex < 0 && trimmedEnd > indentEnd && payload[trimmedEnd - 1] == (byte)'{')
            {
                nodeStack.Add(Latin1(payload, indentEnd, trimmedEnd - 1 - indentEnd).Trim());
            }
            else if (equalsIndex < 0 && trimmedEnd - indentEnd == 1 && payload[indentEnd] == (byte)'}')
            {
                if (nodeStack.Count > 0)
                {
                    nodeStack.RemoveAt(nodeStack.Count - 1);
                }
            }
            else if (equalsIndex >= 0
                     && nodeStack.Count == 2
                     && string.Equals(nodeStack[0], PlotStyleNode, StringComparison.Ordinal)
                     && string.Equals(Latin1(payload, indentEnd, equalsIndex - indentEnd).Trim(), LineweightKey, StringComparison.Ordinal))
            {
                totalStyles++;
                var oldValue = Latin1(payload, equalsIndex + 1, contentEnd - equalsIndex - 1).Trim();
                if (!string.Equals(oldValue, UseObjectLineweightValue.ToString(), StringComparison.Ordinal))
                {
                    changedStyles++;
                }

                // 缩进 + "lineweight=" 原样保留，只替换值；行尾 \r（若有）与 \n 照旧写回。
                output.Write(payload, lineStart, equalsIndex + 1 - lineStart);
                output.Write(replacementValue, 0, replacementValue.Length);
                output.Write(payload, contentEnd, lineEnd - contentEnd);
                rewritten = true;
            }

            if (!rewritten)
            {
                output.Write(payload, lineStart, lineEnd - lineStart);
            }

            if (newline >= 0)
            {
                output.WriteByte((byte)'\n');
            }

            lineStart = lineEnd + 1;
        }

        return output.ToArray();
    }

    private static byte[] Inflate(byte[] data, int offset, int count, int expectedLength)
    {
        using var input = new MemoryStream(data, offset, count, false);
        using var inflater = new InflaterInputStream(input) { IsStreamOwner = false };
        using var output = new MemoryStream(expectedLength > 0 ? expectedLength : count * 8);
        var buffer = new byte[81920];
        int read;
        while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
        }

        // 长度字段仅作容量提示；数据完整性由 zlib 流末尾的 adler32 校验（损坏时 Inflater 会抛异常）。
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        var output = new MemoryStream();
        var deflater = new Deflater(Deflater.DEFAULT_COMPRESSION);
        var stream = new DeflaterOutputStream(output, deflater) { IsStreamOwner = false };
        stream.Write(data, 0, data.Length);
        stream.Finish();
        stream.Dispose();
        return output.ToArray();
    }

    /** Adler32：zlib 校验和（与 PiaSerializer 写入文件头的算法相同）。 */
    public static uint Adler32(byte[] data, int offset, int count)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;
        var end = offset + count;
        for (var i = offset; i < end; i++)
        {
            a = (a + data[i]) % Mod;
            b = (b + a) % Mod;
        }

        return (b << 16) | a;
    }

    private static int IndexOf(byte[] data, byte value, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (data[i] == value)
            {
                return i;
            }
        }

        return -1;
    }

    private static uint ReadUInt32(byte[] data, int offset)
        => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    /** Latin1：逐字节映射成字符，不依赖系统代码页，GBK/UTF-8 字节也能无损比较。 */
    private static string Latin1(byte[] data, int offset, int count)
    {
        var chars = new char[Math.Max(0, count)];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)data[offset + i];
        }

        return new string(chars);
    }

    private static string SafeFileName(string? value)
    {
        var text = (value ?? "").Trim();
        try
        {
            return Path.GetFileName(text);
        }
        catch
        {
            return text;
        }
    }

    private static string SafeFileNameWithoutExtension(string? value)
    {
        var name = SafeFileName(value);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name.Substring(0, dot) : name;
    }

    private static string SafeExtension(string? value)
    {
        var name = SafeFileName(value);
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name.Substring(dot) : "";
    }
}