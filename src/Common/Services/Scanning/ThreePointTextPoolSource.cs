using System;
using System.Collections.Generic;
using System.IO;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 三点图签的文字来源：按「来源 DWG + 空间名」取文字池。
/// <para>
/// 单文档时直接用当前文档；多文档批打时清单里的图框来自多个 DWG，
/// 早先不管图框是谁的，统一拿当前文档的文字去匹配——坐标正好撞上就串成别张图的文字，
/// 坐标不撞就整框判空（日志里表现为「frame pool empty」，界面表现为这些图纸的
/// 图名/图号/阶段全空）。这里按任务的 SourceFile 侧载打开对应 DWG，
/// 让每份图纸只用自己文件里的文字定锚点、取字段。
/// </para>
/// <para>
/// 侧载数据库按文件缓存、识别结束统一释放：一张图可能有很多图框、多个布局，
/// 每个图框都重新 ReadDwgFile 会把识别拖成分钟级。
/// </para>
/// </summary>
internal sealed class ThreePointTextPoolSource : IDisposable
{
    private readonly Document _current;
    private readonly Dictionary<string, Database> _sideDatabases =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _openOrder = new();
    private readonly HashSet<string> _failedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ThreePointTextSample>> _pools =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// 同时最多开几份侧载 DWG。文字已经收进内存，关掉数据库不影响后续匹配；
    /// 清单里有几十份图时全开着会白占几百 MB 甚至上 G，所以按打开顺序关掉最旧的。
    /// </summary>
    private const int MaxOpenSideDatabases = 6;

    public ThreePointTextPoolSource(Document current)
    {
        _current = current;
        CurrentFile = NormalizeFile(current?.Database?.Filename);
    }

    /// <summary>当前文档的规范化路径（未保存的图纸为空串）。</summary>
    public string CurrentFile { get; }

    /// <summary>打不开的 DWG（被占用、已删除、权限不足）：这些文件的图框一律认不出图签。</summary>
    public IReadOnlyCollection<string> FailedFiles => _failedFiles;

    /// <summary>取某个文件某个空间的文字池；同一组合只收一次。</summary>
    public List<ThreePointTextSample> Get(string? sourceFile, string? spaceName)
    {
        if (_disposed)
        {
            return new List<ThreePointTextSample>();
        }

        var file = NormalizeFile(sourceFile);
        if (file.Length == 0)
        {
            file = CurrentFile;
        }

        var space = spaceName ?? "";
        var key = file + "|" + space;
        if (_pools.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var pool = Collect(file, space);
        _pools[key] = pool;
        ThreePointScanLog.Write(
            $"pool: {LabelOf(file)} [{space}] = {pool.Count} texts"
            + (pool.Count == 0 ? "（该空间没收到文字，或文件打不开）" : ""));
        return pool;
    }

    private List<ThreePointTextSample> Collect(string file, string space)
    {
        Database? database;
        if (file.Length == 0 || string.Equals(file, CurrentFile, StringComparison.OrdinalIgnoreCase))
        {
            database = _current?.Database;
        }
        else
        {
            database = OpenSideDatabase(file);
        }

        if (database == null)
        {
            return new List<ThreePointTextSample>();
        }

        try
        {
            using var tr = database.TransactionManager.StartTransaction();
            var samples = ThreePointTextCollector.CollectSpace(database, tr, space);
            tr.Commit();
            return samples;
        }
        catch (Exception ex)
        {
            ThreePointScanLog.Write($"pool: {LabelOf(file)} [{space}] 收文字失败: {ex.Message}");
            return new List<ThreePointTextSample>();
        }
    }

    private Database? OpenSideDatabase(string file)
    {
        if (_sideDatabases.TryGetValue(file, out var cached))
        {
            // 命中缓存时把它挪到队尾，保证关掉的是最久没用过的那份。
            _openOrder.Remove(file);
            _openOrder.Add(file);
            return cached;
        }

        if (_failedFiles.Contains(file))
        {
            return null;
        }

        Database? database = null;
        try
        {
            database = new Database(false, true);
            database.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
            database.CloseInput(true);
            _sideDatabases[file] = database;
            _openOrder.Add(file);
            TrimOpenDatabases();
            return database;
        }
        catch (Exception ex)
        {
            try
            {
                database?.Dispose();
            }
            catch
            {
                // 侧载失败时数据库可能已处于半开状态，释放异常不影响识别流程。
            }

            _failedFiles.Add(file);
            ThreePointScanLog.Write($"pool: 打开 {LabelOf(file)} 失败: {ex.Message}");
            return null;
        }
    }

    private void TrimOpenDatabases()
    {
        while (_openOrder.Count > MaxOpenSideDatabases)
        {
            var oldest = _openOrder[0];
            _openOrder.RemoveAt(0);
            if (_sideDatabases.TryGetValue(oldest, out var database))
            {
                _sideDatabases.Remove(oldest);
                try
                {
                    database.Dispose();
                }
                catch
                {
                    // 关闭失败不影响识别结果，剩余数据库在 Dispose 里再兜一次。
                }
            }
        }
    }

    private static string LabelOf(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return "(当前文档)";
        }

        try
        {
            return Path.GetFileName(file);
        }
        catch
        {
            return file;
        }
    }

    /// <summary>统一成全路径、无反斜杠差异的形式，用于判断「是不是同一个 DWG」。</summary>
    private static string NormalizeFile(string? file)
    {
        if (file == null)
        {
            return "";
        }

        var trimmed = file.Trim();
        if (trimmed.Length == 0)
        {
            return "";
        }

        try
        {
            return Path.GetFullPath(trimmed).Replace('/', '\\');
        }
        catch
        {
            return trimmed.Replace('/', '\\');
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var database in _sideDatabases.Values)
        {
            try
            {
                database.Dispose();
            }
            catch
            {
                // 释放失败不影响识别结果。
            }
        }

        _sideDatabases.Clear();
        _openOrder.Clear();
        _pools.Clear();
    }
}
