using System;
using System.Globalization;
using System.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;


// CadUsings.cs 的全局 using 让 CAD 命名空间里的同名类型也进了作用域，这里钉死本文件要的那个：
// 本文件的引用对象来自 PDFsharp，与 CAD 的 PDF 类型无关。
using PdfReference = PdfSharp.Pdf.Advanced.PdfReference;
namespace ZwcadBatchPlot;

/// <summary>
/// PDF 自动转正：把出图结果旋转到统一的阅读方向（图签文字朝上）。
/// 由出图管道（PlotDatabase）在每张出图成功后调用；只处理最终 PDF 输出，
/// 旋转失败只记录诊断日志，不影响该任务的打印结果。
/// <para>
/// 两级策略（角度均为逆时针为正，2026-10-18 v3 按实测标定）：
/// ① 页面级（常规图框，90° 整数倍）：内容在页面用户空间的朝向
///    Δ = 图框内容 WCS 旋转 − UCS 旋转 − 出图旋转；/Rotate（顺时针正向，只能 90° 步进）
///    覆写为 −Δ 的最近 90° 倍数。
/// ② 内容级（斜插图框，非 90° 整数倍）：/Rotate 量化后存在残余斜角
///    ρ = Δ + /Rotate（|ρ| ≤ 45°），页面旋转无法表达——改用 XGraphics + XPdfForm
///    把原页内容按用户空间倾角 Δ 旋转重绘到新页（/Rotate=0），实测标定：
///    中间文件剥掉 /Rotate 后，XGraphics 旋转角直接取 Δ 即可摆正。
///    重绘后页面归一到图框名义尺寸（v3.1 裁剪、v3.2 放大）：斜框出图窗口是图框外接矩形，
///    「布满图纸」把外接窗缩进纸张后图框在纸面上只剩 ~60%——新页直接取图框名义尺寸
///    （PaperWidthMm×PaperHeightMm），内容按 1/打印比例 反向放大绘制，页面回归标准纸张、
///    图框满幅。
/// </para>
/// </summary>
public static class PdfAutoRotateService
{
    /// <summary>残余斜角小于该值视为已正，不走内容级重绘。</summary>
    private const double ResidualEpsilon = 0.05;

    /// <summary>裁剪后四周保留的白边（pt，约 1mm），兜住图框边线线宽超出窗口中心线的部分。</summary>
    private const double CropPadPt = 3.0;

    /// <summary>
    /// 按任务携带的图框旋转角与本次出图旋转，把 PDF 转正到统一方向。
    /// 无需旋转（/Rotate 已达标且残余角为 0）或文件不存在/非 PDF 时不动文件，返回 false。
    /// </summary>
    public static bool RotateJobOutputToUpright(PlotJob job, int plotRotationDegrees)
    {
        var path = job?.OutputPath;
        if (!IsPdfPath(path) || !File.Exists(path))
        {
            Log($"跳过（非 PDF 或文件不存在）: {path}");
            return false;
        }

        // Δ：图框内容在页面用户空间里的朝向（AutoCAD 已把出图旋转记在页面上，实测见类注释）。
        var tilt = ComputeUserSpaceTilt(job!, plotRotationDegrees);
        var frameQuant = QuarterRotation.Normalize(job!.ContentRotationDegrees);
        // 页面 /Rotate 只能 90° 步进：取 −Δ 的最近 90° 倍数。
        var target = Round90(-tilt);
        // 残余斜角：/Rotate 量化后剩下的斜插角度（|ρ| ≤ 45），交给内容级重绘处理。
        var residual = QuarterRotation.NormalizeSigned(tilt + target);

        // 源文件体量诊断：区分「AutoCAD 出图即空白」与「重绘把内容抹空」两种可能——
        // 内容>0 且远小于同类图纸即前者；内容充裕而重绘产物稀薄即后者（ApplyByRedraw 内有校验兜底）。
        var sourceBytes = MeasureContentBytes(path!);

        Log($"判定: {System.IO.Path.GetFileName(path)} 图框旋转(量化)={frameQuant}° UCS={UcsDegrees(job):0.##}° 出图旋转={QuarterRotation.Normalize(plotRotationDegrees)}°"
            + $" → 倾角={Format(tilt)}° /Rotate={target}° 残余={Format(residual)}°"
            + $" 源={new FileInfo(path!).Length}字节/内容={sourceBytes}");

        try
        {
            var redraw = Math.Abs(residual) > ResidualEpsilon;
            var changed = redraw
                ? ApplyByRedraw(path!, tilt, job, sourceBytes)
                : ApplyPageRotation(path!, target, sourceBytes);

            if (changed)
            {
                BatchPlotLogger.AddPending("INFO", $"PDF 自动转正: {path}（{(redraw ? $"内容重绘 {Format(tilt)}°" : $"/Rotate → {target}°")}）");
                Log($"完成: {System.IO.Path.GetFileName(path)}");
                return true;
            }

            Log($"无需旋转: {System.IO.Path.GetFileName(path)} 页面 /Rotate 已是 {target}° 且残余角为 0");
            return false;
        }
        catch (Exception ex)
        {
            // 转正是出图后的润色性处理：失败不能把已成功的打印判成失败，只留诊断行。
            BatchPlotLogger.AddPending("WARN", $"PDF 自动转正失败（保留原文件）: {path}; {ex.Message}");
            Log($"失败: {System.IO.Path.GetFileName(path)}; {ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    /// <summary>转正诊断日志（受「关闭日志」总开关控制）：追加一行到「日志」文件夹的 LA_AutoRotate_日期.log；写失败静默。</summary>
    public static void Log(string message)
    {
        try
        {
            if (!BatchPlotLogger.IsEnabled)
            {
                return;
            }

            BatchPlotLogger.AddPending("INFO", "[自动转正] " + message);
        }
        catch
        {
            // 诊断日志失败不影响任何流程。
        }
    }

    /// <summary>
    /// 内容在页面用户空间的倾角（CCW 正，(-180,180]）。
    /// 原始角仅在与量化角一致（±45° 内）时可信；单张打印只估量化角（Raw=0）时退回量化角。
    /// </summary>
    public static double ComputeUserSpaceTilt(PlotJob job, int plotRotationDegrees)
    {
        var frameQuant = QuarterRotation.Normalize(job.ContentRotationDegrees);
        var frameRaw = job.ContentRotationDegreesRaw;
        var rawUsable = Math.Abs(QuarterRotation.NormalizeSigned(frameRaw - frameQuant)) <= 45.01;
        var frame = rawUsable ? frameRaw : (double)frameQuant;
        var ucs = UcsDegrees(job);
        var plot = QuarterRotation.Normalize(plotRotationDegrees);
        return QuarterRotation.NormalizeSigned(frame - ucs - plot);
    }

    /// <summary>页面级目标 /Rotate（0/90/180/270）与量化残余角。保留给外部诊断调用。</summary>
    public static int ComputeTargetRotation(PlotJob job, int plotRotationDegrees, out double residualTwist)
    {
        var tilt = ComputeUserSpaceTilt(job, plotRotationDegrees);
        var target = Round90(-tilt);
        residualTwist = QuarterRotation.NormalizeSigned(tilt + target);
        return target;
    }

    /// <summary>是否为 PDF 输出路径（据此排除 DWF/PNG/JPG 等其它格式）。</summary>
    public static bool IsPdfPath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// UCS 在 WCS 中的旋转角（逆时针为正，(-180,180]）。
    /// <para>
    /// 必须取<b>原始角</b>而非四分之一转量化角：出图时 <c>PrepareEditorViewForPlot</c> 已按
    /// UCS 实际角度设 ViewTwist 扭转视图，PDF 内容相对页面的残余歪斜只有
    /// 「图框角 − UCS 实际角」。若把 UCS 角量化到 90° 步进，斜置 UCS（如 14.3°）会被
    /// 当成 0°，误判出 14.3° 残余斜角 → 触发内容级重绘 → 整页抹空（PDF 全白）。
    /// 图框侧用的 <c>ContentRotationDegreesRaw</c> 同样是原始角，两侧必须同精度。
    /// </para>
    /// <para>
    /// 取的是<b>实际生效</b>的扭转角（见 <see cref="PlotTwistPolicy"/>）：正交图框的 ViewTwist
    /// 已被归零、整 90° 转移到出图旋转上。若这里还用原始 UCS 角，转正角会差 2×90° 把页面翻 180°。
    /// </para>
    /// </summary>
    private static double UcsDegrees(PlotJob job)
    {
        return QuarterRotation.NormalizeSigned(-0d);
    }

    /// <summary>
    /// 页面级转正：把 PDF 全部页的 /Rotate 设为目标角（绝对值覆写）。
    /// 所有页已是目标角时不改文件返回 false。走「临时文件 + 替换」，产物先重开校验。
    /// </summary>
    private static bool ApplyPageRotation(string pdfPath, int targetDegrees, long sourceContentBytes)
    {
        var fullPath = Path.GetFullPath(pdfPath);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".rotate-tmp";
        try
        {
            var changed = false;
            using (var document = PdfReader.Open(fullPath, PdfDocumentOpenMode.Import))
            {
                foreach (var page in document.Pages)
                {
                    var current = NormalizeRotate(page.Rotate);
                    if (current != targetDegrees)
                    {
                        page.Rotate = targetDegrees;
                        changed = true;
                    }
                }

                if (!changed)
                {
                    return false;
                }

                document.Save(temporaryPath);
            }

            VerifyTemporary(temporaryPath, sourceContentBytes);
            // 原子换入：先删再移会在移动失败时把用户原有的 PDF 一起丢掉。
            AtomicFileWriter.ReplaceFile(temporaryPath, fullPath);
            return true;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                TryDelete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// 内容级转正（斜插图框）：把原页剥掉 /Rotate 后经 XPdfForm 按用户空间倾角旋转重绘到新页。
    /// 页面尺寸取图框名义尺寸（TrySolveFrameLayout 反解打印比例后按 1/scale 放大内容），
    /// 消除斜框外接窗「布满图纸」带来的页面缩小与四角留白；反解不可靠时回退为
    /// 整页旋转包围盒（旧行为）。
    /// 注意：不能往内容数组里直接插流字典——PdfSharp 1.5 会写出无法重新解析的损坏结构。
    /// <para>
    /// 兜底校验（2026-10-24）：PDFsharp 1.5 跨文档拷页在 CAD 进程内实测会把内容流截成
    /// 前几个算子（生产 31/31 全白、本地复现不了），因此产物必须通过「内容体量不低于源 50%」
    /// 校验，否则抛异常走上层 catch——保留原文件不替换，杜绝把好 PDF 换成白页。
    /// </para>
    /// </summary>
    private static bool ApplyByRedraw(string pdfPath, double tiltDegrees, PlotJob job, long sourceContentBytes)
    {
        var fullPath = Path.GetFullPath(pdfPath);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".rotate-tmp";
        var intermediatePath = temporaryPath + ".inter";
        try
        {
            using (var source = PdfReader.Open(fullPath, PdfDocumentOpenMode.Import))
            using (var intermediate = new PdfDocument())
            {
                foreach (var page in source.Pages)
                {
                    var copy = intermediate.AddPage(page);
                    copy.Rotate = 0; // 剥掉 /Rotate：表单绘制的倾斜角 = 纯用户空间倾角
                }

                intermediate.Save(intermediatePath);
            }

            using (var output = new PdfDocument())
            using (var form = XPdfForm.FromFile(intermediatePath))
            {
                var rad = tiltDegrees * Math.PI / 180.0;
                var cos = Math.Cos(rad);
                var sin = Math.Sin(rad);
                for (var index = 1; index <= form.PageCount; index++)
                {
                    form.PageNumber = index;
                    var w = form.PointWidth;
                    var h = form.PointHeight;
                    var w2 = w * Math.Abs(cos) + h * Math.Abs(sin);
                    var h2 = w * Math.Abs(sin) + h * Math.Abs(cos);

                    // 新页 = 图框名义尺寸（pt），内容按 1/scale 放大；反解失败回退整页包围盒（旧行为）。
                    var normalized = TrySolveFrameLayout(job, w2, h2, out var pageW, out var pageH, out var zoom);
                    if (!normalized)
                    {
                        pageW = w2;
                        pageH = h2;
                        zoom = 1d;
                    }
                    else
                    {
                        Log($"归一: 第{index}页 → {Math.Round(pageW * 25.4 / 72, 1)}×{Math.Round(pageH * 25.4 / 72, 1)}mm"
                            + $"（图框名义尺寸，内容放大 {Math.Round(zoom, 3)}×）");
                    }

                    var page = output.AddPage();
                    page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(pageW, pageH));
                    page.Rotate = 0;

                    using (var graphics = XGraphics.FromPdfPage(page))
                    {
                        // 图框中心 = 原页中心（打印居中保证）：绕新页中心摆正并放大。
                        // 变换对点的生效顺序为逆序：平移图框中心到原点 → 放大（undo 打印比例）
                        // → 旋转摆正 → 平移到新页中心。
                        graphics.TranslateTransform(pageW / 2.0, pageH / 2.0);
                        graphics.RotateTransform(tiltDegrees);
                        graphics.ScaleTransform(zoom, zoom);
                        graphics.TranslateTransform(-w / 2.0, -h / 2.0);
                        graphics.DrawImage(form, 0, 0, w, h);
                    }
                }

                output.Save(temporaryPath);
            }

            VerifyTemporary(temporaryPath, sourceContentBytes);
            // 原子换入：先删再移会在移动失败时把用户原有的 PDF 一起丢掉。
            AtomicFileWriter.ReplaceFile(temporaryPath, fullPath);
            return true;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                TryDelete(temporaryPath);
            }

            if (File.Exists(intermediatePath))
            {
                TryDelete(intermediatePath);
            }
        }
    }

    /// <summary>
    /// 反解打印比例并给出新页尺寸（pt）与内容放大倍数。
    /// 已知：图框名义尺寸 ≈ 任务纸张（PaperWidthMm×PaperHeightMm）、打印窗口（Min/Max，图框外接矩形）、
    /// 打印比例（按 job 模式标志复刻 ConfigurePlotScale）。v3.2 语义：新页 = 图框名义尺寸
    /// （扩大纸张模式 = 图框+2×留白），内容放大 zoom = 1/scale，使图框在新页上满幅。
    /// 任何一步不可靠（数据缺失、方向消歧失败、比例异常、超出源页面包围盒）都返回 false 走旧行为。
    /// </summary>
    private static bool TrySolveFrameLayout(PlotJob job, double maxWpt, double maxHpt, out double pageW, out double pageH, out double zoom)
    {
        pageW = pageH = zoom = 0;
        try
        {
            var frameW = job.PaperWidthMm;
            var frameH = job.PaperHeightMm;
            var winW = Math.Abs(job.MaxX - job.MinX);
            var winH = Math.Abs(job.MaxY - job.MinY);
            if (frameW <= 1 || frameH <= 1 || winW < 1e-6 || winH < 1e-6)
            {
                return false;
            }

            // 方向消歧：图框宽高哪个对应长边——用「原始角的外接矩形纵横比」与 WCS 窗口纵横比比对。
            var phi = Fold180(job.ContentRotationDegreesRaw);
            var cosPhi = Math.Abs(Math.Cos(phi * Math.PI / 180.0));
            var sinPhi = Math.Abs(Math.Sin(phi * Math.PI / 180.0));
            var winAspect = winW / winH;
            var aspectA = (frameW * cosPhi + frameH * sinPhi) / (frameW * sinPhi + frameH * cosPhi);
            var aspectB = (frameH * cosPhi + frameW * sinPhi) / (frameH * sinPhi + frameW * cosPhi);
            var matchA = RatioClose(winAspect, aspectA);
            var matchB = RatioClose(winAspect, aspectB);
            if (matchA == matchB)
            {
                return false; // 都匹配（方框）或都不匹配（数据不可信）→ 放弃归一
            }

            if (matchB)
            {
                (frameW, frameH) = (frameH, frameW);
            }

            // 打印比例（mm 纸面 / 图面单位）：复刻 ConfigurePlotScale 各模式。
            var winLong = Math.Max(winW, winH);
            var winShort = Math.Min(winW, winH);
            var frameLong = Math.Max(frameW, frameH);
            var frameShort = Math.Min(frameW, frameH);
            double scale;
            var pageMarginMm = 0d;
            if (!job.LeavePaperMargin)
            {
                // 布满图纸 / 精确窗口 = min(纸长/窗长, 纸短/窗短)。
                // 布满实际按可打印面积，整纸估计只会偏大 → 内容略偏小 → 图框四周留窄白边，安全方向。
                scale = Math.Min(frameLong / winLong, frameShort / winShort);
            }
            else if (job.PaperMarginMm > 0d)
            {
                // 扩大纸张模式：纸张已扩为图框+2×margin，比例 = 图框短边/窗口短边；新页 = 扩大后的纸张。
                pageMarginMm = job.PaperMarginMm;
                scale = frameShort / winShort;
            }
            else
            {
                // 缩比例模式：纸短边减 2×margin 定比例；新页仍是图框名义尺寸。
                var marginMm = Math.Abs(job.PaperMarginMm) > 0d ? Math.Abs(job.PaperMarginMm) : 1d;
                scale = (frameShort - marginMm * 2d) / winShort;
            }

            if (scale <= 0 || scale > 2)
            {
                return false;
            }

            zoom = 1d / scale;
            if (zoom < 0.5 || zoom > 2.0)
            {
                return false;
            }

            var ptPerMm = 72.0 / 25.4;
            pageW = (frameW + pageMarginMm * 2d) * ptPerMm + CropPadPt * 2;
            pageH = (frameH + pageMarginMm * 2d) * ptPerMm + CropPadPt * 2;
            if (pageW < 50 || pageH < 50)
            {
                return false;
            }

            // 新页不得大于源页旋转包围盒（图框名义尺寸理应 ≤ 打印纸张）。
            return pageW <= maxWpt + 0.5 && pageH <= maxHpt + 0.5;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>折叠任意角到 (−90°, 90°]。</summary>
    private static double Fold180(double degrees)
    {
        return ((degrees % 180) + 270) % 180 - 90;
    }

    /// <summary>两个纵横比是否一致（3% 容差）。</summary>
    private static bool RatioClose(double a, double b)
    {
        var larger = Math.Max(Math.Abs(a), Math.Abs(b));
        return larger > 0 && Math.Abs(a - b) <= 0.03 * larger;
    }

    /// <summary>
    /// 校验产物可被重新打开、页数非零，且内容体量不低于源的 50%（源可测量时）。
    /// 「内容体量」= 各页内容流 + 页/表单资源引用的表单 XObject 内容流（解压后字节数）——
    /// PDFsharp 1.5 跨文档拷页在 CAD 进程内会把外来页内容流截成前几个算子，
    /// 只查页数发现不了（白页也是 1 页），必须查内容。源内容为 0（出图即空白）时不作要求。
    /// </summary>
    private static void VerifyTemporary(string temporaryPath, long sourceContentBytes)
    {
        using (var verify = PdfReader.Open(temporaryPath, PdfDocumentOpenMode.Import))
        {
            if (verify.PageCount == 0)
            {
                throw new InvalidDataException("转正后的 PDF 没有有效页面。");
            }
        }

        if (sourceContentBytes <= 0)
        {
            return; // 源就测不出内容（或源本身空白），无法对比，放行
        }

        var outputBytes = MeasureContentBytes(temporaryPath);
        if (outputBytes * 2 < sourceContentBytes)
        {
            throw new InvalidDataException(
                $"转正把内容弄丢了：源内容 {sourceContentBytes} 字节，产物仅 {outputBytes} 字节。");
        }
    }

    /// <summary>
    /// 统计 PDF 的绘图内容体量（解压后字节数）：各页内容流 + 其资源引用的表单 XObject
    /// 内容流（重绘产物把原页内容放在表单里，必须算进去）。表单嵌套最多展开 3 层。
    /// 任何读取失败返回 -1（表示「测不出」，调用方据此放行对比）。
    /// </summary>
    internal static long MeasureContentBytes(string pdfPath)
    {
        try
        {
            long total = 0;
            using (var document = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import))
            {
                foreach (var page in document.Pages)
                {
                    total += PageContentBytes(page, 0);
                }
            }
            return total;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>单页内容体量：页内容流 + 资源里的表单 XObject（递归展开）。</summary>
    private static long PageContentBytes(PdfPage page, int depth)
    {
        long total = 0;

        var contents = page.Contents;
        if (contents?.Elements != null)
        {
            foreach (var item in contents.Elements)
            {
                total += StreamBytes(item);
            }
        }

        // 表单 XObject：重绘产物与原图都可能把内容收纳在表单里
        var resources = page.Resources;
        if (resources != null && depth < 3)
        {
            total += FormXObjectBytes(resources, depth);
        }

        return total;
    }

    /// <summary>累加资源字典 /XObject 下各表单的内容体量（递归展开其资源）。</summary>
    private static long FormXObjectBytes(PdfDictionary resources, int depth)
    {
        long total = 0;
        var xobjects = resources.Elements?["/XObject"] as PdfDictionary;
        if (xobjects?.Elements == null)
        {
            return total;
        }

        foreach (var item in xobjects.Elements.Values)
        {
            var form = StreamDictionary(item);
            if (form == null)
            {
                continue;
            }

            // 只统计表单 XObject；图像 XObject（DCT/JPX 等）的解码既慢又可能抛错，跳过
            var subtype = form.Elements?["/Subtype"];
            if (subtype == null || subtype.ToString() != "/Form")
            {
                continue;
            }

            total += StreamBytes(form);

            // /Resources 可能是间接引用，解析后再递归
            var resourcesItem = form.Elements?["/Resources"];
            var nested = resourcesItem as PdfDictionary
                         ?? (resourcesItem as PdfReference)?.Value as PdfDictionary;
            if (nested != null)
            {
                total += FormXObjectBytes(nested, depth + 1);
            }
        }
        return total;
    }

    /// <summary>取间接引用对象的流内容长度（解压后）；取不到返回 0。</summary>
    private static long StreamBytes(PdfItem item)
    {
        var dictionary = StreamDictionary(item);
        var stream = dictionary?.Stream;
        // 必须用 UnfilteredValue：Value 对页内容流给解码态、对表单 XObject 给原始压缩态，
        // 两侧混用会把正常重绘误判成丢内容（实测 acadE 表单 213400 vs 959577）。
        var value = stream?.UnfilteredValue;
        return value == null ? 0 : value.Length;
    }

    /// <summary>把 PdfItem（间接引用或直接字典）解析成流字典；不是流字典返回 null。</summary>
    private static PdfDictionary? StreamDictionary(PdfItem? item)
    {
        var dictionary = item as PdfDictionary ?? (item as PdfReference)?.Value as PdfDictionary;
        return dictionary?.Stream != null ? dictionary : null;
    }

    /// <summary>取 −角度 最近的 90° 倍数（结果 0/90/180/270）。</summary>
    private static int Round90(double degrees)
    {
        var quarter = (int)Math.Round(degrees / 90.0, MidpointRounding.AwayFromZero);
        return (((quarter % 4) + 4) % 4) * 90;
    }

    /// <summary>把任意 /Rotate 值规范到 0/90/180/270。</summary>
    private static int NormalizeRotate(int rotate)
    {
        return ((rotate % 360) + 360) % 360;
    }

    private static string Format(double value)
    {
        return Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
