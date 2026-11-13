using System;

namespace ZwcadBatchPlot;

/// <summary>
/// 图框上的一个二维码位置：内容模板 + 纸面位置。
/// <para>
/// 与 <see cref="SignatureSlot"/> 分开建模，而不是给槽位加一个「类型 = 二维码」：
/// 槽位的解析链路是「绑定签名 → 校验图片 → 校验有效期 → 取图片尺寸」，
/// 二维码既不查签名库也没有有效期，硬塞进去得在每一段加分支，反而更容易出错。
/// 两者只在最后一步汇合——都产出 <see cref="StampPlacement"/>，交同一次 PDF 重写。
/// </para>
/// <para>
/// 两套坐标同时存，语义与 <see cref="SignatureSlot"/> 完全一致：
/// <see cref="CadRegion"/> 给界面校准与未来「DWG 内嵌」路线用，
/// <see cref="RightInsetMm"/> 等给 PDF 后处理用。不运行时互算的原因见槽位那段的注释——
/// 打印窗口会被留白模式、居中缩放多次改写，从 DWG 坐标反推纸面毫米不可靠。
/// </para>
/// </summary>
public sealed class TitleBlockQrCode
{
    /// <summary>位置名，只用于日志与界面展示。</summary>
    public string Name { get; set; } = "";

    /// <summary>是否启用。关掉后该位置整张图都不出二维码。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 内容模板，支持占位符：{图号} {图名} {版本} {日期} {阶段} {文件名} {布局} {块名}。
    /// 未知占位符原样保留（不吞掉，便于用户看出自己写错了）。默认只出图号。
    /// </summary>
    public string ContentTemplate { get; set; } = "{图号}";

    /// <summary>相对图框锚点角的区域，语义与 <see cref="TitleBlockDefinition.TitleRegion"/> 一致。</summary>
    public LocalRectangle CadRegion { get; set; } = new();

    /// <summary>纸张右边缘 → 二维码右边缘的距离（毫米）。</summary>
    public double RightInsetMm { get; set; }

    /// <summary>纸张下边缘 → 二维码下边缘的距离（毫米）。</summary>
    public double BottomInsetMm { get; set; }

    /// <summary>二维码边长（毫米）。正方形，不符合比例时按较小边取。</summary>
    public double WidthMm { get; set; }

    /// <summary>二维码边长（毫米）。</summary>
    public double HeightMm { get; set; }

    /// <summary>槽位内附加旋转（度，逆时针）。图签竖排栏位才需要。</summary>
    public double Rotation { get; set; }

    /// <summary>
    /// 纠错等级：L(7%) / M(15%) / Q(25%) / H(30%)。默认 M——
    /// 图纸会折、会复印、会盖在别的东西上，L 太脆；H 在同尺寸下模块数翻倍，25mm 内反而更难扫。
    /// </summary>
    public string ErrorCorrection { get; set; } = "M";

    /// <summary>是否已有可用的纸面位置（宽高都大于 0.1mm 才算框好了）。</summary>
    public bool IsConfigured()
    {
        return WidthMm > 0.1 && HeightMm > 0.1;
    }
}
