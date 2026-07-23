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
        var customJobs = jobs
            .Where(job => job.RequiresCustomPaperRegistration)
            .ToList();
        if (customJobs.Count == 0)
        {
            return new PreparationResult();
        }

        if (!string.Equals(
                deviceName,
                AcadPlotterInstaller.PreferredPdfPlotter,
                StringComparison.OrdinalIgnoreCase))
        {
            // 动态物理纸张只适用于 LA_pdf；切换到其他输出格式时不得沿用之前预览设置的严格介质标记。
            foreach (var job in customJobs)
            {
                job.RequireExactPaperSize = false;
                job.UseExactWindowScale = false;
                job.CustomPaperWasAdded = false;
            }

            return new PreparationResult();
        }

        var plottersDirectory = AcadPlotterInstaller.GetPlottersDirectory();
        var installedPlotter = Path.Combine(plottersDirectory, AcadPlotterInstaller.PreferredPdfPlotter);
        var installedPmp = Path.Combine(plottersDirectory, "PMP Files", "LA_pdf.pmp");
        if (!File.Exists(installedPlotter) || !File.Exists(installedPmp))
        {
            var installResult = AcadPlotterInstaller.InstallBundledPlotter();
            if (!installResult.Installed)
            {
                throw new InvalidOperationException("LA_pdf 打印机配置不完整: " + installResult.Message);
            }

            plottersDirectory = AcadPlotterInstaller.GetPlottersDirectory();
            installedPlotter = Path.Combine(plottersDirectory, AcadPlotterInstaller.PreferredPdfPlotter);
            installedPmp = Path.Combine(plottersDirectory, "PMP Files", "LA_pdf.pmp");
        }

        if (!File.Exists(installedPlotter) || !File.Exists(installedPmp))
        {
            throw new FileNotFoundException(
                "LA_pdf.pc3/pc5 或 LA_pdf.pmp 不存在，无法批量注册任意纸张。",
                installedPmp);
        }

        var requests = customJobs
            .Select(job => new PmpCustomPaper.PaperRequest
            {
                WidthMm = job.PaperWidthMm,
                HeightMm = job.PaperHeightMm
            })
            .ToList();
        var registrations = PmpCustomPaper.RegisterCustomPapers(installedPmp, requests)
            ?? throw new InvalidOperationException(
                "LA_pdf.pmp 批量注册任意纸张失败，已停止打印，避免回退到错误纸张。");
        var anyAdded = registrations.Any(registration => registration.WasAdded);
        var attachmentMessage = "";

#if AUTOCAD
        if (!AcadPlotterInstaller.EnsurePmpAttachment(
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
            // 任意加长图必须按实测纸张精确选纸和缩放，禁止名称匹配或相近纸张回退。
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
