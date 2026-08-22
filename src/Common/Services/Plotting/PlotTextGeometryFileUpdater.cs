using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZwcadBatchPlot;

/// <summary>
/// 绘图仪文件中的文字输出字段转换。这里只处理字符串，不接触用户文件；宿主安装器负责限定
/// 只能写插件自有 LA 文件。独立纯函数便于用随包 PC5/PIA3 模板做双向回归。
/// </summary>
internal static class PlotTextGeometryFileUpdater
{
    public static string UpdateZwcadPc5(string original, bool convertToGeometry, out bool changed)
    {
        var expected = "truetype_as_text=" + (convertToGeometry ? "0" : "1");
        var updated = Regex.IsMatch(original, @"(?im)^truetype_as_text=\d+\s*$")
            ? Regex.Replace(original, @"(?im)^truetype_as_text=\d+\s*$", expected)
            : new Regex(@"(?im)^\[res_color_mem\]\s*$").Replace(
                original,
                "$0\r\n" + expected,
                1);
        changed = !string.Equals(original, updated, StringComparison.Ordinal);
        return updated;
    }

    public static bool TryUpdatePia3(
        string original,
        bool convertToGeometry,
        bool updateAllAsGeometry,
        out string updated,
        out bool changed)
    {
        updated = original;
        changed = false;
        if (!original.StartsWith("PIAFILEVERSION_3.0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var jsonStart = original.IndexOf('{');
        if (jsonStart < 0)
        {
            return false;
        }

        var root = JObject.Parse(original.Substring(jsonStart));
        var meta = root["data"]?["meta"] as JObject
            ?? throw new InvalidOperationException("PIA3 缺少 meta 节点。");
        var truetypeAsText = !convertToGeometry;
        if (meta["truetype_as_text"]?.Value<bool>() != truetypeAsText)
        {
            meta["truetype_as_text"] = truetypeAsText;
            changed = true;
        }

        if (updateAllAsGeometry && root["data"]?["custom"] is JObject custom)
        {
            foreach (var property in custom.Properties())
            {
                if (property.Value is not JObject item
                    || !string.Equals(item["name"]?.Value<string>(), "All_As_Geometry", StringComparison.OrdinalIgnoreCase)
                    || item["value"]?.Value<bool>() == convertToGeometry)
                {
                    continue;
                }

                item["value"] = convertToGeometry;
                changed = true;
            }
        }

        if (changed)
        {
            updated = "PIAFILEVERSION_3.0,json\n" + root.ToString(Formatting.Indented);
        }
        return true;
    }
}
