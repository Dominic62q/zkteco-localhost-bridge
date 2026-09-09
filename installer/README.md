# Fingerprint Bridge — installer & web-developer guide

(Bridge 1.2.0 — windowless background process, auto-starts after install,
universal open access. File: `installer/output/FingerprintBridgeSetup-1.2.0.exe`)
**Built from:** `installer/bridge-setup.iss` (Inno Setup 6 — recompile with
`ISCC.exe installer\bridge-setup.iss` from the project root)

### What it does on a scanner PC
1. Checks the ZKTeco driver is present (`libzkfp.dll`). Warns with guidance
   if missing — it does not bundle the vendor driver.
2. Installs one self-contained `bridge.exe` (no .NET needed on the target)
   to `C:\Program Files\FingerprintBridge\bin\`, plus Start-menu entries and
   an optional start-with-Windows task.
3. Creates the data folder and writes `bin\appsettings.json` with
   open universal access: any page origin (`"AllowedOrigins": ["*"]`),
   no tokens.
4. Uninstall removes the app but **keeps the data folder** — biometric data
   is never deleted silently. Remove it manually for full erasure.

### Files after install
- `bin\bridge.exe` — the bridge (serves `http://127.0.0.1:5050`)
- `bin\appsettings.json` — origins, tokens, data path (edit, then restart bridge)
- `data\fingerprint.db` — local store (only used by the legacy test endpoints)

---

## Web-developer guide: using the bridge from your app

### 1. No pairing needed
Default is **open universal mode**: any page origin is accepted, no tokens,
your app just calls the API. No domain registration, ever.

Tokens remain available the day you want them: add one entry per app to
`Bridge:ApiTokens` in `bin\appsettings.json`, hand each app its token
out-of-band, and restart the bridge. Only then does every `/api/*` call
(except `/api/health`) need its `X-Bridge-Token` (401 otherwise). With the
default empty list, no token is needed for anything.

### 2. Minimal client (copy into your frontend)
```js
const BRIDGE = "http://127.0.0.1:5050";
const TOKEN = ""; // pairing token — leave empty in open mode

async function bridge(path, body) {
  const headers = { "Content-Type": "application/json" };
  if (TOKEN) headers["X-Bridge-Token"] = TOKEN;
  const res = await fetch(BRIDGE + path, { method: "POST", headers, body: body ? JSON.stringify(body) : undefined });
  const data = await res.json();
  if (!res.ok) throw new Error(data.errorCode ?? ("HTTP_" + res.status));
  return data;
}

export const capture = (timeoutSeconds = 30) =>
  bridge("/api/v1/capture", { timeoutSeconds });          // → { templateBase64 }
export const merge = (templates) =>
  bridge("/api/v1/merge", { templates });                 // 3 in → { templateBase64 }
export const verify = (templateBase64, timeoutSeconds = 30) =>
  bridge("/api/v1/verify", { templateBase64, timeoutSeconds }); // → { matched, score }
export const identify = (candidates, timeoutSeconds = 30) =>
  bridge("/api/v1/identify", { candidates, timeoutSeconds });   // → { matched, matchId, score }
```

**Touch-and-go (no name picking):** pull all staff templates from your
backend as `[{id, templateBase64}]`, then `identify` while they press —
returns whoever matched (up to 500 candidates per call). This is the
kiosk flow: press finger, system knows who.

### 3. Flows
**Enrol once per person:** `capture` ×3 (same finger, fresh presses — the
sensor ignores an already-held finger) → `merge` → POST the merged
template to **your** backend, stored against the person. The bridge
retains nothing.

**Verify (e.g. clock-in):** fetch the person's stored template from
**your** backend → `verify` while they press → `{ matched, score }` →
record the business event (attendance row, door open) in **your** system.
The bridge's answer is advisory; your backend is the authority.

**Status:** `GET /api/v1/device/status` (or the always-open
`GET /api/health`) — poll every few seconds; `connected:false` means
unplugged, `busy:true` means another app is mid-capture.

### 4. Error codes you must handle
| Code | Meaning | UI text suggestion |
|---|---|---|
| `BRIDGE_BUSY` (409) | another app is capturing | "Reader busy — try again in a moment." |
| `CAPTURE_TIMEOUT` (408) | no finger in time | "No finger detected — press firmly and retry." |
| `DEVICE_NOT_FOUND` (503) | reader unplugged/missing | "Reader not detected — check the USB cable." |
| `UNAUTHORIZED` (401) | missing/wrong token | "App not paired — contact IT." |
| `BAD_TEMPLATE` (400) | corrupt stored template | "Fingerprint record invalid — re-enrol this person." |

### 5. Rules that keep everyone safe
- One capture at a time across **all** apps — expect occasional `BRIDGE_BUSY`.
- Templates are opaque strings to you: never log them, never put them in URLs.
- Matching happens **only** inside the bridge (only the vendor DLL can
  compare). Never ship templates to a remote server for matching.
- Tokens are for shared PCs only: skip them for a single app you control
  (open mode). When sharing, one entry per app; revoke by deleting its
  entry and restarting. A compromised token cannot reach other apps'
  data — because the bridge holds no app data at all.
- `score` is diagnostic support, not a decision threshold: `matched` is
  the verdict.

### 6. Troubleshooting for web devs
- `Loading…` forever / connection reset: bridge process down or mid-restart —
  check `GET /api/health` in the browser; restart `bridge.exe` if needed.
- Reader unplugged mid-day: status flips to disconnected within ~10s;
  capture calls fail with `DEVICE_NOT_FOUND` until replugged.
