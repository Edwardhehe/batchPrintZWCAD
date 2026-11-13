using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ZwcadBatchPlot;

/// <summary>
/// 三点图签的字段键。键名写入 JSON，必须保持稳定；显示名只用于界面与提示。
/// 前五项是本版新增的重点字段（比例/图幅/工程名称），其余沿用图框库的既有字段。
/// </summary>
public static class ThreePointFieldKeys
{
    public const string Title = "Title";
    public const string DrawingNumber = "DrawingNumber";
    public const string Scale = "Scale";
    public const string SheetSize = "SheetSize";
    public const string ProjectName = "ProjectName";
    public const string Date = "Date";
    public const string Revision = "Revision";
    public const string Phase = "Phase";
    public const string Info1 = "Info1";
    public const string Info2 = "Info2";

    /// <summary>入库向导的提问顺序：前两个必填，其余可回车跳过。</summary>
    public static readonly string[] CaptureOrder =
    {
        Title,
        DrawingNumber,
        Scale,
        SheetSize,
        ProjectName,
        Date,
        Revision,
        Phase,
        Info1,
        Info2
    };

    public static string Display(string key)
    {
        return key switch
        {
            Title => "图名",
            DrawingNumber => "图号",
            Scale => "比例",
            SheetSize => "图幅",
            ProjectName => "工程名称",
            Date => "日期",
            Revision => "版次",
            Phase => "阶段",
            Info1 => "信息1",
            Info2 => "信息2",
            _ => key
        };
    }

    public static bool IsRequired(string key)
    {
        return key == Title || key == DrawingNumber;
    }
}

/// <summary>单个锚点：入库时点取的文字内容（单行/多行均可）与其 WCS 插入点。</summary>
public sealed class ThreePointAnchor
{
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Height { get; set; }
}

/// <summary>
/// 三点图签定义：三个文字（单行/多行均可）作为识别基准，其余字段区域记录在模板 WCS 中，
/// 识别时由三点解出的变换映射到当前图纸。
/// <para>
/// 三点撑开成三角形时按完整仿射处理（支持旋转、镜像、非等比缩放）；
/// 三点共线时仿射欠定，自动降级为相似变换（等比缩放 + 旋转 + 平移，不支持镜像与非等比）。
/// </para>
/// </summary>
public sealed class ThreePointDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public List<ThreePointAnchor> Anchors { get; set; } = new();

    /// <summary>字段区域（模板 WCS）。键见 <see cref="ThreePointFieldKeys"/>。</summary>
    public Dictionary<string, LocalRectangle> Fields { get; set; } = new();

    /// <summary>入库时框选的图框范围（模板 WCS），用于试识别时定位与人工核对。</summary>
    public LocalRectangle SearchRegion { get; set; } = new();

    /// <summary>相似性误差上限（百分比）。三点应近似等比缩放，超过上限判为不同图签。</summary>
    public double SimilarityTolerancePercent { get; set; } = 8.0;

    /// <summary>
    /// 排序优先级：数字越小越优先。多个条目都能匹配同一图框时，
    /// 先按优先级、再按相似性误差择优（误差相差 0.5% 以内视为同级）。
    /// </summary>
    public int Priority { get; set; } = 1;

    /// <summary>是否允许镜像图签。默认关闭，避免镜像文字被误认。</summary>
    public bool AllowMirror { get; set; }

    public string PaperName { get; set; } = "";
    public double PaperWidthMm { get; set; }
    public double PaperHeightMm { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public bool IsComplete => Anchors.Count == 3
        && Anchors.All(x => !string.IsNullOrWhiteSpace(x.Text))
        && Fields.Count > 0;

    [JsonIgnore]
    public string AnchorSummary => Anchors.Count == 3
        ? string.Join(" / ", Anchors.Select(x => x.Text))
        : "未完成";

    /// <summary>
    /// 三个锚点是否接近共线。共线时识别会降级为相似变换（等比缩放 + 旋转 + 平移），
    /// 不再支持镜像与非等比缩放；界面据此给出提示，判定阈值与求解器保持一致。
    /// </summary>
    [JsonIgnore]
    public bool IsCollinearAnchors
    {
        get
        {
            if (Anchors.Count != 3)
            {
                return false;
            }

            var a = Anchors[0];
            var b = Anchors[1];
            var c = Anchors[2];
            var ux = b.X - a.X;
            var uy = b.Y - a.Y;
            var vx = c.X - a.X;
            var vy = c.Y - a.Y;
            var lenu = Math.Sqrt(ux * ux + uy * uy);
            var lenv = Math.Sqrt(vx * vx + vy * vy);
            if (lenu < 1e-9 || lenv < 1e-9)
            {
                return false;
            }

            return Math.Abs(ux * vy - uy * vx) / (lenu * lenv) < ThreePointAffine.CollinearSineThreshold;
        }
    }
}

public sealed class ThreePointLibrary
{
    public int Version { get; set; } = 1;
    public List<ThreePointDefinition> Items { get; set; } = new();
}

/// <summary>
/// 三点图签库的读写。与图框库分离成独立文件，老版本的 TitleBlockLibrary.json 不受影响。
/// </summary>
public static class ThreePointLibraryStore
{
    public static string DefaultPath =>
        Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "ThreePointLibrary.json");

    public static ThreePointLibrary Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            var backupOnly = path + ".bak";
            return File.Exists(backupOnly) ? Deserialize(backupOnly) : new ThreePointLibrary();
        }

        try
        {
            return Deserialize(path);
        }
        catch
        {
            var backupPath = path + ".bak";
            if (File.Exists(backupPath))
            {
                try
                {
                    return Deserialize(backupPath);
                }
                catch
                {
                }
            }

            throw new InvalidDataException("三点图签库文件损坏，且备份无法恢复: " + path);
        }
    }

    public static void Save(ThreePointLibrary library, string? path = null)
    {
        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("三点图签库路径无效: " + path);
        Directory.CreateDirectory(directory);
        var json = JsonConvert.SerializeObject(library, Formatting.Indented);
        WriteAtomically(path, json);
    }

    /// <summary>新增或按 Id/名称覆盖，返回 true 表示新增。</summary>
    public static bool Upsert(ThreePointDefinition definition, string? path = null)
    {
        var library = Load(path);
        var existing = library.Items.FirstOrDefault(x =>
            string.Equals(x.Id, definition.Id, StringComparison.OrdinalIgnoreCase))
            ?? library.Items.FirstOrDefault(x =>
                string.Equals(x.Name, definition.Name, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            library.Items.Add(definition);
            Save(library, path);
            return true;
        }

        existing.Name = definition.Name;
        existing.Enabled = definition.Enabled;
        existing.Anchors = definition.Anchors;
        existing.Fields = definition.Fields;
        existing.SearchRegion = definition.SearchRegion;
        existing.SimilarityTolerancePercent = definition.SimilarityTolerancePercent;
        existing.Priority = definition.Priority;
        existing.AllowMirror = definition.AllowMirror;
        existing.PaperName = definition.PaperName;
        existing.PaperWidthMm = definition.PaperWidthMm;
        existing.PaperHeightMm = definition.PaperHeightMm;
        existing.UpdatedAt = DateTime.Now;
        Save(library, path);
        return false;
    }

    public static bool Delete(string id, string? path = null)
    {
        var library = Load(path);
        var removed = library.Items.RemoveAll(x =>
            string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save(library, path);
        }

        return removed;
    }

    private static ThreePointLibrary Deserialize(string path)
    {
        var json = File.ReadAllText(path);
        var loaded = JsonConvert.DeserializeObject<ThreePointLibrary>(json) ?? new ThreePointLibrary();
        loaded.Items ??= new List<ThreePointDefinition>();
        foreach (var item in loaded.Items)
        {
            item.Anchors ??= new List<ThreePointAnchor>();
            item.Fields ??= new Dictionary<string, LocalRectangle>();
            if (item.SimilarityTolerancePercent <= 0 || item.SimilarityTolerancePercent > 100)
            {
                item.SimilarityTolerancePercent = 8.0;
            }

            if (item.Priority <= 0)
            {
                item.Priority = 1;
            }
        }

        return loaded;
    }

    private static void WriteAtomically(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        var tempPath = fullPath + ".tmp";
        var backupPath = fullPath + ".bak";
        File.WriteAllText(tempPath, contents);
        try
        {
            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, backupPath, true);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
