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
5. Choose all beams, selected beams, or manually entered ETABS group names.
   Choose all levels or a single ETABS story. The level filters the chosen
   beam scope using ETABS story assignments.
   Click **Export schedule** and choose an Excel workbook or a station-data CSV.
   The suggested filename includes the local date and time, beam scope and
   level, for example `261123_163512 ETABS beam schedule - All beams - Level 2.xlsx`.
   It can be changed before saving.

The export requires existing concrete design results using Eurocode 2-2004.
It does not run analysis or design or change model units. Results are converted
from the current API units to mm, mm2, and mm2/m.

The Excel workbook has a **Beam summary** sheet with one row per beam and a
**Design stations** sheet with the original station-level demands, governing
combinations and warnings. The summary includes filter dropdowns for story,
review status, ETABS errors and warnings, geometry warning, and assigned and
design sections. The summary uses the highest reported station demand
in each I, middle and J zone. Fractions apply to the clear length between ETABS
end offsets; stations on boundaries count in both zones. Stations in end-offset
regions belong to the adjacent end zone. A zone without stations is flagged,
not interpolated. All beams use the same three positive fractions, including
cantilevers; there is no separate cantilever zoning. The CSV option exports only
the station sheet. Model path, design code and export time are not included
in the output.

The summary groups required areas, reinforcement descriptions, provided areas,
and ratios in the same order as the reference ETABS design sheet. Width and
height are read from rectangular design sections. The I and J ends correspond
to the ETABS object orientation, not necessarily physical left and right.

In automatic mode the reinforcement settings are enabled. Bar diameters are
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
In manual mode enter the reinforcement description and numeric provided area
separately. Ratios are suppressed for missing stations, design errors, ETABS
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
the existing Excel output; CSV always exports station data only and ignores
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

Mapping uses the in-memory ETABS object endpoints already collected for the
summary, after applying the selected beam scope and story filter. End offsets
are not applied to mapping geometry. It estimates one global translation:

```text
RevitXYZ = ETABSXYZ + (dX, dY, dZ)
```

No rotation or scaling is applied. As in the Grasshopper prototype, compatible
beam directions vote for midpoint offsets in 10 mm bins; the top 24 bins are
shortlisted using deterministic ties and their mean offsets are scored.
Both 3D endpoints must be within **10 mm inclusive**, allowing reversed ends.
For prototype parity, each translation is scored by greedily assigning endpoint
pairs in ascending squared maximum endpoint error, using each line once.
Candidates rank by pair count, then summed squared endpoint error. Equal votes,
pair errors and candidate scores keep the prototype's stable input/traversal
order. The shortlist is a heuristic, not exhaustive registration. Duplicate
endpoint counterparts/orientations and spatially distinct equal-count translation
alternatives are flagged for review. If no qualifying endpoint pair establishes
a vector, the export explicitly reports **Translation not established - No match**,
leaves the components blank and does not run later checks.

### Nine ordered checks

The matcher follows the updated Grasshopper script, not generic nearest-neighbor
assignment. It replaces the earlier broad 100 mm / 5 degree proximity pass.
Coordinates are translated once. Reversed ends are allowed throughout.
Once a line is assigned to a group, later checks cannot reuse or reassign it.
Split and chain groups may contain several links using the same model reference.

| Pass | Check | Limits and coverage |
|---|---|---|
| 1 | Endpoint | Both 3D endpoints within 10 mm. |
| 2 | Strict axis / split | At most 0.25 degrees and 10 mm 3D axis offset; at least 90% of each ETABS segment within the Revit span, 95% combined Revit coverage; 50 mm span, gap and projected overlap checks. |
| 3 | Offset review | At most 0.25 degrees, 400 mm lateral, 100 mm vertical; at least 95% overlap of the longer span. |
| 4 | Elevation-offset review | Same standard angle/lateral/coverage; vertical offset greater than 100 mm and at most 650 mm. |
| 5 | Split found (offset) | Multiple connected ETABS segments to one Revit beam; standard angle/lateral and 100 mm vertical; 90% per-segment containment, 95% Revit coverage, 50 mm outer overhang and internal projected/3D joins. |
| 6 | End-extension review | Standard angle/lateral, 650 mm vertical; at least 98% shorter-span and 85% longer-span overlap, below the standard 95% cutoff; at most 600 mm difference at either projected end. |
| 7 | Wide-offset review | At most 0.5 degrees, 500 mm lateral, 650 mm vertical, at least 98% longer-span overlap; only used when the standard angle or lateral limit is exceeded. |
| 8 | Split found (extended) | Pass 5 with up to 350 mm outer overhang; unchanged 50 mm internal joins and 95% Revit coverage. Only groups exceeding the earlier 50 mm outer allowance are added here. |
| 9 | Chain-to-chain review | Unambiguous connected chains with at least two segments per side; 50 mm internal joins, 0.5 degrees, 500 mm lateral, 650 mm vertical, 98% longer-span overlap, and 600 mm projected outer-end differences. Competing chain pairings are rejected. Only segment pairs with projected overlap **greater than 1 mm** emit links. |

Measurements follow the prototype: project the ETABS endpoints onto the Revit
axis, then measure residual horizontal magnitude and absolute vertical offset.
Coverage uses actual line lengths, not a component-wise coordinate test.
The chain check measures all constituent endpoints against the opposite span.
Staggered internal split locations do not have to align.

The 1:1 review checks (3, 4, 6, 7) require mutual-nearest candidates ranked by
maximum 3D endpoint-to-axis residual, with a **25 mm runner-up margin in both
directions** (exactly 25 mm is accepted). Lower-offset or standard candidates
remain in fallback ranking where the prototype specifies, so a later check
cannot bypass a better unresolved candidate merely by relabelling it.
Competing strict-axis targets, split groups sharing a source segment, ambiguous
chain neighbors and competing chain pairings are audited rather than forced.
Passes 3-9 are visibly **review candidates**, not strict geometric matches.

With mapping enabled, **Beam summary** appends status, linked Revit references,
group ID, review flag, Revit Marks, pass number and available endpoint error.
Design, reinforcement and formula columns are neither moved nor changed.
**Revit mapping** contains:

- Translation, direction, all tolerance/coverage settings and warnings.
- Per-pass distinct Revit/ETABS additions, group/link counts, running totals,
  percentage-point coverage gains and running coverage percentages.
- One **Segment link** row per assigned ETABS-Revit segment pair, with a common
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

Optional offline parity verification against the full Grasshopper trial inputs:

```powershell
dotnet run --project .\tests\BeamScheduleTests.csproj -c Release -- --mapping-workbooks "C:\path\ETABS.xlsx" "C:\path\Revit.xlsx" "C:\path\verification-output"
```

This test-only path reads the existing trial exports and writes a separate mapped
workbook, CSV link audit and JSON report; the plugin itself always maps in-memory
ETABS coordinates. The full-input parity assertions expect distinct Revit/ETABS
additions of `160/160, 43/52, 666/666, 188/188, 27/76, 230/230, 6/6, 14/59,
26/27`, totalling 1,360/1,966 Revit beams and 1,464/2,953 ETABS lines including
review candidates. Pass 9 adds 7 groups and 46 segment links. These are test
references only, never matcher inputs or hard-coded assignments. The project
workbooks are not committed.
