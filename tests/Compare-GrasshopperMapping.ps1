param(
    [string]$GrasshopperSource,
    [string]$PrepareHostDirectory,
    [string]$ReferenceJson,
    [string]$PluginVerificationJson,
    [string]$RhinoSystem = "$env:ProgramFiles\Rhino 8\System"
)

$ErrorActionPreference = 'Stop'
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "beam-mapping-oracle-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    if ($PrepareHostDirectory) {
    if (!$GrasshopperSource) { throw 'Specify -GrasshopperSource when preparing the Rhino-hosted reference.' }
    $rhino = [System.Security.SecurityElement]::Escape((Join-Path $RhinoSystem 'RhinoCommon.dll'))
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><Reference Include="RhinoCommon"><HintPath>$rhino</HintPath></Reference></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $scratch 'Oracle.csproj') -Encoding UTF8
    $source = Get-Content -LiteralPath $GrasshopperSource -Raw
    function Extract([string]$start, [string]$end) {
        $i = $source.IndexOf($start, [StringComparison]::Ordinal)
        $j = if ($i -ge 0) { $source.IndexOf($end, $i, [StringComparison]::Ordinal) } else { -1 }
        if ($j -lt 0) { throw "Cannot find reference extraction boundaries: $start / $end" }
        $source.Substring($i, $j - $i)
    }
    $geometry = Extract '    private static bool TryFindTranslation(' '    private static bool MarkMatches('
    $reader = Extract '    private static List<Line> ReadLines(' '    private static Stream OpenPart('
    @"
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Rhino.Geometry;
static class Oracle
{
    private const string SplitFound = "Split found", EndExtensionReview = "End-extension review", ChainReview = "Chain-to-chain review";
    public static void Main(string[] args)
    {
        var e = ReadLines(args[0], "Beam summary", new[] { "I X (mm)", "I Y (mm)", "I Z (mm)", "J X (mm)", "J Y (mm)", "J Z (mm)" },
            false, null, out var er, out _, out _, out _, out _);
        var r = ReadLines(args[1], "RC Beam Schedule", new[] { "StartX_mm", "StartY_mm", "StartZ_mm", "EndX_mm", "EndY_mm", "EndZ_mm" },
            false, null, out var rr, out _, out _, out _, out _);
        if (!TryFindTranslation(e, r, 10, out var offset, out var endpoints)) throw new Exception("Reference alignment failed.");
        var moved = e.Select(l => new Line(l.From + offset, l.To + offset)).ToArray();
        var links = FindMatches(moved, r, endpoints, 10, .5, 50, .90, .85, 850, 100, 750, .85, .85, 1500, 350, 25, 1, out _);
        var usedE = links.Select(l => l.Source).ToHashSet();
        var usedR = links.Select(l => l.Target).ToHashSet();
        File.WriteAllText(args[2], JsonSerializer.Serialize(new
        {
            TranslationMm = new { offset.X, offset.Y, offset.Z },
            Links = links.Select(l => new { Source = int.Parse(er[l.Source]), Target = int.Parse(rr[l.Target]), PassName = l.Kind,
                ReferenceGroup = l.Kind == ChainReview ? l.Description.Split('|')[2].Split(':')[0].Trim() :
                    l.Kind == SplitFound ? $"Split {l.Target}" : $"Pair {l.Source}/{l.Target}" }),
            UnmatchedEtabsRows = er.Where((_, i) => !usedE.Contains(i)).Select(int.Parse),
            UnmatchedRevitRows = rr.Where((_, i) => !usedR.Contains(i)).Select(int.Parse)
        }));
    }
$geometry
$reader
    private static Stream OpenPart(ZipArchive workbook, string path) =>
        (workbook.GetEntry(path) ?? throw new InvalidDataException(path)).Open();
}
"@ | Set-Content -LiteralPath (Join-Path $scratch 'Program.cs') -Encoding UTF8
    dotnet build (Join-Path $scratch 'Oracle.csproj') -o $PrepareHostDirectory
    if ($LASTEXITCODE -ne 0) { throw "Grasshopper reference build failed (exit $LASTEXITCODE)." }
    return
    }
    if (!$ReferenceJson -or !$PluginVerificationJson) { throw 'Specify -ReferenceJson and -PluginVerificationJson to compare runs.' }
    $expected = Get-Content -LiteralPath $ReferenceJson -Raw | ConvertFrom-Json
    $actual = Get-Content -LiteralPath $PluginVerificationJson -Raw | ConvertFrom-Json
    foreach ($axis in 'X','Y','Z') {
        if ([Math]::Abs($actual.TranslationMm.$axis - $expected.TranslationMm.$axis) -gt 1e-8) { throw "Translation differs on $axis." }
    }
    function LinkKey($link) { "$($link.Source)|$($link.Target)|$($link.PassName)" }
    $diff = Compare-Object @($expected.Links | ForEach-Object { LinkKey $_ }) @($actual.Links | ForEach-Object { LinkKey $_ })
    if ($diff) { $diff | Select-Object -First 12 | Format-Table | Out-Host; throw 'Exact link/pass sets differ.' }
    foreach ($field in 'UnmatchedEtabsRows','UnmatchedRevitRows') {
        if (($expected.$field -join ',') -ne ($actual.$field -join ',')) { throw "$field differs." }
    }
    $referenceByLink = @{}
    foreach ($link in $expected.Links) { $referenceByLink[(LinkKey $link)] = $link.ReferenceGroup }
    $pluginToReference = @{}
    $referenceToPlugin = @{}
    foreach ($link in $actual.Links) {
        $reference = $referenceByLink[(LinkKey $link)]
        if (($pluginToReference.ContainsKey($link.GroupId) -and $pluginToReference[$link.GroupId] -ne $reference) -or
            ($referenceToPlugin.ContainsKey($reference) -and $referenceToPlugin[$reference] -ne $link.GroupId)) {
            throw "Mapping group differs for $($link.Source) / $($link.Target)."
        }
        $pluginToReference[$link.GroupId] = $reference
        $referenceToPlugin[$reference] = $link.GroupId
    }
    Write-Output "Grasshopper parity passed: translation, $($actual.Links.Count) exact links/pass names and groups, and both unmatched sets."
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
