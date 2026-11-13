using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
#if AUTOCAD
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif


// CadUsings.cs 的全局 using 让 CAD 命名空间里的同名类型也进了作用域，这里钉死本文件要的那个：
// WinForms 布局的流向枚举。
using FlowDirection = System.Windows.Forms.FlowDirection;
namespace ZwcadBatchPlot;

/// <summary>
/// 图框签章区域设置：9 个固定槽位，逐个「框选」图签上对应的格子。
/// <para>
/// 槽位名固定（设计人员1~6、出图章、注册师章、竣工图章）而不是让用户随便起名，
/// 是为了让不同图框、不同项目的签章位置有一致的语义：换一张图框重配时不用再想名字，
/// 打印时也能按槽位名统一套用预设。
/// </para>
/// <para>
/// 一个槽位可以有**多个位置**：同一个人既要在图签栏签名，又要在会签栏签名（或一张图上两个图签）。
/// 点一次「框选」进入连框模式，可以连着框多个格子，右键结束；第一个框会替换旧位置，后续框是追加。
/// </para>
/// <para>
/// 每个槽位还可以勾「自动读取」：出图时从签名库自动取同类签名（库里只有一个手写签名时就是它），
/// 换人只需换库里的图片，图框库不用动。不勾则按下拉里手工绑定的那条盖。
/// </para>
/// <para>
/// 每次框选同时算两份数据：相对打印范围右下角的块内坐标（<see cref="SignatureSlotRegion.CadRegion"/>）
/// 与纸张毫米偏移（<see cref="SignatureSlotRegion.RightInsetMm"/> 等）。前者保证打印范围或纸张被改动时
/// 能重新换算，后者是 PDF 盖章实际使用的值。不运行时互算——打印窗口会被留白、居中、
/// 学习修正多次改写，从图面坐标反推纸面毫米并不可靠。
/// </para>
/// </summary>
public sealed class SignatureSlotSetupDialog : Form
{
    /// <summary>固定槽位名，顺序即界面顺序。签章相关的其余代码也以此为白名单。</summary>
    public static readonly string[] StandardSlots =
    {
        "设计人员1", "设计人员2", "设计人员3", "设计人员4", "设计人员5", "设计人员6",
        "出图章", "注册师章", "竣工图章",
    };

    private readonly Editor _editor;
    private readonly Matrix3d _inverseBlockTransform;
    private readonly Matrix3d _blockTransform;
    private readonly TransientFrameMarkers _markers;
    private readonly Dictionary<string, SignatureSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Label> _statusLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ComboBox> _signatureCombos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _autoChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolTip _toolTip = new();
    private readonly SignatureLibrary _library;

    /// <summary>重建下拉项时置位，挡住 SelectionChanged / CheckedChanged 的相互触发。</summary>
    private bool _updating;

    /// <summary>打印范围（块局部坐标），用作签章区域换算的基准。</summary>
    public LocalRectangle ReferenceFrame { get; }

    public double PaperWidthMm { get; }

    public double PaperHeightMm { get; }

    /// <summary>
    /// 确定后的槽位列表：先按固定顺序，再补上旧版自定义槽位（原样保留，不做管理）。
    /// 完全空白的标准槽位（没位置也没绑定签名）不落库，避免图框库里堆 9 条空记录。
    /// </summary>
    public List<SignatureSlot> Slots
    {
        get
        {
            var result = new List<SignatureSlot>();
            foreach (var name in StandardSlots)
            {
                if (!_slots.TryGetValue(name, out var slot))
                {
                    continue;
                }

                if (slot.GetRegions().Count == 0 && !HasSignatureBinding(slot))
                {
                    continue;
                }

                result.Add(slot);
            }

            result.AddRange(_slots
                .Where(pair => !StandardSlots.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .Select(pair => pair.Value));
            return result;
        }
    }

    public SignatureSlotSetupDialog(
        Editor editor,
        Matrix3d inverseBlockTransform,
        Matrix3d blockTransform,
        TransientFrameMarkers markers,
        LocalRectangle referenceFrame,
        double paperWidthMm,
        double paperHeightMm,
        IEnumerable<SignatureSlot>? existing)
    {
        _editor = editor;
        _inverseBlockTransform = inverseBlockTransform;
        _blockTransform = blockTransform;
        _markers = markers;
        ReferenceFrame = referenceFrame;
        PaperWidthMm = paperWidthMm;
        PaperHeightMm = paperHeightMm;
        _library = LoadLibrary();

        foreach (var slot in existing ?? Enumerable.Empty<SignatureSlot>())
        {
            if (slot == null || string.IsNullOrWhiteSpace(slot.SlotName))
            {
                continue;
            }

            var copy = CloneSlot(slot);
            copy.SlotName = slot.SlotName.Trim();

            // 旧版命令行入口可以给槽位随便起名（设计/审核…）。这里不认识的槽位**原样保留**，
            // 只在提示里列出名字——直接丢弃等于用户一编辑图框就静默少掉几个章，比藏在界面外的问题大得多。
            EnsureCadRegion(copy);
            ApplyFrameScale(copy, ReferenceFrame, PaperWidthMm, PaperHeightMm);
            _slots[copy.SlotName] = copy;
        }

        // 9 个固定槽位一律先建好：签名绑定可以在框位置之前就设，不必等有格子。
        foreach (var name in StandardSlots)
        {
            if (!_slots.ContainsKey(name))
            {
                _slots[name] = new SignatureSlot { SlotName = name };
            }
        }

        Text = "设置签章区域";
        // 高度留出提示行：有旧版自定义槽位时提示会多出一行。
        UiLayout.ConfigureForm(this, 690, 476, 640, 444);
        ClientSize = new Size(UiLayout.Scale(690), UiLayout.Scale(476));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        // 与父对话框一致：核对红框时不挡 CAD 缩放/平移。
        TopMost = true;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(UiLayout.Scale(12), UiLayout.Scale(8), UiLayout.Scale(12), UiLayout.Scale(8))
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var _ in StandardSlots)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
        }

        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(36)));

        for (var index = 0; index < StandardSlots.Length; index++)
        {
            var slotName = StandardSlots[index];
            table.Controls.Add(MakeLabel(slotName), 0, index);

            var status = new Label
            {
                Text = "(未选择)",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray,
                AutoEllipsis = true
            };
            _statusLabels[slotName] = status;

            table.Controls.Add(MakeSlotRow(slotName, status), 1, index);
        }

        var legacySlots = _slots.Keys
            .Where(name => !StandardSlots.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var hintText =
            "逐项点「框选」，在 CAD 里框住图签上对应的格子；一次可以连框多个位置（右键结束），"
            + "适合一个人要签多处的情况。\n"
            + "「自动读取」＝出图时从签名库自动取同类签名（库里只有一个手写签名时就是它）；"
            + "不勾则按下拉里手工绑定的那条盖。设计人员槽位不填也可以，出图时自动跳过。\n"
            + "注意：这里点「确定」只写进本次编辑，返回后还要在「设置图框字段与纸张」窗口点「确定」才会存进图框库。";
        if (legacySlots.Count > 0)
        {
            hintText += $"\n另有 {legacySlots.Count} 个旧版自定义槽位原样保留（本窗口不管理）：{string.Join("、", legacySlots)}";
        }

        var hint = new Label
        {
            Text = hintText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = Color.DimGray,
            Font = new Font(Font.FontFamily, Math.Max(Font.Size - 1, 7))
        };
        table.SetColumnSpan(hint, 2);
        table.Controls.Add(hint, 0, StandardSlots.Length);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, UiLayout.Scale(4), 0, 0)
        };
        var ok = UiLayout.CreateButton("确定", 76);
        var cancel = UiLayout.CreateButton("取消", 76);
        ok.Click += (_, _) =>
        {
            if (!ValidateBeforeAccept())
            {
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        };
        cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = ok;
        CancelButton = cancel;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        table.SetColumnSpan(buttons, 2);
        table.Controls.Add(buttons, 0, StandardSlots.Length + 1);

        Controls.Add(table);

        DrawSlotMarkers(_markers, _slots.Values, ReferenceFrame, _blockTransform, refresh: true);
        foreach (var slotName in StandardSlots)
        {
            RefreshSignatureState(slotName);
            UpdateStatus(slotName);
        }

        
    }

    /// <summary>
    /// 确定前提醒「框了位置但没选要盖什么」的槽位。
    /// <para>
    /// 这种槽位出图时会被静默跳过——用户勾了「打印印章」却什么都没盖出来，最常见的原因就是
    /// 章的位置框好了、下面那一栏却没选印章（或没勾自动读取）。
    /// </para>
    /// </summary>
    private bool ValidateBeforeAccept()
    {
        var pending = new List<string>();
        foreach (var name in StandardSlots)
        {
            if (!_slots.TryGetValue(name, out var slot) || HasSignatureBinding(slot))
            {
                continue;
            }

            if (slot.GetRegions().Count == 0)
            {
                continue;
            }

            pending.Add(name + (PdfStampService.IsStampSlotName(name) ? "（未选印章）" : "（未选签名）"));
        }

        if (pending.Count == 0)
        {
            return true;
        }

        var text = "以下槽位已经框了位置，但没选要盖的签名 / 印章，出图时不会盖：\n\n  "
                   + string.Join("\n  ", pending)
                   + "\n\n点「否」回去在下拉里选一个（或勾上「自动读取」）；点「是」只保存位置。";
        var answer = MessageBox.Show(
            this,
            text,
            Text,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return answer == DialogResult.Yes;
    }

    /// <summary>标记键前缀：与父对话框的字段键（图名/图号…）区分开，避免互相覆盖。</summary>
    public static string MarkerPrefix(string slotName) => "签章:" + slotName;

    /// <summary>第 <paramref name="index"/> 个位置的标记键（0 号沿用旧键名，保持兼容）。</summary>
    public static string MarkerKeyFor(string slotName, int index)
        => MarkerPrefix(slotName) + (index <= 0 ? "" : "#" + (index + 1));

    /// <summary>兼容旧调用：0 号位置。</summary>
    public static string MarkerKeyFor(string slotName) => MarkerKeyFor(slotName, 0);

    /// <summary>
    /// 连续框选一个槽位的多个位置：<c>GetPoint → GetCorner</c> 循环，右键 / ESC 结束。
    /// 第一个成功框替换该槽位原有位置，后续框追加——重新点「框选」即可整组重来，不会越点越多。
    /// </summary>
    private void PickSlot(string slotName)
    {
        if (!ReferenceFrame.HasArea() || PaperWidthMm <= 0d || PaperHeightMm <= 0d)
        {
            MessageBox.Show(this,
                "还没有确定打印范围或纸张。\n请先关闭本窗口，在上一个对话框里选好打印范围与纸张。",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!_slots.TryGetValue(slotName, out var slot))
        {
            slot = new SignatureSlot { SlotName = slotName };
        }

        var added = 0;
        BeginCadPick();
        try
        {
            while (true)
            {
                var firstPrompt = added == 0
                    ? $"\n框选「{slotName}」第 1 个位置第一个角点（可连续框多个位置，右键结束）: "
                    : $"\n继续框选「{slotName}」第 {added + 1} 个位置第一个角点（右键结束）: ";
                var first = _editor.GetPoint(new PromptPointOptions(firstPrompt));
                if (first.Status != PromptStatus.OK)
                {
                    break;
                }

                var second = _editor.GetCorner(
                    new PromptCornerOptions($"\n框选「{slotName}」第 {added + 1} 个位置对角点: ", first.Value));
                if (second.Status != PromptStatus.OK)
                {
                    break;
                }

                var p1 = first.Value.TransformBy(_inverseBlockTransform);
                var p2 = second.Value.TransformBy(_inverseBlockTransform);
                var local = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);
                if (!local.HasArea())
                {
                    continue;
                }

                if (added == 0)
                {
                    // 第一处框完即替换旧位置：重新框选 = 整组重来，避免和上次的位置混在一起。
                    slot.Regions = new List<SignatureSlotRegion>();
                    slot.CadRegion = new LocalRectangle();
                    slot.WidthMm = 0d;
                    slot.HeightMm = 0d;
                }

                var region = new SignatureSlotRegion
                {
                    // 相对打印范围右下角：与 ZBP_SIGN_SLOT 命令行入口保持同一语义，
                    // 加长图框延长左边长边时右下角不动，槽位自然跟着图签走。
                    CadRegion = LocalRectangle.FromPoints(
                        local.MinX - ReferenceFrame.MaxX,
                        local.MinY - ReferenceFrame.MinY,
                        local.MaxX - ReferenceFrame.MaxX,
                        local.MaxY - ReferenceFrame.MinY),
                };
                ApplyRegionScale(region, ReferenceFrame, PaperWidthMm, PaperHeightMm);
                slot.Regions.Add(region);

                added++;

                _slots[slotName] = slot;
                ApplyFrameScale(slot, ReferenceFrame, PaperWidthMm, PaperHeightMm);
                RedrawSlotMarkers(slotName);
                UpdateStatus(slotName);
            }
        }
        finally
        {
            EndCadPick();
        }

        if (added == 0)
        {
            // 一个都没框（右键退出）：保持原样，别把已有位置清掉。
            return;
        }

        var regions = slot.GetRegions();
        var last = regions[regions.Count - 1];
        PluginLog.Timing(
            $"[签章] 槽位「{slotName}」本次框选 {added} 处，共 {regions.Count} 处；"
            + $"末处 距右 {last.RightInsetMm:F1} 距下 {last.BottomInsetMm:F1} {last.WidthMm:F1}×{last.HeightMm:F1}mm");
    }

    private void ClearSlot(string slotName)
    {
        if (_slots.TryGetValue(slotName, out var slot))
        {
            // 只清位置，不清签名绑定：重新框位置时不用重选人。
            slot.Regions = new List<SignatureSlotRegion>();
            slot.CadRegion = new LocalRectangle();
            slot.WidthMm = 0d;
            slot.HeightMm = 0d;
        }

        _markers.RemoveByPrefix(MarkerPrefix(slotName));
        UpdateStatus(slotName);
    }

    private void UpdateStatus(string slotName)
    {
        if (!_statusLabels.TryGetValue(slotName, out var label))
        {
            return;
        }

        var regions = _slots.TryGetValue(slotName, out var slot)
            ? slot.GetRegions().Where(r => r.WidthMm > 0.1 && r.HeightMm > 0.1).ToList()
            : new List<SignatureSlotRegion>();

        if (regions.Count == 0)
        {
            label.Text = "(未选择)";
            label.ForeColor = Color.DimGray;
            _toolTip.SetToolTip(label, "");
            return;
        }

        var first = regions[0];
        var text = $"距右 {first.RightInsetMm:0.0}  距下 {first.BottomInsetMm:0.0}  "
            + $"{first.WidthMm:0.0}×{first.HeightMm:0.0} mm";
        if (regions.Count > 1)
        {
            text += $"  共 {regions.Count} 处";
        }

        // 位置框好了却没选要盖什么：出图会静默跳过，所以标黄提醒，别让用户以为已经配好。
        var missingBinding = slot != null && !HasSignatureBinding(slot);
        if (missingBinding)
        {
            text += PdfStampService.IsStampSlotName(slotName) ? "  ⚠未选印章" : "  ⚠未选签名";
        }

        label.Text = text;
        label.ForeColor = missingBinding ? Color.DarkOrange : Color.Green;
        _toolTip.SetToolTip(
            label,
            string.Join("\n", regions.Select((region, index) =>
                $"第 {index + 1} 处：距右 {region.RightInsetMm:0.0}、距下 {region.BottomInsetMm:0.0}、"
                + $"{region.WidthMm:0.0}×{region.HeightMm:0.0} mm")));
    }

    /// <summary>
    /// 按打印范围与纸张把块内相对矩形换算成纸张毫米偏移（全部位置一起换算）。
    /// 打印范围（<paramref name="frame"/>）在图面单位里，纸张是毫米，两者的比值就是比例。
    /// </summary>
    public static void ApplyFrameScale(
        SignatureSlot slot,
        LocalRectangle frame,
        double paperWidthMm,
        double paperHeightMm)
    {
        if (slot == null || !frame.HasArea() || paperWidthMm <= 0d || paperHeightMm <= 0d)
        {
            return;
        }

        var scaleX = paperWidthMm / (frame.MaxX - frame.MinX);
        var scaleY = paperHeightMm / (frame.MaxY - frame.MinY);

        if (slot.Regions != null && slot.Regions.Count > 0)
        {
            foreach (var region in slot.Regions)
            {
                if (region != null)
                {
                    ApplyRegionScale(region, frame, paperWidthMm, paperHeightMm, scaleX, scaleY);
                }
            }

            // 旧读取方（命令行列表、早期 UI）只看槽位上的单组毫米值：同步成第一处，别让它们读到 0。
            var first = slot.Regions[0];
            if (first != null)
            {
                slot.CadRegion = first.CadRegion;
                slot.RightInsetMm = first.RightInsetMm;
                slot.BottomInsetMm = first.BottomInsetMm;
                slot.WidthMm = first.WidthMm;
                slot.HeightMm = first.HeightMm;
            }

            return;
        }

        slot.WidthMm = (slot.CadRegion.MaxX - slot.CadRegion.MinX) * scaleX;
        slot.HeightMm = (slot.CadRegion.MaxY - slot.CadRegion.MinY) * scaleY;
        // CadRegion 的 X 向右为正、Y 向上为正，原点在打印范围右下角：
        // 距右 = -MaxX、距下 = MinY。
        slot.RightInsetMm = -slot.CadRegion.MaxX * scaleX;
        slot.BottomInsetMm = slot.CadRegion.MinY * scaleY;
    }

    private static void ApplyRegionScale(
        SignatureSlotRegion region,
        LocalRectangle frame,
        double paperWidthMm,
        double paperHeightMm,
        double? knownScaleX = null,
        double? knownScaleY = null)
    {
        if (region == null || !frame.HasArea() || paperWidthMm <= 0d || paperHeightMm <= 0d)
        {
            return;
        }

        var scaleX = knownScaleX ?? paperWidthMm / (frame.MaxX - frame.MinX);
        var scaleY = knownScaleY ?? paperHeightMm / (frame.MaxY - frame.MinY);
        region.WidthMm = (region.CadRegion.MaxX - region.CadRegion.MinX) * scaleX;
        region.HeightMm = (region.CadRegion.MaxY - region.CadRegion.MinY) * scaleY;
        region.RightInsetMm = -region.CadRegion.MaxX * scaleX;
        region.BottomInsetMm = region.CadRegion.MinY * scaleY;
    }

    /// <summary>
    /// 只有毫米值、没有块内坐标的历史槽位（早期命令行录入的）：按当前打印范围反推出 CadRegion，
    /// 否则界面上量不出来、打印范围一改就彻底失准。
    /// </summary>
    private static void EnsureCadRegion(SignatureSlot slot)
    {
        if (slot.Regions != null && slot.Regions.Count > 0)
        {
            return;
        }

        if (slot.CadRegion != null && slot.CadRegion.HasArea())
        {
            return;
        }

        slot.CadRegion ??= new LocalRectangle();
        if (slot.WidthMm <= 0.1 || slot.HeightMm <= 0.1)
        {
            return;
        }

        // 只勾了毫米、没有块内坐标时先按 1:1 存，随后 ApplyFrameScale 会按真实比例覆盖毫米值。
        slot.CadRegion.MinX = -slot.RightInsetMm;
        slot.CadRegion.MaxX = slot.CadRegion.MinX + slot.WidthMm;
        slot.CadRegion.MinY = slot.BottomInsetMm;
        slot.CadRegion.MaxY = slot.CadRegion.MinY + slot.HeightMm;
    }

    /// <summary>按槽位列表重画红框（父对话框在子窗口关闭后也用它纠正显示）。</summary>
    public static void DrawSlotMarkers(
        TransientFrameMarkers markers,
        IEnumerable<SignatureSlot> slots,
        LocalRectangle frame,
        Matrix3d blockTransform,
        bool refresh = true)
    {
        if (markers == null)
        {
            return;
        }

        foreach (var slotName in StandardSlots)
        {
            markers.RemoveByPrefix(MarkerPrefix(slotName), refresh: false);
        }

        if (frame.HasArea())
        {
            foreach (var slot in slots ?? Enumerable.Empty<SignatureSlot>())
            {
                if (slot?.SlotName == null)
                {
                    continue;
                }

                var regions = slot.GetRegions();
                for (var index = 0; index < regions.Count; index++)
                {
                    var region = regions[index];
                    if (region?.CadRegion == null || !region.CadRegion.HasArea())
                    {
                        continue;
                    }

                    var first = new Point3d(
                        frame.MaxX + region.CadRegion.MinX,
                        frame.MinY + region.CadRegion.MinY,
                        0).TransformBy(blockTransform);
                    var second = new Point3d(
                        frame.MaxX + region.CadRegion.MaxX,
                        frame.MinY + region.CadRegion.MaxY,
                        0).TransformBy(blockTransform);
                    var label = regions.Count > 1
                        ? $"{slot.SlotName} {index + 1}"
                        : slot.SlotName;
                    markers.SetBox(MarkerKeyFor(slot.SlotName, index), first, second, label, refresh: false);
                }
            }
        }

        if (refresh)
        {
            markers.RefreshDisplay();
        }
    }

    private void RedrawSlotMarkers(string slotName)
    {
        _markers.RemoveByPrefix(MarkerPrefix(slotName), refresh: false);
        if (!_slots.TryGetValue(slotName, out var slot) || !ReferenceFrame.HasArea())
        {
            _markers.RefreshDisplay();
            return;
        }

        var regions = slot.GetRegions();
        for (var index = 0; index < regions.Count; index++)
        {
            var region = regions[index];
            if (region?.CadRegion == null || !region.CadRegion.HasArea())
            {
                continue;
            }

            var first = new Point3d(
                ReferenceFrame.MaxX + region.CadRegion.MinX,
                ReferenceFrame.MinY + region.CadRegion.MinY,
                0).TransformBy(_blockTransform);
            var second = new Point3d(
                ReferenceFrame.MaxX + region.CadRegion.MaxX,
                ReferenceFrame.MinY + region.CadRegion.MaxY,
                0).TransformBy(_blockTransform);
            var label = regions.Count > 1 ? $"{slotName} {index + 1}" : slotName;
            _markers.SetBox(MarkerKeyFor(slotName, index), first, second, label, refresh: false);
        }

        _markers.RefreshDisplay();
    }

    public static SignatureSlot CloneSlot(SignatureSlot source)
    {
        var regions = new List<SignatureSlotRegion>();
        if (source.Regions != null)
        {
            foreach (var region in source.Regions)
            {
                if (region != null)
                {
                    regions.Add(region.Clone());
                }
            }
        }

        return new SignatureSlot
        {
            SlotName = source.SlotName,
            CadRegion = new LocalRectangle
            {
                MinX = source.CadRegion?.MinX ?? 0d,
                MinY = source.CadRegion?.MinY ?? 0d,
                MaxX = source.CadRegion?.MaxX ?? 0d,
                MaxY = source.CadRegion?.MaxY ?? 0d,
            },
            Regions = regions,
            RightInsetMm = source.RightInsetMm,
            BottomInsetMm = source.BottomInsetMm,
            WidthMm = source.WidthMm,
            HeightMm = source.HeightMm,
            DefaultSignatureId = source.DefaultSignatureId,
            AutoPickSignature = source.AutoPickSignature,
            Rotation = source.Rotation,
            StretchToFill = source.StretchToFill,
        };
    }

    /// <summary>
    /// 以非模态方式显示并等待关闭：框选期间 CAD 必须能响应，模态会被拦在外面。
    /// 等待逻辑与父对话框共用 <see cref="CadModelessWait"/>（有超时与异常保护）。
    /// <para>
    /// 关闭后不主动激活 CAD 窗口——调用方是父对话框，焦点应回到父窗口而不是被 CAD 盖住。
    /// </para>
    /// </summary>
    public static DialogResult ShowOverCad(Form form)
    {
        form.TopMost = true;
#if ACAD_CORE
        form.Show();
#else
        CadApp.ShowModelessDialog(form);
#endif
        return WaitForClose(form);
    }

    /// <summary>
    /// 非模态录入窗在 CAD 取点期间的计数。
    /// WaitForClose 按 Visible 判断关闭；Opacity 设为 0 时中望会把窗体改成 Visible=false，
    /// 取点期间必须忽略这次隐藏，否则会当成用户关窗，框选结果写不回去。
    /// </summary>
    private static int _modelessCadPickDepth;

    /// <summary>框选前让出 CAD 输入：近乎透明+禁用，避免 Hide/Show 整窗拆装造成图面闪烁。</summary>
    internal static void BeginModelessCadPick(Form form)
    {
        _modelessCadPickDepth++;
        form.Enabled = false;
        // 不要用 0：alpha=0 时 Win32 认为窗口不可见，中望的非模态宿主会把 Visible 置 false。
        form.Opacity = 0.01;
        form.TopMost = false;
        CadWindowFocus.ActivateCadWindow();
        System.Windows.Forms.Application.DoEvents();
    }

    /// <summary>框选结束后恢复录入窗；若取点期间被宿主藏起来，这里要重新显示。</summary>
    internal static void EndModelessCadPick(Form form)
    {
        try
        {
            if (form.IsDisposed)
            {
                return;
            }

            if (!form.Visible)
            {
                form.Visible = true;
            }

            form.Opacity = 1;
            form.Enabled = true;
            form.TopMost = true;
            form.BringToFront();
            form.Activate();
        }
        finally
        {
            if (_modelessCadPickDepth > 0)
            {
                _modelessCadPickDepth--;
            }
        }
    }

    private static DialogResult WaitForClose(Form form)
    {
        var started = DateTime.UtcNow;
        while (!form.IsDisposed)
        {
            // 取点期间即使 Visible=false 也要继续等，否则框选结果来不及写回就被当成取消。
            if (!form.Visible && _modelessCadPickDepth == 0)
            {
                break;
            }

            System.Windows.Forms.Application.DoEvents();
            if ((DateTime.UtcNow - started).TotalMinutes > 30)
            {
                break;
            }

            System.Threading.Thread.Sleep(15);
        }

        return form.IsDisposed ? DialogResult.Cancel : form.DialogResult;
    }

    private Control MakeSlotRow(string slotName, Label statusLabel)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("框选", 60) + UiLayout.Scale(8)));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("清除", 60) + UiLayout.Scale(2)));

        var bindPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, UiLayout.Scale(4), 0),
            Padding = Padding.Empty
        };

        var autoCheck = new CheckBox
        {
            Text = "自动读取",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, UiLayout.Scale(7), UiLayout.Scale(4), 0),
            Checked = _slots.TryGetValue(slotName, out var seeded) && seeded.AutoPickSignature
        };
        autoCheck.CheckedChanged += (_, _) => OnAutoCheckChanged(slotName);
        _autoChecks[slotName] = autoCheck;

        var combo = new ComboBox
        {
            Width = UiLayout.Scale(132),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, UiLayout.Scale(5), 0, 0),
            IntegralHeight = false,
            DropDownHeight = UiLayout.Scale(200),
            MaxDropDownItems = 14
        };
        combo.SelectedIndexChanged += (_, _) => OnSignatureSelectionChanged(slotName);
        _signatureCombos[slotName] = combo;

        bindPanel.Controls.Add(autoCheck);
        bindPanel.Controls.Add(combo);

        var selectButton = UiLayout.CreateButton("框选", 60);
        selectButton.Dock = DockStyle.Fill;
        selectButton.Margin = new Padding(0, UiLayout.Scale(5), UiLayout.Scale(3), UiLayout.Scale(5));
        selectButton.Click += (_, _) => PickSlot(slotName);
        _toolTip.SetToolTip(selectButton, "可以连续框多个位置：框完一个接着框下一个，右键结束。重新点一次则整组替换。");

        var clearButton = UiLayout.CreateButton("清除", 60);
        clearButton.Dock = DockStyle.Fill;
        clearButton.Margin = new Padding(0, UiLayout.Scale(5), 0, UiLayout.Scale(5));
        clearButton.Click += (_, _) => ClearSlot(slotName);
        _toolTip.SetToolTip(clearButton, "清除该槽位的全部位置（保留已选的签名）。");

        panel.Controls.Add(statusLabel, 0, 0);
        panel.Controls.Add(bindPanel, 1, 0);
        panel.Controls.Add(selectButton, 2, 0);
        panel.Controls.Add(clearButton, 3, 0);
        return panel;
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoSize = true
    };

    // ───────────────────────── 签名绑定（自动读取 / 手工指定） ─────────────────────────

    /// <summary>下拉项：Id 为空表示「自动读取」或「不盖」，靠索引区分。</summary>
    private sealed class SignatureChoice
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public override string ToString() => Text;
    }

    private static SignatureLibrary LoadLibrary()
    {
        try
        {
            return SignatureLibraryStore.Load();
        }
        catch (Exception ex)
        {
            PluginLog.Error("签章", "读取签名库失败，签章区域窗口按空库显示：" + ex.Message, ex);
            return new SignatureLibrary();
        }
    }

    private static bool HasSignatureBinding(SignatureSlot slot)
        => slot != null && (slot.AutoPickSignature || !string.IsNullOrWhiteSpace(slot.DefaultSignatureId));

    /// <summary>
    /// 该槽位可选的库条目：设计人员 1~6 只列手写签名，出图章 / 注册师章 / 竣工图章 只列印章。
    /// <para>
    /// 两类互不显示。以前是「同类优先、另类排在后面也能选」，结果人员栏的下拉里能选到出图章——
    /// 绑上去出图才会发现盖错了东西，所以这里直接不在列表里出现。
    /// </para>
    /// </summary>
    private List<SignatureItem> LibraryItemsForSlot(string slotName)
    {
        var slotIsStamp = PdfStampService.IsStampSlotName(slotName);
        var result = new List<SignatureItem>();
        foreach (var item in _library.Items)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id))
            {
                continue;
            }

            if (PdfStampService.IsStampItem(item) != slotIsStamp)
            {
                continue;
            }

            result.Add(item);
        }

        return result;
    }

    private void OnAutoCheckChanged(string slotName)
    {
        if (_updating || !_slots.TryGetValue(slotName, out var slot))
        {
            return;
        }

        slot.AutoPickSignature = _autoChecks[slotName].Checked;
        RefreshSignatureState(slotName);
    }

    private void OnSignatureSelectionChanged(string slotName)
    {
        if (_updating || !_slots.TryGetValue(slotName, out var slot))
        {
            return;
        }

        var combo = _signatureCombos[slotName];
        if (combo.SelectedIndex == 0)
        {
            // 直接选「自动读取」等价于勾上自动。
            slot.AutoPickSignature = true;
        }
        else
        {
            slot.AutoPickSignature = false;
            slot.DefaultSignatureId = (combo.SelectedItem as SignatureChoice)?.Id ?? "";
        }

        RefreshSignatureState(slotName);
    }

    /// <summary>把某个槽位的勾选状态、下拉项、下拉可用性、提示统一刷一遍（幂等，事件里也调它）。</summary>
    private void RefreshSignatureState(string slotName)
    {
        if (!_slots.TryGetValue(slotName, out var slot)
            || !_signatureCombos.TryGetValue(slotName, out var combo)
            || !_autoChecks.TryGetValue(slotName, out var check))
        {
            return;
        }

        _updating = true;
        try
        {
            check.Checked = slot.AutoPickSignature;

            combo.Items.Clear();
            var autoText = "(自动读取)";
            if (slot.AutoPickSignature)
            {
                var resolved = PdfStampService.ResolveSignature(slot, _library, null, out _);
                if (resolved != null)
                {
                    autoText = "(自动读取：" + resolved.Name + ")";
                }
            }

            combo.Items.Add(new SignatureChoice { Text = autoText });
            combo.Items.Add(new SignatureChoice { Text = "(不盖)" });

            var selectedIndex = slot.AutoPickSignature ? 0 : 1;
            var choices = LibraryItemsForSlot(slotName);
            for (var index = 0; index < choices.Count; index++)
            {
                var item = choices[index];
                combo.Items.Add(new SignatureChoice { Id = item.Id, Text = item.Name });
                if (!slot.AutoPickSignature
                    && string.Equals(item.Id, slot.DefaultSignatureId, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = index + 2;
                }
            }

            combo.SelectedIndex = selectedIndex;
            // 下拉保持可点：勾了「自动读取」时它显示「(自动读取：某某)」，用户想改成人也可以直接选名字，
            // 选名字即自动取消自动读取——比禁用一个灰控件更好懂，也不会在深色主题下变成看不清的暗字。
            combo.Enabled = true;

            var tip = DescribeSignatureBinding(slot);
            if (choices.Count == 0)
            {
                tip = (PdfStampService.IsStampSlotName(slotName)
                          ? "印章库里还没有印章图片，请到「其他设置 → 印章库管理」导入。"
                          : "签名库里还没有手写签名图片，请到「其他设置 → 签名库管理」导入。")
                      + Environment.NewLine
                      + tip;
            }

            _toolTip.SetToolTip(combo, tip);
            _toolTip.SetToolTip(check, "勾选＝出图时从签名库自动读取同类签名（库里只有一个手写签名时就是它），换人只需换库里的图片。");
        }
        finally
        {
            _updating = false;
        }

        // 绑定/自动读取变化会影响状态行里的「未选印章」提醒。
        UpdateStatus(slotName);
    }

    private string DescribeSignatureBinding(SignatureSlot slot)
    {
        if (slot.AutoPickSignature)
        {
            var resolved = PdfStampService.ResolveSignature(slot, _library, null, out var warning);
            if (resolved != null)
            {
                return $"自动读取：{resolved.Name}";
            }

            return string.IsNullOrWhiteSpace(warning)
                ? "自动读取：签名库里还没有同类图片"
                : warning;
        }

        if (string.IsNullOrWhiteSpace(slot.DefaultSignatureId))
        {
            return "该槽位不盖。";
        }

        var bound = _library.Find(slot.DefaultSignatureId);
        if (bound == null)
        {
            return "绑定的签名已不在签名库中，出图会跳过。";
        }

        if (!PdfStampService.MatchesSlotKind(slot, bound))
        {
            return $"原绑定「{bound.Name}」是{(PdfStampService.IsStampItem(bound) ? "印章" : "手写签名")}，"
                   + "与这个槽位的类型不符，出图会跳过；请重新选择。";
        }

        return $"手工绑定：{bound.Name}";
    }

    private void BeginCadPick() => BeginModelessCadPick(this);

    private void EndCadPick() => EndModelessCadPick(this);
}
