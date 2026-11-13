using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.Runtime;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

public sealed partial class BatchPlotCommands
{
    [CommandMethod("ZBP_THREE_POINT_LIBRARY", CommandFlags.Session)]
    public void ManageThreePointLibrary()
    {
        // 录入向导必须在“没有别的插件模态窗”的上下文里跑：
        // 管理窗点「新建图签信息」后先关掉自己，向导跑完再把管理窗重新打开。
        // 原先是“隐藏的管理窗里再弹一个 CAD 模态录入窗”，嵌套模态的拆装会让 CAD 崩。
        while (true)
        {
            var form = new ThreePointLibraryManagerForm();
            CadDialog.ShowModal(form);

            var createNew = form.RequestNewCapture;
            var recapture = form.RequestRecapture;
            if (!createNew && recapture == null)
            {
                return;
            }

            try
            {
                CaptureThreePointDefinition(createNew ? null : recapture);
            }
            catch (System.Exception ex)
            {
                LogCaptureStep("capture wizard crashed: " + ex);
                MessageBox.Show(
                    "录入图签信息时出错，已取消本次录入（图签库未改动）。\n\n" + ex.Message,
                    "批量打印", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [CommandMethod("_ZBP_INTERNAL_THREE_POINT_LIBRARY", CommandFlags.Session)]
    public void ManageThreePointLibraryLegacy() => ManageThreePointLibrary();

    /// <summary>
    /// 三点图签录入向导：点三个文字（单行/多行均可） → 弹录入窗（名称/排序优先级/各字段范围）→ 保存。
    /// 不走“先选图签块”这一步，锚点直接在整张图纸上点取（块内文字同样可用）；
    /// 名称先按锚点文字自动填好，可在录入窗里改。
    /// <paramref name="existing"/> 为空表示新建；非空表示按原条目重新录入，
    /// 沿用名称、Id、优先级与容差等设置，只是重新点锚点、按需重框范围。
    /// </summary>
    internal static ThreePointDefinition? CaptureThreePointDefinition(ThreePointDefinition? existing = null)
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return null;
        }

        var editor = doc.Editor;
        LogCaptureStep($"wizard start: existing={(existing == null ? "(new)" : existing.Name)}");

        // 与图框入库一致：录入三点文字必须在 WCS 下进行，避免非世界坐标系下锚点/范围对不齐。
        var ucsBlocker = DescribeUcsBlocker(editor);
        if (ucsBlocker != null)
        {
            MessageBox.Show(ucsBlocker, "批量打印", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        // UCS→WCS 一次取齐，后面三个锚点、字段范围都用同一份（取点期间用户改不了 UCS）。
        var ucsToWcs = GetUcsToWcs(editor);
        var ucsAxes = ucsToWcs.CoordinateSystem3d;
        LogCaptureStep($"ucs: origin=({ucsAxes.Origin.X:0.###},{ucsAxes.Origin.Y:0.###},{ucsAxes.Origin.Z:0.###}) "
            + $"x=({ucsAxes.Xaxis.X:0.####},{ucsAxes.Xaxis.Y:0.####},{ucsAxes.Xaxis.Z:0.####}) "
            + $"y=({ucsAxes.Yaxis.X:0.####},{ucsAxes.Yaxis.Y:0.####},{ucsAxes.Yaxis.Z:0.####})");

        ThreePointDefinition definition;
        if (existing == null)
        {
            // 新建不再先问名称：直接进选块/点锚点，名称在锚点确定后按锚点文字自动生成，
            // 需要改名可在库管理窗口点「重命名」。
            definition = new ThreePointDefinition();
        }
        else
        {
            // 重新录入：名称与设置沿用原条目，只换锚点和范围；
            // 已有字段范围一并带入录入窗，改哪项就点哪项，不必全部重框。
            definition = new ThreePointDefinition
            {
                Id = existing.Id,
                Name = existing.Name,
                Enabled = existing.Enabled,
                SimilarityTolerancePercent = existing.SimilarityTolerancePercent,
                Priority = existing.Priority,
                AllowMirror = existing.AllowMirror,
                PaperName = existing.PaperName,
                PaperWidthMm = existing.PaperWidthMm,
                PaperHeightMm = existing.PaperHeightMm,
                CreatedAt = existing.CreatedAt,
                Fields = new Dictionary<string, LocalRectangle>(existing.Fields),
                SearchRegion = existing.SearchRegion
            };
            editor.WriteMessage($"\n重新录入三点图签「{definition.Name}」。");
        }

        // ---- 三个锚点 ----
        // 收集文字（整张图纸，含块内文字与属性）要遍历当前空间里的每个实体：
        // 十几万实体的图上实测 0.3~1 s，原先放在“点按钮之后、取点提示之前”，
        // 于是点「新建图签信息」会先卡一下（UCS 与 WCS 不一致的图上格外明显，因为那张图更大）。
        // 现在改成第一次取点之后再按需采集一次，取点提示立刻出来，后两个锚点直接复用结果。
        // 不再单独“选图签块”：CollectCurrentSpace 递归展开块参照，
        // 块内文字同样能作锚点，少一步交互。
        var samples = new List<ThreePointTextSample>();
        var samplesLoaded = false;
        var anchors = new List<ThreePointTextSample>();
        while (anchors.Count < 3)
        {
            var prompt = new PromptPointOptions($"\n点取第 {anchors.Count + 1} 个锚点文字（单行/多行文字均可，三点尽量分散）: ");
            if (anchors.Count == 0)
            {
                LogCaptureStep("first anchor prompt shown（文字采集推迟到首次取点之后）");
            }

            var pointResult = editor.GetPoint(prompt);
            if (pointResult.Status != PromptStatus.OK)
            {
                return null;
            }

            if (!samplesLoaded)
            {
                samplesLoaded = true;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    samples = ThreePointTextCollector.CollectCurrentSpace(doc, tr);
                    tr.Commit();
                }

                LogCaptureStep($"texts collected: {samples.Count} "
                    + $"(anchorable={samples.Count(x => x.CanAnchor)}) in {watch.ElapsedMilliseconds} ms");

                if (samples.Count == 0)
                {
                    MessageBox.Show("当前图纸里没有读到文字，无法新建图签信息。", "批量打印", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }

                if (!samples.Any(x => x.CanAnchor))
                {
                    MessageBox.Show("没有读到可作锚点的文字（单行、多行、块属性均可）。\n请确认图面文字位于当前模型空间或布局，外部参照请先绑定。",
                        "批量打印", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }

                var anchorableCount = samples.Count(x => x.CanAnchor);
                if (anchorableCount < 3)
                {
                    // 「点来点去只能选一个文字」多半是文字池里压根只有一两个可作锚点的文字：
                    // 文字在外部参照里、或当前不在文字所在的空间/布局。
                    editor.WriteMessage($"\n提示：当前空间可作锚点的文字只有 {anchorableCount} 个"
                        + $"（共采集到 {samples.Count} 个文字，单行/多行均可作锚点）。三点不共线需要 3 个分散的锚点，"
                        + "请确认图签文字位于当前模型空间或布局；若是外部参照请先绑定。");
                }
            }

            var worldPick = pointResult.Value.TransformBy(ucsToWcs);
            var sample = FindNearestAnchorText(samples, worldPick);
            if (sample == null && !ucsToWcs.IsEqualTo(Matrix3d.Identity))
            {
                // 少数宿主（含部分兼容 .NET API 的国产 CAD）GetPoint 直接返回 WCS。
                // 主路径没命中时再用原始点试一次，避免整张图只能点中一个文字。
                sample = FindNearestAnchorText(samples, pointResult.Value);
                if (sample != null)
                {
                    LogCaptureStep("anchor pick: host appears to return WCS from GetPoint, used raw pick point");
                }
            }

            LogCaptureStep($"anchor pick {anchors.Count + 1}: "
                + $"raw=({pointResult.Value.X:0.###},{pointResult.Value.Y:0.###}) "
                + $"wcs=({worldPick.X:0.###},{worldPick.Y:0.###}) -> "
                + (sample == null ? "no match" : $"'{sample.Text}' @ ({sample.Point.X:0.###},{sample.Point.Y:0.###})"));

            if (sample == null)
            {
                editor.WriteMessage("距点取位置 3 倍字高内没有可用的文字（单行/多行均可），请重新点取。");
                continue;
            }

            // 只按对象身份判重。旧实现按坐标判重：图签里多个单行文字共用同一插入点
            // （居中/右对齐时 Position 相同、AlignmentPoint 不同）会被误判成「已经选过」，
            // 表现就是点来点去只能选到一个文字。
            if (anchors.Contains(sample))
            {
                editor.WriteMessage($"\n该文字（{sample.Text}）已经选过了，请换一个。");
                continue;
            }

            anchors.Add(sample);
            editor.WriteMessage($"\n已选锚点 {anchors.Count}: {sample.Text}");
        }

        if (!IsTripleUsable(anchors[0].Point, anchors[1].Point, anchors[2].Point))
        {
            MessageBox.Show("三个锚点里有重合的点（或三个点几乎聚在一起），无法确定方向与缩放比例。\n请选择彼此分开的三个文字。",
                "批量打印", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        definition.Anchors = anchors
            .Select(x => new ThreePointAnchor { Text = x.Text, X = x.Point.X, Y = x.Point.Y, Height = x.Height })
            .ToList();
        LogCaptureStep("anchors: " + definition.AnchorSummary);

        // 共线也允许录入：求解时会自动降级成相似变换。这里把限制先讲清楚，
        // 免得用户以为共线锚点同样支持非等比缩放和镜像图签。
        if (definition.IsCollinearAnchors)
        {
            editor.WriteMessage("\n提示：三个锚点接近共线，将按相似变换处理（等比缩放 + 旋转 + 平移）；"
                + "不支持非等比缩放与镜像图签。需要这两项能力请改选成三角形的三个文字。");
            LogCaptureStep("anchors collinear: similar transform only");
        }

        if (existing == null)
        {
            definition.Name = BuildAutoName(anchors);
            editor.WriteMessage($"\n三个锚点已确定，名称自动生成为「{definition.Name}」，接着在弹出的窗口里定义字段范围。");
        }

        // ---- 录入窗：名称 / 排序优先级 / 各字段范围 ----
        // 把逐个框选搬到窗口里，按钮点一次取一个范围，避免命令行连问十几次。
        // 此时没有别的插件模态窗（管理窗已先关闭），录入窗就是唯一的顶层模态窗。
        LogCaptureStep("showing capture form");
        var captureForm = new ThreePointCaptureForm(definition, allowRename: existing == null);

        // 录入时在图面显示红框（矩形 + 对角线，临时图素，不写数据库）：
        // 框完某个字段立刻能看到框住了哪块，和「图框型」的字段框选窗核对方式一致。
        // 标高跟着锚点所在平面走，避免红框画在 Z=0 被图面挡住。
        var planeZ = anchors.Count > 0 ? anchors.Average(x => x.Point.Z) : 0;
        using (var markers = new TransientFrameMarkers(editor))
        {
            captureForm.AttachRegionPreview((key, label, region) =>
            {
                if (region == null)
                {
                    markers.Remove(key);
                }
                else
                {
                    markers.SetBox(key, region, label, planeZ);
                }
            });

            ShowCaptureFormAndWait(captureForm);
        }

        LogCaptureStep($"capture form closed: confirmed={captureForm.Confirmed}");
        if (!captureForm.Confirmed || captureForm.Result == null)
        {
            return null;
        }

        definition = captureForm.Result;
        var savedName = "";
        var probeCount = 0;
        string? failure = null;
        try
        {
            // ---- 保存 ----
            // 名称自动生成，正常情况下不会撞名；若库中已有锚点文字相同的条目，
            // 说明多半是重复录入，问一次是覆盖还是另存一条。
            var library = ThreePointLibraryStore.Load();
            if (existing == null)
            {
                var duplicate = library.Items.FirstOrDefault(x =>
                    string.Equals(x.AnchorSummary, definition.AnchorSummary, StringComparison.OrdinalIgnoreCase));
                if (duplicate != null)
                {
                    var sameGeometry = AnchorsIdentical(duplicate, definition);
                    var question = sameGeometry
                        ? $"库中已有锚点完全相同的图签「{duplicate.Name}」，看起来是同一种图签。\n\n是：覆盖该条目（推荐）\n否：另存为一条新图签"
                        : $"库中已有锚点文字相同的图签「{duplicate.Name}」，但锚点位置不同，可能是另一种图签。\n\n是：覆盖该条目\n否：另存为一条新图签（推荐）";
                    var overwrite = MessageBox.Show(question, "批量打印", MessageBoxButton.YesNo,
                        MessageBoxImage.Question, sameGeometry ? MessageBoxResult.Yes : MessageBoxResult.No);
                    if (overwrite == MessageBoxResult.Yes)
                    {
                        definition.Id = duplicate.Id;
                        definition.Name = duplicate.Name;
                        definition.CreatedAt = duplicate.CreatedAt;
                    }
                    else
                    {
                        definition.Name = MakeUniqueName(library, definition.Name);
                    }
                }
                else
                {
                    definition.Name = MakeUniqueName(library, definition.Name);
                }
            }

            LogCaptureStep($"saving: name={definition.Name}, fields={definition.Fields.Count}, priority={definition.Priority}");
            ThreePointLibraryStore.Upsert(definition);
            savedName = definition.Name;
            LogCaptureStep("saved ok");

            // ---- 试识别 ----
            try
            {
                probeCount = ThreePointTitleScanner.Probe(doc, definition).Count;
                LogCaptureStep($"probe done: {probeCount}");
            }
            catch (System.Exception ex)
            {
                LogCaptureStep("probe failed: " + ex);
            }
        }
        catch (System.Exception ex)
        {
            LogCaptureStep("save failed: " + ex);
            failure = ex.Message;
        }

        if (failure != null)
        {
            MessageBox.Show("图签信息保存失败，库文件未改动：\n\n" + failure,
                "批量打印", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }

        var message = $"{(existing == null ? "已新建图签信息" : "已重新录入")}「{savedName}」。\n\n"
            + $"锚点：{definition.AnchorSummary}\n"
            + $"字段：{definition.Fields.Count} 项（{string.Join("、", definition.Fields.Keys.Select(ThreePointFieldKeys.Display))}）\n"
            + $"排序优先级：{definition.Priority}\n"
            + $"试识别：本图命中 {probeCount} 处";
        if (existing == null)
        {
            message += "\n\n名称与优先级在录入窗里填，之后也可在管理窗口选该行点「重命名」改名。";
        }
        if (probeCount == 0)
        {
            message += "\n\n注意：本图没有命中。请确认三个锚点选的是固定不变的文字（单行/多行均可，如“图名”“图号”这类标签），"
                + "而不是随项目变化的内容。";
        }

        MessageBox.Show(message, "批量打印", MessageBoxButton.OK,
            probeCount > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

        return definition;
    }

    /// <summary>
    /// 显示录入窗并一直等到用户把它关掉（确定 / 取消 / 关闭按钮）。
    /// <para>
    /// 这里刻意不用 <see cref="CadDialog.ShowModal"/>：录入窗里每点一次「范围」都要把整窗隐藏，
    /// 而 CAD 的模态循环在窗口隐藏后会提前结束，调用方就被当成“用户已取消”，
    /// 于是框完一个范围图签库管理窗会自己冒出来；之后再点确定，又是在一个模态循环已经结束的窗口上收尾，
    /// 表现为卡死。改成非模态显示 + 本线程消息泵等待窗口关闭：Hide/Restore 走的是主面板那条成熟路径，
    /// 窗口生命周期与 CAD 模态状态彻底解耦。
    /// </para>
    /// </summary>
    private static void ShowCaptureFormAndWait(Window window)
    {
        var frame = new DispatcherFrame();
        void OnClosed(object? sender, EventArgs e)
        {
            window.Closed -= OnClosed;
            frame.Continue = false;
        }

        window.Closed += OnClosed;
        try
        {
            CadDialog.ShowModelessTransient(window);
            if (!window.IsVisible)
            {
                LogCaptureStep("capture form did not show");
                return;
            }

            Dispatcher.PushFrame(frame);
        }
        finally
        {
            window.Closed -= OnClosed;
            if (window.IsVisible)
            {
                // 消息泵因异常提前退出时，别把窗口留在图面上当孤儿窗。
                try
                {
                    window.Close();
                }
                catch
                {
                    // 关不掉也不能再抛出去，否则会连带 CAD 一起挂。
                }
            }
        }
    }

    /// <summary>
    /// UCS→WCS 变换矩阵。图面点取（GetPoint/GetCorner）给的是 UCS 坐标，
    /// 而文字采集、图框检测都用 WCS，取不到时退回单位矩阵。
    /// </summary>
    private static Matrix3d GetUcsToWcs(Editor editor)
    {
        try
        {
            return editor.CurrentUserCoordinateSystem;
        }
        catch
        {
            return Matrix3d.Identity;
        }
    }

    /// <summary>
    /// 录入三点文字必须在世界坐标系下进行。返回 null 表示当前已是 WCS，否则返回提示文字。
    /// </summary>
    private static string? DescribeUcsBlocker(Editor editor)
    {
        Matrix3d ucs;
        try
        {
            ucs = editor.CurrentUserCoordinateSystem;
        }
        catch (System.Exception ex)
        {
            LogCaptureStep("read ucs failed: " + ex);
            return UcsToWorldMessage;
        }

        if (ucs.IsEqualTo(Matrix3d.Identity))
        {
            return null;
        }

        LogCaptureStep("ucs is not world, capture blocked");
        return UcsToWorldMessage;
    }

    private const string UcsToWorldMessage =
        "录入三点文字前请先将 UCS 切换为世界坐标系（WCS）。\n命令行输入 UCS 然后回车即可。";


    /// <summary>
    /// 录入窗里点「范围」按钮时调用：先隐藏本窗再去图面框两个角点，取完自动恢复窗口。
    /// 用户按 Esc 取消或框出的范围过小时返回 null，窗口保持原状。
    /// 返回的矩形已换算到 WCS，并带真实四角（旋转 UCS 下是斜矩形）。
    /// </summary>
    internal static LocalRectangle? PickRectangle(Window dialog, string label)
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return null;
        }

        var editor = doc.Editor;
        LogCaptureStep($"pick range start: {label}");
        CadWindowFocus.HideForCadInput(dialog);
        try
        {
            var ucs = GetUcsToWcs(editor);
            var first = editor.GetPoint(new PromptPointOptions($"\n框选「{label}」- 第一角点: "));
            if (first.Status != PromptStatus.OK)
            {
                LogCaptureStep($"pick range cancelled at first point: {label} ({first.Status})");
                return null;
            }

            // 第二角点走 DrawJig：拖动过程中实时画红色矩形（含对角线），
            // 用户在点下去之前就能看清这一框圈住了哪片文字，不必「点完才发现框偏了」。
            // 提交后的红框仍由录入窗用 TransientFrameMarkers 画，两者同形同位，不会跳变。
            var jig = new RangeCornerJig(first.Value, ucs, label);
            var second = editor.Drag(jig);
            if (second.Status != PromptStatus.OK || !jig.HasPoint)
            {
                LogCaptureStep($"pick range cancelled at corner: {label} ({second.Status})");
                return null;
            }

            // GetPoint 返回 UCS，jig 的 AcquirePoint 返回 WCS（ARX 文档明确、托管文档没写）。
            // 旧实现把两者都当 UCS 又乘了一次 UCS→WCS，旋转/带原点的 UCS 下框会跑到反方向、尺寸翻倍；
            // 而且 FromPoints 会把 Min/Max 归一化，从右上往左下拖时框的角点顺序被翻转。
            // 现在统一交给 RectangleGeometry.FromUcsDragCorners：在 WCS 里把位移分解到 UCS 基轴，
            // 保留实际拖动方向，真实四角写进 CornerPoints（旋转 UCS 下是斜矩形，识别按四边形判定）。
            var rectangle = RectangleGeometry.FromUcsDragCorners(first.Value, jig.CurrentWcs, ucs);
            LogCaptureStep($"pick range points: {label} "
                + $"firstUcs=({first.Value.X:0.###},{first.Value.Y:0.###}) "
                + $"secondWcs=({jig.CurrentWcs.X:0.###},{jig.CurrentWcs.Y:0.###}) "
                + $"secondUcs=({jig.CurrentUcs.X:0.###},{jig.CurrentUcs.Y:0.###}) "
                + $"box=({rectangle.MinX:0.###},{rectangle.MinY:0.###})-({rectangle.MaxX:0.###},{rectangle.MaxY:0.###})");

            if (!rectangle.HasArea()
                || rectangle.ActualWidth < 1e-6
                || rectangle.ActualHeight < 1e-6)
            {
                return null;
            }

            return rectangle;
        }
        finally
        {
            CadWindowFocus.RestoreDialog(dialog);
            LogCaptureStep($"pick range end: {label}");
        }
    }

    /// <summary>
    /// 三点图签录入过程的轻量诊断日志。始终写（不受打印日志开关影响），
    /// 因为这一段是“点确定后 CAD 崩溃”的现场，出问题时需要有迹可查；写入失败静默忽略。
    /// </summary>
    internal static void LogCaptureStep(string message)
    {
        try
        {
            var directory = BatchPlotLogger.LogDirectory;
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "ThreePointCapture.log");
            System.IO.File.AppendAllText(
                path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + message + Environment.NewLine);
        }
        catch
        {
            // 诊断日志写不进去不能影响录入流程。
        }
    }

    /// <summary>库管理窗口的“试识别”：返回可直接弹给用户看的文本报告。</summary>
    internal static string ProbeForManager(ThreePointDefinition definition)
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return "当前没有打开的图纸。";
        }

        List<ThreePointMatch> matches;
        try
        {
            matches = ThreePointTitleScanner.Probe(doc, definition);
        }
        catch (System.Exception ex)
        {
            return "试识别失败: " + ex.Message;
        }

        if (matches.Count == 0)
        {
            return "本图未命中该三点图签。\n\n请确认：\n"
                + "1) 三个锚点选的是固定不变的文字（单行/多行均可）；\n"
                + "2) 三个点足够分散（共线也可，但共线时只按等比缩放 + 旋转处理）；\n"
                + "3) 当前图纸与录入时的图签比例关系一致。";
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"本图命中 {matches.Count} 处，下面列出前 3 处的字段取值：");
        foreach (var match in matches.Take(3))
        {
            builder.AppendLine();
            builder.AppendLine($"— 相似性误差 {match.Score * 100:0.##}% —");
            foreach (var key in ThreePointFieldKeys.CaptureOrder)
            {
                if (match.Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    builder.AppendLine($"{ThreePointFieldKeys.Display(key)}：{value}");
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 按三个锚点文字自动生成名称，例如锚点为“图名/图号/比例”时得到“图名-图号-比例”。
    /// 用户不输入名称，避免在向导开头打断“选块→点三点”的动作。
    /// </summary>
    private static string BuildAutoName(IReadOnlyList<ThreePointTextSample> anchors)
    {
        var parts = new List<string>();
        foreach (var anchor in anchors)
        {
            var text = (anchor.Text ?? "").Trim();
            if (text.Length == 0 || parts.Contains(text))
            {
                continue;
            }

            parts.Add(text);
        }

        var name = string.Join("-", parts);
        if (name.Length > 40)
        {
            name = name.Substring(0, 40).Trim();
        }

        return name.Length > 0 ? name : "三点图签 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>名称重复时追加序号，保证“另存为一条新图签”不会覆盖已有条目。</summary>
    private static string MakeUniqueName(ThreePointLibrary library, string candidate)
    {
        bool Taken(string value) => library.Items.Any(x =>
            string.Equals(x.Name, value, StringComparison.OrdinalIgnoreCase));

        if (!Taken(candidate))
        {
            return candidate;
        }

        for (var index = 2; index <= 99; index++)
        {
            var name = $"{candidate}({index})";
            if (!Taken(name))
            {
                return name;
            }
        }

        return $"{candidate}({DateTime.Now:HHmmss})";
    }

    /// <summary>三个锚点的文字与插入点是否完全一致（用于判断是否为同一种图签）。</summary>
    private static bool AnchorsIdentical(ThreePointDefinition left, ThreePointDefinition right)
    {
        if (left.Anchors.Count != 3 || right.Anchors.Count != 3)
        {
            return false;
        }

        for (var index = 0; index < 3; index++)
        {
            var a = left.Anchors[index];
            var b = right.Anchors[index];
            if (!string.Equals(a.Text, b.Text, StringComparison.Ordinal)
                || Math.Abs(a.X - b.X) > 1e-6
                || Math.Abs(a.Y - b.Y) > 1e-6)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>点取位置附近最近的文字（单行或多行），距离按字高归一化。</summary>
    private static ThreePointTextSample? FindNearestAnchorText(
        IReadOnlyList<ThreePointTextSample> samples,
        Point3d pickPoint)
    {
        ThreePointTextSample? best = null;
        var bestScore = double.MaxValue;
        foreach (var sample in samples)
        {
            if (!sample.CanAnchor)
            {
                continue;
            }

            var reference = Math.Max(sample.Height, 1.0);
            // 非左下对齐的文字同时留了 Position 与 AlignmentPoint，两个位置都参与命中判断：
            // 只比 Primary 会让「点在文字上却选不中」，看起来像只有少数文字能点。
            var distance = DistanceBetween(sample.Point, pickPoint);
            if (sample.AltPoint.HasValue)
            {
                distance = Math.Min(distance, DistanceBetween(sample.AltPoint.Value, pickPoint));
            }

            // 多行文字的插入点随 Attachment 可能落在左上/中心，文字本体却延伸很宽；
            // 几何代表点（中心 + 四边中点）也参与命中，点在文字任意位置都能选中。
            if (sample.ExtraPoints != null)
            {
                foreach (var point in sample.ExtraPoints)
                {
                    distance = Math.Min(distance, DistanceBetween(point, pickPoint));
                    if (distance <= 0)
                    {
                        break;
                    }
                }
            }

            // 允许点在文字附近：3 倍字高以内视为命中。
            if (distance > reference * 3.0 + 2.0)
            {
                continue;
            }

            var score = distance / reference;
            if (score < bestScore)
            {
                bestScore = score;
                best = sample;
            }
        }

        return best;
    }

    /// <summary>
    /// 三点几何是否可用于定标。
    /// <para>
    /// 早先要求三点撑开成三角形（高 / 最长边 &gt; 0.08），共线一律拒绝。
    /// 现在共线也放行：求解时检测到共线会自动降级为相似变换（等比缩放 + 旋转 + 平移），
    /// 由距离最远的一对点定标、第三个点做一致性校验。所以这里只排除
    /// 「有两点重合」和「三点几乎聚成一个点」这两种真正没法定标的情形。
    /// </para>
    /// </summary>
    private static bool IsTripleUsable(Point3d a, Point3d b, Point3d c)
    {
        var ab = DistanceBetween(a, b);
        var ac = DistanceBetween(a, c);
        var bc = DistanceBetween(b, c);
        var min = Math.Min(ab, Math.Min(ac, bc));
        var max = Math.Max(ab, Math.Max(ac, bc));
        return min > 1e-6 && max > 1e-6;
    }

    private static double DistanceBetween(Point3d a, Point3d b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
