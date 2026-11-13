using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZwcadBatchPlot;

/// <summary>
/// 把「图框签章槽位 + 签名库」落到一次具体出图上：找图框定义 → 生成盖章计划 → 调 PDF 盖章。
/// <para>
/// 放在每个 worker 出完自己那张 PDF 之后执行：各进程写各自文件，不存在并发写同一 PDF 的问题，
/// 也不需要为盖章加跨进程锁。
/// </para>
/// </summary>
public static class SignatureStampService
{
    /// <summary>
    /// 对一张已出好的 PDF 盖章。任何前置条件不满足都静默跳过（只记日志）——
    /// 签章是可选增强，不能因为它让整批出图失败。
    /// </summary>
    public static void Apply(PlotJob job, AppSettings settings)
    {
        if (job == null || settings == null)
        {
            return;
        }

        // 三个开关并列：二维码不依赖签名库，不能因为「签名/印章都没勾」就一起跳过。
        var signatureEnabled = settings.PlotSignatureEnabled;
        var stampEnabled = settings.PlotStampEnabled;
        var qrEnabled = settings.PlotQrCodeEnabled;

        if (!signatureEnabled && !stampEnabled && !qrEnabled)
        {
            PluginLog.Timing("[签章] 打印签名/打印印章/打印二维码均未勾选，跳过盖章");
            return;
        }

        if (!PdfAutoRotateService.IsPdfPath(job.OutputPath))
        {
            return;
        }

        if (!File.Exists(job.OutputPath))
        {
            return;
        }

        var definition = FindDefinition(job.BlockName);
        if (definition == null)
        {
            PluginLog.Timing($"[签章] 图框「{job.BlockName}」不在图框库，跳过盖章");
            return;
        }

        // 签章与二维码必须合并成一次 TryApply：分两次会重写两遍 PDF，
        // 既慢，又可能让 PdfStampService 的内容字节数校验误判成「内容丢了」。
        var placements = new List<StampPlacement>();
        var qrTempDir = "";

        try
        {
            if (signatureEnabled || stampEnabled)
            {
                CollectSignaturePlacements(definition, settings, placements);
            }

            if (qrEnabled)
            {
                qrTempDir = CollectQrPlacements(job, definition, placements);
            }

            if (placements.Count == 0)
            {
                PluginLog.Timing($"[签章] 图框「{definition.BlockName}」没有有效签章/二维码，跳过");
                return;
            }

            var applied = PdfStampService.TryApply(job.OutputPath, placements, out var message);
            PluginLog.Timing(
                $"[签章] {Path.GetFileName(job.OutputPath)} 图框={definition.BlockName} "
                + $"贴图={string.Join("/", placements.Select(p => p.SlotName))} → {(applied ? "成功" : "未盖章")} {message}");
        }
        catch (Exception ex)
        {
            // 签章失败不得打断已出好的 PDF / 整批打印。
            PluginLog.Error("签章", "盖章过程出错，已跳过且不影响出图：" + ex.Message, ex);
        }
        finally
        {
            // 二维码位图是本次出图专属的中间产物，不清理会在 %TEMP% 里越积越多。
            CleanupTempDirectory(qrTempDir);
        }
    }

    /// <summary>
    /// 收集签名/印章贴图。查不到槽位、签名库为空都是正常状态（有的图只出二维码），
    /// 只记日志不抛。
    /// </summary>
    private static void CollectSignaturePlacements(
        TitleBlockDefinition definition,
        AppSettings settings,
        List<StampPlacement> placements)
    {
        var slots = definition.SignatureSlots;
        if (slots == null || slots.Count == 0)
        {
            PluginLog.Timing($"[签章] 图框「{definition.BlockName}」未配置签章槽位，跳过盖章");
            return;
        }

        SignatureLibrary library;
        try
        {
            library = SignatureLibraryStore.Load();
        }
        catch (Exception ex)
        {
            PluginLog.Error("签章", "读取签名库失败，跳过盖章：" + ex.Message, ex);
            return;
        }

        if (library.Items.Count == 0)
        {
            PluginLog.Timing("[签章] 签名库为空，跳过");
            return;
        }

        // 把图框里配了哪些槽位、每个槽位有没有选东西记一行：
        // 「勾了打印印章却没盖」时，这行日志能立刻区分是没配槽位、还是槽位没选印章。
        PluginLog.Timing(
            $"[签章] 图框「{definition.BlockName}」槽位 {slots.Count} 个："
            + string.Join("、", slots.Select(slot =>
            {
                var binding = !string.IsNullOrWhiteSpace(slot.DefaultSignatureId)
                    ? "已选"
                    : (slot.AutoPickSignature ? "自动读取" : "未选");
                var regions = slot.GetRegions().Count;
                return $"{slot.SlotName}(位置{regions},{binding})";
            }))
            + $"；打印签名={(settings.PlotSignatureEnabled ? "开" : "关")} "
            + $"打印印章={(settings.PlotStampEnabled ? "开" : "关")}");

        var built = PdfStampService.BuildPlacements(
            slots,
            library,
            fileName => SignatureLibraryStore.ResolveImagePath(new SignatureItem { ImageFile = fileName }),
            null,
            settings.PlotSignatureEnabled,
            settings.PlotStampEnabled,
            out var warnings);

        foreach (var warning in warnings)
        {
            PluginLog.Timing("[签章] " + warning);
        }

        placements.AddRange(built);
    }

    /// <summary>
    /// 渲染二维码并追加贴图计划，返回临时目录（无成功项时返回空串）。
    /// <para>
    /// 位置尺寸用纸面毫米（<see cref="TitleBlockQrCode.WidthMm"/>），与图幅无关：
    /// A0 和 A4 上印出来的二维码物理尺寸一致，才符合「打印出来都一样大」的直觉。
    /// </para>
    /// </summary>
    private static string CollectQrPlacements(
        PlotJob job,
        TitleBlockDefinition definition,
        List<StampPlacement> placements)
    {
        var codes = definition.QrCodes;
        if (codes == null || codes.Count == 0)
        {
            PluginLog.Timing($"[二维码] 图框「{definition.BlockName}」未配置二维码位置，跳过");
            return "";
        }

        var workDir = Path.Combine(
            Path.GetTempPath(),
            Path.GetFileName(TitleBlockLibraryStore.DefaultDirectory),
            "qr",
            Guid.NewGuid().ToString("N"));
        var created = false;

        for (var index = 0; index < codes.Count; index++)
        {
            var code = codes[index];
            if (code == null || !code.Enabled)
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(code.Name) ? $"二维码{index + 1}" : code.Name;

            if (!code.IsConfigured())
            {
                PluginLog.Timing($"[二维码] 位置「{label}」尺寸无效（宽或高为 0），已跳过");
                continue;
            }

            var content = QrCodeRenderer.ExpandTemplate(code.ContentTemplate, job);
            if (!QrCodeRenderer.TryRenderPng(
                    content,
                    code.ErrorCorrection,
                    code.WidthMm,
                    code.HeightMm,
                    out var png,
                    out var error))
            {
                PluginLog.Timing($"[二维码] 位置「{label}」未生成：{error}");
                continue;
            }

            if (!string.IsNullOrEmpty(error))
            {
                // 生成了但有提醒（例如内容偏长），照样贴，只留一行日志。
                PluginLog.Timing($"[二维码] 位置「{label}」提醒：{error}");
            }

            string path;
            try
            {
                if (!created)
                {
                    Directory.CreateDirectory(workDir);
                    created = true;
                }

                path = Path.Combine(workDir, $"qr{index + 1}.png");
                File.WriteAllBytes(path, png);
            }
            catch (Exception ex)
            {
                // 临时目录写不进去（磁盘满 / 权限）只影响这一个位置，不拖垮整批。
                PluginLog.Timing($"[二维码] 位置「{label}」写临时文件失败：{ex.Message}");
                continue;
            }

            placements.Add(new StampPlacement
            {
                ImagePath = path,
                RightInsetMm = code.RightInsetMm,
                BottomInsetMm = code.BottomInsetMm,
                WidthMm = code.WidthMm,
                HeightMm = code.HeightMm,
                Rotation = code.Rotation,
                // 二维码必须保持正方形，等比内接；拉伸铺满会把正方形拉成矩形，直接扫不出。
                StretchToFill = false,
                SlotName = label,
                IsStamp = false,
                SizeSource = "qr",
            });

            PluginLog.Timing(
                $"[二维码] 位置「{label}」内容={Truncate(content, 60)} "
                + $"尺寸={code.WidthMm:0.#}×{code.HeightMm:0.#}mm 纠错={code.ErrorCorrection}");
        }

        return created ? workDir : "";
    }

    /// <summary>日志里截断内容，避免一个几千字的模板把日志刷屏。</summary>
    private static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value ?? "";
        }

        return value.Substring(0, maxChars) + "…";
    }

    /// <summary>删除二维码临时目录；失败只当清理没做成，绝不能影响出图结果。</summary>
    private static void CleanupTempDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (Exception ex)
        {
            PluginLog.Timing($"[二维码] 临时目录清理失败：{ex.Message}");
        }
    }


    /// <summary>
    /// 按块名找图框定义。先精确匹配；未命中再退「同系列」——按块名前缀找唯一候选，
    /// 用于加长图框（如 A1图框-900 用 A1图框 的槽位，因为加长只改长边、图签位置不变）。
    /// </summary>
    private static TitleBlockDefinition? FindDefinition(string blockName)
    {
        if (string.IsNullOrWhiteSpace(blockName))
        {
            return null;
        }

        TitleBlockLibrary library;
        try
        {
            library = TitleBlockLibraryStore.Load();
        }
        catch (Exception ex)
        {
            PluginLog.Error("签章", "读取图框库失败，跳过盖章：" + ex.Message, ex);
            return null;
        }

        var exact = library.Blocks.FirstOrDefault(
            x => string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        var normalized = Normalize(blockName);
        var candidates = library.Blocks
            .Where(x => !string.IsNullOrWhiteSpace(x.BlockName)
                        && normalized.StartsWith(Normalize(x.BlockName), StringComparison.Ordinal))
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>去掉分隔符与空白并转小写，用于块名前缀比较。</summary>
    private static string Normalize(string value)
    {
        var chars = value.Where(ch => !char.IsWhiteSpace(ch) && ch != '-' && ch != '_' && ch != '.').ToArray();
        return new string(chars).ToLowerInvariant();
    }
}
