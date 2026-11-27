using System.Globalization;

namespace BeamSchedulePlugin;

internal static class MappingLevels
{
    private static bool Number(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    private static int PrefixLength(string value) => value.TakeWhile(char.IsDigit).Count();

    internal static bool LevelMatches(string level, string filter) =>
        string.Equals(level.Trim(), filter, StringComparison.OrdinalIgnoreCase)
        || Number(filter, out int requested) && Number(level.Trim()[..PrefixLength(level.Trim())], out int actual) && actual == requested;

    internal static bool StoryMatches(string story, string filter)
    {
        story = story.Trim();
        if (LevelMatches(story, filter)) return true;
        if (!story.StartsWith("Story", StringComparison.OrdinalIgnoreCase) || !Number(story[5..], out int storyNumber))
            return false;
        int prefix = PrefixLength(filter);
        if (!Number(filter[..prefix], out int level) || level != storyNumber) return false;
        return filter[prefix..].Trim().ToUpperInvariant() is "" or "ST STOREY" or "ND STOREY" or "RD STOREY" or "TH STOREY";
    }

    internal static bool SameLevel(string? story, string? level) =>
        !string.IsNullOrWhiteSpace(story) && !string.IsNullOrWhiteSpace(level) && StoryMatches(story, level.Trim());
}
