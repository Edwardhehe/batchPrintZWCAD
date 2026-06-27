using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
#if !ZWCAD
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#endif
using PiaNO;

namespace ZwcadBatchPlot;

/// <summary>
/// 向 LA_pdf.pmp 添加自定义纸张尺寸。
/// 支持三种格式：
///   - PIA 3.0 JSON（新版 AutoCAD）
///   - PIA 2.0 压缩（旧版 AutoCAD，PianNoCN）
///   - INI 文本（ZWCAD）
/// </summary>
public static class PmpCustomPaper
{
    /// <summary>
    /// 在 PMP 文件中注册自定义纸张尺寸。如果已存在同尺寸则跳过。
    /// 返回纸张名称（用于 CanonicalMediaName 和删除），失败返回 null。
    /// </summary>
    public static string? RegisterCustomPaper(string pmpPath, double widthMm, double heightMm)
    {
        if (!File.Exists(pmpPath)) return null;

        try
        {
            var raw = File.ReadAllText(pmpPath);
            if (string.IsNullOrWhiteSpace(raw)) return null;

#if !ZWCAD
            if (raw.StartsWith("PIAFILEVERSION_3.0", StringComparison.OrdinalIgnoreCase))
                return RegisterPia3(pmpPath, raw, widthMm, heightMm);
#endif

            if (raw.StartsWith("[Meta]", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("[Meta]\r", StringComparison.OrdinalIgnoreCase))
                return RegisterZwcadIni(pmpPath, raw, widthMm, heightMm);

            return RegisterPia2(pmpPath, widthMm, heightMm);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 PMP 文件中删除已注册的自定义纸张。
    /// paperName 为 RegisterCustomPaper 的返回值。
    /// </summary>
    public static void RemoveCustomPaper(string pmpPath, string paperName)
    {
        if (!File.Exists(pmpPath) || string.IsNullOrWhiteSpace(paperName))
            return;

        try
        {
            var raw = File.ReadAllText(pmpPath);
            if (string.IsNullOrWhiteSpace(raw)) return;

#if !ZWCAD
            if (raw.StartsWith("PIAFILEVERSION_3.0", StringComparison.OrdinalIgnoreCase))
                RemovePia3(pmpPath, raw, paperName);
            else
#endif
            if (raw.StartsWith("[Meta]", StringComparison.OrdinalIgnoreCase)
                     || raw.StartsWith("[Meta]\r", StringComparison.OrdinalIgnoreCase))
                RemoveZwcadIni(pmpPath, raw, paperName);
            else
                RemovePia2(pmpPath, paperName);
        }
        catch
        {
            // 删除失败不阻塞
        }
    }

#if !ZWCAD
    private static string? RegisterPia3(string pmpPath, string raw, double widthMm, double heightMm)
    {
        var jsonStart = raw.IndexOf('{');
        if (jsonStart < 0) return null;
        var root = JObject.Parse(raw.Substring(jsonStart));

        var udm = root["data"]?["udm"] as JObject;
        if (udm == null) return null;
        var media = udm["media"] as JObject;
        if (media == null) return null;
        var desc = media["description"] as JObject;
        var size = media["size"] as JObject;
        if (desc == null || size == null) return null;

        // 检查同尺寸是否已存在
        var wFmt = widthMm.ToString("0.##");
        var hFmt = heightMm.ToString("0.##");
        var paperName = $"自定义 ({wFmt} x {hFmt} 毫米)";
        foreach (var prop in desc.Properties())
        {
            var entry = prop.Value as JObject;
            if (entry == null || entry["caps_type"]?.Value<int>() != 2) continue;
            var urx = entry["media_bounds_urx"]?.Value<double>() ?? 0;
            var ury = entry["media_bounds_ury"]?.Value<double>() ?? 0;
            if (Math.Abs(urx - widthMm) < 0.5 && Math.Abs(ury - heightMm) < 0.5)
            {
                // 返回已有的 localized_name
                var match = size[prop.Name] as JObject;
                return match?["localized_name"]?.Value<string>() ?? prop.Name;
            }
        }

        // 找下一个可用索引
        var maxIdx = 0;
        foreach (var prop in desc.Properties())
            if (int.TryParse(prop.Name, out var idx) && idx > maxIdx)
                maxIdx = idx;
        var index = (maxIdx + 1).ToString();

        // 添加 description 条目
        var area = widthMm * heightMm;
        var descName = $"UserDefinedMetric 自定义 {wFmt}W x {hFmt}H - (0, 0) x ({wFmt}, {hFmt}) ={area:0.} 毫米";
        var descEntry = new JObject
        {
            ["caps_type"] = 2,
            ["dimensional"] = true,
            ["media_bounds_urx"] = widthMm,
            ["media_bounds_ury"] = heightMm,
            ["name"] = descName,
            ["printable_area"] = area,
            ["printable_bounds_llx"] = 0.0,
            ["printable_bounds_lly"] = 0.0,
            ["printable_bounds_urx"] = widthMm,
            ["printable_bounds_ury"] = heightMm
        };
        desc[index] = descEntry;

        // 添加 size 条目
        var sizeEntry = new JObject
        {
            ["caps_type"] = 2,
            ["landscape_mode"] = true,
            ["localized_name"] = paperName,
            ["media_description_name"] = descName,
            ["media_group"] = 15,
            ["name"] = paperName
        };
        size[index] = sizeEntry;

        // 写回
        var newJson = root.ToString(Formatting.Indented);
        File.WriteAllText(pmpPath, "PIAFILEVERSION_3.0,json\n" + newJson);

        return paperName;
    }
#endif

    private static string? RegisterPia2(string pmpPath, double widthMm, double heightMm)
    {
        // PIA 2.0：使用 PianNoCN 库
        try
        {
            var config = new PlotterConfiguration(pmpPath);
            var paperName = $"UserDefined_{widthMm:0.##}x{heightMm:0.##}";
            var modNode = config["mod"];
            if (modNode?[paperName] != null) return paperName;

            // 检查同尺寸不同名（与 PIA 3.0 一致）
            if (modNode != null)
            {
                foreach (var child in modNode)
                {
                    var n = child.NodeName;
                    if (string.IsNullOrEmpty(n)) continue;
                    var sxStr = child.GetValue("size_max_x");
                    var syStr = child.GetValue("size_max_y");
                    double.TryParse(sxStr, out var sx);
                    double.TryParse(syStr, out var sy);
                    if (Math.Abs(sx - widthMm) < 0.5 && Math.Abs(sy - heightMm) < 0.5)
                        return n;
                }
            }

            var mediaNode = modNode!.Add(paperName);
            mediaNode.SetValue("size_max_x", widthMm.ToString("0.##"));
            mediaNode.SetValue("size_max_y", heightMm.ToString("0.##"));
            mediaNode.SetValue("printable_max_x", widthMm.ToString("0.##"));
            mediaNode.SetValue("printable_max_y", heightMm.ToString("0.##"));
            mediaNode.SetValue("hard_clip_ll_x", "0");
            mediaNode.SetValue("hard_clip_ll_y", "0");
            mediaNode.SetValue("hard_clip_ur_x", widthMm.ToString("0.##"));
            mediaNode.SetValue("hard_clip_ur_y", heightMm.ToString("0.##"));

            config.Saves(pmpPath);
            return paperName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// ZWCAD INI 格式 PMP：
    /// [Meta] ... userdef_num=N ...
    /// [user]
    /// paper_name0=...  paper_local_name0=...  size_x0=...  size_y0=...
    /// </summary>
    private static string? RegisterZwcadIni(string pmpPath, string raw, double widthMm, double heightMm)
    {
        // 检查同尺寸是否已存在
        var existingLocalName = FindZwcadPaperBySize(raw, widthMm, heightMm);
        if (existingLocalName != null) return existingLocalName;

        // 解析 userdef_num
        var numMatch = Regex.Match(raw, @"userdef_num=(\d+)");
        if (!numMatch.Success) return null;
        var index = int.Parse(numMatch.Groups[1].Value);

        // 生成唯一本地名称
        var localName = $"LA_Custom_{widthMm:0.}x{heightMm:0.}";

        // 检查 local_name 冲突
        while (raw.Contains($"paper_local_name{index - 1}=") || raw.Contains(localName))
        {
            // 确保下一个 index 也不冲突
            index++;
            localName = $"LA_Custom_{widthMm:0.}x{heightMm:0.}_{index}";
            if (index > 9999) return null;
        }

        // 构建新纸张条目
        var w = widthMm.ToString("0.000000");
        var h = heightMm.ToString("0.000000");
        var area = (widthMm * heightMm).ToString("0.000000");
        var newEntry = $"\r\npaper_name{index}=UserDefinedMetric ({w} x {h} 毫米)\r\n" +
                       $"paper_local_name{index}={localName}\r\n" +
                       $"size_x{index}={w}\r\n" +
                       $"size_y{index}={h}\r\n" +
                       $"llx{index}=0.000000\r\n" +
                       $"lly{index}=0.000000\r\n" +
                       $"urx{index}={w}\r\n" +
                       $"ury{index}={h}\r\n" +
                       $"actual_x{index}={w}\r\n" +
                       $"actual_y{index}={h}\r\n" +
                       $"area{index}={area}\r\n" +
                       $"Unit{index}=1";

        // 更新 userdef_num
        var newRaw = Regex.Replace(raw, @"userdef_num=\d+", $"userdef_num={index + 1}");

        // 在 [user] 段末尾追加（文件末尾）
        newRaw = newRaw.TrimEnd() + newEntry + "\r\n";

        File.WriteAllText(pmpPath, newRaw);
        return localName;
    }

    private static string? FindZwcadPaperBySize(string raw, double widthMm, double heightMm)
    {
        var pattern = $@"paper_local_name(\d+)=(.+?)[\r\n].*?size_x\1={widthMm:0.000000}[\r\n].*?size_y\1={heightMm:0.000000}";
        var match = Regex.Match(raw, pattern, RegexOptions.Singleline);
        if (match.Success)
            return match.Groups[2].Value.Trim();

        // 也尝试反向
        pattern = $@"paper_local_name(\d+)=(.+?)[\r\n].*?size_x\1={heightMm:0.000000}[\r\n].*?size_y\1={widthMm:0.000000}";
        match = Regex.Match(raw, pattern, RegexOptions.Singleline);
        if (match.Success)
            return match.Groups[2].Value.Trim();

        return null;
    }

#if !ZWCAD
    private static void RemovePia3(string pmpPath, string raw, string paperName)
    {
        var jsonStart = raw.IndexOf('{');
        if (jsonStart < 0) return;
        var root = JObject.Parse(raw.Substring(jsonStart));

        var size = root["data"]?["udm"]?["media"]?["size"] as JObject;
        var desc = root["data"]?["udm"]?["media"]?["description"] as JObject;
        if (size == null || desc == null) return;

        // 在 size 中找匹配 localized_name 的条目
        string? targetIndex = null;
        foreach (var prop in size.Properties())
        {
            var entry = prop.Value as JObject;
            if (entry?["localized_name"]?.Value<string>() == paperName)
            {
                targetIndex = prop.Name;
                break;
            }
        }
        if (targetIndex == null) return;

        // 删除 description 和 size 条目
        desc.Remove(targetIndex);
        size.Remove(targetIndex);

        var newJson = root.ToString(Formatting.Indented);
        File.WriteAllText(pmpPath, "PIAFILEVERSION_3.0,json\n" + newJson);
    }
#endif

    private static void RemovePia2(string pmpPath, string paperName)
    {
        try
        {
            var config = new PlotterConfiguration(pmpPath);
            config["mod"]?.Remove(paperName);
            config.Saves(pmpPath);
        }
        catch { }
    }

    private static void RemoveZwcadIni(string pmpPath, string raw, string paperName)
    {
        // 匹配以这个 local_name 开头的一整块（名字→末尾，含尾部 \r\n）
        var pattern = $@"\r\npaper_local_name\d+={Regex.Escape(paperName)}[\r\n].*?Unit\d+=1[\r\n]*";
        var newRaw = Regex.Replace(raw, pattern, "", RegexOptions.Singleline);
        if (newRaw == raw) return; // 没找到

        // 清理可能产生的多余空行
        newRaw = Regex.Replace(newRaw, @"[\r\n]{3,}", "\r\n\r\n");

        // 更新 userdef_num（少一个）
        var numMatch = Regex.Match(newRaw, @"userdef_num=(\d+)");
        if (numMatch.Success)
        {
            var count = int.Parse(numMatch.Groups[1].Value) - 1;
            newRaw = Regex.Replace(newRaw, @"userdef_num=\d+", $"userdef_num={Math.Max(0, count)}");
        }

        File.WriteAllText(pmpPath, newRaw);
    }
}
