# BAS2 connection verification

Run from the repository root:

```powershell
dotnet run --project tools/ConnectionRegression -c Release
./android/gradlew.bat -p android testReleaseUnitTest --console=plain
```

The desktop suite uses ephemeral loopback TCP/UDP ports. It checks bounded framing, malformed messages, real bidirectional media-port probing, duplicate requests, stale streams, source contention and TCP media. Android tests cover route ranking, simultaneous upstream/hotspot candidates, bounded competition, cancellation, late winners, recovery intent and TCP backpressure. These tests do not open audio devices.

## Isolated physical phone probe

Build the probe using its init script; it replaces the manifest and applicationId only for this build, leaving product sources and release output unchanged:

```powershell
./android/gradlew.bat -p android assembleRelease -I ../tools/ConnectionRegression/android-probe/probe.init.gradle --console=plain
adb install -r artifacts/bas2/android-probe-build/outputs/apk/release/app-release.apk
```

The project needs its existing local release signing configuration. No signing material belongs in Git. Probe APKs go exclusively under `artifacts/bas2` and are not user releases.

Start the host, then invoke the probe on a physical USB-connected phone. Substitute the computer's address on the phone's LAN; do not use adb port forwarding, which bypasses the network being tested:

```powershell
dotnet run --project tools/ConnectionRegression -c Release -- --device-host --seconds 45 --result artifacts/bas2/device-udp-host.json
# In a second terminal while the host is running:
adb shell am start -n com.blueaudiosink.bas2probe/com.blueaudiosink.sender.net.bas2.Bas2ProbeActivity --es host 192.168.233.68 --ei seconds 10
adb pull /sdcard/Android/data/com.blueaudiosink.bas2probe/files/result.txt artifacts/bas2/device-udp-phone.txt
```

Repeat with host flag `--block-udp` and distinct result filenames. This suppresses probe replies in the test server and should cause automatic TCP fallback after the UDP validation budget. It does not modify firewall rules. Test ports are TCP45778 and TCP/UDP45779; firewall policy still applies to the host process. A timeout is not proof of a firewall or VPN restriction.

For a 10-second healthy run, expect 2,000 generated frames, 2,000 received frames, zero invalid payloads, and the correct physical interface in the phone result. Each frame contains deterministic 960-byte test PCM. Packet loss, reconnects or repeated epochs require inspecting the detailed results rather than accepting the total alone. The host never plays this PCM. This verifies connectivity and byte integrity, not capture authorization or listening quality.

After collecting local results, remove the isolated probe:

```powershell
adb uninstall com.blueaudiosink.bas2probe
```

The installed product package `com.blueaudiosink.sender` is separate. Store addresses, command output and reports under ignored `artifacts`; technical documentation describes repeatable checks rather than historical results.

## Music through the playback pipeline

Prepare a fixed-length 48 kHz, 16-bit, stereo raw PCM clip with a whole number of 960-byte frames. Use exactly the same file for both transports. Copy it to the probe app as `reference.pcm`:

```powershell
adb push artifacts/bas2/reference.pcm /sdcard/Android/data/com.blueaudiosink.bas2probe/files/reference.pcm
dotnet run --project tools/AudioRegression -c Release -- --bas2-music-host --reference artifacts/bas2/reference.pcm --seconds 45 --result artifacts/bas2/music-udp.json
# In a second terminal; substitute the current PC address:
adb shell am start -n com.blueaudiosink.bas2probe/com.blueaudiosink.sender.net.bas2.Bas2ProbeActivity --es host 192.168.0.165 --ei seconds 10 --ez music true
```

Repeat the host with `--block-udp` and a distinct result filename. The playback harness fixes software volume at 100%, shared output, initial buffering at 80 ms, and the same 10 ms consumer clock. It uses the production reorder/buffer/gain/output-provider chain with an isolated output device, never the system speaker. Reference and collected output SHA-256, exact byte equality, first mismatch, ingress/accepted packet counts, missing/late frames, queue drops and underruns are reported. The captured output PCM is stored next to the JSON.

The probe waits for STOP acknowledgement and finite output drain before closing control. Closing immediately means disconnect, which intentionally discards pending playback; it is a separate test. Preserve failures, including packet loss and stale-address attempts, instead of replacing them with successful runs. An exact result proves the pre-WASAPI PCM path for that clip and network; it is not an AudioPlaybackCapture or actual sound-card quality test.
