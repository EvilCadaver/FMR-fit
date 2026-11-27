# FMR-fit

Desktop tool for processing ferromagnetic resonance (FMR) field-sweep measurements. .NET 10, C#.

Keep this file trim: only rules, conventions and pitfalls needed to change the code correctly. Feature and usage descriptions go in README.md, and what the code does goes in its doc comments.

## Layout

| Project | Role |
|---|---|
| `src/FmrFit.Core` | All non-UI code: file IO and math. References MathNet.Numerics only. |
| `src/FmrFit.App` | Avalonia desktop app with ScottPlot.Avalonia. UI only. `MainWindow` is the entry window; it opens `DataImport` (file loading, corrections, processed X), `DataFitting` (peak fitting) and `DataExport` (export of selected work files) as modal dialogs. |
| `tests/FmrFit.Core.Tests` | xUnit tests for Core. Sample measurements in `tests/data/*.csv`. |

Shared build settings (target framework, nullable, implicit usings) are in `Directory.Build.props`; don't repeat them in project files.

User settings live in `<root>/fmr-fit.ini` (`AppSettingsFile` in Core IO; the App's `SettingsStore` loads, applies and saves them). A new setting = a property in `AppSettings` plus one entry in the key table of `AppSettingsFile` (section, comment with range and default, parse, format). Core math receives settings as parameters (default = the old constant) and never reads the file.

## Drive writes

Always try to minimize repeated writes to the drive: keep changing state in memory and write it once, at a natural end point (Finish, Save, exit), never on every selection, resize or keystroke. Example: `SettingsStore` writes `fmr-fit.ini` at start-up only if it is missing or incomplete, otherwise only on exit and only if something changed.

## Keep UI, math and IO separate

- **IO** (`FmrFit.Core.IO`): reads and writes files and returns plain data records (`FmrMeasurement`, `WorkFile`, `FitFile`). No physics or fitting here. Only clean-up of logger artefacts belongs here (skipping the first 5 data rows, dropping rows whose Time repeats the previous one).
- **Math** (`FmrFit.Core.Processing`, `FmrFit.Core.Fitting` and other non-IO Core namespaces): pure functions on arrays and records. No file paths, streams or console output, no UI types, no static mutable state.
- **UI** (`FmrFit.App`): controls, plotting, file dialogs and user input only. Code-behind calls Core and displays the result; it must not parse files or contain calculations beyond trivial formatting. If logic is needed, add it to Core with tests and call it.
- Dependencies point one way: App → Core. Core must never reference Avalonia, ScottPlot or anything UI-related. Within Core, Processing and Fitting may use IO record types but must not read files (e.g. `FitFileConversion` maps `Peak` ↔ `FitFile`).
- New features follow the same split, e.g. a fit = model and fitter in Core (tested), panel and plot in App.

## UI controls

- Every numeric input is a `NumberBox` (App), in every window: applies on Enter/leaving the field, wheel steps via `WheelStep` (Core). Don't use `NumericUpDown` or a bare `TextBox` for numbers. Set `Value` from code (raises nothing); react to `ValueCommitted`.
- Redraws triggered by input go through `RedrawThrottle.Request()`; each window's redraw calls `Redrawing()` first.

## Fitting rules

- A new line shape implements `IPeakShape` and is added to `PeakShapes.All`. Its `Id` is stored in fit files: never rename it.
- `ParameterKind` decides units and the fitter's scaling; a new kind of quantity needs a new kind.
- Lorentzian derivative sign convention: A1, A2 > 0 for FMR, < 0 for FMAR. Keep it. `LorentzianAbsorption` is its field integral with the same parameters (same signs); keep the two consistent.
- An amplitude that carries a field unit needs `PeakScaling.WidthPower` and the fitter's scale for its kind.
- Fit statistics weight every point 1 (the data have no per-point errors) and count only free parameters in N − p. Keep that unless asked otherwise.

## Testing

- Every Core feature gets tests in `FmrFit.Core.Tests`, mirroring the namespace folder (`IO/`, `Processing/`, `Fitting/`, `Numbers/`).
- Prefer synthetic data with known answers (e.g. a known delay or line width) and add one check against a real file in `tests/data`.
- The sample CSVs are copied to the test output folder; open them via `Path.Combine(AppContext.BaseDirectory, "data", name)`.

## Commands

```
dotnet build FmrFit.slnx
dotnet test FmrFit.slnx
dotnet run --project src/FmrFit.App
dotnet publish src/FmrFit.App -p:PublishProfile=win-x64 -p:Version=0.1.6 -o <folder outside the repo>
```

Release settings (single compressed exe) live in the publish profile `src/FmrFit.App/Properties/PublishProfiles/win-x64.pubxml`, not in the `.csproj`, so normal builds stay RID-neutral. The `.csproj` drops all `.pdb` files from any publish.

## Measurement files

CSV with preamble lines (sample name; `Freq, <GHz>, GHz, Power, <dBm>, dBm, <free comment>`; `SR850: <lock-in settings>`), then the header `Time,Field,X,Y,R,theta`, a units row `s,Oe,V,V,V,deg`, then data. Only Time, Field, X and Y are used; R and theta are ignored. Each file sweeps the field up and then back down. **Work files** (written by DataImport → Finish, listed and plotted by MainWindow) live in `<root>/Projects/Current`, where root is the repository root when running from a checkout (found via `FmrFit.slnx`), otherwise the exe folder; `/Projects/` is git-ignored. Format: `WorkFileWriter` (`#`-commented `key = value` header in sections, then `H_Oe,V_mV,Int_mV_Oe,dVdH_mV_per_Oe` columns; older files have only `H_Oe,V_mV`, and the reader accepts both). The `display` entry picks the column that MainWindow plots, DataFitting fits (recorded as `data` in the fit file; units via `ParameterUnits.OfData`) and the 3D export writes; use `SignalCalculus.Column`/`DisplayedView`, not `SignalMv` directly. Stored columns never include `display_offset`; it is added only when `display_offset_applied`, via `SignalCalculus.DisplayedColumn` (main window, fitting, export). Never write shifted data into a work file. `recorded_signal` (dP/dH or ~P, [measurement] section) plus the display gives the physical quantity (`MeasuredQuantity.Order`); the export combines only files of equal order. Names `[sample]_[freq]GHz.csv`, then `(01)`, `(02)`…

Take the frequency from the header, not the file name (some names have a ` (1)` suffix). The free comment is everything between the first `dBm,` and `SR850:` and may contain line breaks; never parse metadata out of it.

## Fit files

`[work file name].fit.csv` next to the work file. `ProjectFolder.ListWorkFiles` must skip them. Change `FitFileWriter` and `FitFileReader` together.

## Commits

End every commit message (and PR description) with one attribution line naming the model that did the work, instead of a `Co-Authored-By` trailer with an e-mail address:

```
Vibe-coded with Anthropic Claude <model> <version>
```

e.g. `Vibe-coded with Anthropic Claude Opus 5.5`.
