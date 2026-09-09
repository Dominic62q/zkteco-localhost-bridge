# ZKTeco Live20R (SLK20R) localhost fingerprint test

React `:5173` → C# bridge `127.0.0.1:5050` → ZKTeco SDK → USB reader → local SQLite.
Test app only — not attendance/access-control.

## Prerequisites
- ZKTeco driver installed (device shows as SLK20R, `USB\VID_1B55&PID_0120`, status OK).
- ZKFinger Standard SDK 5.3.0.33 extracted somewhere; set `ZK_SDK_PATH` to it.
- .NET 8 SDK to build (x64 host fine; the bridge itself targets **x86**).
- Node 22+ for the frontend.

## Why x86?
`C#/lib/{x64,x86}/libzkfpcsharp.dll` are both 13KB managed wrappers with PE
machine `0x14c` (x86, `processorArchitecture=x86` in `Demo2.csproj`). The x86
native driver (`SysWOW64\libzkfp.dll`) is installed, so the bridge targets
`win-x86` to match the vendor wrapper. The x64 native driver also exists
(`System32\libzkfp.dll`); if ZKTeco ships a true x64 wrapper, flip
`<PlatformTarget>`/`RuntimeIdentifier` to x64.

## Setup (development)
```powershell
$env:ZK_SDK_PATH = "C:\Users\rigel\Downloads\9774a946c3f659ddf2ae90bc8dadc3eb\ZKFingerSDK_Windows_Standard\ZKFinger Standard SDK 5.3.0.33"
.\scripts\start-dev.ps1
# or two terminals:
# cd bridge; dotnet run
# cd frontend; npm install; npm run dev -- --host 127.0.0.1
```
Health: `http://127.0.0.1:5050/api/health` · UI: `http://127.0.0.1:5173`

## Packaging (single exe)
```powershell
.\scripts\publish-bridge.ps1
```
Produces `dist\bridge\bridge.exe` (~90MB, self-contained, no .NET install
needed on the target PC — only the ZKTeco driver). Verified: the exe alone,
copied to an empty folder, loads the SDK, detects the reader, and serves the
API. The database lives in `data\` next to wherever the exe runs from (or
wherever `DataPath` in appsettings.json points), so a fresh folder starts
empty and the real folder keeps its enrolled users across restarts.

## Installer (any web app)
`installer/bridge-setup.iss` (Inno Setup 6) builds
`installer/output/FingerprintBridgeSetup-1.0.0.exe` (~30MB). It installs
`bridge.exe` to Program Files, creates the data folder, checks the ZKTeco
driver is present (warns, doesn't block), generates a per-install pairing
token shown once on the finish page, and offers start-with-Windows plus
Start-menu entries. Uninstall removes the app but keeps `data\` (biometric
data is never deleted silently — remove it manually).

## Generic v1 API (multi-app)
App-agnostic endpoints with no users and no storage — templates travel as
base64 and the calling app owns them:
- `GET /api/v1/device/status`
- `POST /api/v1/capture` → `{templateBase64}` (app stores it)
- `POST /api/v1/merge` (three captures in → one template out)
- `POST /api/v1/verify` (reference template in → `{matched, score}`)
- `POST /api/v1/identify` (candidate list in → `{matched, matchId, score}`)
Matching still executes inside the bridge (only the vendor DLL can compare).

## Tokens and origins
`Bridge:ApiTokens` (per-app `X-Bridge-Token`) and `Bridge:AllowedOrigins`
live in appsettings.json. Empty token list = open mode (dev). With tokens
set, every `/api/*` except `/api/health` needs a valid token (401 otherwise);
the test UI sends `VITE_BRIDGE_TOKEN` when set. CORS preflights always pass.
## SDK reference
Primary: `C#/Demo2/Form1.cs` (`zkfp2.Init → GetDeviceCount → OpenDevice(0) →
DBInit → GetParameters(1/2) → AcquireFingerprint → DBMerge/DBAdd →
DBMatch/DBIdentify → CloseDevice → Terminate`). The `ActiveX/` sample is NOT used.

## Live device detection
`DevicePoller` refreshes the USB device cache every 10s on a background
thread (Terminate+Init, timed in logs: ~160ms cold, ~5ms warm). HTTP
endpoints serve the cache instantly and never call the SDK, so even a hung
driver call can only stall the poller thread — requests fail over to a 15s
timeout (504) instead of hanging. Status lags plug changes by ~10s plus the
2.5s UI poll. Refresh is skipped while a capture session holds the device.

## API summary
- `GET /api/health`, `GET /api/fingerprint/status`
- `POST /api/fingerprint/enrol/start`, `GET /api/fingerprint/enrol/{id}`, `POST …/cancel`
- `POST /api/fingerprint/verify/start`, `GET /api/fingerprint/verify/{id}`, `POST …/cancel`
- `GET /api/users`, `GET /api/users/{id}`, `DELETE /api/users/{id}`
- Stable error codes: `BRIDGE_BUSY`, `DEVICE_NOT_FOUND`, `DUPLICATE_FINGERPRINT`,
  `SAME_FINGER_REQUIRED`, `CAPTURE_TIMEOUT`, `USER_NOT_FOUND`, `NO_TEMPLATE`, …

## Deleting biometric data
Delete the user in the Enrolled-users table (asks for confirmation; removes
user + template together), or stop the bridge and delete `data/fingerprint.db`.
Templates are DPAPI-encrypted (CurrentUser scope) and never committed to Git;
raw images are never stored. Same Windows user must run the bridge to decrypt
across restarts.

## Troubleshooting
- `sdkRet` nonzero / `SDK_NOT_FOUND`: `ZK_SDK_PATH` wrong or wrapper DLL unloadable.
- `deviceCount=0`: reader unplugged or driver missing; status returns
  `connected:false` by design, bridge keeps running.
- Page stuck on Loading / connection reset: the bridge process occasionally
  stops answering new connections (cause still under investigation — see below).
  Fix: stop `bridge.exe` and start it again; enrolled data survives in `data\`.
- Reset error exactly during a restart: the old process is down and the new one
  isn't listening yet — retry after a few seconds.
- `BadImageFormatException`: bitness mismatch — keep bridge x86 with this SDK.
- `dotnet bridge.dll` with the x64 SDK host fails (`FileLoadException`): the
  assembly is x86-marked. Use `dotnet run` (x86 apphost via `win-x86` RID, needs
  the x86 ASP.NET runtime) or the self-contained exe.
- Stopping a supervised bridge sometimes leaves the record at `stopping` after
  the OS process is already gone; start the replacement under a fresh name.

## Acceptance status (spec section 14)
All 20 proven live, including duplicate rejection, delete removes
user+template, and cold start with reader disconnected.
