using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZwcadBatchPlot;

/// <summary>
/// 为一次批量打印汇总并注册全部任意纸张。实际用户 PMP 只在全部尺寸写入临时副本成功后替换一次，
/// 矩形框批打通过此入口复用图框库批打已经验证过的整批准备语义，避免逐张修改 PMP。
/// </summary>
public static class CustomPaperBatchPreparer
{
    public sealed class PreparationResult
    {
        public IReadOnlyList<PmpCustomPaper.Registration> Registrations { get; set; }
            = Array.Empty<PmpCustomPaper.Registration>();
        public string AttachmentMessage { get; set; } = "";
    }

    public static PreparationResult Prepare(IReadOnlyList<PlotJob> jobs, string deviceName)
    {
        var isPdfDevice = string.Equals(
            deviceName,
            AcadPlotterInstaller.PreferredPdfPlotter,
            StringComparison.OrdinalIgnoreCase);
        var isDwfDevice = string.Equals(
            deviceName,
            AcadPlotterInstaller.PreferredDwfPlotter,
            StringComparison.OrdinalIgnoreCase);

        // 每次都从“扫描固有纸张”重新推导运行状态，避免正→负留白切换后残留扩大纸张，
        // 也避免把任意加长图原本需要精确注册的标记误清掉。
        foreach (var job in jobs)
        {
            var expandsPaper = job.LeavePaperMargin
                               && job.PaperMarginMm > 0
                               && job.PaperWidthMm > 0
                               && job.PaperHeightMm > 0;
            if (expandsPaper)
            {
                job.EffectivePaperWidthMm = job.PaperWidthMm + job.PaperMarginMm * 2;
                job.EffectivePaperHeightMm = job.PaperHeightMm + job.PaperMarginMm * 2;
            }
            else
            {
                job.EffectivePaperWidthMm = 0;
                job.EffectivePaperHeightMm = 0;
            }

            job.RequiresCustomPaperRegistration =
                job.DetectedRequiresCustomPaperRegistration || expandsPaper;
            job.RequireExactPaperSize = false;
            job.UseExactWindowScale = false;
            job.CustomPaperWasAdded = false;
        }

        var customJobs = jobs
            .Where(job => job.RequiresCustomPaperRegistration)
            .ToList();
        if (customJobs.Count == 0)
        {
            return new PreparationResult();
        }

        if (!isPdfDevice && !isDwfDevice)
        {
            // PNG/JPG 等设备不支持本软件的动态毫米纸张；不得沿用 PDF/DWF 的严格介质标记。
            foreach (var job in customJobs)
            {
                job.EffectivePaperWidthMm = 0;
                job.EffectivePaperHeightMm = 0;
                job.RequiresCustomPaperRegistration = false;
                job.RequireExactPaperSize = false;
                job.UseExactWindowScale = false;
                job.CustomPaperWasAdded = false;
            }

            return new PreparationResult();
        }

        var plottersDirectory = AcadPlotterInstaller.GetPlottersDirectory();
        var preferredPlotter = isDwfDevice
            ? AcadPlotterInstaller.PreferredDwfPlotter
            : AcadPlotterInstaller.PreferredPdfPlotter;
        var pmpFileName = isDwfDevice ? "LA_dwf.pmp" : "LA_pdf.pmp";
        var outputKind = isDwfDevice ? "DWF" : "PDF";
        var installedPlotter = Path.Combine(plottersDirectory, preferredPlotter);
        var installedPmp = Path.Combine(plottersDirectory, "PMP Files", pmpFileName);
        if (!File.Exists(installedPlotter) || !File.Exists(installedPmp))
        {
            if (isDwfDevice)
            {
                var installedDevice = AcadPlotterInstaller.InstallDwfPlotter();
                if (!string.Equals(installedDevice, preferredPlotter, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("LA_dwf 打印机配置不完整，无法注册 DWF 正负留白纸张。");
                }
            }
            else
            {
                var installResult = AcadPlotterInstaller.InstallBundledPlotter();
                if (!installResult.Installed)
                {
                    throw new InvalidOperationException("LA_pdf 打印机配置不完整: " + installResult.Message);
                }
            }

            plottersDirectory = AcadPlotterInstaller.GetPlottersDirectory();
            installedPlotter = Path.Combine(plottersDirectory, preferredPlotter);
            installedPmp = Path.Combine(plottersDirectory, "PMP Files", pmpFileName);
        }

        if (!File.Exists(installedPlotter) || !File.Exists(installedPmp))
        {
            throw new FileNotFoundException(
                $"LA_{outputKind.ToLowerInvariant()} 绘图器或 {pmpFileName} 不存在，无法批量注册正负留白纸张。",
                installedPmp);
        }

        var requests = customJobs
            .Select(job => new PmpCustomPaper.PaperRequest
            {
                // 扩大纸张模式时用有效尺寸（+margin*2），任意加长时用实测尺寸
                WidthMm = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm : job.PaperWidthMm,
                HeightMm = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm : job.PaperHeightMm
            })
            .ToList();
        var registrations = PmpCustomPaper.RegisterCustomPapers(installedPmp, requests)
            ?? throw new InvalidOperationException(
                $"{pmpFileName} 批量注册 {outputKind} 正负留白纸张失败，已停止打印，避免回退到错误纸张。");
        var anyAdded = registrations.Any(registration => registration.WasAdded);
        var attachmentMessage = "";

#if AUTOCAD
        // PDF 的跨版本 PC3 需要现有兼容层修正 PMP 关联。DWF PC3 继承其原生驱动，
        // 不能套用 DWG To PDF 的驱动路径；它只需保持安装时建立的 LA_dwf.pmp 关联。
        if (isPdfDevice && !AcadPlotterInstaller.EnsurePmpAttachment(
                installedPlotter,
                installedPmp,
                forceRewrite: anyAdded,
                out attachmentMessage))
        {
            throw new InvalidOperationException("LA_pdf.pc3 关联批量 PMP 失败: " + attachmentMessage);
        }
#endif

        foreach (var job in customJobs)
        {
            // DWF 与 PDF 都必须按注册后的实际纸张精确选纸；正值使用扩大纸张，
            // 负值使用原纸并在 ConfigurePlotScale 中缩小内容。
            job.RequireExactPaperSize = true;
            job.UseExactWindowScale = true;
            job.CustomPaperWasAdded = false;
        }

        if (anyAdded)
        {
            // 介质目录按模型/布局分别缓存；每类空间只让第一张触发一次重载，后续纸张直接使用刷新后的目录。
            foreach (var firstJob in customJobs.GroupBy(job => job.IsPaperSpace).Select(group => group.First()))
            {
                firstJob.CustomPaperWasAdded = true;
            }
        }

        return new PreparationResult
        {
            Registrations = registrations,
            AttachmentMessage = attachmentMessage
        };
    }
}
