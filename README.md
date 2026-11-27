# FMR-fit
FMR data processing suite: turns ferromagnetic resonance field-sweep measurements into clean field-signal curves
and fits their resonance lines.

## Analysis routine

The usual workflow is: import every measurement of a sample → check the list → fit the spectra one by one → save the
project (and export the spectra for 3D plots if needed).

**Number entries** behave the same in the import and fitting windows:

- A typed value is applied (and the graph updated) on **Enter** or when you leave the field; nothing changes while you
  type. Invalid text is replaced by the current value, **Esc** restores it, and a comma is accepted as the decimal point.
- The **mouse wheel** over the field you are editing steps the value and applies it at once. Fields with a fixed
  number of decimals (sweep correction, phase: 0.1; interpolation from/to: 10 Oe, step: 1 Oe) step by that amount;
  the others step by one unit of the value's second significant digit (3.2E-5 → 3.3E-5), keeping that step for the
  whole run of wheel turns, also through zero. **Shift** makes the step 10× larger, **Ctrl** 10× smaller. Such a
  field that has never held anything but 0 has no scale yet: type a first value.

### 1. Add measurement files

In the main window press **Import data…**. In the import window:

1. **Open…** (Ctrl+O) a measurement CSV (the lock-in logger file with its `Freq, …, GHz` and `SR850:` header lines).
   Frequency, power and lock-in settings are read from the header.
2. Set **Recorded** in the measurement info to what the lock-in measured: **dP/dH** (field modulation, the
   default) or **~P** (a signal proportional to the absorbed power).
3. Check the corrections on the right: **sweep correction** (lag between the up and down sweeps), **lock-in phase**,
   which sweeps to use, backgrounds, the field grid and optional power normalization. **Auto** buttons estimate the
   values; the plot shows the processed X signal (red). Opening a file estimates the sweep correction and the phase;
   the field background starts at 0 and is estimated only by its Auto button. If the best sweep match lies at an end
   of the search window (`lag_search_min_s`, `lag_search_max_s`), the automatic alignment has failed: the correction
   is set to 0 and a message asks you to judge it yourself. The square **−90** and **+90** buttons beside the phase
   entry rotate the phase by 90°, wrapped to (−180°, 180°].
4. Optionally smooth the processed signal. Smoothing works on the interpolated points, so its span is counted in grid
   points:
   - **FFT** removes variations with a period shorter than the span (default 3 points);
   - **Savitzky-Golay** fits a polynomial (default quadratic) over a window of an odd number of points (default 5).
5. Choose with the selector at the top right what to show: the **Signal**, its **Integral** or its **Derivative**
   over field (calculated after smoothing; integral and derivative are plotted on the right axis). This choice is
   what the main window plots, what the fitting window fits and what the 3D export writes for this file. For example,
   to fit and export P from a dP/dH measurement, choose Integral.

   **Offset to 0** (under the form selector) adds an offset to the shown form, e.g. to put the baseline of an integrated line
   at 0. The square **A** button sets the offset so that the minimum of the shown form is 0; the entry next to it
   adjusts it by hand (V, V·Oe or V/Oe). The checkbox decides whether the offset is used: when it is off, the entry
   and A are disabled and the value is kept but not applied. It is on (with the A value) by default for an integral
   and for ~P measurements, off otherwise, and goes back to that default when you change Recorded or the form shown.
   The offset does not follow later changes of the other settings by itself: press A again after them.
   The work file stores the data without the offset, together with the offset and whether it is applied; the main
   window, fitting and the 3D export add it when it is applied.
6. **Next** saves the processed signal as a *work file* and asks for the next measurement, so a whole frequency series
   can be imported in one go. **Finish** saves and closes; **Cancel** (or closing the window) closes without saving
   the current file, after asking if an opened file has not been saved yet. Esc does not close the window.

Each work file appears in the main window's list (sorted by frequency) and on its plot as soon as it is saved.
**Modify…** reopens the selected work file's measurement in the import window with its saved settings; Finish then
replaces the work file, and closing without Finish asks first if any setting (or the source file) was changed.
**Remove** deletes the selected work file (the original measurement is never touched).

### 2. The `Current` folder

All work files of the sample you are working on live in one folder, `Projects/Current`:

- When running from a source checkout it is `<repository>/Projects/Current` (the `Projects` folder is git-ignored);
  otherwise it is next to the program (`<exe folder>/Projects/Current`). It is created on first start.
- Work files are named `[sample]_[frequency]GHz.csv`; a second import of the same sample and frequency gets
  `(01)`, `(02)`, ….
- They are plain CSV: `#` comment lines (source file, measurement metadata, what was recorded, the original header
  and every processing setting, including smoothing and the chosen display), then the header row
  `H_Oe,V_mV,Int_mV_Oe,dVdH_mV_per_Oe` and the data: the processed signal (mV), its integral over field (running sum
  from the first point, mV·Oe) and its derivative (mV/Oe). The data never include the display offset; it is
  recorded as `display_offset` (in the shown column's unit) with `display_offset_applied = yes/no`. Work files made with earlier versions of FMR-fit have only
  the first two columns; they still open, and the integral and derivative are calculated when needed.
- The main window always shows what is in the folder. If you add, remove or edit files there by hand, press
  **Refresh** (F5).
- `Current` is the *working* project. To start on another sample, save the project (below) and then remove the work
  files from the list (or empty the folder).

### 3. Fit the spectra one by one

When all measurements are imported, select the first spectrum in the list and press **Fit…**. The fitting window
shows the data with each peak and their sum on top, and the residual (data − fit) below.

**Fit range.** Under the Save/Next/Discard buttons, two entries set the field range that is fitted (blue: lower end,
red: upper end). Only points inside it are used by Add peak, Fit and Fit selected and counted in the Result
statistics; the graph zooms to the range (plus 5 % on each side) and greys out everything outside it. Use it to fit
the FMR line without features elsewhere in the spectrum pulling the fit — e.g. an FMAR dip at low field with its
raised baseline. The entries are kept inside the data and min below max; an empty entry means the end of the data.
The range is saved with the fit.

1. Choose a function under **Add Function** and press **Add peak**. A line shape is placed on the largest feature
   the current peaks don't explain; a **Linear background** `c + s·H` is placed as the straight line through what
   they leave. Add as many as needed — e.g. one line plus a background for a resonance sitting on an offset or tilted
   baseline. Which line shape suits depends on what the spectrum shows:
   - dP/dH: **Lorentzian derivative** or **Asymmetric Gaussian derivative**;
   - P (~P as recorded, or the integral of dP/dH): **Lorentzian** or **Asymmetric Gaussian**.

   A spectrum without a saved fit starts with the matching Lorentzian selected. Both Lorentzians have absorption (A1)
   and dispersion (A2) parts, and the Lorentzian is the integral of the Lorentzian derivative with the same xc, w, A1,
   A2: fitting dP/dH or its integral gives the same parameters. The units of the amplitudes follow the data shown
   (e.g. A1 in mV·Oe² for the Lorentzian derivative of a dP/dH signal, mV·Oe for the Lorentzian of a ~P signal).
2. Adjust the selected peak by hand if needed: entries and sliders for every parameter, the slim slider between the
   plots for the resonance field, limits (min/max; blue and red shading show the forbidden range; widths never go
   below 0.1 Oe, whatever their limit), a checkbox per
   parameter to **fix** it during fitting, **Fix all / Unfix all**, and for the Lorentzians a *keep height* box that
   scales A1, A2 with the width (w² for the derivative, w for the Lorentzian) when you change it, so the line keeps
   its height. The entries work as described under *Number entries* above.
3. **Fit** fits all peaks together; **Fit selected** fits only the selected peak with the others locked. Both respect
   limits and fixed parameters. A manual change re-evaluates only the peak that changed; the square refresh button
   next to Fit selected evaluates all peaks again from scratch, recomputes the Result and redraws, without fitting.
4. Judge the fit from the residual plot and the **Result** panel:
   - χ² = Σ(data − fit)², the residual sum of squares (mV² for a signal; the square of the data unit otherwise);
   - reduced χ² = χ²/(N − p), N data points, p free parameters; σ = √(reduced χ²) is the typical deviation of data
     from fit;
   - R² = 1 − χ²/Σ(data − mean)², the fraction of the signal explained (1 = perfect).

   The data carry no per-point uncertainties, so, as in Origin, every point has weight 1: compare χ² between fits of
   the same spectrum, and σ with the noise level of the measurement.
5. Then use the buttons at the top:
   - **Save** writes the fit and closes the window;
   - **Next** writes the fit and opens the next spectrum of the main list (after the last one it continues with the
     first), so the whole series can be fitted in one pass;
   - **Discard** (or closing the window) leaves without saving; if there are unsaved changes it asks first.

A saved fit is loaded again whenever that spectrum is opened for fitting, so you can continue later. **Reload** brings
back the saved fit and discards your changes (after a confirmation if there are any).

**Fit files** are saved next to their work file as `[work file name].fit.csv`, e.g. `MM100610_cprllH_30GHz.fit.csv`.
They are plain CSV (`#` lines are comments) and open in Excel or any CSV reader:

```
# FMR-fit peak fit, format 2
# [fit]
# work_file = MM100610_cprllH_30GHz.csv
# data = signal                   (fitted column: signal, integral or derivative)
# chi_squared_mV2 = 0.0035588624235362
# reduced_chi_squared_mV2 = 2.936355134930858E-06
# r_squared = 0.9236429003829888
# standard_deviation_mV = 0.0017135796260841975
# points = 1216
# free_parameters = 4
# fit_range_Oe = 4500; 8000        (empty = whole spectrum)
# iterations = 26                  (empty if the peaks were changed after the last fit)
# exit_reason = RelativePoints
# saved = 2026-11-24T14:49:18+01:00
# [functions]                      (formula of each line shape used)
# LorentzianDerivative = f(H) = -A1*4*w*u/(4*u^2 + w^2)^2 - A2*(w^2 - 4*u^2)/(4*u^2 + w^2)^2, with u = H - xc
# [peak options]
# peak 1 = LorentzianDerivative; keep_height = yes
peak,parameter,value,error,unit,min,max,fixed
1,xc,772.5866870852908,4.010092663719275,Oe,120,12270,no
1,w,806.6874295118005,8.069403518999392,Oe,0,5022.947341949744,no
1,A1,20563.7624240652,438.90730179796014,mV*Oe^2,-97341.54624128505,97341.54624128505,no
1,A2,-12603.814041294367,405.90385616957695,mV*Oe^2,-97341.54624128505,97341.54624128505,no
```

The statistics (`points`, χ², R², …) refer to the fit range. `error` is the standard error from the fit (empty for
fixed or not yet fitted parameters); `min`/`max` are the limits.
`data` is the column that was fitted (the one the work file displays). For an integral or derivative the `unit`
column shows the matching units (e.g. mV*Oe^3 for A1 of a Lorentzian derivative fitted to an integral), and the
statistics are in that column's unit, although their names end in `mV2` and `mV`. If you change a work file's display
after fitting it, the fitting window tells you when you open it: fit again.
For both Lorentzians, A1 > 0 is the usual sign of an FMR line and A1 < 0 of an FMAR line.

Fit files are not listed as work files. Removing a work file also removes its fit file.

### 4. Save the project

**Save…** in the main window archives everything in `Projects/Current` — work files **and their fit files** — into a
single `.zip` (by default `Projects/[sample name].zip`). Keep that archive with the original measurements.

Opening a saved project from the program is not implemented yet: to continue with an archived project, unpack its
files into `Projects/Current` and press **Refresh**.

### 5. Export for 3D plots

**Export…** (top right of the main window) opens the *Export settings* window with the readable work files of the
list, all selected:

- **Click** a file to select or deselect it. **Press and drag** (left or right button) over several files: if the
  first one becomes selected, every file you pass is selected; if it becomes deselected, they are deselected.
- **Select all** (or **Num \***) selects every file, or deselects them all if all are already selected.

Format *CSV for 3D plots* writes one file (by default `[sample name]_3D.csv` in `Projects`; not allowed in
`Projects/Current`) with a short header and three columns — frequency, field and what each work file displays — one
row per point, spectra in order of frequency, ready for a contour or 3D plot in Origin, gnuplot, Python, ….
**Round values to** sets the decimal places of the three columns (0–15, default 6, remembered); trailing zeros are
dropped and no value is written in exponent form.

Three optional operations change the exported values of each spectrum (never the work files); all start unticked
every time the window opens:

- **Align** subtracts the straight line through the mean of the first and the mean of the last N points (N =
  `alignment_points` in `fmr-fit.ini`, default 10), so both ends average 0. This removes an offset and a tilt.
- **Shift** subtracts the mean of the last N points (same N), so the high-field end averages 0. This removes only an
  offset. Align already does this, so ticking one unticks the other.
- **Normalise** then divides the spectrum by its largest |value|, so its values range up to ±1 and have unit 1; the
  value column is renamed accordingly (e.g. `dPdH_1`, `P_1`). Relative intensities between frequencies are lost.

The file notes what was done, with the subtracted line (a shift is a line without slope) and the divisor of every
spectrum, so the original values can be recovered: original = value × divisor + baseline at 0 Oe + slope × H. A
spectrum with fewer than 2N points cannot be aligned, one with fewer than N cannot be shifted, and one that is zero
everywhere cannot be normalised; Export then says which one and writes nothing.

What a work file displays, together with what was recorded, is a physical quantity: ∫P dH, P, dP/dH or d²P/dH².
All selected files must give the same one — e.g. the integral of a dP/dH measurement and the signal of a ~P
measurement are both P. If they don't, Export is disabled and the window lists the quantities; change the display
of the odd files with **Modify…**. The value column is named after the quantity, with its unit if all files share one
(`dPdH_mV`, `P_mV_Oe`), without it if they don't (`P` when ~P in mV is combined with integrated dP/dH in mV·Oe):

```
# FMR-fit 3D export, format 1
# sample = MM100610_cprllH
# frequency_from_to_step_GHz = 30; 39; 1     (most regular frequency step)
# field_step_Oe = 10                         (interpolation step of the work files)
# spectra = 10
# quantity = dP/dH
# data = dP/dH signal (10)                  (recorded signal, displayed column, number of files)
# align = off                                (or what was subtracted)
# shift = off                                (or what was subtracted)
# normalise = off                            (or what was divided by)
f_GHz,H_Oe,dPdH_mV
30,230,-5.0123
```

## Settings (`fmr-fit.ini`)

The program keeps its settings in `fmr-fit.ini`, in the same root folder as `Projects` (the repository root when run
from a checkout, otherwise the program folder). The file is created at start-up if it is missing, and rewritten
complete if it can't be read or a value is missing or invalid: each bad value goes back to its default, and the valid
ones are kept. Settings changed while the program runs are written once, when it exits, and only if something changed.
Every key has a comment above it with its range and default. Edit the file while the program is closed: changes made
while it runs are overwritten on exit if a setting changed.

| Section | Key | Default | Meaning |
|---|---|---|---|
| `state` | `import_folder` | — | Folder the measurement file dialog opens in (remembered) |
| `state` | `export_folder` | — | Folder the export file dialog opens in (remembered; empty: `Projects`) |
| `state` | `selected_work_file` | — | Work file selected in the main window at start-up (remembered; a file that is gone just selects nothing) |
| `ui` | `window_width`, `window_height` | 1920, 1000 | Size of the main, import and fitting windows (remembered from the last one closed) |
| `ui` | `window_maximized` | false | Windows open maximized (remembered) |
| `ui` | `max_redraws_per_second` | 60 | Plot redraws per second while a slider is dragged or an entry is stepped with the mouse wheel |
| `import` | `grid_step_oe` | 10 | Interpolation grid step of a new import (Oe) |
| `import` | `sweep_selection` | up_and_down | Sweeps of a new import: `up_and_down`, `up` or `down` |
| `import` | `normalize_power` | true | Normalize a new import to 10 dBm |
| `import` | `lag_search_min_s`, `lag_search_max_s` | 0, 5 | Search window of the automatic sweep correction (s); if the best match lies at an end of it, the import warns that the automatic alignment failed and sets the correction to 0 (a negative lag is not physical) |
| `fitting` | `max_iterations` | 2000 | Most optimizer iterations per Fit |
| `fitting` | `default_limit_factor` | 10 | Limits of a new peak's widths and amplitudes, × the largest value of that kind |
| `export` | `decimals` | 6 | Decimal places of the values in an export for 3D plots (remembered) |
| `export` | `alignment_points` | 10 | Points averaged at each end of a spectrum when the 3D export aligns or shifts it (1–10000) |

## Structure

| Project | Path | Purpose |
|---|---|---|
| `FmrFit.Core` | `src/FmrFit.Core` | Class library: file IO, signal processing, line shapes and fitting (MathNet.Numerics) |
| `FmrFit.Core.Tests` | `tests/FmrFit.Core.Tests` | xUnit tests for Core |
| `FmrFit.App` | `src/FmrFit.App` | Avalonia desktop app, plotting with ScottPlot.Avalonia |

Shared build settings (target framework `net10.0`, nullable, implicit usings) are in `Directory.Build.props`.

## Build, test, run

Requires the .NET 10 SDK.

```
dotnet build FmrFit.slnx
dotnet test FmrFit.slnx
dotnet run --project src/FmrFit.App
```

To make a release, publish one self-contained `FmrFit.App.exe` (about 50 MB; no .NET install needed on the target PC)
with the `win-x64` profile, into a folder **outside** the repository:

```
dotnet publish src/FmrFit.App -p:PublishProfile=win-x64 -p:Version=0.1.6 -o C:\Temp\FMR-fit
```

## AI-assisted development

This project was developed with assistance from Anthropic Claude (Claude Code) for code generation, refactoring,
documentation, and debugging. The software is provided "as is", without warranty of any kind (see Articles 7 and 8
of the EUPL). You use it at your own risk and are solely responsible for checking its results before you rely on
them, for example in publications.

## Licence

This project is licensed under **EUPL-1.2-or-later**. See `LICENSE` for the project licensing statement and `EUPL`
for the full official EUPL 1.2 text. To cite FMR-fit, use the details in `CITATION.cff`.

When distributing or communicating the work or a derivative, retain the required copyright and licence notices,
identify modifications, and provide the source as required by the EUPL.
