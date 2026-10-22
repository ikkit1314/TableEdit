# Native mouse and keyboard integration checks

`InteractionChecks` opens the real application and sends native X11 mouse and keyboard input with XTest. It checks cell clicks, Shift ranges, drag rectangles, keyboard ranges, clearing cells, deleting/undoing rows, header sorting, current-column duplicates and filtered selection. Fixtures are created in a temporary directory; the runner does not edit a user document.

Requirements: .NET 8, Linux with an X11 display, `libX11` and `libXtst`, and fonts usable by Avalonia. Run on an otherwise unused virtual display because these checks move its pointer and change focus.

```sh
# Use an existing virtual display, or start the cloud image's configured dummy Xorg:
Xorg :99 -config /etc/X11/xorg.conf -logfile /tmp/tableedit-test-xorg.log -nolisten tcp -noreset &
export DISPLAY=:99
# In the prepared cloud environment, also source /workspace/.setup/activate.sh.
dotnet run --project tests/InteractionChecks/InteractionChecks.csproj
```

A successful run prints each passed check and exits zero. Failures return nonzero. These checks validate the native Linux input path; they do not replace checking macOS keyboard conventions on a Mac.

The runner reports a native clipboard check as **SKIP** if even a direct platform write/read fails on the virtual display. In that case, it still validates the copied TSV and exercises the application's paste/undo logic directly; this is not an end-to-end OS clipboard pass.
