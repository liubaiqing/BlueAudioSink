# Windows interaction responsiveness

Run on a logged-in Windows desktop:

```powershell
dotnet run --project tools/UiResponsiveness -c Release -- artifacts/hover/light.json
dotnet run --project tools/UiResponsiveness -c Release -- artifacts/hover/dark.json --dark
```

Creates an isolated real Win32/GDI window off screen without activating it, connecting devices, opening audio, registering user hotkeys or writing preferences. Blocks physical-interface and output-device enumeration behind test gates, then dispatches 300 mouse moves across the actual settings hit regions. The result reports dispatch-to-completed-paint latency and checks that highlighters are painted before the input handler returns. Movement uses window messages and does not move the user's pointer. It also holds a connection operation behind a gate, verifies that the initiating call and native message dispatch return promptly, then delivers the completion message and checks that controls leave the busy state.

This is a responsiveness regression under deliberately slow inventories, not a measurement of monitor scanout or end-to-end hardware input latency. Timing thresholds allow machine variation; raw measurements belong under ignored `artifacts`. Original appearance and tooltip delay remain unchanged.

## Output isolation and window placement

`dotnet run --project tools/UiResponsiveness -c Release -- --issue-one` checks output selection against all three real Windows default roles, including selecting a different physical endpoint when available. It uses a temporary settings file for native window move/resize, minimize, maximize, restart and restore checks. This probe briefly shows test windows; it never writes user preferences or changes system audio defaults. Pure geometry regressions cover negative desktops, removed monitors, DPI scaling, small work areas and corrupted settings in AudioRegression.
