# Fingerprint Bridge API

One bridge, one reader, plain HTTP. Every request and response is JSON.

- **Base URL:** `http://127.0.0.1:5050` (the PC with the reader)
- **Auth:** none. **CORS:** any website allowed.
- **You store everything.** The bridge keeps no people, no fingers, no records.
  Templates come from you in each call and are forgotten after.

## Concepts (30 seconds)

- A **template** is a fingerprint as text (base64). You can't picture it; the
  bridge turns finger presses into these.
- A **raw capture** is a template from one press. A **merge** combines three
  raw captures of the same finger into one better template for storage.
- The bridge never decides anything. It returns `matched` + `score`; your
  app decides what to do.

## Endpoints

### Check the reader — `GET /api/v1/device/status`

```bash
curl http://127.0.0.1:5050/api/v1/device/status
```

```json
{ "connected": true, "model": "SLK20R", "deviceCount": 1, "busy": false }
```

`connected:false` = unplugged. `busy:true` = a scan is running, wait a moment.

### Scan one press — `POST /api/v1/capture`

Waits for a finger (up to `timeoutSeconds`), returns its template.

```bash
curl -X POST http://127.0.0.1:5050/api/v1/capture \
  -H "Content-Type: application/json" \
  --data '{"timeoutSeconds": 30}'
```

```json
{ "templateBase64": "Sq9TUzIxAAAA..." }
```

Save the returned string. Ask the person to lift between presses.

### Combine three presses — `POST /api/v1/merge`

```bash
curl -X POST http://127.0.0.1:5050/api/v1/merge \
  -H "Content-Type: application/json" \
  --data '{"templates": ["<press1>", "<press2>", "<press3>"]}'
```

```json
{ "templateBase64": "SqlTUzIxAAAA..." }
```

Needs **exactly 3**. Store the result against the person in **your** database.

### Sign in with a live finger — `POST /api/v1/identify`

Person presses now; bridge says who (searches your list).

```bash
curl -X POST http://127.0.0.1:5050/api/v1/identify \
  -H "Content-Type: application/json" \
  --data '{"candidates": [{"id": "ama", "templateBase64": "<stored>"}], "timeoutSeconds": 30}'
```

```json
{ "matched": true, "matchId": "ama", "score": 704 }
```

- `candidates`: everyone who could be pressing (id + your stored template each, max 500).
- `matched:false` = finger not recognized (`matchId` is null).
- Higher `score` = stronger match. Hundreds = confident.

### Duplicate check, no press — `POST /api/v1/identify-template`

Same as identify, but you supply a template you already hold instead of a
finger. Use it at enrolment so one finger can't register twice.

```bash
curl -X POST http://127.0.0.1:5050/api/v1/identify-template \
  -H "Content-Type: application/json" \
  --data '{"templateBase64": "<one raw capture>", "candidates": [{"id": "ama", "templateBase64": "<stored>"}]}'
```

```json
{ "matched": true, "matchId": "ama", "score": 704 }
```

**Rules that matter:**

1. Send one **raw capture** as `templateBase64`, never the merge. The matcher
   reads live-format presses; a merged template as the probe matches nothing.
2. Store the **merge**. Check with raw, keep the merge.
3. `matched:true` = already enrolled as `matchId` → don't save the newcomer.

### Compare one-to-one — `POST /api/v1/verify`

Person presses now; bridge checks against the single template you send.

```bash
curl -X POST http://127.0.0.1:5050/api/v1/verify \
  -H "Content-Type: application/json" \
  --data '{"templateBase64": "<stored>", "timeoutSeconds": 30}'
```

```json
{ "matched": true, "score": 512 }
```

## Whole flows

**Enrol once per person**

1. `capture` ×3 (same finger, lift between presses).
2. `merge` the three → store against the person in your database.
3. (Recommended) `identify-template` with capture #1 over all stored
   templates first — `matched:true` means stop, finger already taken.

**Sign in**

1. Pull stored templates as `{id, templateBase64}` pairs from your database.
2. `identify` while they press → record the event in your system.

## Errors

Every failure is JSON: `{ "errorCode": "...", "message": "..." }`.

| Code | HTTP | Meaning | Do this |
|---|---|---|---|
| `VALIDATION_ERROR` | 400 | Missing/bad field (see message) | Fix the request |
| `BAD_TEMPLATE` | 400 | A template isn't valid base64 | Re-capture |
| `BRIDGE_BUSY` | 409 | Another scan is running | Wait, retry |
| `CAPTURE_TIMEOUT` | 408 | No finger in time | Ask them to press firmly, retry |
| `DEVICE_NOT_FOUND` | 503 | Reader unplugged | Check the USB cable |

Notes:

- One scan at a time across all apps — occasional `BRIDGE_BUSY` is normal.
- Opening the reader takes ~2–3s once after idle; repeat scans answer at once.
- Never log templates. Never put them in URLs.
- `GET /api/health` is a second status endpoint (`bridge/sdk/database/device*`
  fields). `database` refers only to the bridge's own legacy store — the
  endpoints above don't use it; ignore that field.
