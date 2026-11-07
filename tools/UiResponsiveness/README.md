# Windows hover responsiveness

Run on a logged-in Windows desktop:

```powershell
dotnet run --project tools/UiResponsiveness -c Release -- artifacts/hover/light.json
dotnet run --project tools/UiResponsiveness -c Release -- artifacts/hover/dark.json --dark
```

Creates an isolated real Win32/GDI window off screen without activating it, connecting devices, opening audio, registering user hotkeys or writing preferences. Blocks physical-interface and output-device enumeration behind test gates, then dispatches 300 mouse moves across the actual settings hit regions. The result reports dispatch-to-completed-paint latency and checks that highlighters are painted before the input handler returns. Movement uses window messages and does not move the user's pointer.

This is a responsiveness regression under deliberately slow inventories, not a measurement of monitor scanout or end-to-end hardware input latency. Timing thresholds allow machine variation; raw measurements belong under ignored `artifacts`. Original appearance and tooltip delay remain unchanged.
