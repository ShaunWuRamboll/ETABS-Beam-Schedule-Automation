using System.Security.Cryptography;
using System.Text.Json;
using BeamSchedulePlugin;

internal static class WorkbookMappingVerification
{
    internal static void Run(string etabsPath, string revitPath, string outputDirectory, RevitMappingOptions options)
    {
        string etabsHash = Hash(etabsPath), revitHash = Hash(revitPath);
        var mapping = MappingReviewExport.MapWorkbooks(etabsPath, revitPath, options);
        var summary = new SummaryTable(Array.Empty<object?[]>(), mapping);
        var report = new DemandReport(Array.Empty<object?[]>(), 0, 0);
        Directory.CreateDirectory(outputDirectory);
        string workbook = Path.Combine(outputDirectory, "Beam mapping review.xlsx");
        MappingReviewExport.Write(workbook, report, summary);

        if (etabsHash != Hash(etabsPath) || revitHash != Hash(revitPath))
            throw new InvalidOperationException("A source workbook changed during verification.");
        var mappedEtabs = mapping.Links.Select(l => l.Etabs).ToHashSet();
        var mappedRevit = mapping.Links.Select(l => l.Revit).ToHashSet();
        var results = new
        {
            mapping.RunId, mapping.Alignment, TranslationMm = mapping.Translation,
            EtabsValid = mapping.Etabs.Count, RevitValid = mapping.Revit.Beams.Count,
            mapping.MappedEtabsCount, mapping.MappedRevitCount, mapping.PassTotals,
            SkippedEtabs = mapping.SkippedEtabs.Count, SkippedRevit = mapping.Revit.Skipped.Count,
            VisualReviewPairs = mapping.VisualCandidates.Count, SourceHashesUnchanged = true,
            Links = mapping.Links.Select(l => new { Source = l.Etabs.ExcelRow, Target = l.Revit.ExcelRow,
                EtabsId = l.Etabs.StableId, RevitId = l.Revit.StableId, l.PassName, l.GroupId }),
            UnmatchedEtabsRows = mapping.Etabs.Where(b => !mappedEtabs.Contains(b)).Select(b => b.ExcelRow),
            UnmatchedRevitRows = mapping.Revit.Beams.Where(b => !mappedRevit.Contains(b)).Select(b => b.ExcelRow)
        };
        string json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outputDirectory, "mapping-verification.json"), json);
        Console.WriteLine($"{mapping.Alignment}: {mapping.Links.Count} links, {mapping.VisualCandidates.Count} visual pairs.");
    }

    private static string Hash(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(file));
    }
}
