using ZwcadBatchPlot;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

var failures = new List<string>();

CheckScale(210, 148.5, "2:1");
CheckScale(42, 29.7, "10:1");
CheckScale(840, 594, "1:2");
CheckScale(420, 297, "1:1");
CheckPaperCandidates();
CheckLongFileName();
CheckLongFileNameCollision();
CheckLibraryRecovery();
CheckLibraryRecoveryWhenPrimaryIsMissing();
CheckPdfMerge();

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("Robustness tests passed.");
return 0;

void CheckScale(double width, double height, string expected)
{
    var detected = PaperSizeDetector.Detect(width, height);
    if (!string.Equals(detected.ScaleText, expected, StringComparison.Ordinal))
    {
        failures.Add($"Scale {width}x{height}: expected {expected}, actual {detected.ScaleText}");
    }
}

void CheckPaperCandidates()
{
    var candidates = PaperSizeDetector.DetectCandidates(420, 297);
    if (!candidates.Any(candidate => candidate.PaperName == "A3" && candidate.ScaleText == "1:1")
        || !candidates.Any(candidate => candidate.PaperName == "A1" && candidate.ScaleText == "2:1"))
    {
        failures.Add("Paper candidate detection did not retain valid alternate paper/scale matches.");
    }
}

void CheckLongFileName()
{
    var directory = Path.Combine(Path.GetTempPath(), "ZbpRobustness", new string('d', 80));
    var path = FileNameSanitizer.MakeUnique(directory, new string('图', 300), new HashSet<string>(), false);
    if (Path.GetFileNameWithoutExtension(path).Length > 120 || path.Length > 245)
    {
        failures.Add($"Filename sanitizer produced an unsafe path length: {path.Length}");
    }
}

void CheckLongFileNameCollision()
{
    var root = Path.Combine(Path.GetTempPath(), "ZbpRobustness");
    var directory = Path.Combine(root, new string('d', Math.Max(1, 170 - root.Length)));
    var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var first = FileNameSanitizer.MakeUnique(directory, new string('图', 300), reserved, false);
    var second = FileNameSanitizer.MakeUnique(directory, new string('图', 300), reserved, false);
    if (first.Length > 240 || second.Length > 240 || string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
    {
        failures.Add($"Filename collision handling is unsafe: first={first.Length}, second={second.Length}");
    }
}

void CheckLibraryRecovery()
{
    var directory = Path.Combine(Path.GetTempPath(), "ZbpRobustness", Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "TitleBlockLibrary.json");
    var first = new TitleBlockLibrary
    {
        Blocks =
        {
            new TitleBlockDefinition { BlockName = "FIRST" }
        }
    };
    var second = new TitleBlockLibrary
    {
        Blocks =
        {
            new TitleBlockDefinition { BlockName = "SECOND" }
        }
    };

    TitleBlockLibraryStore.Save(first, path);
    TitleBlockLibraryStore.Save(second, path);
    File.WriteAllText(path, "{broken json");
    var recovered = TitleBlockLibraryStore.Load(path);
    if (recovered.Blocks.Count != 1 || recovered.Blocks[0].BlockName != "FIRST")
    {
        failures.Add("Title-block library backup recovery failed.");
    }

    Directory.Delete(directory, true);
}

void CheckLibraryRecoveryWhenPrimaryIsMissing()
{
    var directory = Path.Combine(Path.GetTempPath(), "ZbpRobustness", Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "TitleBlockLibrary.json");
    var library = new TitleBlockLibrary
    {
        Blocks =
        {
            new TitleBlockDefinition { BlockName = "BACKUP_ONLY" }
        }
    };

    Directory.CreateDirectory(directory);
    File.WriteAllText(path + ".bak", Newtonsoft.Json.JsonConvert.SerializeObject(library));
    var recovered = TitleBlockLibraryStore.Load(path);
    if (recovered.Blocks.Count != 1 || recovered.Blocks[0].BlockName != "BACKUP_ONLY")
    {
        failures.Add("Title-block library did not recover when only the backup exists.");
    }

    Directory.Delete(directory, true);
}

void CheckPdfMerge()
{
    var directory = Path.Combine(Path.GetTempPath(), "ZbpRobustness", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var first = Path.Combine(directory, "first.pdf");
    var second = Path.Combine(directory, "second.pdf");
    var merged = Path.Combine(directory, "merged.pdf");

    try
    {
        CreateTestPdf(first);
        CreateTestPdf(second);
        PdfDocumentService.Merge(new[] { first, second }, merged);

        using var document = PdfReader.Open(merged, PdfDocumentOpenMode.Import);
        if (document.PageCount != 2)
        {
            failures.Add($"PDF merge page count is incorrect: expected 2, actual {document.PageCount}.");
        }
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

void CreateTestPdf(string path)
{
    using var document = new PdfDocument();
    var page = document.AddPage();
    using (var graphics = XGraphics.FromPdfPage(page))
    {
        graphics.DrawLine(XPens.Black, 10, 10, 100, 100);
    }

    document.Save(path);
}
