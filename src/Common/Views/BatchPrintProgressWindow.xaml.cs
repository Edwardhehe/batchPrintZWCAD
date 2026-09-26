using System;
using System.ComponentModel;
using System.Windows;

namespace ZwcadBatchPlot;

/// <summary>
/// 批量打印进度窗：显示进度条、当前张数与当前任务说明，并提供停止按钮。
/// 由批打主窗以非模态方式打开；打印结束时调用 <see cref="ForceClose"/>。
/// </summary>
public sealed partial class BatchPrintProgressWindow : Window
{
    private Action _onCancel;
    private bool _forceClose;
    private bool _cancelRequested;
    private readonly bool _scanMode;

    public BatchPrintProgressWindow(int total, Action onCancel, bool scanMode = false)
    {
        InitializeComponent();
        _onCancel = onCancel ?? throw new ArgumentNullException(nameof(onCancel));
        _scanMode = scanMode;
        if (_scanMode)
        {
            Title = "识别图框";
            _titleText.Text = "正在识别图框…";
            _stopButton.Content = "取消";
            _progressBar.IsIndeterminate = true;
            _countText.Text = "";
            _detailText.Text = "准备扫描…";
        }
        else
        {
            _progressBar.Maximum = Math.Max(1, total);
            _progressBar.Value = 0;
            _countText.Text = $"0 / {total}";
        }

        Closing += OnClosing;
    }

    /// <summary>构造后替换取消回调（识别进度会话需先创建再绑定）。</summary>
    internal void ReplaceCancelHandler(Action onCancel)
        => _onCancel = onCancel ?? throw new ArgumentNullException(nameof(onCancel));

    /// <summary>
    /// 更新进度与当前任务说明。<paramref name="started"/> 为正在开始的第几张（1..total，0 表示尚未开始）；
    /// 进度条与计数只统计已完成张数（started-1），避免只打一张时出图前就满格并显示“即将完成”。
    /// </summary>
    public void Report(int started, int total, string detail)
    {
        if (total <= 0)
        {
            total = 1;
        }

        var finished = Math.Max(0, Math.Min(started - 1, total));
        _progressBar.IsIndeterminate = false;
        _progressBar.Maximum = total;
        _progressBar.Value = finished;
        _countText.Text = $"{finished} / {total}";
        _titleText.Text = total > 1 && started >= total ? "正在打印最后一张…" : "正在批量打印…";
        _detailText.Text = string.IsNullOrWhiteSpace(detail) ? "准备中…" : detail;
    }

    /// <summary>识别图框进度：total≤0 时用不确定进度条，并显示已处理数量。</summary>
    public void ReportScan(int current, int total, string detail, string? title = null)
    {
        _titleText.Text = string.IsNullOrWhiteSpace(title) ? "正在识别图框…" : title;
        _detailText.Text = string.IsNullOrWhiteSpace(detail) ? "准备扫描…" : detail;
        if (total <= 0)
        {
            _progressBar.IsIndeterminate = true;
            _countText.Text = current > 0 ? current.ToString("N0") : "";
            return;
        }

        _progressBar.IsIndeterminate = false;
        _progressBar.Maximum = Math.Max(1, total);
        _progressBar.Value = Math.Max(0, Math.Min(current, total));
        _countText.Text = $"{current} / {total}";
    }

    /// <summary>进入合并等阶段：进度条拉满并改说明文字。</summary>
    public void SetPhase(string title, string detail)
    {
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = _progressBar.Maximum;
        _titleText.Text = title;
        _detailText.Text = detail;
        _stopButton.IsEnabled = false;
    }

    /// <summary>打印流程结束时强制关闭，忽略关闭确认。</summary>
    public void ForceClose()
    {
        _forceClose = true;
        try
        {
            Close();
        }
        catch
        {
            // 窗口可能已关闭。
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        RequestCancel();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_forceClose)
        {
            return;
        }

        // 点标题栏关闭等同于停止：取消打印，但先留住窗口直到外层 ForceClose。
        e.Cancel = true;
        RequestCancel();
    }

    private void RequestCancel()
    {
        if (_cancelRequested)
        {
            return;
        }

        _cancelRequested = true;
        _stopButton.IsEnabled = false;
        _stopButton.Content = _scanMode ? "正在取消…" : "正在停止…";
        _titleText.Text = _scanMode ? "正在取消…" : "正在停止…";
        try
        {
            _onCancel();
        }
        catch
        {
            // 取消失败仍保持窗口，由外层 finally 关闭。
        }
    }
}