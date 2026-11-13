using System;
using System.Collections.Generic;
using System.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ZwcadBatchPlot;

/// <summary>一次盖章请求：把某张图片画到 PDF 页面的指定位置。</summary>
public sealed class StampPlacement
{
    /// <summary>签名/印章图片绝对路径（已确认存在）。</summary>
    public string ImagePath { get; set; } = "";

    /// <summary>纸张右边缘 → 槽位右边缘（毫米）。</summary>
    public double RightInsetMm { get; set; }

    /// <summary>纸张下边缘 → 槽位下边缘（毫米）。</summary>
    public double BottomInsetMm { get; set; }

    /// <summary>槽位宽（毫米）。</summary>
    public double WidthMm { get; set; }

    /// <summary>槽位高（毫米）。</summary>
    public double HeightMm { get; set; }

    /// <summary>槽位内附加旋转（度，逆时针为正；视觉方向）。</summary>
    public double Rotation { get; set; }

    /// <summary>是否拉伸铺满槽位；false=等比缩放内接。</summary>
    public bool StretchToFill { get; set; }

    /// <summary>
    /// 印章标注宽度（毫米），0 = 不限制、按槽位等比内接。
    /// 大于 0 时按该宽度缩放（仍受槽位大小约束，不会溢出格子），用于让同类章在不同图幅上大小一致。
    /// </summary>
    public double PreferredWidthMm { get; set; }

    /// <summary>
    /// 印章标注高度（毫米），0 = 由宽度与图片纵横比推出。
    /// 与 <see cref="PreferredWidthMm"/> 同时大于 0 时取两者较小的缩放，保证不变形且都不超出。
    /// </summary>
    public double PreferredHeightMm { get; set; }

    /// <summary>槽位名，仅用于日志与告警。</summary>
    public string SlotName { get; set; } = "";

    /// <summary>
    /// 标注尺寸的来源，仅用于日志（physical / pixel / legacy / slot）。
    /// <para>
    /// 「设了尺寸但出图还是格子大小」这类问题，只看日志里的最终尺寸分不清是没读到、
    /// 还是读到了被槽位限制住，标一个来源就能一眼定位。
    /// </para>
    /// </summary>
    public string SizeSource { get; set; } = "";

    /// <summary>
    /// 是否属于「印章」类（出图章/注册师章/竣工图章）。false 表示手写签名类。
    /// 打印面板的「打印签名 / 打印印章」按此分派是否绘制。
    /// </summary>
    public bool IsStamp { get; set; }
}

/// <summary>
/// PDF 后处理盖章：在已出好的 PDF 页面上叠加签名/印章图片。
/// <para>
/// 为什么在 PDF 层做而不是往 DWG 插光栅图：源图零改动（无撤销失败污染、无需处理
/// RasterImage 的外部引用与透明度差异），而且与批打/多进程天然兼容——每个进程盖自己的产物。
/// 代价是 DWF/DWG 直出覆盖不到，调用方据此禁用勾选。
/// </para>
/// <para>
/// 实现沿用 <see cref="PdfAutoRotateService"/> 已验证的范式：新建文档 + 跨文档拷页 +
/// XGraphics 追加绘制 + 产物校验 + 失败不替换。PDFsharp 1.5 在 CAD 进程内跨文档拷页
/// 有内容流被截断的实测案例（生产 31/31 白页），所以校验不是可选项。
/// </para>
/// </summary>
public static class PdfStampService
{
    /// <summary>1 毫米 = 2.834645669 点（PDF 用户空间单位）。</summary>
    private const double PointsPerMillimeter = 72.0 / 25.4;

    /// <summary>产物内容体量允许的最低比例：盖章只增不减，低于 90% 视为内容丢失。</summary>
    private const double MinimumContentRatio = 0.9;

    /// <summary>
    /// 给 <paramref name="pdfPath"/> 全部页面盖章，原地替换（原子写）。
    /// <returns>true=已盖章；false=未改动（原文件保持有效），<paramref name="message"/> 说明原因。</returns>
    /// </summary>
    public static bool TryApply(string pdfPath, IReadOnlyList<StampPlacement> placements, out string message)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
        {
            message = "PDF 不存在，跳过盖章。";
            return false;
        }

        if (!PdfAutoRotateService.IsPdfPath(pdfPath))
        {
            message = "不是 PDF 文件，跳过盖章。";
            return false;
        }

        var usable = new List<StampPlacement>();
        if (placements != null)
        {
            foreach (var placement in placements)
            {
                if (placement == null || placement.WidthMm <= 0.1 || placement.HeightMm <= 0.1)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(placement.ImagePath) || !File.Exists(placement.ImagePath))
                {
                    // 单个签名图丢失不该拖垮整批：跳过它，其余照盖。
                    PluginLog.Timing($"[签章] 槽位「{placement.SlotName}」的图片不存在，已跳过：{placement.ImagePath}");
                    continue;
                }

                usable.Add(placement);
            }
        }

        if (usable.Count == 0)
        {
            message = "没有可用的签章图片。";
            return false;
        }

        var fullPath = Path.GetFullPath(pdfPath);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".stamp-tmp";
        try
        {
            var sourceBytes = PdfAutoRotateService.MeasureContentBytes(fullPath);

            // 图片只加载一次：同一批里同一个签名会在多页/多次调用间复用。
            var images = new Dictionary<string, XImage>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var placement in usable)
                {
                    if (!images.ContainsKey(placement.ImagePath))
                    {
                        images[placement.ImagePath] = XImage.FromFile(placement.ImagePath);
                    }
                }

                using (var source = PdfReader.Open(fullPath, PdfDocumentOpenMode.Import))
                using (var output = new PdfDocument())
                {
                    var pageIndex = 0;
                    foreach (var sourcePage in source.Pages)
                    {
                        pageIndex++;
                        var page = output.AddPage(sourcePage);

                        // 视觉尺寸：/Rotate 90/270 时页面宽高在视觉上互换。
                        var rotate = ((page.Rotate % 360) + 360) % 360;
                        var mediaWidth = page.Width.Point;
                        var mediaHeight = page.Height.Point;
                        var visualWidth = rotate is 90 or 270 ? mediaHeight : mediaWidth;
                        var visualHeight = rotate is 90 or 270 ? mediaWidth : mediaHeight;

                        using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                        // PdfSharp 1.5 的 XGraphics 不处理 /Rotate（实测：在 /Rotate=90 的页上
                        // 按视觉坐标画会落到页面外），必须自己补一个补偿矩阵，之后坐标才是「视觉坐标」。
                        var rotateMatrix = PageTransform(rotate, mediaWidth, mediaHeight);
                        if (rotate != 0)
                        {
                            graphics.MultiplyTransform(rotateMatrix);
                        }

                        foreach (var placement in usable)
                        {
                            DrawPlacement(graphics, images[placement.ImagePath], placement, pageIndex, visualWidth, visualHeight);
                        }
                    }

                    if (output.PageCount == 0)
                    {
                        message = "源 PDF 没有有效页面，跳过盖章。";
                        return false;
                    }

                    output.Save(temporaryPath);
                }
            }
            finally
            {
                foreach (var image in images.Values)
                {
                    image.Dispose();
                }
            }

            VerifyTemporary(temporaryPath, sourceBytes);
            // 原子换入：盖章是"可选增强"，宁可这张没盖，也不能把好 PDF 换成坏 PDF。
            AtomicFileWriter.ReplaceFile(temporaryPath, fullPath);
            message = $"已盖章 {usable.Count} 处。";
            return true;
        }
        catch (Exception ex)
        {
            PluginLog.Error("签章", $"PDF 盖章失败，保留原文件：{Path.GetFileName(pdfPath)} — {ex.Message}", ex);
            message = "盖章失败：" + ex.Message;
            return false;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // 临时文件删不掉不影响结果，下次覆盖式写入会再试。
                }
            }
        }
    }

    /// <summary>
    /// 各 /Rotate 值下「视觉坐标 → XGraphics 绘制坐标」的补偿矩阵。
    /// <para>
    /// PdfSharp 1.5 的 XGraphics 不处理页面 /Rotate（标定实测：/Rotate=90 的页上按视觉坐标画，
    /// 内容落到页面外，因为 XGraphics 的 y 范围仍是 MediaBox 高、不是视觉高）。
    /// 矩阵推导：默认变换把绘制坐标 (gx,gy) 映射为用户空间 (gx, mediaH-gy)；
    /// 视觉坐标 (vx,vy) 与用户空间的关系由旋转方向决定，两者复合即得下表。
    /// </para>
    /// <para>标定结果（A3 横向，槽位距右 40mm、距下 30mm、60×20mm）：/Rotate=0 与 90 实测
    /// 落位误差均 ≤0.3mm（线宽与像素取整量级）。</para>
    /// </summary>
    private static XMatrix PageTransform(int rotate, double mediaWidth, double mediaHeight)
    {
        return rotate switch
        {
            // 视觉左上 = 用户空间左下；视觉 +x → uy 增大，视觉 +y → ux 增大
            90 => new XMatrix(0, -1, 1, 0, 0, mediaHeight),
            // 视觉左上 = 用户空间右下
            180 => new XMatrix(-1, 0, 0, -1, mediaWidth, mediaHeight),
            // 视觉左上 = 用户空间右上
            270 => new XMatrix(0, 1, -1, 0, mediaWidth, 0),
            _ => XMatrix.Identity,
        };
    }

    /// <summary>
    /// 画一处签章。坐标约定：调用方已在 /Rotate≠0 时套好补偿矩阵，因此这里一律按
    /// 「视觉坐标、左上原点、y 向下」计算——「距纸张右边 X 毫米」= visualWidth - X - 宽。
    /// </summary>
    private static void DrawPlacement(
        XGraphics graphics,
        XImage image,
        StampPlacement placement,
        int pageIndex,
        double visualWidth,
        double visualHeight)
    {
        var width = placement.WidthMm * PointsPerMillimeter;
        var height = placement.HeightMm * PointsPerMillimeter;
        var x = visualWidth - placement.RightInsetMm * PointsPerMillimeter - width;
        var y = visualHeight - placement.BottomInsetMm * PointsPerMillimeter - height;

        if (placement.StretchToFill)
        {
            DrawRotated(graphics, image, placement, x, y, width, height);
            PluginLog.Timing(
                $"[签章] 第{pageIndex}页 槽位={placement.SlotName} "
                + $"mm=({x / PointsPerMillimeter:F1},{y / PointsPerMillimeter:F1}) "
                + $"尺寸={placement.WidthMm:F1}x{placement.HeightMm:F1} 拉伸"
                // 拉伸模式按定义铺满槽位，标注尺寸不参与计算——写进日志免得用户以为设了没用。
                + (placement.PreferredWidthMm > 0.1 || placement.PreferredHeightMm > 0.1
                    ? $"（标注 {placement.PreferredWidthMm:F1}x{placement.PreferredHeightMm:F1} 被拉伸忽略）"
                    : ""));
            return;
        }

        // 等比内接：按图片纵横比缩到槽位内并居中，避免公章被压扁。
        var imageWidth = image.PixelWidth > 0 ? image.PointWidth : width;
        var imageHeight = image.PixelHeight > 0 ? image.PointHeight : height;
        var scale = Math.Min(width / Math.Max(imageWidth, 0.01), height / Math.Max(imageHeight, 0.01));

        // 印章库填了「印章宽度 / 印章高度」时按标注尺寸缩：同类章在不同大小的格子里能保持一致的物理尺寸。
        // 两个都填时取较小的那个比例（等比，不拉伸），格子比标注小时仍按格子内接，永不溢出。
        scale = ApplyPreferredSize(placement, imageWidth, imageHeight, scale);
        var drawWidth = imageWidth * scale;
        var drawHeight = imageHeight * scale;
        var drawX = x + (width - drawWidth) / 2.0;
        var drawY = y + (height - drawHeight) / 2.0;

        DrawRotated(graphics, image, placement, drawX, drawY, drawWidth, drawHeight);
        PluginLog.Timing(
            $"[签章] 第{pageIndex}页 槽位={placement.SlotName} "
            + $"mm=({drawX / PointsPerMillimeter:F1},{drawY / PointsPerMillimeter:F1}) "
            + $"尺寸={drawWidth / PointsPerMillimeter:F1}x{drawHeight / PointsPerMillimeter:F1} 等比"
            + (placement.PreferredWidthMm > 0.1 || placement.PreferredHeightMm > 0.1
                ? $"（标注 {placement.PreferredWidthMm:F1}x{placement.PreferredHeightMm:F1}"
                    + $" 来源={placement.SizeSource}）"
                : $"（来源={placement.SizeSource}）"));
    }

    /// <summary>
    /// 把印章库的「印章宽度 / 印章高度」标注换算成缩放系数。
    /// <para>
    /// 保持等比：填了宽度就按宽度定比例、高度随之推导；只填高度则反之；
    /// 两个都填时取较小的比例，使宽高都落在标注以内（图片纵横比与标注不一致时不会变形）。
    /// 最终结果再与格子给出的 <paramref name="slotScale"/> 取小，保证不超出槽位。
    /// </para>
    /// </summary>
    private static double ApplyPreferredSize(
        StampPlacement placement,
        double imageWidth,
        double imageHeight,
        double slotScale)
    {
        var scale = slotScale;
        if (placement.PreferredWidthMm > 0.1)
        {
            scale = Math.Min(scale,
                placement.PreferredWidthMm * PointsPerMillimeter / Math.Max(imageWidth, 0.01));
        }

        if (placement.PreferredHeightMm > 0.1)
        {
            scale = Math.Min(scale,
                placement.PreferredHeightMm * PointsPerMillimeter / Math.Max(imageHeight, 0.01));
        }

        return scale;
    }

    /// <summary>
    /// 标注尺寸是从哪儿读到的，只写进日志。取值：
    /// <c>physical</c>=图片处理／编辑印章里显式填的毫米；<c>legacy</c>=旧库条目的
    /// WidthMm/HeightMm 字段；<c>slot</c>=没有标注，完全跟随图框槽位。
    /// </summary>
    private static string DescribeSizeSource(SignatureItem item, double widthMm, double heightMm)
    {
        if (widthMm <= 0.1 && heightMm <= 0.1)
        {
            return "slot";
        }

        var process = item.ImageProcess;
        return process != null && (process.PhysicalWidthMm > 0.1 || process.PhysicalHeightMm > 0.1)
            ? "physical"
            : "legacy";
    }

    private static void DrawRotated(
        XGraphics graphics,
        XImage image,
        StampPlacement placement,
        double x,
        double y,
        double width,
        double height)
    {
        if (Math.Abs(placement.Rotation) < 0.01)
        {
            graphics.DrawImage(image, x, y, width, height);
            return;
        }

        // 绕图片中心旋转；XGraphics 的旋转为正角度顺时针（屏幕坐标 y 向下），
        // 故逆时针语义要取负。
        var centerX = x + width / 2.0;
        var centerY = y + height / 2.0;
        var state = graphics.Save();
        try
        {
            graphics.RotateAtTransform(-placement.Rotation, new XPoint(centerX, centerY));
            graphics.DrawImage(image, x, y, width, height);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    /// <summary>校验产物：能重新打开、页数一致（且非零）、内容体量不低于源。
    /// 源内容测不出（返回负值）时只做前两项，不误伤。</summary>
    private static void VerifyTemporary(string temporaryPath, long sourceContentBytes)
    {
        var pageCount = 0;
        using (var verify = PdfReader.Open(temporaryPath, PdfDocumentOpenMode.Import))
        {
            pageCount = verify.PageCount;
        }

        if (pageCount == 0)
        {
            throw new InvalidDataException("盖章产物没有有效页面。");
        }

        if (sourceContentBytes <= 0)
        {
            return;
        }

        var outputBytes = PdfAutoRotateService.MeasureContentBytes(temporaryPath);
        if (outputBytes > 0 && outputBytes < sourceContentBytes * MinimumContentRatio)
        {
            throw new InvalidDataException(
                $"盖章把内容弄丢了：源内容 {sourceContentBytes} 字节，产物仅 {outputBytes} 字节。");
        }
    }

    /// <summary>
    /// 由图框槽位与当前图纸的纸张尺寸生成盖章计划。
    /// 纸张尺寸取页面实际 MediaBox（毫米），不用 job 里的标称值——
    /// 留白模式（扩大纸张）会让实际页面比标称尺寸大一圈，用标称值会整体偏移。
    /// </summary>
    /// <param name="slots">图框上配置的槽位。</param>
    /// <param name="library">签名库。</param>
    /// <param name="resolveImagePath">图片文件名 → 绝对路径（缺失返回空串）。</param>
    /// <param name="overrides">本次打印的临时覆盖：槽位名 → 签名 Id，优先于槽位默认绑定。</param>
    /// <param name="includeSignatures">是否包含手写签名类槽位（面板「打印签名」）。</param>
    /// <param name="includeStamps">是否包含印章类槽位（面板「打印印章」）。</param>
    /// <param name="warnings">未绑定/已过期/图片缺失等告警，供界面汇总提示。</param>
    public static List<StampPlacement> BuildPlacements(
        IEnumerable<SignatureSlot>? slots,
        SignatureLibrary library,
        Func<string, string> resolveImagePath,
        IReadOnlyDictionary<string, string>? overrides,
        bool includeSignatures,
        bool includeStamps,
        out List<string> warnings)
    {
        warnings = new List<string>();
        var placements = new List<StampPlacement>();
        if (slots == null || library == null)
        {
            return placements;
        }

        foreach (var slot in slots)
        {
            if (slot == null || !slot.IsConfigured())
            {
                continue;
            }

            var item = ResolveSignature(slot, library, overrides, out var bindingWarning);
            if (!string.IsNullOrWhiteSpace(bindingWarning))
            {
                warnings.Add(bindingWarning);
            }

            if (item == null)
            {
                // 未绑定 / 自动读取没有唯一候选都是正常状态（比如某类图只盖出图章，不签设计），
                // 具体原因由 ResolveSignature 决定是否告警。
                // 但「位置框好了、却什么都没选」不是正常状态：用户以为会盖，实际静默跳过。
                if (slot.GetRegions().Count > 0
                    && !slot.AutoPickSignature
                    && string.IsNullOrWhiteSpace(slot.DefaultSignatureId))
                {
                    var slotIsStamp = IsStampSlotName(slot.SlotName);
                    // 面板上对应开关没勾时不用提醒（本来就是这次不盖）。
                    if (slotIsStamp ? includeStamps : includeSignatures)
                    {
                        warnings.Add(
                            $"槽位「{slot.SlotName}」已框位置但没选{(slotIsStamp ? "印章" : "签名")}"
                            + "，本次不盖；请在图框的「签章设置」里选一个。");
                    }
                }

                continue;
            }

            var isStamp = IsStampSlot(slot, item);
            if (isStamp ? !includeStamps : !includeSignatures)
            {
                // 面板上对应复选框没勾：正常跳过，不告警。
                continue;
            }

            if (!item.IsValidOn(DateTime.Now))
            {
                warnings.Add($"签章「{item.Name}」已过期（{item.ValidFrom}~{item.ValidTo}）");
                continue;
            }

            var path = resolveImagePath(item.ImageFile);
            if (string.IsNullOrWhiteSpace(path))
            {
                warnings.Add($"签章「{item.Name}」的图片文件缺失");
                continue;
            }

            // 一个槽位可以有多个位置（同一个人签多处）：每个位置各生成一处盖章。
            var regions = slot.GetRegions();
            if (regions.Count == 0)
            {
                continue;
            }

            var usableRegions = 0;
            for (var index = 0; index < regions.Count; index++)
            {
                var region = regions[index];
                if (region == null || region.WidthMm <= 0.1 || region.HeightMm <= 0.1)
                {
                    continue;
                }

                usableRegions++;

                // 尺寸只认一个入口：图片处理里定下的标注尺寸优先，没有才回退旧的
                // WidthMm/HeightMm 字段。0/0 = 跟随槽位内接（旧行为）。
                item.TryGetEffectiveSizeMm(out var preferredWidthMm, out var preferredHeightMm);

                placements.Add(new StampPlacement
                {
                    ImagePath = path,
                    RightInsetMm = region.RightInsetMm,
                    BottomInsetMm = region.BottomInsetMm,
                    WidthMm = region.WidthMm,
                    HeightMm = region.HeightMm,
                    Rotation = slot.Rotation,
                    StretchToFill = slot.StretchToFill,
                    SlotName = regions.Count > 1 ? $"{slot.SlotName}#{index + 1}" : slot.SlotName,
                    IsStamp = isStamp,
                    PreferredWidthMm = preferredWidthMm,
                    PreferredHeightMm = preferredHeightMm,
                    SizeSource = DescribeSizeSource(item, preferredWidthMm, preferredHeightMm),
                });
            }

            if (usableRegions == 0)
            {
                warnings.Add($"槽位「{slot.SlotName}」的位置尺寸无效（宽度或高度为 0），已跳过");
            }
        }

        return placements;
    }

    /// <summary>
    /// 解析某个槽位该盖哪条签名：覆盖 &gt; 手工绑定 &gt; 自动读取。
    /// <para>
    /// 「自动读取」只在同类签名**唯一**时才自动套用：猜错等于替别人签名，比不盖更糟，
    /// 所以候选多于一个时宁可不盖并告警，交给用户在图框的签章设置里手工指定。
    /// </para>
    /// </summary>
    /// <param name="slot">槽位。</param>
    /// <param name="library">签名库。</param>
    /// <param name="overrides">本次打印的临时覆盖：槽位名 → 签名 Id。</param>
    /// <param name="warning">需要用户知道的问题（绑定失效 / 自动读取有歧义）；正常跳过时为空。</param>
    public static SignatureItem? ResolveSignature(
        SignatureSlot slot,
        SignatureLibrary library,
        IReadOnlyDictionary<string, string>? overrides,
        out string warning)
    {
        warning = "";
        if (slot == null || library == null)
        {
            return null;
        }

        var signatureId = slot.DefaultSignatureId ?? "";
        if (overrides != null && overrides.TryGetValue(slot.SlotName, out var overridden))
        {
            signatureId = overridden;
        }

        if (!string.IsNullOrWhiteSpace(signatureId))
        {
            var bound = library.Find(signatureId);
            if (bound == null)
            {
                warning = $"槽位「{slot.SlotName}」绑定的签名不存在（Id={signatureId}）";
                return null;
            }

            // 旧的图框库/命令行可能在人员栏绑了印章、或在章栏绑了手写签名。
            // 界面已经两边互不显示，这里再兜一道：类型不符宁可不盖并说清楚，不能把出图章当签名盖上去。
            if (!MatchesSlotKind(slot, bound))
            {
                warning = $"槽位「{slot.SlotName}」绑定的是"
                          + $"{(IsStampItem(bound) ? "印章" : "手写签名")}「{bound.Name}」，与槽位类型不符，已跳过；"
                          + "请在「签章设置」里重新选择。";
                return null;
            }

            return bound;
        }

        if (!slot.AutoPickSignature)
        {
            return null;
        }

        var candidates = new List<SignatureItem>();
        foreach (var item in library.Items)
        {
            if (item != null && MatchesSlotKind(slot, item) && item.IsValidOn(DateTime.Now))
            {
                candidates.Add(item);
            }
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        warning = $"槽位「{slot.SlotName}」设了自动读取，但签名库有 {candidates.Count} 个同类候选"
            + $"（{string.Join("、", candidates.ConvertAll(c => c.Name))}），无法判断该用哪个，已跳过；"
            + "请在该槽位的下拉里手工指定签名。";
        return null;
    }

    /// <summary>槽位名带「章」＝印章槽（出图章 / 注册师章 / 竣工图章）；其余（设计人员 1~6 等）＝手写签名槽。</summary>
    public static bool IsStampSlotName(string? slotName)
        => slotName != null && slotName.IndexOf('章') >= 0;

    /// <summary>库里条目是不是印章类（由导入时选的类型决定：印章库管理＝Stamp，签名库管理＝Signature）。</summary>
    public static bool IsStampItem(SignatureItem? item)
        => item != null
           && string.Equals(
               SignatureLibraryStore.NormalizeKind(item.Kind),
               SignatureLibraryStore.StampKind,
               StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 库条目与槽位是否同一类。
    /// <para>
    /// 印章只能进印章槽，手写签名只能进设计人员槽——两边互不串台。
    /// 下拉列表的过滤与出图时的校验都走这里，保证「界面里看不到的也绝不会盖上去」。
    /// </para>
    /// </summary>
    public static bool MatchesSlotKind(SignatureSlot? slot, SignatureItem? item)
        => item != null && IsStampSlotName(slot?.SlotName) == IsStampItem(item);

    /// <summary>槽位该按「打印印章」还是「打印签名」的开关放行。</summary>
    private static bool IsStampSlot(SignatureSlot slot, SignatureItem item)
        => IsStampItem(item) || IsStampSlotName(slot?.SlotName);
}
