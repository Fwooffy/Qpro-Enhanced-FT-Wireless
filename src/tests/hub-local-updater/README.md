# Local automatic updater test

This test publishes two dedicated test executables (2.9.99 and 3.0.0) into a new disposable folder. It uses the production update dialog, HTTP download/staging code, copied helper, file replacement, and restart. An injected HTTP handler supplies local release metadata and ZIP bytes; production download trust rules are unchanged.

The replacement executable verifies its version and personal-data sentinels, writes `verification.json`, and exits. Neither executable starts the normal Hub. Runtime scripts, ADB, bridge DLLs, and models in the fixture are inert placeholders. The test does not install dependencies, publish releases, operate a headset, or use the normal Hub's settings directory.

Run from the repository with an already available .NET SDK and cached Windows runtime packages:

```powershell
# Prepare only; default behavior does not launch an executable.
.\src\test_local_updater.ps1

# Build a fresh fixture and open its dialog for manual interaction.
.\src\test_local_updater.ps1 -Open

# Build a fresh fixture and run the automated dialog/helper/restart test.
.\src\test_local_updater.ps1 -Run
```

The script finds the workspace's private SDK or `dotnet` on PATH; `-Dotnet` accepts another SDK executable. `-OutputRoot` changes the parent output directory. Every invocation creates a unique `local-updater-<guid>` folder; previous evidence is retained. Restore uses an empty local package feed and the existing NuGet cache, so missing packages cause a failure rather than a download. Set `NUGET_PACKAGES` if the existing cache is elsewhere.

For manual mode, click **Update**, wait for local verification, then click **Restart and update**. The initial executable exits, the real copied helper applies the package, and the replacement writes its verification receipt. All windows belong to the disposable fixture.

To reopen a prepared fixture without rebuilding:

```powershell
$fixture = 'FULL PATH PRINTED AS LOCAL_UPDATER_FIXTURE'
$marker = Get-Content -LiteralPath (Join-Path $fixture 'local-updater.json') -Raw | ConvertFrom-Json
$previousRoot = $env:QPRO_LOCAL_UPDATER_ROOT
$previousNonce = $env:QPRO_LOCAL_UPDATER_NONCE
try {
    $env:QPRO_LOCAL_UPDATER_ROOT = $fixture
    $env:QPRO_LOCAL_UPDATER_NONCE = $marker.nonce
    & (Join-Path $fixture 'app\QproFaceTracking.exe')
} finally {
    $env:QPRO_LOCAL_UPDATER_ROOT = $previousRoot
    $env:QPRO_LOCAL_UPDATER_NONCE = $previousNonce
}
```

A successful automated run requires `verification.json` with `passed: true` and `version: 3.0.0`, a successful original-process exit, and exit of fixture helper/replacement processes. `update-complete.txt` alone is insufficient proof of successful replacement startup. The default timeout is 180 seconds, adjustable with `-TimeoutSeconds`; timeout leaves evidence and any test processes intact for inspection.

This covers the local dialog-to-helper-to-replacement sequence and sample-data preservation. It does not validate GitHub's real network responses, normal Hub initialization, headset/ADB shutdown, power-loss durability, or recovery-helper startup through the normal application-data directory. Separate updater fixture suites cover malformed packages and interrupted transactions.
