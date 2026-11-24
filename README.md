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
calculations; area ratios retain their precision. If an automatic description
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

This follows CSI's [.NET 8 plugin example](https://web.wiki.csiamerica.com/wiki/spaces/kb/pages/2011456/NET+8+Plugin+Example+-+All+Products).
ETABS uses .NET 8, not the separate legacy .NET Framework product.

## Test

Run the focused summary and workbook tests with:

```powershell
dotnet run --project .\tests\BeamScheduleTests.csproj -c Release
```
