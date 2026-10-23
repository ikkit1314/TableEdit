# Native mouse and keyboard integration checks

`InteractionChecks` opens the real application and sends native X11 mouse and keyboard input with XTest. It checks cell clicks, Shift ranges, drag rectangles, keyboard ranges, clearing cells, deleting/undoing rows, header sorting, current-column duplicates and filtered selection. Fixtures are created in a temporary directory; the runner does not edit a user document.

The scoped-operation checks use the actual Find & Replace dialog and cell context menu. They cover captured selection boundaries across repeated searches, sorting and filtering; single and batch replacement with undo/redo; stale or empty scopes; selected-cell transformations; and refreshing filtered results after edits.

The 1.3.1 Release build passed all 66 checks on the prepared Linux X11 display. The native clipboard round trip was reported as skipped for the limitation described below.

The range-typing and duplicate-highlighting implementation passed 75 checks. New checks verify typing over an entire rectangular selection or column, one-step undo/redo and cancellation, actual duplicate-cell background colors, selected duplicate appearance, and clearing duplicate highlights. They also catch delayed search refreshes erasing a newly clicked selection.

Requirements: .NET 8, Linux with an X11 display, `libX11` and `libXtst`, and fonts usable by Avalonia. Run on an otherwise unused virtual display because these checks move its pointer and change focus.

```sh
# Use an existing virtual display, or start the cloud image's configured dummy Xorg:
Xorg :99 -config /etc/X11/xorg.conf -logfile /tmp/tableedit-test-xorg.log -nolisten tcp -noreset &
export DISPLAY=:99
# In the prepared cloud environment, also source /workspace/.setup/activate.sh.
dotnet run --project tests/InteractionChecks/InteractionChecks.csproj
# To run just the scoped-operation checks:
dotnet run --project tests/InteractionChecks/InteractionChecks.csproj -- --scope-only
```

A successful run prints each passed check and exits zero. Failures return nonzero. These checks validate the native Linux input path; they do not replace checking macOS keyboard conventions on a Mac.

The runner reports a native clipboard check as **SKIP** if even a direct platform write/read fails on the virtual display. In that case, it still validates the copied TSV and exercises the application's paste/undo logic directly; this is not an end-to-end OS clipboard pass.
