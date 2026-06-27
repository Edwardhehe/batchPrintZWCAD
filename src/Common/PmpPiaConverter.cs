using System;
using System.IO;
using Newtonsoft.Json.Linq;
using PiaNO;

namespace ZwcadBatchPlot;

/// <summary>
/// 读取 PC3/PMP 文件头判断 PIA 版本，并可在安装时将 PIA 3.0 转为 PIA 2.0。
/// </summary>
public static class PmpPiaConverter
{
    /// <summary>检查目标 AutoCAD 的 DWG To PDF.pc3 是否 PIA 3.0 JSON。</summary>
    public static bool IsCadPia3Compatible()
    {
        try
        {
            var plottersDir = AcadPlotterInstaller.GetPlottersDirectory();
            if (string.IsNullOrWhiteSpace(plottersDir)) return false;

            var dwgPc3 = Path.Combine(plottersDir, "DWG To PDF.pc3");
            if (!File.Exists(dwgPc3)) return false;

            var raw = File.ReadAllText(dwgPc3);
            return raw.StartsWith("PIAFILEVERSION_3.0", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>将 PIA 3.0 JSON 转为 PIA 2.0 压缩格式写入目标路径。失败抛异常。</summary>
    public static void ConvertToPia2(string pia3Path, string pia2Path)
    {
        if (!File.Exists(pia3Path)) return;
        var raw = File.ReadAllText(pia3Path);
        var jsonStart = raw.IndexOf('{');
        if (jsonStart < 0) return;

        var root = JObject.Parse(raw.Substring(jsonStart));
        var config = new PlotterConfiguration();
        config.Header = new PiaHeader("PIAFILEVERSION_2.0,PC3VER1,compress     \0\0\0\0\0\0\0\0\0");
        JObjectToNode(root, config);
        config.Saves(pia2Path);
    }

    private static void JObjectToNode(JObject json, PiaNode parent)
    {
        foreach (var prop in json.Properties())
        {
            if (prop.Value is JObject child)
            {
                var childNode = parent.Add(prop.Name);
                JObjectToNode(child, childNode);
            }
            else if (prop.Value is JArray arr)
            {
                var childNode = parent.Add(prop.Name);
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JObject arrObj)
                    {
                        var item = childNode.Add(i.ToString());
                        JObjectToNode(arrObj, item);
                    }
                }
            }
            else if (prop.Value is JValue jVal)
            {
                var str = jVal.Value?.ToString() ?? "";
                if (jVal.Value is bool b)
                    str = b ? "TRUE" : "FALSE";
                parent.SetValue(prop.Name, str);
            }
        }
    }
}
