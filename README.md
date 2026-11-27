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
without cached values are not evaluated. An **Element ID** column
(also `ElementId`, `Element_ID`, `Revit Element ID`, `RevitElementId`, or
`Revit ID`) supplies the stable Revit reference. Rows with missing or duplicate
IDs are skipped and audited. The audit retains
the Excel row number. An optional **Mark** column supplies the Revit beam name;
it is retained alongside the reference, not used as a unique identifier. Missing
Marks are reported and fall back to the ElementId in the review views. Invalid or coincident endpoints are skipped and listed
separately, not counted as valid unmatched Revit beams. Unreadable workbooks or
missing/duplicate required headers stop the export with an error so the selection
can be corrected or cleared.

The **Revit mapping settings** tab exposes endpoint tolerance, angle, long-
and short-span projection coverage, lateral and vertical offsets, and maximum
end-extension. The other thresholds use the defaults in the table below.
**Reset to defaults** restores all editable mapping thresholds without changing
the selected workbook or other export settings. Mapping settings apply when exporting Excel
with a Revit workbook selected or when running **Map two workbooks and export
review**. The button prompts for the Revit workbook if none was selected under
**Output and review**, then asks for the ETABS workbook and output filename.
Coverage inputs are inclusive fractions from 0 to 1; the angle is
0 to 90 degrees; distances are non-negative and the endpoint tolerance must be
positive. All settings must be finite.
The actual selected settings are recorded on the **Revit mapping** worksheet.
The form starts with the current defaults each time; it does not persist or
reload settings from an older export.

Mapping uses the full live ETABS model, or the full ETABS `Beam summary` and
Revit `RC Beam Schedule` for workbook-only review. The ETABS workbook supplies
`Unique ID`, `Beam label`, `Story` and the six I/J coordinate columns in mm;
Revit supplies `ElementId`, `Mark` and `Level`. Beam scope still controls the
live schedule, and a selected story limits the review view only after matching.
Linked ETABS lines use their matched Revit levels for display; unmatched lines
use ETABS Story, including the `Story3` / `3rd Storey` convention.
End offsets are not applied. The matcher estimates one global translation:

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

### Seven ordered checks

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
| 5 | End-extension review | Same angle and offsets as pass 3, but below 85% longer-span coverage and at least 85% shorter-span coverage. Each projected end difference must be at most 1,500 mm. Unresolved standard offsets still compete in the ranking. |
| 6 | Split found (extended) | Pass 4 with up to 350 mm outer overhang; unchanged 50 mm internal joins and 85% combined Revit coverage. Only groups exceeding the earlier 50 mm outer allowance are added here. |
| 7 | Chain-to-chain review | Unambiguous connected chains with at least two segments per side; 50 mm internal joins, 0.5 degrees, 850 mm lateral, 750 mm vertical, 85% longer-span overlap, and 1,500 mm projected outer-end differences at each end. Competing chain pairings are rejected. Only segment pairs with projected overlap **greater than 1 mm** emit links. |

Split group coverage is the union of projected covered intervals divided by
Revit length; overlaps are not counted twice. The 85% longer-span check is not
applied independently to each split segment.

Measurements follow the prototype: project the ETABS endpoints onto the Revit
axis, then measure residual horizontal magnitude and absolute vertical offset.
Coverage uses actual line lengths, not a component-wise coordinate test.
The chain check measures all constituent endpoints against the opposite span.
Staggered internal split locations do not have to align.

Both 1:1 offset passes require mutual-nearest candidates ranked by
maximum 3D endpoint-to-axis residual, with a **25 mm runner-up margin in both
directions** (exactly 25 mm is accepted). Candidates at all eligible elevations
compete together without a separate low-elevation priority.
Competing strict-axis targets, split groups sharing a source segment, ambiguous
chain neighbors and competing chain pairings are audited rather than forced.
Every assigned link is **Matched**; later-pass provenance is recorded separately.

With mapping enabled, **Beam summary** appends status, linked Revit references,
group ID, review flag, Revit Marks, pass name and available endpoint error.
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

The companion PowerPoint automatically applies the review style: a navy level
header, a color-coded legend, and framed Revit and ETABS panels with match counts.
Each level has one zoomable slide with same-scale vector plans. Green lines are assigned, gray lines are
unmatched, and magenta markers identify unresolved same-level end-extension
alternatives. To enter the review queue, both beams must still be unmatched and
pass the angle, lateral, vertical, and overlap checks. The projected end
difference limits automatic assignment only; it is recorded with the reason
in Excel. The slide footer lists review IDs, beam names, and longer-span
overlap; Excel lists all candidates when a slide's footer runs out of room.

Enter **Accept**, **Reject** or **Defer** in **Visual Review** along with the
reviewer and notes. Blank means pending. Edit only those decision fields;
do not copy decision cells to a different run. Excel recalculates **Effective
Links** using the run and stable beam IDs. Filter `Active` to `Yes` for the
usable set: it includes all automatic groups plus non-conflicting accepted
overrides. Conflicting or stale decisions stay inactive and are reported.

Totals count **distinct model lines separately**, not segment-link rows.
**Full match** means all valid lines are linked and none skipped; otherwise
a non-empty alignment is **Partial alignment**.
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

This path uses the same workbook mapping and paired export as the plugin and
also writes `mapping-verification.json` for exact comparison. It checks that both
source workbooks remain unchanged. Append `100` to test a 100 mm automatic
end-extension / chain limit. Project workbooks are not committed.

For Grasshopper parity, use [Compare-GrasshopperMapping.ps1](./tests/Compare-GrasshopperMapping.ps1)
with `-GrasshopperSource` and `-PrepareHostDirectory` to compile the supplied
reference methods. In Rhino 8 (.NET Core), load the resulting `Oracle.dll` from
bytes and invoke its assembly entry point with a `string[]` containing the ETABS,
Revit and reference JSON paths. Run the script again with `-ReferenceJson` and
`-PluginVerificationJson` to verify translation, exact links, groups and unmatched
sets.
