# HTTP API

**Status: the authentication, custody and audit endpoints below are built. Administration,
alarms and CSV export are not** — see the capability table in the README.

The server is the authoritative boundary. Everything the desktop client can do, it does
through this surface, and every rule is enforced here rather than in the client.

## Conventions

- JSON, UTF-8, `application/json`.
- Timestamps are UTC, ISO 8601, with an explicit `Z`. Local time is a display concern.
- Bearer tokens: `Authorization: Bearer <jwt>`.
- Commands accept an `Idempotency-Key` header, and repeating a key returns the original
  result rather than performing the action twice.

Every command returns the same envelope, so a client never has to infer what happened from a
status code alone:

```json
{
  "success": false,
  "message": "This asset is not in a group you may check out.",
  "correlationId": "0f9c1b3e-...",
  "state": "Denied"
}
```

`message` is written for the person at the workstation. `correlationId` links the command to
its audit records and to any device command it caused.

## Authentication

| Method | Path | Purpose |
| ------ | ---- | ------- |
| `POST` | `/api/auth/login` | Exchange credentials for an access and refresh token |
| `POST` | `/api/auth/refresh` | Exchange a refresh token for a new access token |

Failed authentication is recorded with the account and source, and returns the same response
and timing as an unknown account — enumerating valid holders should not be free.

## Custody

| Method | Path | Purpose |
| ------ | ---- | ------- |
| `GET` | `/api/dashboard` | Cabinet health, active checkouts, recent events |
| `GET` | `/api/assets` | Assets and their custody state |
| `GET` | `/api/cabinets` | Cabinets, online status, firmware |
| `GET` | `/api/cabinets/{id}/snapshot` | Slot-by-slot state of one cabinet |
| `POST` | `/api/checkouts` | Request custody of an asset |
| `POST` | `/api/checkouts/{id}/return` | Return an asset |
| `GET` | `/api/audit-events` | Search the audit trail |

## Administration

| Method | Path | Purpose |
| ------ | ---- | ------- |
Not built yet.

| `GET` `POST` | `/api/users` | List and create holders |
| `PATCH` | `/api/users/{id}` | Amend a holder |
| `GET` `POST` | `/api/assets` | List and create assets |
| `GET` | `/api/alarms?status=active` | Active alarms |
| `POST` | `/api/alarms/{id}/acknowledge` | Acknowledge an alarm |
| `GET` | `/api/audit-events/export` | CSV export |

## Live events

`/hubs/events` (SignalR). A connection is authorized like any request and receives only what
its holder may see.

| Event | Payload | Status |
| ----- | ------- | ------ |
| `Activity` | An audit record: type, time, correlation id, summary, subjects | **works** |
| `CabinetStatusChanged` | Cabinet id, online state, timestamp | with the device layer |
| `AlarmRaised` | Alarm id, type, severity, source | with alarms |

The hub sends and never receives. A client cannot ask it to do anything, so there is no second
command surface to secure alongside the API. A push that fails is logged and dropped rather than
failing the command that produced it: the database is the system of record, and a client that
missed one recovers on its next reload.

## Authorization

Role-based, evaluated server-side on every request:

| Permission | Grants |
| ---------- | ------ |
| `CheckoutAsset` | Request and return assets in a permitted group |
| `ManageUsers` | Create and amend holders, roles and group membership |
| `AcknowledgeAlarm` | Acknowledge an active alarm |
| `ViewAudit` | Search and export the audit trail |

Checkout is additionally scoped by asset group: holding `CheckoutAsset` permits assets in the
holder's groups, not every asset. A denial is a recorded event, not a silent 403.

## Errors

Problem details (RFC 9457) for transport and validation failures. A refused custody
operation is not an error — it returns `200` with `success: false` and a `state` of `Denied`,
because a denial is a legitimate outcome the system is expected to produce and record.
