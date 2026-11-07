param([string]$OutputDirectory = 'artifacts/BAS2-final')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repo
try {
    $out = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
    New-Item -ItemType Directory -Force $out | Out-Null
    $version = ([xml](Get-Content -LiteralPath 'src/SinkLite/SinkLite.csproj' -Raw)).Project.PropertyGroup.Version | Where-Object { $_ }
    $androidVersion = [regex]::Match((Get-Content -LiteralPath 'android/app/build.gradle.kts' -Raw), 'versionName\s*=\s*"([^"]+)"').Groups[1].Value
    if (!$androidVersion) { throw 'Android versionName missing' }
    dotnet publish src/SinkLite/SinkLite.csproj -c Release -p:Platform=x64 -o (Join-Path $out 'windows') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed' }
    $gradleOut = (Join-Path $out 'android-build').Replace('\','/').Replace("'","\'")
    $init = Join-Path $out 'android.init.gradle'
    "gradle.beforeProject { p -> if (p.path == ':app') p.layout.buildDirectory.set(new File('$gradleOut')) }" | Set-Content -LiteralPath $init -Encoding utf8
    & ./android/gradlew.bat -p android assembleRelease testReleaseUnitTest -I $init --no-parallel --max-workers=2 --console=plain
    if ($LASTEXITCODE -ne 0) { throw 'Android build or tests failed' }
    $apk = Join-Path $out 'android-build/outputs/apk/release/app-release.apk'
    if (!(Test-Path -LiteralPath $apk)) { throw 'Signed release APK missing; configure local signing.properties' }
    Copy-Item -LiteralPath (Join-Path $out 'windows/BlueAudioSink.exe') -Destination (Join-Path $out "BlueAudioSink-$version.exe") -Force
    Copy-Item -LiteralPath $apk -Destination (Join-Path $out "BlueAudioSink-Sender-$androidVersion.apk") -Force
    $files = @("BlueAudioSink-$version.exe","BlueAudioSink-Sender-$androidVersion.apk")
    $hashes = foreach ($file in $files) { '{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $out $file) -Algorithm SHA256).Hash.ToLowerInvariant(),$file }
    $hashes | Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii
    Write-Output "Local BAS2 packages: $out"
} finally { Pop-Location }
