using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace ZwcadBatchPlot;

/// <summary>
/// 三点文字识别图签在「通用型批量打印」窗口的接入层。
/// 对清单里已有的矩形图框直接补图名 / 图号 / 比例 / 图幅等字段，不重新扫描、不追加行。
/// 单独成文件是为了不改动主窗口已有的 3000+ 行实现，也便于后续随上游代码独立演进。
/// </summary>
public sealed partial class RectangleBatchPlotForm
{
    private void ThreePointRecognize_Click(object sender, RoutedEventArgs e)
    {
        ThreePointLibrary library;
        try
        {
            library = ThreePointLibraryStore.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show("读取三点图签库失败: " + ex.Message, Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (library.Items.Count == 0)
        {
            MessageBox.Show(
                "三点图签库为空。\n请先在菜单「LA批量打印 → 三点图签信息库管理」里新建一个三点图签。",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_rows.Count == 0)
        {
            MessageBox.Show(
                "清单里还没有图纸。请先用「扫描当前图」「框选扫描」或「多文件批打」把图框加入清单，再执行本功能。",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 清单里已有图框，直接拿来做三点匹配；打印范围仍来自矩形框检测，二者互不干扰。
        var frames = _rows
            .Select(row => new RectangleFrameScanner.Result { Job = row.Job })
            .ToList();

        ThreePointScanLog.StartRun($"recognize: existing rows={frames.Count}");
        List<ThreePointMatch> matches;
        int skipped;
        ThreePointScanDiagnostics diagnostics;
        var previousCursor = Cursor;
        Cursor = Cursors.Wait;
        var beforeKeys = CaptureIdentitySortKeys();
        var hadIdentity = _hasAttributeIdentity;
        try
        {
            // 清单可能跨多份 DWG，文字必须按「文件 + 空间」各收一份，否则会串图或整框判空。
            using var poolSource = new ThreePointTextPoolSource(_document);
            matches = ThreePointTitleScanner.MatchFrames(
                poolSource.Get, frames, library, out skipped, out diagnostics);
            foreach (var match in matches)
            {
                ThreePointTitleScanner.Apply(match);
            }
        }
        catch (Exception ex)
        {
            ThreePointScanLog.Write("recognize failed: " + ex);
            MessageBox.Show("三点图签识别失败: " + ex.Message, Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            Cursor = previousCursor;
        }

        // 与改纸后重识别相同：有 Cad 图号/图名才打开清单列，并进入属性命名与排序。
        _hasAttributeIdentity = _rows.Any(RowHasAttributeIdentity);
        if (hadIdentity != _hasAttributeIdentity)
        {
            UpdateAttributeIdentityColumns();
        }

        foreach (var row in _rows)
        {
            row.RefreshFromJob();
        }

        if (hadIdentity != _hasAttributeIdentity || !IdentitySortKeysEqual(beforeKeys, CaptureIdentitySortKeys()))
        {
            SortRows();
        }

        var summary = diagnostics.Summary();
        MessageBox.Show(
            $"三点图签识别完成：命中 {matches.Count} 张，未命中 {skipped} 张。"
            + (string.IsNullOrWhiteSpace(summary) ? "" : "\n诊断：" + summary)
            + "\n识别日志：" + ThreePointScanLog.FilePath,
            Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
