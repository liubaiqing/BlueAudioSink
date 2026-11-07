# Project tools

The product sources live in `src/SinkLite` (Windows) and `android/app` (Android). These tools support the current BAS2 application and its shared audio engine.

| Tool | Purpose |
| --- | --- |
| `build-bas2.ps1` | Build and test both release packages; read the version from the Windows project |
| `ConnectionRegression` | BAS2 framing, real sockets, session isolation, path recovery and isolated physical-phone probe |
| `AudioRegression` | Shared playback, WASAPI, buffering, volume, settings and transport integration regressions |
| `UiResponsiveness` | Native Windows hover and background-operation responsiveness |
| `FirewallRegression` | Firewall rule generation and state interpretation |
| `SinkProbe` / `diagnose-flow.ps1` | Windows Bluetooth/system-audio diagnostics |
| `loopback-inject.ps1` | Explicit diagnostic audio injection |
| `build_app_icon.py` | Generate Windows icon assets from the shared brand |
| `build_readme_guides.py` | Generate redacted bilingual guides from newly captured local screenshots |

Build and diagnostic outputs belong under ignored `artifacts`; keep only the latest distributable packages after verification. Screenshot generation requires fresh inputs in `artifacts/readme-source`; published guide images remain in `docs/images`. Release signing material and user settings must never be included in Git or deleted as build caches.
