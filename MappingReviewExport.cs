namespace BeamSchedulePlugin;

internal static class MappingReviewExport
{
    internal static RevitMappingResult MapWorkbooks(string etabsPath, string revitPath, RevitMappingOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(revitPath))
            throw new InvalidOperationException("Select the Revit beam schedule workbook before exporting a mapping review.");
        var etabs = RevitScheduleReader.ReadEtabs(etabsPath);
        return RevitBeamMatcher.Match(etabs.Beams, RevitScheduleReader.Read(revitPath), etabs.Skipped, options)
            with { EtabsFilename = etabs.Filename };
    }

    internal static string Write(string workbook, DemandReport report, SummaryTable summary,
        bool overwriteWorkbook = false, string? levelFilter = null)
    {
        var review = new MappingReview(summary.Mapping ?? throw new ArgumentException("Mapping is required."), levelFilter);
        string deck = Path.ChangeExtension(workbook, ".pptx");
        string temporary = deck + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool published = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                MappingReviewDeck.Write(stream, review);
            File.Move(temporary, deck);
            published = true;
            DemandScheduleWriter.Write(workbook, report, summary, overwriteWorkbook, review);
            return deck;
        }
        catch
        {
            if (published) File.Delete(deck);
            throw;
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
