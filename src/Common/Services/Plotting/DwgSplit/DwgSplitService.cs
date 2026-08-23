using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.DatabaseServices.Filters;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.DatabaseServices.Filters;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>DWG 拆图入口，负责批量调度、结果汇总和输出路径管理。</summary>
public static class DwgSplitService
{
    public sealed class SplitResult
    {
        public PlotJob Job { get; set; } = new();
        public string OutputPath { get; set; } = "";
        public Exception? Error { get; set; }
        public int RemovedEntities { get; set; }
        public int KeptEntities { get; set; }
        public int UnknownExtentsKept { get; set; }
    }

    /// <summary>
    /// 单个拆图任务的源数据库句柄。当前文档直接借用内存数据库，以包含未保存修改；
    /// 外部 DWG 才创建并持有侧库。文档锁和侧库都由本对象统一释放。
    /// </summary>
    private sealed class SourceDatabaseContext : IDisposable
    {
        private readonly IDisposable? _documentLock;
        private readonly Database? _ownedDatabase;

        private SourceDatabaseContext(
            Database database,
            string identityPath,
            IDisposable? documentLock,
            Database? ownedDatabase)
        {
            Database = database;
            IdentityPath = identityPath;
            _documentLock = documentLock;
            _ownedDatabase = ownedDatabase;
        }

        public Database Database { get; }
        public string IdentityPath { get; }

        public static SourceDatabaseContext Open(PlotJob job, Document currentDocument)
        {
            var currentPath = NormalizePathOrEmpty(currentDocument.Database.Filename);
            var requestedPath = NormalizePathOrEmpty(job.SourceFile);
            var useCurrentDatabase = string.IsNullOrWhiteSpace(requestedPath)
                || (!string.IsNullOrWhiteSpace(currentPath)
                    && PathsEqual(requestedPath, currentPath));

            if (useCurrentDatabase)
            {
                var documentLock = currentDocument.LockDocument();
                return new SourceDatabaseContext(
                    currentDocument.Database,
                    currentPath,
                    documentLock,
                    ownedDatabase: null);
            }

            if (!File.Exists(requestedPath))
            {
                throw new FileNotFoundException("源 DWG 文件不存在。", requestedPath);
            }

            var sideDatabase = new Database(false, true);
            try
            {
                sideDatabase.ReadDwgFile(
                    requestedPath,
                    FileOpenMode.OpenForReadAndAllShare,
                    true,
                    "");
                sideDatabase.CloseInput(true);
                return new SourceDatabaseContext(
                    sideDatabase,
                    requestedPath,
                    documentLock: null,
                    ownedDatabase: sideDatabase);
            }
            catch
            {
                sideDatabase.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _ownedDatabase?.Dispose();
            _documentLock?.Dispose();
        }
    }

    public static List<SplitResult> SplitMany(
        IReadOnlyList<PlotJob> jobs,
        Document currentDocument,
        AppSettings settings,
        Action<PlotJob>? beforeJob = null,
        string? customOutputDirectory = null,
        string? sourceSubfolder = null,
        IReadOnlyDictionary<PlotJob, string>? explicitOutputPaths = null)
    {
        var results = new List<SplitResult>();
        Dictionary<PlotJob, string> outputPaths;
        try
        {
            outputPaths = explicitOutputPaths == null
                ? BuildOutputPaths(
                    jobs,
                    currentDocument,
                    settings,
                    createDirectories: true,
                    customOutputDirectory: customOutputDirectory,
                    sourceSubfolder: sourceSubfolder)
                : ResolveExplicitOutputPaths(jobs, explicitOutputPaths);
        }
        catch (Exception ex)
        {
            foreach (var job in jobs)
            {
                results.Add(new SplitResult { Job = job, Error = ex });
            }

            return results;
        }

        foreach (var job in jobs)
        {
            var result = new SplitResult
            {
                Job = job,
                OutputPath = outputPaths[job]
            };
            try
            {
                beforeJob?.Invoke(job);
                using var source = SourceDatabaseContext.Open(job, currentDocument);
                var outputPath = NormalizeRequiredOutputPath(outputPaths[job]);
                EnsureSourceAndOutputDiffer(source.IdentityPath, outputPath);
                ExecuteWithSafeOutput(
                    outputPath,
                    temporaryPath =>
                    {
                        if (job.IsPaperSpace)
                        {
                            DwgPaperSplitter.Split(
                                source.Database,
                                source.IdentityPath,
                                temporaryPath,
                                job,
                                result);
                        }
                        else
                        {
                            DwgModelSplitter.Split(
                                source.Database,
                                source.IdentityPath,
                                temporaryPath,
                                job,
                                result);
                        }
                    });
                result.OutputPath = outputPath;
            }
            catch (Exception ex)
            {
                result.Error = ex;
            }

            results.Add(result);
        }

        return results;
    }

    private static Dictionary<PlotJob, string> ResolveExplicitOutputPaths(
        IReadOnlyList<PlotJob> jobs,
        IReadOnlyDictionary<PlotJob, string> explicitOutputPaths)
    {
        var result = new Dictionary<PlotJob, string>();
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            if (!explicitOutputPaths.TryGetValue(job, out var outputPath)
                || string.IsNullOrWhiteSpace(outputPath))
            {
                throw new InvalidOperationException(
                    $"拆图任务“{job.DrawingNumber}_{job.Title}”没有有效输出路径。");
            }

            var normalizedPath = NormalizeRequiredOutputPath(outputPath);
            if (!reservedPaths.Add(normalizedPath))
            {
                throw new InvalidOperationException("多个拆图任务不能使用同一输出路径: " + normalizedPath);
            }

            result[job] = normalizedPath;
        }

        return result;
    }

    public static Dictionary<PlotJob, string> BuildOutputPaths(
        IReadOnlyList<PlotJob> jobs,
        Document currentDocument,
        AppSettings settings,
        bool createDirectories = false,
        string? customOutputDirectory = null,
        string? sourceSubfolder = null)
    {
        var outputPaths = new Dictionary<PlotJob, string>();
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sequenceDigits = FileNameSanitizer.ResolveSequenceDigits(
            settings.AutoFileNameSequenceDigits,
            settings.FileNameSequenceDigits,
            settings.FileNameSequenceStartNumber,
            jobs.Count);
        var sequenceNumbers = jobs
            .Select((job, index) => new { Job = job, Number = settings.FileNameSequenceStartNumber + index })
            .ToDictionary(x => x.Job, x => x.Number);
        foreach (var job in jobs)
        {
            var sourceFile = ResolveSourceIdentityPath(job, currentDocument);
            outputPaths[job] = BuildOutputPath(
                job,
                sourceFile,
                settings,
                sequenceNumbers[job],
                sequenceDigits,
                reservedPaths,
                createDirectories,
                customOutputDirectory,
                sourceSubfolder);
        }

        return outputPaths;
    }

    private static string BuildOutputPath(
        PlotJob job,
        string sourceFile,
        AppSettings settings,
        int sequenceNumber,
        int sequenceDigits,
        ISet<string> reservedPaths,
        bool createDirectory,
        string? customOutputDirectory,
        string? sourceSubfolder)
    {
        var sourceDirectory = string.IsNullOrWhiteSpace(sourceFile)
            ? ""
            : Path.GetDirectoryName(sourceFile) ?? "";
        if (string.IsNullOrWhiteSpace(customOutputDirectory)
            && string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new InvalidOperationException("当前图纸尚未保存，请指定拆图输出目录。");
        }

        string directory;
        if (!string.IsNullOrWhiteSpace(customOutputDirectory))
        {
            directory = Path.GetFullPath(customOutputDirectory!);
        }
        else if (string.IsNullOrWhiteSpace(sourceSubfolder))
        {
            directory = sourceDirectory;
        }
        else
        {
            directory = Path.Combine(sourceDirectory, FileNameSanitizer.Clean(sourceSubfolder!));
        }

        var baseName = FileNameSanitizer.FormatFileNamePattern(
            settings.PdfFileNamePattern,
            job,
            sequenceNumber,
            sequenceDigits,
            settings.LongPaperNameFormat,
            settings.LongPaperSnapToleranceMm);
        return FileNameSanitizer.MakeUnique(
            directory,
            baseName,
            reservedPaths,
            settings.AddSequenceWhenPdfExists,
            ".dwg",
            createDirectory);
    }

    private static string ResolveSourceIdentityPath(PlotJob job, Document currentDocument)
    {
        var currentPath = NormalizePathOrEmpty(currentDocument.Database.Filename);
        var requestedPath = NormalizePathOrEmpty(job.SourceFile);
        if (string.IsNullOrWhiteSpace(requestedPath)
            || (!string.IsNullOrWhiteSpace(currentPath) && PathsEqual(requestedPath, currentPath)))
        {
            return currentPath;
        }

        if (!File.Exists(requestedPath))
        {
            throw new FileNotFoundException("源 DWG 文件不存在。", requestedPath);
        }

        return requestedPath;
    }

    private static string NormalizeRequiredOutputPath(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new InvalidOperationException("拆图输出路径不能为空。");
        }

        var fullPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".dwg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拆图输出文件必须使用 .dwg 扩展名: " + fullPath);
        }

        return fullPath;
    }

    private static string NormalizePathOrEmpty(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureSourceAndOutputDiffer(string sourcePath, string outputPath)
    {
        if (!string.IsNullOrWhiteSpace(sourcePath) && PathsEqual(sourcePath, outputPath))
        {
            throw new InvalidOperationException("拆图输出路径不能与源 DWG 相同: " + outputPath);
        }
    }

    /// <summary>先生成并验证临时 DWG，成功后才替换正式输出。</summary>
    private static void ExecuteWithSafeOutput(string outputPath, Action<string> buildTemporaryDwg)
    {
        var directory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("无法确定拆图输出目录: " + outputPath);
        Directory.CreateDirectory(directory);

        var stem = Path.GetFileNameWithoutExtension(outputPath);
        var token = Guid.NewGuid().ToString("N");
        var temporaryPath = Path.Combine(directory, $".{stem}.split-{token}.dwg");
        var backupPath = Path.Combine(directory, $".{stem}.backup-{token}.dwg");
        try
        {
            buildTemporaryDwg(temporaryPath);
            ValidateGeneratedDwg(temporaryPath);
            ReplaceOutput(temporaryPath, outputPath, backupPath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
            TryDeleteFile(backupPath);
        }
    }

    private static void ValidateGeneratedDwg(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException("拆图未生成有效 DWG: " + path);
        }

        using var validationDatabase = new Database(false, true);
        validationDatabase.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
        validationDatabase.CloseInput(true);
    }

    private static void ReplaceOutput(string temporaryPath, string outputPath, string backupPath)
    {
        if (!File.Exists(outputPath))
        {
            File.Move(temporaryPath, outputPath);
            return;
        }

        // 同目录 Replace 保证旧输出在新文件完全就位前始终可恢复。
        File.Replace(temporaryPath, outputPath, backupPath, ignoreMetadataErrors: true);
        TryDeleteFile(backupPath);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时文件清理失败不应掩盖真实拆图结果。
        }
    }
}
