using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

// 全局 using 里 CAD 的 DatabaseServices 也有 Image（栅格图像实体），本文件只用 GDI+ 位图。
using Image = System.Drawing.Image;

namespace ZwcadBatchPlot;

/// <summary>
/// 签名库（Signatures.json）读写与图片资源管理。
/// <para>
/// 图片按类型分两个文件夹放在<b>插件 DLL 所在目录</b>下：「签名」放手写签名，「签章」放印章/出图章。
/// 好处是跟着插件走——换机器、拷给同事，整个插件目录复制过去即可，不用去 %APPDATA% 里翻。
/// 库内只存文件名，不引用用户原始路径：桌面上的图删了、挪了，出图时也不会突然丢章。
/// </para>
/// <para>
/// 插件目录不可写（装在 Program Files 且无管理员权限）时，两个文件夹自动退回用户数据目录
/// %APPDATA%\AcadBatchPlot\ 下同名创建，功能不受影响——「图框库配置」当初就是因为这个原因
/// 从插件目录搬走的，这里用「先探测可写性」避免重蹈覆辙。
/// </para>
/// </summary>
public static class SignatureLibraryStore
{
    /// <summary>签名类资源的 Kind 值。</summary>
    public const string SignatureKind = "Signature";

    /// <summary>印章（签章）类资源的 Kind 值。</summary>
    public const string StampKind = "Stamp";

    /// <summary>手写签名图片文件夹名（插件目录下）。</summary>
    public const string SignatureFolderName = "签名";

    /// <summary>印章图片文件夹名（插件目录下）。用户口中的「签章文件夹」。</summary>
    public const string StampFolderName = "签章";

    /// <summary>旧版单目录名（%APPDATA% 下），仅用于迁移与兼容读取。</summary>
    private const string LegacyFolderName = "Signatures";

    /// <summary>
    /// 「图片处理」前自动留的原图备份后缀（如 <c>sig-1a2b3c4d5e.orig.png</c>）。
    /// 处理永远从这份备份重算，所以反复调去底/修剪/尺寸不会一层层劣化。
    /// </summary>
    public const string OriginalBackupSuffix = ".orig.png";

    public static string DefaultDirectory => TitleBlockLibraryStore.DefaultDirectory;

    public static string DefaultPath => Path.Combine(DefaultDirectory, "Signatures.json");

    /// <summary>
    /// 图片根目录：插件 DLL 所在目录（可写时）或用户数据目录（插件目录只读时）。
    /// 静态初始化时就探测一次并建好两个子文件夹，用户打开插件目录能直接看到它们。
    /// </summary>
    public static string ImagesRoot { get; } = ResolveImagesRoot();

    /// <summary>
    /// 插件 DLL 所在目录；以内存方式加载（Assembly.Location 为空）时返回空串。
    /// 找图时作为兜底目录用：ImagesRoot 可能因为可写性变化退到了 %APPDATA%，而图片还在插件目录里。
    /// </summary>
    public static string PluginDirectory { get; } = ResolvePluginDirectory();

    /// <summary>手写签名图片目录（「签名」）。</summary>
    public static string SignatureDirectory => Path.Combine(ImagesRoot, SignatureFolderName);

    /// <summary>印章图片目录（「签章」）。</summary>
    public static string StampDirectory => Path.Combine(ImagesRoot, StampFolderName);

    /// <summary>旧版单目录 %APPDATA%\{产品}\Signatures，仅作为迁移来源与兼容读取的兜底。</summary>
    public static string LegacyImagesDirectory => Path.Combine(DefaultDirectory, LegacyFolderName);

    /// <summary>把任意 Kind 值归一化成 <see cref="SignatureKind"/> / <see cref="StampKind"/>。</summary>
    public static string NormalizeKind(string? kind)
    {
        return string.Equals(kind, StampKind, StringComparison.OrdinalIgnoreCase) ? StampKind : SignatureKind;
    }

    /// <summary>取某类资源的图片目录。</summary>
    public static string GetDirectory(string? kind)
    {
        return Path.Combine(ImagesRoot, NormalizeKind(kind) == StampKind ? StampFolderName : SignatureFolderName);
    }

    /// <summary>
    /// 确保两个图片文件夹存在（插件初始化时调用，用户打开插件目录即可看到「签名」「签章」）。
    /// 失败不抛异常：目录建不出来时导入会自己再试并给出明确报错。
    /// </summary>
    public static void EnsureStorage()
    {
        TryCreateDirectory(SignatureDirectory);
        TryCreateDirectory(StampDirectory);
    }

    /// <summary>
    /// 找出条目图片的真实路径：先按 Kind 找对应文件夹，再找另一个（用户手挪过），
    /// 最后退回旧版单目录。都没有返回空串。
    /// </summary>
    public static string LocateImageFile(SignatureItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.ImageFile))
        {
            return "";
        }

        foreach (var directory in CandidateImageDirectories(item.Kind))
        {
            try
            {
                var path = Path.Combine(directory, item.ImageFile);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
                // 非法文件名（用户手改过 JSON）跳过，继续找下一个候选。
            }
        }

        return "";
    }

    /// <summary>
    /// 找图时依次尝试的目录。除了本类型/另一类型的「签名」「签章」文件夹和旧版单目录，
    /// 还要兜住<b>图片目录和当初写入时不是同一个</b>的情况：插件目录的可写性可能在两次运行之间变化
    /// （装了杀软、拷到 Program Files、以内存方式加载导致 Assembly.Location 为空），
    /// 于是 ImagesRoot 一次是插件目录、一次退到 %APPDATA%，图片就"消失"了。
    /// 这些兜底目录能让这类历史遗留的图片重新被找到。
    /// </summary>
    private static IEnumerable<string> CandidateImageDirectories(string? kind)
    {
        var normalized = NormalizeKind(kind);
        var opposite = normalized == StampKind ? SignatureKind : StampKind;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>
        {
            GetDirectory(normalized),
            GetDirectory(opposite),
            LegacyImagesDirectory,
            Path.Combine(DefaultDirectory, StampFolderName),
            Path.Combine(DefaultDirectory, SignatureFolderName),
        };

        var assemblyDirectory = PluginDirectory;
        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            candidates.Add(Path.Combine(assemblyDirectory, StampFolderName));
            candidates.Add(Path.Combine(assemblyDirectory, SignatureFolderName));
        }

        foreach (var directory in candidates)
        {
            if (!string.IsNullOrWhiteSpace(directory) && seen.Add(directory))
            {
                yield return directory;
            }
        }
    }

    /// <summary>
    /// 把旧版单目录里的图片按 Kind 复制进新的「签名」「签章」文件夹。
    /// 复制而非移动：迁移出错还能退回旧目录，旧文件原样保留（占空间但安全）。
    /// <returns>迁移成功的数量。</returns>
    /// </summary>
    public static int MigrateLegacyImages(SignatureLibrary? library)
    {
        if (library?.Items == null || library.Items.Count == 0)
        {
            return 0;
        }

        var migrated = 0;
        foreach (var item in library.Items)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ImageFile))
            {
                continue;
            }

            try
            {
                var targetDirectory = GetDirectory(item.Kind);
                var target = Path.Combine(targetDirectory, item.ImageFile);
                if (File.Exists(target))
                {
                    continue;
                }

                var legacy = Path.Combine(LegacyImagesDirectory, item.ImageFile);
                if (!File.Exists(legacy))
                {
                    continue;
                }

                TryCreateDirectory(targetDirectory);
                File.Copy(legacy, target, false);
                migrated++;
            }
            catch (Exception ex)
            {
                PluginLog.Error("签章", $"迁移旧版签章图片失败：{item.ImageFile} — {ex.Message}", ex);
            }
        }

        if (migrated > 0)
        {
            PluginLog.Run($"[签章] 已把 {migrated} 张旧版图片从 {LegacyImagesDirectory} 迁到 {ImagesRoot}");
        }

        return migrated;
    }

    /// <summary>
    /// 扫描某个类型的图片文件夹，把还没入库的图片登记进库（返回新增数量）。
    /// 这样用户直接往「签名」「签章」文件夹里丢图片也能用，不必走一遍导入对话框。
    /// 非 PNG 的图片会转成同名 PNG 后登记（PDF 盖章只吃 PNG 最稳）。
    /// </summary>
    public static int RegisterImagesFromFolder(SignatureLibrary library, string kind)
    {
        if (library == null)
        {
            return 0;
        }

        var directory = GetDirectory(kind);
        TryCreateDirectory(directory);
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var known = new HashSet<string>(
            library.Items.Where(x => x.ImageFile != null).Select(x => x.ImageFile!),
            StringComparer.OrdinalIgnoreCase);

        var added = 0;
        string[] files;
        try
        {
            files = Directory.GetFiles(directory);
        }
        catch
        {
            return 0;
        }

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (Array.IndexOf(SupportedExtensions, extension) < 0)
            {
                continue;
            }

            var fileName = Path.GetFileName(file);

            // 「图片处理」会在同目录留一份原图备份 xxx.orig.png，它不是用户放的签名，不能当新条目登记。
            if (fileName.EndsWith(OriginalBackupSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (known.Contains(fileName))
            {
                continue;
            }

            try
            {
                var storedFile = fileName;
                if (!string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
                {
                    storedFile = Path.GetFileNameWithoutExtension(file) + ".png";
                    var pngPath = Path.Combine(directory, storedFile);
                    if (!File.Exists(pngPath) && !known.Contains(storedFile))
                    {
                        using var source = LoadBitmap(file);
                        using var converted = ToArgb(source);
                        converted.Save(pngPath, ImageFormat.Png);
                    }
                }
                else
                {
                    // PNG 也要试解码一次再收人：只看扩展名的话，文件夹里的缩略图缓存、
                    // 被改名成 .png 的文本文件都会被登记成"读不出来的印章"，列表里一片"读取失败"。
                    // 解码失败会抛异常，落到下面的 catch 里跳过并记日志。
                    using var probe = LoadBitmap(file);
                }

                if (known.Contains(storedFile))
                {
                    // 同名 PNG 已入库（上一轮转换过，原图还留着），不重复登记。
                    continue;
                }

                library.Items.Add(new SignatureItem
                {
                    Id = "sig-" + Guid.NewGuid().ToString("N").Substring(0, 10),
                    Kind = NormalizeKind(kind),
                    Name = Path.GetFileNameWithoutExtension(file),
                    ImageFile = storedFile,
                    BackgroundRemoved = false,
                    SourceFileName = fileName,
                });

                known.Add(storedFile);
                added++;
            }
            catch (Exception ex)
            {
                PluginLog.Error("签章", $"扫描图片文件夹失败：{fileName} — {ex.Message}", ex);
            }
        }

        return added;
    }

    private static readonly string[] SupportedExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    /// <summary>
    /// 解析图片根目录：优先插件 DLL 所在目录，写不进就用用户数据目录。
    /// 注意只读目录上对「已存在的子目录」调 CreateDirectory 也会成功，所以必须真写一次文件才作数。
    /// </summary>
    /// <summary>插件 DLL 所在目录，取不到返回空串（内存加载 / 单文件打包）。</summary>
    private static string ResolvePluginDirectory()
    {
        try
        {
            return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string ResolveImagesRoot()
    {
        string? failure = null;
        try
        {
            var assemblyDirectory = ResolvePluginDirectory();
            if (!string.IsNullOrEmpty(assemblyDirectory))
            {
                var signatureDirectory = Path.Combine(assemblyDirectory, SignatureFolderName);
                Directory.CreateDirectory(signatureDirectory);
                Directory.CreateDirectory(Path.Combine(assemblyDirectory, StampFolderName));

                var probe = Path.Combine(signatureDirectory, ".gmp_write_probe_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return assemblyDirectory;
            }

            failure = "取不到插件所在目录";
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        try
        {
            TryCreateDirectory(Path.Combine(DefaultDirectory, SignatureFolderName));
            TryCreateDirectory(Path.Combine(DefaultDirectory, StampFolderName));
            PluginLog.Run($"[签章] 插件目录不可写（{failure}），签名/签章图片改存 {DefaultDirectory}");
        }
        catch
        {
            // 连用户目录都建不出来时也不能崩：后续导入会报具体错误。
        }

        return DefaultDirectory;
    }

    private static void TryCreateDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            // 权限/路径问题留给真正写文件的时候报错，这里静默。
        }
    }

    public static SignatureLibrary Load(string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path))
        {
            try
            {
                return Deserialize(path);
            }
            catch (Exception primaryError)
            {
                // 主库坏了：先试备份，都失败就隔离主库并退回空库。
                // 以前末尾还会再 Deserialize 一次备份且不包 try —— 备份坏一次，签名库就永久加载不了。
                var backupPath = path + ".bak";
                if (File.Exists(backupPath))
                {
                    try
                    {
                        return Deserialize(backupPath);
                    }
                    catch (Exception backupError)
                    {
                        PluginLog.Error("签章", $"签名库与其备份都无法解析，已退回空库：{path}", backupError);
                    }
                }
                else
                {
                    PluginLog.Error("签章", $"签名库无法解析，已退回空库：{path}", primaryError);
                }

                QuarantineCorrupt(path, primaryError);
                return new SignatureLibrary();
            }
        }

        // 主文件不存在：备份能用就用，坏了也绝不能抛到出图流程里。
        var missingBackup = path + ".bak";
        if (File.Exists(missingBackup))
        {
            try
            {
                return Deserialize(missingBackup);
            }
            catch (Exception backupError)
            {
                PluginLog.Error("签章", $"签名库备份无法解析：{missingBackup}", backupError);
            }
        }

        return new SignatureLibrary();
    }

    /// <summary>
    /// 把无法解析的库文件改名留证（.corrupt-时间戳）；隔离失败只记日志，文件保持原样。
    /// 目的：坏文件必须留现场，且不能每轮启动都因同一个坏文件失败。
    /// </summary>
    private static void QuarantineCorrupt(string path, Exception error)
    {
        try
        {
            var target = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(path, target);
            PluginLog.Error("签章", "签名库无法解析，已隔离为 " + target, error);
        }
        catch
        {
            PluginLog.Error("签章", "签名库无法解析，且隔离失败（文件保持原样）：" + path, error);
        }
    }

    public static void Save(SignatureLibrary library, string? path = null)
    {
        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("签名库路径无效: " + path);
        Directory.CreateDirectory(directory);
        // 整份覆盖保存，必须跨进程串行：两个会话各自改完条目再保存时，后写者会把前者的新增抹掉。
        // （这只是把写串起来，长时间编辑会话的「后写者胜」语义仍然存在，需要版本号才能做合并。）
        CrossProcessMutex.RunOrFallback(
            CrossProcessMutex.NameForPath("LABatchPlot_SignatureLibrary", path),
            () => AtomicFileWriter.Write(path, JsonConvert.SerializeObject(library, Formatting.Indented)),
            "保存签名/印章库");
    }

    /// <summary>签章图片的绝对路径；文件缺失返回空串（调用方据此跳过并告警）。</summary>
    public static string ResolveImagePath(SignatureItem item)
    {
        return LocateImageFile(item);
    }

    /// <summary>
    /// 统计图框库里绑定到该条目的签章槽位数量。删除前提示用，避免悄悄让已有图框失效。
    /// 图框库读不出来时返回 0（不因为提示失败就挡住删除）。
    /// </summary>
    public static int CountSlotReferences(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return 0;
        }

        try
        {
            var frames = TitleBlockLibraryStore.Load();
            var count = 0;
            foreach (var block in frames.Blocks)
            {
                if (block.SignatureSlots == null)
                {
                    continue;
                }

                foreach (var slot in block.SignatureSlots)
                {
                    if (string.Equals(slot.DefaultSignatureId, id, StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                    }
                }
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 导入一张签名/印章图片：复制进库目录（统一转 PNG），可选去白底。
    /// <returns>入库的条目；失败抛异常由调用方提示。</returns>
    /// </summary>
    public static SignatureItem ImportImage(string sourcePath, string kind, string name, bool removeBackground)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("找不到图片文件: " + sourcePath, sourcePath);
        }

        var normalizedKind = NormalizeKind(kind);
        var directory = GetDirectory(normalizedKind);
        Directory.CreateDirectory(directory);

        var id = "sig-" + Guid.NewGuid().ToString("N").Substring(0, 10);
        var fileName = id + ".png";
        var destination = Path.Combine(directory, fileName);

        using (var source = LoadBitmap(sourcePath))
        {
            var output = removeBackground ? RemoveWhiteBackground(source) : ToArgb(source);
            try
            {
                output.Save(destination, ImageFormat.Png);
            }
            finally
            {
                if (!ReferenceEquals(output, source))
                {
                    output.Dispose();
                }
            }
        }

        return new SignatureItem
        {
            Id = id,
            Kind = normalizedKind,
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(sourcePath) : name.Trim(),
            ImageFile = fileName,
            BackgroundRemoved = removeBackground,
            SourceFileName = Path.GetFileName(sourcePath),
        };
    }

    /// <summary>替换某条目的图片（保留 Id 与绑定关系）。</summary>
    public static void ReplaceImage(SignatureItem item, string sourcePath, bool removeBackground)
    {
        if (item == null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        var directory = GetDirectory(item.Kind);
        Directory.CreateDirectory(directory);
        if (string.IsNullOrWhiteSpace(item.ImageFile))
        {
            item.ImageFile = item.Id + ".png";
        }

        // 图片可能还在另一个文件夹（旧版单目录 / 用户手挪过）：新图写进当前类型的目录后，把旧文件删掉。
        var previousPath = LocateImageFile(item);
        var destination = Path.Combine(directory, item.ImageFile);
        using (var source = LoadBitmap(sourcePath))
        {
            var output = removeBackground ? RemoveWhiteBackground(source) : ToArgb(source);
            try
            {
                output.Save(destination, ImageFormat.Png);
            }
            finally
            {
                if (!ReferenceEquals(output, source))
                {
                    output.Dispose();
                }
            }
        }

        item.BackgroundRemoved = removeBackground;
        item.SourceFileName = Path.GetFileName(sourcePath);
        item.UpdatedAt = DateTime.Now;

        if (!string.IsNullOrEmpty(previousPath)
            && !string.Equals(Path.GetFullPath(previousPath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Delete(previousPath);
            }
            catch
            {
                // 旧文件删不掉不影响新图生效，残留下来下次手工清理即可。
            }
        }
    }

    /// <summary>删除条目并连带删除图片文件；被槽位或预设引用时由调用方先清理引用。</summary>
    public static void DeleteItem(SignatureLibrary library, SignatureItem item)
    {
        if (item == null)
        {
            return;
        }

        library.Items.Remove(item);
        TryDeleteFile(LocateImageFile(item));
        TryDeleteFile(GetOriginalBackupPath(item));
    }

    /// <summary>
    /// 处理前的原图备份路径（与原图同目录，文件名去扩展名 + <c>.orig.png</c>）。
    /// 条目还没有图片文件名时返回空串。
    /// </summary>
    public static string GetOriginalBackupPath(SignatureItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.ImageFile))
        {
            return "";
        }

        try
        {
            var directory = GetDirectory(item.Kind);
            var name = Path.GetFileNameWithoutExtension(item.ImageFile);
            return string.IsNullOrWhiteSpace(name) ? "" : Path.Combine(directory, name + OriginalBackupSuffix);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>是否已有原图备份（决定「图片处理」里「恢复原图」按钮是否可用）。</summary>
    public static bool HasOriginalBackup(SignatureItem? item)
    {
        var path = GetOriginalBackupPath(item);
        return path.Length > 0 && File.Exists(path);
    }

    /// <summary>
    /// 「图片处理」的基准图：有原图备份就用备份，否则用当前图片。
    /// <para>
    /// 这条是整个功能可反复调整的前提——第二次调滑块时若从已处理的图再处理，
    /// 去底会把笔画边缘再削一轮、缩放会二次重采样，几次下来签名就糊了。
    /// </para>
    /// </summary>
    public static string GetProcessingBasePath(SignatureItem? item)
    {
        if (item == null)
        {
            return "";
        }

        var backup = GetOriginalBackupPath(item);
        if (backup.Length > 0 && File.Exists(backup))
        {
            return backup;
        }

        return LocateImageFile(item);
    }

    /// <summary>首次处理前把当前图片复制成 <c>.orig.png</c>。已存在则不动（保留最早的原图）。</summary>
    public static void EnsureOriginalBackup(SignatureItem item)
    {
        var backup = GetOriginalBackupPath(item);
        if (backup.Length == 0 || File.Exists(backup))
        {
            return;
        }

        var current = LocateImageFile(item);
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(backup);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(current, backup, false);
        }
        catch (Exception ex)
        {
            PluginLog.Error("签章", $"备份原图失败：{item.ImageFile} — {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 按参数重新生成条目图片（永远从原图备份重算），并把参数记进条目。
    /// <returns>处理后的像素尺寸。</returns>
    /// </summary>
    public static Size ApplyImageProcessing(SignatureItem item, ImageProcessSettings settings)
    {
        if (item == null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var basePath = GetProcessingBasePath(item);
        if (string.IsNullOrWhiteSpace(basePath) || !File.Exists(basePath))
        {
            throw new FileNotFoundException("找不到要处理的图片文件，请先重新导入图片。", basePath ?? "");
        }

        // 备份必须在写目标文件之前建立：写失败也只是留下一个备份，不会丢原图。
        EnsureOriginalBackup(item);

        if (string.IsNullOrWhiteSpace(item.ImageFile))
        {
            item.ImageFile = item.Id + ".png";
        }

        var directory = GetDirectory(item.Kind);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, item.ImageFile);

        Size size;
        var temp = Path.Combine(directory, "proc-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp.png");
        try
        {
            using (var source = LoadBitmap(basePath))
            using (var output = ImageProcessor.Process(source, settings))
            {
                size = output.Size;
                output.Save(temp, ImageFormat.Png);
            }

            // 先写临时文件再替换：中途崩了也只是留下 tmp，不会出现半张图。
            try
            {
                if (File.Exists(destination))
                {
                    File.Replace(temp, destination, null);
                }
                else
                {
                    File.Move(temp, destination);
                }
            }
            catch (Exception)
            {
                // File.Replace 在某些网络盘 / 只读属性上会失败，退回直接覆盖。
                File.Copy(temp, destination, true);
            }
        }
        finally
        {
            TryDeleteFile(temp);
        }

        item.ImageProcess = settings.Clone();
        item.BackgroundRemoved = settings.RemoveBackground;

        // 尺寸真值在 ImageProcess 里；有标注就同步旧字段镜像，别让仍读 WidthMm/HeightMm
        // 的老版本插件看到过期的数。没有标注时**不动**旧字段——老条目可能只靠它就定着 65mm，
        // 顺手清零等于把用户的设置删了。
        if (settings.TryGetDeclaredSizeMm(out var declaredWidthMm, out var declaredHeightMm))
        {
            item.WidthMm = declaredWidthMm;
            item.HeightMm = declaredHeightMm;
        }

        item.UpdatedAt = DateTime.Now;
        return size;
    }

    /// <summary>
    /// 还原成处理前的原图（备份仍在时）。成功返回 true；没有备份返回 false。
    /// </summary>
    public static bool RestoreOriginalImage(SignatureItem item)
    {
        if (item == null)
        {
            return false;
        }

        var backup = GetOriginalBackupPath(item);
        if (backup.Length == 0 || !File.Exists(backup))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(item.ImageFile))
        {
            item.ImageFile = item.Id + ".png";
        }

        var directory = GetDirectory(item.Kind);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, item.ImageFile);

        try
        {
            File.Copy(backup, destination, true);
        }
        catch (Exception ex)
        {
            PluginLog.Error("签章", $"还原原图失败：{item.ImageFile} — {ex.Message}", ex);
            return false;
        }

        item.ImageProcess = null;
        item.BackgroundRemoved = false;
        item.UpdatedAt = DateTime.Now;
        return true;
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 文件被占用删不掉不影响主流程：条目该删的已删，残留文件下次可手工清理。
        }
    }

    private static SignatureLibrary Deserialize(string path)
    {
        var loaded = JsonConvert.DeserializeObject<SignatureLibrary>(File.ReadAllText(path)) ?? new SignatureLibrary();
        loaded.Items ??= new List<SignatureItem>();
        loaded.Presets ??= new List<SignaturePreset>();
        foreach (var preset in loaded.Presets)
        {
            preset.SlotMap ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var item in loaded.Items)
        {
            item.ImageFile ??= "";
            item.Name ??= "";
        }

        return loaded;
    }

    private static Bitmap LoadBitmap(string path)
    {
        // 先读进内存再解码：直接 Image.FromFile 会一直持有文件句柄，
        // 用户从 U 盘/网络盘导入时后续无法释放，甚至影响文件删除。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var image = Image.FromStream(stream);
        return ToArgb(image);
    }

    private static Bitmap ToArgb(Image image)
    {
        var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.DrawImage(image, 0, 0, image.Width, image.Height);
        return bitmap;
    }

    /// <summary>
    /// 按默认阈值去白底（导入 / 替换时的快捷处理；细化调整走「图片处理」对话框）。
    /// 具体像素规则见 <see cref="ImageProcessor.ApplyBackgroundRemoval"/>。
    /// </summary>
    private static Bitmap RemoveWhiteBackground(Bitmap source)
    {
        return ImageProcessor.ApplyBackgroundRemoval(source, ImageProcessor.DefaultThreshold);
    }
}
