# ManifestApp — Web API Routes

> **Base URL:** `https://opensteam.lol`  
> All requests/responses use `Content-Type: application/json`.

---

## Client-Facing Routes
*Called directly by the ManifestApp desktop client.*

---

### POST `/api/{key}/activate`

Registers a machine on startup, returns user identity and current usage quota in a single round-trip.

**Path param**

| Param | Description |
|-------|-------------|
| `key` | URL-encoded OpenSteam API key |

**Request body**

```json
{
  "machineId": "a3f8c1d2e4b5...",
  "os": "Windows 11 Pro (Build 22631.3737)",
  "version": "3.2.1"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `machineId` | `string` | Stable per-machine GUID generated on first launch and persisted in client settings |
| `os` | `string` | OS product name + build number |
| `version` | `string` | ManifestApp semver (`major.minor.patch`) |

**Response `200`**

```json
{
  "success": true,
  "data": {
    "activationId": "act_abc123",
    "isNewUser": false,
    "user": {
      "username": "pokem",
      "plan": "PRO",
      "role": "USER",
      "isStaff": false
    }
  },
  "usage": {
    "today": 12,
    "limit": 100,
    "remaining": 88,
    "resetAt": 1715385600
  }
}
```

> `resetAt` may be a Unix timestamp (integer) **or** an ISO-8601 string — the client handles both.

**Error responses**

| Status | Meaning |
|--------|---------|
| `401` | Invalid API key |
| `429` | Daily quota exceeded |
| `5xx` | Server error |

---

### POST `/api/report`

Receives heartbeats and user-action events from the desktop client every 30 seconds and on key actions (startup, install, remove, search). The server responds with any queued admin commands for that API key.

**Request body**

```json
{
  "sessionId": "e8a1f3c9d2b4...",
  "apiKey": "gg_live_xxx",
  "appVersion": "3.2.1",
  "user": {
    "discordId": "123456789012345678",
    "username": "pokem",
    "role": "USER",
    "isStaff": false
  },
  "plan": "PRO",
  "usage": {
    "today": 12,
    "limit": 100,
    "remaining": 88,
    "resetAt": "2024-05-11T00:00:00+00:00"
  },
  "event": {
    "type": "heartbeat",
    "appId": null,
    "gameName": null,
    "success": null,
    "detail": null
  },
  "timestamp": "2024-05-10T14:32:00.000Z"
}
```

**`event.type` values**

| Value | When sent |
|-------|-----------|
| `startup` | App launch |
| `heartbeat` | Every 30 seconds while running |
| `install` | After a manifest install (`appId`, `gameName`, `success` populated) |
| `remove` | After a depot remove (`appId`, `gameName` populated) |
| `search` | After a game search (`detail` = query string) |

**Response `200`**

```json
{
  "disable": false,
  "forceUpdate": false
}
```

| Field | Type | Behavior when `true` |
|-------|------|----------------------|
| `disable` | `bool` | Client shows the lockout overlay and blocks further use |
| `forceUpdate` | `bool` | Client triggers an immediate auto-update |

**Server-side requirements**

- Upsert a session record keyed on `sessionId` in persistent storage (Redis / Postgres).
- Mark the session as online; expire it after **10 minutes** of inactivity.
- Look up any admin commands stored for `apiKey` and return them.
- If the session's `apiKey` is flagged `disable=true`, return that flag every time regardless of command acknowledgement (client re-shows lockout on each heartbeat).

---

## Admin-Facing Routes
*Called by the web admin dashboard. Requires an API key with role `ADMIN` or `OWNER`.*

**Authentication**

Pass the admin's OpenSteam API key in every request:

```
Authorization: Bearer gg_live_adminkey
```

The server should verify the key against the OpenSteam user system and reject non-staff keys with `403`.

---

### GET `/api/admin/sessions`

Returns all tracked client sessions.

**Query params** (all optional)

| Param | Description |
|-------|-------------|
| `online` | `true` / `false` — filter by online status (active within last 10 min) |
| `role` | Filter by role: `USER`, `STAFF`, `ADMIN`, `OWNER` |
| `q` | Search by username or Discord ID (case-insensitive substring) |

**Response `200`**

```json
{
  "total": 42,
  "online": 7,
  "sessions": [
    {
      "sessionId": "e8a1f3c9d2b4...",
      "apiKey": "gg_live_xxx",
      "appVersion": "3.2.1",
      "online": true,
      "lastSeen": "2024-05-10T14:32:00.000Z",
      "user": {
        "discordId": "123456789012345678",
        "username": "pokem",
        "role": "USER",
        "isStaff": false
      },
      "plan": "PRO",
      "usage": {
        "today": 12,
        "limit": 100,
        "remaining": 88,
        "resetAt": "2024-05-11T00:00:00+00:00"
      },
      "lastEvent": {
        "type": "heartbeat",
        "appId": null,
        "gameName": null,
        "success": null,
        "detail": null,
        "timestamp": "2024-05-10T14:32:00.000Z"
      },
      "commands": {
        "disable": false,
        "forceUpdate": false
      }
    }
  ]
}
```

---

### GET `/api/admin/sessions/{sessionId}`

Returns a single session by ID.

**Response `200`** — same shape as one element from the `sessions` array above.

**Response `404`** — session not found.

---

### PUT `/api/admin/keys/{apiKey}/commands`

Sets or clears admin commands for every active (and future) session using the given API key.

**Path param**

| Param | Description |
|-------|-------------|
| `apiKey` | The target user's API key (URL-encoded) |

**Request body**

```json
{
  "disable": true,
  "forceUpdate": false
}
```

Both fields are required. Send `false` to clear a command.

**Response `200`**

```json
{
  "apiKey": "gg_live_xxx",
  "commands": {
    "disable": true,
    "forceUpdate": false
  },
  "affectedSessions": 2
}
```

> Commands are persisted to storage so they survive server restarts and apply to future sessions from the same key.

---

### DELETE `/api/admin/keys/{apiKey}/commands`

Clears all commands for a key (equivalent to `PUT` with `{ "disable": false, "forceUpdate": false }`).

**Response `200`**

```json
{ "ok": true }
```

---

### GET `/api/admin/overview`

Aggregate statistics for the dashboard header.

**Response `200`**

```json
{
  "totalSessions": 42,
  "onlineSessions": 7,
  "disabledKeys": 3,
  "pendingForceUpdate": 1,
  "eventCountsToday": {
    "startup": 38,
    "install": 214,
    "remove": 19,
    "search": 503,
    "heartbeat": 1042
  }
}
```

---

## Client Configuration Notes

The ManifestApp desktop client reads two settings relevant to this API:

| Setting | Key in `AppSettings` | Default |
|---------|----------------------|---------|
| API base URL | `OpenSteamApiBaseUrl` | `https://opensteam.lol` |
| Report endpoint base | `AdminEndpointUrl` | *(empty — reporting disabled until set)* |

Set `AdminEndpointUrl` to `https://opensteam.lol` (no trailing slash) to have reports go to the production web server. The client will POST to `{AdminEndpointUrl}/api/report`.

To enable reporting for all users by default, update the fallback default in [`AppSettings.cs`](ManifestApp.Core/Models/AppSettings.cs) or pre-populate it during the onboarding flow.
