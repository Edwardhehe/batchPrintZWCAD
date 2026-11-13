using System;
using System.Collections.Generic;

namespace ZwcadBatchPlot;

/// <summary>
/// 一个可用的签名 / 印章资源（图片）。
/// <para>
/// 只描述「资源本身」，不含位置：位置属于图框（<see cref="SignatureSlot"/>），
/// 同一个人签名可以在 A0/A1/A2 图框的不同槽位复用，也可以在多种图框里复用。
/// </para>
/// </summary>
public sealed class SignatureItem
{
    /// <summary>稳定标识，被槽位与预设引用；改名不改此值。</summary>
    public string Id { get; set; } = "";

    /// <summary>Signature=手写签名；Stamp=印章（公章/出图章/注册章）。</summary>
    public string Kind { get; set; } = "Signature";

    /// <summary>显示名（人名或印章名）。</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 相对所属类型图片目录（<see cref="SignatureLibraryStore.GetDirectory"/> 返回的
    /// 插件目录下「签名」/「签章」文件夹）的文件名。
    /// 存相对路径而非绝对路径：换机器 / 换用户名后签名库整体拷走即可用。
    /// </summary>
    public string ImageFile { get; set; } = "";

    /// <summary>绘制不透明度 0~1。公章常压半透明（0.85）以露出下面的图线。</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>导入时是否已做去白底（记录状态，便于界面提示重做）。</summary>
    public bool BackgroundRemoved { get; set; }

    /// <summary>
    /// 最近一次「图片处理」用的参数（去底色 / 修剪 / 尺寸）。null = 没处理过。
    /// <para>
    /// 存下来是为了下次打开图片处理时滑块停在原位，并且处理永远从原图备份重算
    /// （<see cref="SignatureLibraryStore.GetProcessingBasePath"/>），反复调参不会逐次劣化。
    /// </para>
    /// </summary>
    public ImageProcessSettings? ImageProcess { get; set; }

    /// <summary>导入时的源文件名，仅供界面展示。</summary>
    public string SourceFileName { get; set; } = "";

    /// <summary>
    /// 有效期起（yyyy-MM-dd，空=不限）。过期后出图会跳过该签章并告警，
    /// 用于「某项目专用章」这类需要自动失效的场景。
    /// </summary>
    public string ValidFrom { get; set; } = "";

    /// <summary>有效期止（yyyy-MM-dd，空=不限）。</summary>
    public string ValidTo { get; set; } = "";

    /// <summary>
    /// 印章类型：<c>注册章</c> / <c>出图章</c>（仅 Kind=Stamp 时有意义）。
    /// 与 Kind 的区别：Kind 决定存哪个文件夹、是否算「签章」；StampType 是业务分类，只用于界面与筛选。
    /// </summary>
    public string StampType { get; set; } = "";

    /// <summary>
    /// 印章标注宽度的<b>镜像</b>，真正的尺寸存在 <see cref="ImageProcess"/> 里
    /// （<see cref="ImageProcessSettings.PhysicalWidthMm"/>）。
    /// <para>
    /// 之所以还留着这两个字段：① 旧版库文件里只有它们，删掉会读不回来；
    /// ② 早期版本插件（或外部脚本）可能仍在读这两个字段。保存时由
    /// <see cref="StampLibraryForm"/> 从统一尺寸回写镜像值，读取一律走
    /// <see cref="TryGetEffectiveSizeMm"/>，不要直接读这两个字段做出图决策。
    /// </para>
    /// </summary>
    public double WidthMm { get; set; }

    /// <summary>
    /// 印章标注高度的镜像（含义同 <see cref="WidthMm"/>）：0 = 由宽度与图片纵横比推出。
    /// 两者同时填写时取「两者都不超出」的较小比例等比缩放，印章始终不变形。
    /// </summary>
    public double HeightMm { get; set; }

    /// <summary>
    /// 这枚签章应该打成多大（毫米）。0 / 0 表示「跟随图框槽位等比内接」。
    /// <para>
    /// 取值顺序：<see cref="ImageProcess"/> 的标注尺寸 → 旧字段 <see cref="WidthMm"/> /
    /// <see cref="HeightMm"/>（兼容没经过图片处理的老条目）→ 都没有就返回 false。
    /// 出图（<see cref="PdfStampService"/>）只认这一个入口，避免「图片处理里填了尺寸、
    /// 出图却不认」这类两处数据各行其是的问题。
    /// </para>
    /// </summary>
    public bool TryGetEffectiveSizeMm(out double widthMm, out double heightMm)
    {
        if (ImageProcess != null && ImageProcess.TryGetDeclaredSizeMm(out widthMm, out heightMm))
        {
            return true;
        }

        widthMm = WidthMm > 0 ? WidthMm : 0;
        heightMm = HeightMm > 0 ? HeightMm : 0;
        return widthMm > 0 || heightMm > 0;
    }

    /// <summary>
    /// 写入尺寸（唯一入口）：同时写 <see cref="ImageProcess"/> 的标注尺寸与旧字段镜像，
    /// 保证「新读法」「旧读法」看到的是同一个数。传 0 / 0 表示恢复「跟随槽位」。
    /// </summary>
    public void SetEffectiveSizeMm(double widthMm, double heightMm)
    {
        var width = widthMm > 0 ? widthMm : 0;
        var height = heightMm > 0 ? heightMm : 0;

        var settings = ImageProcess;
        if (settings == null)
        {
            settings = new ImageProcessSettings();
            ImageProcess = settings;
        }

        settings.PhysicalWidthMm = width;
        settings.PhysicalHeightMm = height;

        WidthMm = width;
        HeightMm = height;
    }

    /// <summary>
    /// 授权码 0~999999，0 表示无需授权。
    /// <para>当前仅记录与展示，不参与出图校验——本插件没有授权服务端，别把它当成防伪手段。</para>
    /// </summary>
    public int AuthCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>当前日期是否落在有效期内。日期串解析失败按「不限」处理，不因为手改配置就整批不出章。</summary>
    public bool IsValidOn(DateTime day)
    {
        if (DateTime.TryParse(ValidFrom, out var from) && day.Date < from.Date)
        {
            return false;
        }

        if (DateTime.TryParse(ValidTo, out var to) && day.Date > to.Date)
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// 槽位里的一个位置（一个格子）。
/// <para>
/// 与 <see cref="SignatureSlot"/> 拆开是因为同一个人可能要在图上签多处（图签栏 + 会签栏，
/// 或一张图上两个图签）：签名与人员绑定属于槽位，位置属于位置，两者数量不必相等。
/// </para>
/// </summary>
public sealed class SignatureSlotRegion
{
    /// <summary>相对图框锚点角的区域，语义与 <see cref="SignatureSlot.CadRegion"/> 一致。</summary>
    public LocalRectangle CadRegion { get; set; } = new();

    /// <summary>纸张右边缘 → 该位置右边缘（毫米）。</summary>
    public double RightInsetMm { get; set; }

    /// <summary>纸张下边缘 → 该位置下边缘（毫米）。</summary>
    public double BottomInsetMm { get; set; }

    /// <summary>位置宽度（毫米）。</summary>
    public double WidthMm { get; set; }

    /// <summary>位置高度（毫米）。</summary>
    public double HeightMm { get; set; }

    public SignatureSlotRegion Clone()
    {
        return new SignatureSlotRegion
        {
            CadRegion = new LocalRectangle
            {
                MinX = CadRegion?.MinX ?? 0d,
                MinY = CadRegion?.MinY ?? 0d,
                MaxX = CadRegion?.MaxX ?? 0d,
                MaxY = CadRegion?.MaxY ?? 0d,
                ActualWidth = CadRegion?.ActualWidth ?? 0d,
                ActualHeight = CadRegion?.ActualHeight ?? 0d,
                CornerPoints = CadRegion?.CornerPoints,
            },
            RightInsetMm = RightInsetMm,
            BottomInsetMm = BottomInsetMm,
            WidthMm = WidthMm,
            HeightMm = HeightMm,
        };
    }
}

/// <summary>
/// 图框上的一个签章槽位：位置 + 默认绑定的签名。
/// <para>
/// 两套坐标同时存：<see cref="CadRegion"/> 给未来「DWG 内嵌」路线与界面校准用，
/// <see cref="RightInsetMm"/> 等给 PDF 后处理用。之所以不运行时互算，是因为打印窗口会被
/// ApplyLearnedWindowFix、留白模式、居中缩放多次改写，从 DWG 坐标反推纸面 mm 不可靠。
/// 录入时一次框选即同时算好两份。
/// </para>
/// </summary>
public sealed class SignatureSlot
{
    /// <summary>槽位名，如「设计」「审核」「出图章」。同一图框内唯一。</summary>
    public string SlotName { get; set; } = "";

    /// <summary>相对图框锚点角的区域，语义与 TitleRegion 完全一致（同 CoordinateMode / ExtendAnchor）。</summary>
    public LocalRectangle CadRegion { get; set; } = new();

    /// <summary>纸张右边缘 → 槽位右边缘的距离（毫米）。</summary>
    public double RightInsetMm { get; set; }

    /// <summary>纸张下边缘 → 槽位下边缘的距离（毫米）。</summary>
    public double BottomInsetMm { get; set; }

    /// <summary>槽位宽度（毫米）。</summary>
    public double WidthMm { get; set; }

    /// <summary>槽位高度（毫米）。</summary>
    public double HeightMm { get; set; }

    /// <summary>默认绑定的签名 Id；空=该槽位默认不盖。</summary>
    public string DefaultSignatureId { get; set; } = "";

    /// <summary>
    /// 「自动读取」：出图时不看 <see cref="DefaultSignatureId"/>，而是从签名库自动取同类签名
    /// （唯一的那个手写签名 / 唯一的那个印章）。
    /// <para>
    /// 用于「图框只配一次、换人不用改图框」的场景：签名库里只维护一个人的签名时，
    /// 勾上它，换人只需换库里的图片，图框库不用动。库里同类签名多于一个时**不做猜测**
    /// （猜错等于替别人签名），改为跳过并写告警日志，让用户手工指定。
    /// </para>
    /// </summary>
    public bool AutoPickSignature { get; set; }

    /// <summary>
    /// 该槽位的全部位置（一个人可同时在图上签多处）。为空时回落到旧版单位置字段
    /// （<see cref="CadRegion"/> / <see cref="RightInsetMm"/> 等），保证旧图框库继续可用。
    /// </summary>
    public List<SignatureSlotRegion> Regions { get; set; } = new();

    /// <summary>槽位内附加旋转（度，逆时针）。印章常用 0，个别竖向签名栏用 90。</summary>
    public double Rotation { get; set; }

    /// <summary>图片按槽位「等比缩放内接」还是「拉伸铺满」。默认等比内接，避免章被压扁。</summary>
    public bool StretchToFill { get; set; }

    /// <summary>
    /// 取全部有效位置：优先 <see cref="Regions"/>，为空则把旧版单位置字段包装成一个位置返回。
    /// 不回写字段——反序列化出来的对象被打印线程读取时不该有副作用。
    /// </summary>
    public List<SignatureSlotRegion> GetRegions()
    {
        var result = new List<SignatureSlotRegion>();
        if (Regions != null && Regions.Count > 0)
        {
            foreach (var region in Regions)
            {
                if (region != null)
                {
                    result.Add(region);
                }
            }

            return result;
        }

        if (CadRegion != null && CadRegion.HasArea() && WidthMm > 0.1 && HeightMm > 0.1)
        {
            result.Add(new SignatureSlotRegion
            {
                CadRegion = new LocalRectangle
                {
                    MinX = CadRegion.MinX,
                    MinY = CadRegion.MinY,
                    MaxX = CadRegion.MaxX,
                    MaxY = CadRegion.MaxY,
                    ActualWidth = CadRegion.ActualWidth,
                    ActualHeight = CadRegion.ActualHeight,
                    CornerPoints = CadRegion.CornerPoints,
                },
                RightInsetMm = RightInsetMm,
                BottomInsetMm = BottomInsetMm,
                WidthMm = WidthMm,
                HeightMm = HeightMm,
            });
        }

        return result;
    }

    public bool IsConfigured()
    {
        if (string.IsNullOrWhiteSpace(SlotName))
        {
            return false;
        }

        foreach (var region in GetRegions())
        {
            if (region.WidthMm > 0.1 && region.HeightMm > 0.1)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>一组槽位 → 签名 Id 的映射，供打印时一键套用（如「张三签批 + 单位出图章」）。</summary>
public sealed class SignaturePreset
{
    public string Name { get; set; } = "";

    /// <summary>槽位名 → 签名 Id。</summary>
    public Dictionary<string, string> SlotMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>签名库文件（%APPDATA%\AcadBatchPlot\Signatures.json）。</summary>
public sealed class SignatureLibrary
{
    public int Version { get; set; } = 1;

    public List<SignatureItem> Items { get; set; } = new();

    public List<SignaturePreset> Presets { get; set; } = new();

    public SignatureItem? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        foreach (var item in Items)
        {
            if (string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }
}
