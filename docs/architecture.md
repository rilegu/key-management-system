# Architecture

## What the system does

Physical keys and assets are held in electronic cabinets. A holder authenticates, the system
decides whether they may take a particular asset, the cabinet releases it, and the transaction
is recorded. Returns work the same way in reverse. Every decision, device transition and
failure is written to an append-only audit trail.

The system of record runs on the customer's own hardware. There is no cloud dependency and no
third-party service in the custody path.

## Components

```
┌──────────────────────┐   HTTPS REST + SignalR    ┌───────────────────────────┐
│  KeyManagement       │ ────────────────────────▶ │  KeyManagement.Server     │
│  .Desktop            │                           │                           │
│  Avalonia, MVVM      │ ◀──── live events ─────── │  ┌─────────────────────┐  │        ┌──────────┐
└──────────────────────┘                           │  │ REST + SignalR      │  │ EF Core│  SQLite  │
                                                   │  ├─────────────────────┤  │───────▶│          │
                                                   │  │ Device gateway      │  │        └──────────┘
                                                   │  │ (BackgroundService) │  │
                                                   │  └──────────┬──────────┘  │
                                                   └─────────────┼─────────────┘
                                                        TCP :5610│ (cabinet dials in)
                                                   ┌─────────────┴─────────────┐
                                                   │  KeyManagement            │
                                                   │  .DeviceSimulator         │
                                                   └───────────────────────────┘
```

| Project | Role |
| ------- | ---- |
| `KeyManagement.Domain` | Entities and the custody state machine. References nothing. |
| `KeyManagement.Application` | Use cases and the port interfaces they depend on. |
| `KeyManagement.Infrastructure` | EF Core, SQLite, repositories, password hashing. |
| `KeyManagement.Contracts` | DTOs on the wire between server and client. References nothing. |
| `KeyManagement.Devices.Protocol` | Frames, codec and message types for the device link. |
| `KeyManagement.Server` | REST API, SignalR hub, device gateway. |
| `KeyManagement.Desktop` | Avalonia MVVM client. |
| `KeyManagement.DeviceSimulator` | Standalone cabinet simulator. |

## Trust boundaries

Three, and each is enforced on the server side of it:

**Client to server.** The desktop client is software on a workstation a user controls. It
holds a bearer token and nothing else — no database credentials, no device protocol. Every
authorization decision is made on the server; the client hiding a button is presentation.

**Cabinet to server.** A cabinet is a device on a building network. It reports state and
executes commands; it is never the authority on whether a checkout was permitted. The server
decides, records, and then instructs. A device that reports a slot change the server did not
authorize raises an alarm rather than updating custody silently.

**Server to database.** The database is the system of record. Custody state and its audit
record change in one transaction, so the trail cannot disagree with the state it describes.

Four SQLite settings are load-bearing, and three of them are easy to lose:

- **WAL journal mode**, so readers proceed while the gateway or the API writes.
- **`PRAGMA foreign_keys=ON` on every connection.** SQLite ignores foreign keys unless asked,
  per connection — the default is silent, so this is cheap to drop and expensive to notice.
- **`busy_timeout` with retry.** SQLite takes one writer at a time; a concurrent write should
  wait rather than fail.
- **UTC timestamps**, converted only at the UI boundary.

## Data flow: a checkout

```
CheckoutView → CheckoutViewModel → IKeyManagementClient
  → POST /api/checkouts
    → CheckoutApplicationService
      → domain rules decide permitted / denied
      → one transaction: custody state + audit record
      → device gateway sends UnlockSlot (correlation id)
        → cabinet reports CommandResult and SlotStateChanged (sequence number)
        → server reconciles, records, pushes over SignalR
```

The command is authorized and recorded before the cabinet is asked to do anything, and the
cabinet's report is reconciled against what was authorized. A denial never reaches the device.

## Reliability

The device link is the unreliable part, and three mechanics carry it:

- **Per-cabinet monotonic sequence numbers** on device events. The server records the last
  applied sequence and discards anything at or below it, so duplicates and out-of-order
  delivery are harmless rather than corrupting.
- **Correlation IDs** on every server command, echoed in the result, so a retried unlock is
  the same unlock rather than a second one.
- **Resume-from-sequence** in the handshake. A reconnecting cabinet says what it last sent,
  the server says what it last received, and the cabinet replays the gap from its buffer.

A cabinet that misses three heartbeats is `Offline`. A slot whose state cannot be established
is `Unknown` — never optimistically `Available`. Uncertain device state is recorded as
uncertain, because the alternative is an audit trail that reads as confident and is wrong.

## Deployment

| Model | Shape |
| ----- | ----- |
| Development | Server, simulator and client as three console processes on one machine. |
| Single site | Server as a Windows Service, SQLite on the same host, clients on workstations. |
| Larger site | Device gateway extracted into its own worker so cabinets stay connected across API restarts. |

## Why this shape

**Eight projects rather than one.** A single project is faster to start and impossible to keep
honest — nothing stops a view model from opening a database connection, and once that happens
the authorization rules stop being enforceable in one place. The cost is mapping code between
layers. `DomainIsolationTests` asserts the domain assembly references no ORM, web host or UI
framework, so the boundary fails the build rather than review.

**SQLite rather than PostgreSQL or SQL Server.** On-premises means someone installs, patches
and backs up whatever this needs, at every site. A file has none of that burden. The limit is
one writer at a time, which suits a single site and would not suit several — that is the point
at which to revisit, not before.

**A simulator in its own process rather than an in-process fake.** A fake can only fail in the
ways it was written to fail in. The failure modes this system most needs to survive — a cabinet
dropping mid-command, replaying buffered events on reconnect — are exactly the ones an
in-process fake cannot produce honestly. The cost is slower tests that need port management.

**No database access from the client.** Direct access would put authorization in the process
the user controls and require database credentials on every workstation. It also rules out
offline operation, which is correct here: a custody decision made without the system of record
is a custody decision that cannot be audited.

## Related documents

- [`protocol.md`](protocol.md) — device link frame format and message lifecycle
- [`api.md`](api.md) — HTTP surface
- [`threat-model.md`](threat-model.md) — assets, attacker model, known limitations
