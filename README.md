# ETABS Beam Schedule Plugin

This plugin exports a draft concrete beam schedule for ETABS 22 and 23.
Automatic reinforcement proposals are area-based suggestions, not completed
designs. The plugin does not verify that analysis and design results are current
or perform a complete structural or detailing check. A structural engineer
must review every result before use.

## Build

Use Windows, a .NET 8-capable SDK, and an ETABS installation with
`CSiAPIv1.dll`. Build against the ETABS 22 API when targeting both versions:

```powershell
dotnet build .\BeamSchedulePlugin.csproj -c Release
```

The project first looks for ETABS 22 in its default installation directory,
then ETABS 23. To specify an API location, use:

```powershell
dotnet build .\BeamSchedulePlugin.csproj -c Release "-p:EtabsApiPath=C:\Program Files\Computers and Structures\ETABS 22\CSiAPIv1.dll"
```

The ETABS 23 API can be used to develop for 23, but a build against the older
ETABS 22 API is recommended for compatibility with both versions. Verify
the finished plugin in each version. The host supplies `CSiAPIv1.dll`; do not
copy it into the plugin output folder.

If ETABS has already loaded the plugin, it may lock the output DLL. Close ETABS
before replacing that DLL, or build to a separate directory without interrupting
the current model session:

```powershell
dotnet build .\BeamSchedulePlugin.csproj -c Release -o .\bin\RevitMappingReview
```

The running ETABS session continues using its previously loaded assembly.

## Load in ETABS

1. Open a model in ETABS 22 or 23.
2. Select **Tools > Add/Show Plugins**, then **Browse**.
3. Select `bin\Release\net8.0-windows\BeamSchedulePlugin.dll` and click **Add**.
4. Run the plugin from the **Tools** menu. Keep the output files together.
5. Choose all beams, selected beams, or **Specified groups** and enter ETABS
   group names in the text box, one per line.
   Choose all levels or a single ETABS story. The level filters the chosen
   beam scope using ETABS story assignments.
   Click **Export schedule** and choose an Excel workbook or a station-data CSV.
   The suggested filename includes the local date and time, beam scope and
   level, for example `261123_163512 ETABS beam schedule - All beams - Level 2.xlsx`.
   It can be changed before saving.

The export requires existing concrete design results using Eurocode 2-2004.
It does not run analysis or design or change model units. Results are converted
from the current API units to mm, mm2, and mm2/m.

The Excel workbook has a **Beam summary** sheet with one row per beam; it does
not include a design stations sheet. Export CSV separately for the original
station-level demands, governing combinations and warnings. The summary includes filter dropdowns for story,
review status, ETABS errors and warnings, geometry warning, and assigned and
design sections. The summary uses the highest reported station demand
in each I, middle and J zone. Fractions apply to the clear length between ETABS
end offsets; stations on boundaries count in both zones. Stations in end-offset
regions belong to the adjacent end zone. A zone without stations is flagged,
not interpolated. All beams use the same three positive fractions, including
cantilevers; there is no separate cantilever zoning. The CSV option exports only
station data. Model path, design code and export time are not included
in the output.

The summary groups required areas, reinforcement descriptions, provided areas,
and ratios in the same order as the reference ETABS design sheet. Width and
height are read from rectangular design sections. The I and J ends correspond
to the ETABS object orientation, not necessarily physical left and right.

Excel exports use the **Reinforcement** tab settings to generate automatic
draft proposals; manual-entry mode is not offered in the form. Bar diameters are
tried in the entered order; single-layer options are tried before multiple
layers. The first-layer minimum bar count follows the colleague's
width-dependent heuristic, with at least two bars. Link diameters and leg
counts are tried in entered order and spacings decrease from the maximum in
the chosen increments. One outer link counts as two effective legs and each
additional hook counts as one. The proposal checks the supplied cover and
spacing inputs, rectangular section width, and approximate space for the top
and bottom layers. It does not check hook anchorage, confinement, continuity,
cut-offs, laps, minimum reinforcement per code, or other detailing requirements.
Torsion is flagged but excluded from the proposals. ETABS warnings and errors,
unsupported section shape, missing zones, or inability to find a proposal are
flagged and proposals are not generated for those affected cases.
Results may be stale. Review the settings recorded in the workbook and confirm
they meet the project's design and detailing requirements.

Provided longitudinal area is the sum of n × π × diameter² / 4 (mm2). Provided
shear area is legs × π × diameter² / 4 × 1000 / spacing (mm2/m). Excel shows
the required area divided by the provided area. Numeric measurements and areas
are displayed as whole numbers in Excel without rounding the stored values or
calculations; area ratios are displayed to two decimal places. If an automatic description
is edited, its calculated area displays a re-export warning and its ratio is
blank: change the settings and regenerate, rather than editing the proposal.
Ratios are suppressed for missing stations, design errors, ETABS
warnings or invalid geometry. Ratios can appear for torsion-flagged beams, but
exclude torsion and are not verification of the full reinforcement required.
Torsional reinforcement must be designed and detailed separately and combined
with the flexural and shear requirements by the engineer. These ratios compare
areas only: they are **not** design, code or detailing checks. No compliance
with the Singapore National Annex or project detailing rules is claimed.

Saving the export may replace an existing file after confirmation.

## Optional Revit beam mapping (Excel only)

In **Output and review**, browse for an optional **Revit beam schedule (.xlsx)**
before exporting. **Clear** removes the selection. Leaving it blank preserves
the beam summary without a mapping sheet; CSV always exports station data only and ignores
the Revit selection. The Revit workbook must have an **RC Beam Schedule**
worksheet whose first non-empty row contains these headers (case-insensitive):

```text
StartX_mm, StartY_mm, StartZ_mm, EndX_mm, EndY_mm, EndZ_mm
```

Coordinates must be numeric, finite and in millimetres. Excel numeric cells,
inline/shared strings and cached numeric formula values are supported; formulas
without cached values are not evaluated. An optional **Element ID** column
(also `ElementId`, `Element_ID`, `Revit Element ID`, `RevitElementId`, or
`Revit ID`) supplies the actual Revit reference. Missing IDs are explicitly
identified as **Excel row N**, never as Element IDs. The audit always retains
the Excel row number. An optional **Mark** column supplies the Revit beam name;
it is retained alongside the reference, not used as a unique identifier. Missing
Marks remain blank or are explicitly shown as not provided. Invalid or coincident endpoints are skipped and listed
separately, not counted as valid unmatched Revit beams. Unreadable workbooks or
missing/duplicate required headers stop the export with an error so the selection
can be corrected or cleared.

The **Revit mapping settings** tab exposes the adjustable thresholds. The
defaults are those in the table below; settings apply only when exporting Excel
with a Revit workbook selected. Chain segment links must overlap by more than
1 mm; this threshold is fixed rather than editable in the form.
Coverage values are fractions, not percentages. Values must be finite and
within their displayed meaning: coverage is greater than 0 and at most 1,
distances are non-negative (endpoint tolerance must be positive), and the shared
angle must be at least zero and less than 90 degrees. Extended overhang must be
at least the join limit. Invalid settings stop the export with an explanation.
The shared angle, lateral offset and span/group coverage apply to all relevant
checks; ETABS segment containment and review-split vertical offset are separate.
The actual selected settings are recorded on the **Revit mapping** worksheet.
The form starts with the current defaults each time; it does not persist or
reload settings from an older export.

Mapping uses the in-memory ETABS object endpoints already collected for the
summary, after applying the selected beam scope and story filter. End offsets
are not applied to mapping geometry. It estimates one global translation:

```text
RevitXYZ = ETABSXYZ + (dX, dY, dZ)
```

No rotation or scaling is applied. As in the Grasshopper prototype, compatible
beam directions vote for midpoint offsets in bins sized by the endpoint tolerance
(10 mm by default); the top 24 bins are
shortlisted using deterministic ties and their mean offsets are scored.
Both 3D endpoints must be within the selected tolerance inclusive (10 mm by
default), allowing reversed ends.
For prototype parity, each translation is scored by greedily assigning endpoint
pairs in ascending squared maximum endpoint error, using each line once.
Candidates rank by pair count, then summed squared endpoint error. Equal votes,
pair errors and candidate scores keep the prototype's stable input/traversal
order. The shortlist is a heuristic, not exhaustive registration. Duplicate
endpoint counterparts/orientations and spatially distinct equal-count translation
alternatives are flagged for review. If no qualifying endpoint pair establishes
a vector, the export explicitly reports **Translation not established - No match**,
leaves the components blank and does not run later checks.

### Six ordered checks

The matcher follows the ordered Grasshopper checks, not generic nearest-neighbor
assignment.
Coordinates are translated once. Reversed ends are allowed throughout.
Once a line is assigned to a group, later checks cannot reuse or reassign it.
Split and chain groups may contain several links using the same model reference.

The following are the default settings; edited values replace the relevant limits.

| Pass | Check | Limits and coverage |
|---|---|---|
| 1 | Endpoint | Both 3D endpoints within 10 mm. |
| 2 | Strict axis / split | At most 0.5 degrees and 10 mm 3D axis offset; at least 90% of each ETABS segment within the Revit span, 85% combined Revit coverage; 50 mm span, gap and projected overlap checks. |
| 3 | Offset review | At most 0.5 degrees, 850 mm lateral and 750 mm vertical; at least 85% overlap of the longer span. All eligible candidates compete together; no projected end-difference limit. |
| 4 | Split found (offset) | Multiple connected ETABS segments to one Revit beam; at most 0.5 degrees, 850 mm lateral and 100 mm vertical; 90% per-segment containment, 85% combined Revit coverage, 50 mm outer overhang and internal projected/3D joins. |
| 5 | Split found (extended) | Pass 4 with up to 350 mm outer overhang; unchanged 50 mm internal joins and 85% combined Revit coverage. Only groups exceeding the earlier 50 mm outer allowance are added here. |
| 6 | Chain-to-chain review | Unambiguous connected chains with at least two segments per side; 50 mm internal joins, 0.5 degrees, 850 mm lateral, 750 mm vertical, 85% longer-span overlap, and 1,500 mm projected outer-end differences at each end. Competing chain pairings are rejected. Only segment pairs with projected overlap **greater than 1 mm** emit links. |

Split group coverage is the union of projected covered intervals divided by
Revit length; overlaps are not counted twice. The 85% longer-span check is not
applied independently to each split segment. The reference's legacy
end-extension criteria use 85% coverage of both shorter and longer lines and
1,500 mm at each end, but those candidates are already included in offset
review and the legacy pass adds no assignments.

Measurements follow the prototype: project the ETABS endpoints onto the Revit
axis, then measure residual horizontal magnitude and absolute vertical offset.
Coverage uses actual line lengths, not a component-wise coordinate test.
The chain check measures all constituent endpoints against the opposite span.
Staggered internal split locations do not have to align.

The unified 1:1 offset review requires mutual-nearest candidates ranked by
maximum 3D endpoint-to-axis residual, with a **25 mm runner-up margin in both
directions** (exactly 25 mm is accepted). Candidates at all eligible elevations
compete together without a separate low-elevation priority.
Competing strict-axis targets, split groups sharing a source segment, ambiguous
chain neighbors and competing chain pairings are audited rather than forced.
Passes 3-6 are visibly **review candidates**, not strict geometric matches.

With mapping enabled, **Beam summary** appends status, linked Revit references,
group ID, review flag, Revit Marks, pass number and available endpoint error.
Design, reinforcement and formula columns are neither moved nor changed.
**Revit mapping** contains:

- Translation, direction, all tolerance/coverage settings and warnings.
- A **Revit Mark** filter. Filtering also hides audit and summary rows without
  the selected Mark; clear the filter to see all rows again.
- Per-pass distinct Revit/ETABS additions, group/link counts, running totals,
  percentage-point coverage gains and running coverage percentages.
- One **Segment link** row per assigned ETABS-Revit segment pair, with the ETABS
  unique ID and beam label in adjacent columns and a common
  group ID for split/chain links, IDs/Marks, original coordinates, pass/review
  flags, angle, axis/lateral/vertical offsets, projected overlap, coverage and
  available end/join measurements.
- Separate **Unmatched ETABS**, **Unmatched Revit**, **Ambiguous candidate** and
  skipped-invalid-row records. Ambiguity records are pass-specific history;
  a line can subsequently be resolved by a later permitted check.

Totals count **distinct model lines separately**, not segment-link rows.
**Full match** means all valid lines are linked (including review candidates)
and none skipped; otherwise a non-empty alignment is **Partial alignment**.
Neither label confirms structural identity. Review flags remain authoritative.
An unmatched line can reflect segmentation or ambiguity, not just a distance
exceeding the endpoint tolerance.

This is a **geometric match for review, not structural verification**. It does
not verify structural identity, design adequacy, section assignments, offsets,
reinforcement, or model freshness. Neither model nor the source Revit workbook
is modified, and the export cannot overwrite the selected Revit source.
Project workbooks are not required for tests; all mapping fixtures are synthetic.

This follows CSI's [.NET 8 plugin example](https://web.wiki.csiamerica.com/wiki/spaces/kb/pages/2011456/NET+8+Plugin+Example+-+All+Products).
ETABS uses .NET 8, not the separate legacy .NET Framework product.

## Test

Run the summary, reinforcement, synthetic Revit mapping and workbook tests with:

```powershell
dotnet run --project .\tests\BeamScheduleTests.csproj -c Release
```

Optional offline verification against the full Grasshopper trial inputs:

```powershell
dotnet run --project .\tests\BeamScheduleTests.csproj -c Release -- --mapping-workbooks "C:\path\ETABS.xlsx" "C:\path\Revit.xlsx" "C:\path\verification-output"
```

This test-only path reads the existing trial exports and writes a separate mapped
workbook, CSV link audit and JSON report; the plugin itself always maps in-memory
ETABS coordinates. It checks that the saved audit contains the calculated links,
review flags and current settings, and that both source workbooks remain unchanged.
Project workbooks are not committed.
