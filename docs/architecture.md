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

Four SQLite settings are load-bearing, and none of them is the default:

- **WAL journal mode**, so readers proceed while the gateway or the API writes.
- **`PRAGMA foreign_keys=ON` on every connection.** SQLite ignores foreign keys unless asked,
  per connection — silently, with no error — so a schema full of correct declarations enforces
  nothing without it. Applied by a connection interceptor, because pooling hands out fresh
  handles.
- **`busy_timeout` with retry.** SQLite takes one writer at a time; a concurrent write should
  wait rather than fail.
- **Timestamps stored as fixed-width UTC ISO-8601 text**, not the provider's own
  `DateTimeOffset` mapping. That mapping keeps each value's original offset, so text order and
  chronological order diverge and EF refuses `ORDER BY` on such a column outright rather than
  answering wrongly. Reading the audit trail newest-first, sweeping for overdue items and
  listing an asset's history all order by time, so without this the indexes on those columns
  would be unusable.

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

## Domain model

Two state machines, deliberately separate. The asset's answers "where is it"; the checkout's
answers "what became of that request". A refused request is a fact about the request — the
asset it was refused stays exactly where it was, which is why `Denied` appears in one and not
the other.

```
Asset      Available ─▶ CheckoutPending ─▶ CheckedOut ─▶ ReturnPending ─▶ Available
                             │                               │
                             └──▶ Available                  └──▶ CheckedOut
           any state ◀──▶ Faulted / Unknown, settled by reconciliation

Checkout   Pending ─▶ Active ─▶ Overdue ─▶ Returned
              │          └──────────────▶ Returned
              └──▶ Abandoned            Denied, Returned, Abandoned are terminal
```

`CustodyTransitions` holds both tables as data, and every entity method asks it before moving.
Nothing outside the entity sets a state, so the machine is a rule rather than a diagram. A move
to the state something is already in is not legal: device reports repeat, and treating a repeat
as a transition would write an audit record for a change that never happened.

| Entity | Holds |
| ------ | ----- |
| `User`, `Role` | Holders, and the permission sets granted through roles |
| `AssetGroupMembership` | Which groups a holder may check out from |
| `Asset`, `AssetGroup` | Items and the grouping authorization is granted over |
| `Cabinet`, `Slot` | Cabinets, their positions, and the last state each reported |
| `Checkout` | One request and everything that became of it |
| `AuditEvent` | The append-only trail |
| `DeviceEvent` | What a cabinet actually said, before interpretation |
| `RefreshToken` | Revocable sessions, stored hashed |

Identifiers are typed rather than bare GUIDs, so passing an `AssetId` where a `SlotId` belongs
fails to compile instead of failing at the database. They are version 7 GUIDs: time-ordered, so
inserts land at the end of an index rather than scattering across it.

## Authorization

Three checks, in order, and only the third is specific to this system:

1. **Authenticated.** A signed access token, fifteen minutes, carrying one claim per permission.
2. **Permitted.** An authorization policy per permission, so an endpoint requiring
   `CheckoutAsset` refuses anyone without the claim before a use case runs.
3. **Entitled to this asset.** Decided by `CheckoutService`, not by the endpoint. Holding
   `CheckoutAsset` permits the holder's asset groups, not every asset in the building, and the
   asset must also be available and its whereabouts confirmed.

The third check is where a refusal becomes a record rather than a status code. It returns `200`
with `success: false`, a reason written for the person at the workstation, and it writes both a
`Denied` checkout and an audit entry. A holder is entitled to ask and entitled to an answer;
what they are not entitled to is the key.

Access tokens are short and cannot be revoked. Refresh tokens are the revocable half: stored
only as a hash, and consumed on use, so a stolen one stops working the moment the rightful
holder refreshes.

## Closing the custody loop

An authorization is not custody. The two are separated on purpose, and the gateway is what joins
them:

```
holder requests  →  server authorizes, records, and only then instructs the cabinet
                 →  cabinet releases the position and reports it emptied
                 →  server confirms custody and completes the checkout
```

Between the second and third steps an item is `CheckoutPending`: released, not yet taken. If the
cabinet never reports, it stays there — visibly unresolved rather than quietly counted as held.

The server refuses a release outright when the cabinet holding the item is not attached.
Authorizing one anyway would leave a checkout waiting on a command that was never sent.

A position that empties with no release behind it is not a checkout. Custody becomes `Unknown`
and an alarm is recorded, because the alternative is a trail that shows someone taking a key
they never asked for.

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

## The client

One main screen, three supporting ones.

**System viewer** is the board: a tile per position, coloured and worded by state, with system
and item detail beside it and a live activity list beneath. Selecting a position shows what it
holds, who has it, when they took it, and when it is due back.

**Items** is a filterable table of everything and where it is. **Activity** searches the trail.
**Sign in** is the way in.

Two rules hold the client together. It talks to `IKeyManagementClient` and nothing else, so
there is no database connection and no device protocol on the workstation. And what it offers
is presentation only: a hidden button is a convenience, never a control, and every request it
makes is judged again on the server.

The interface uses the vocabulary this industry uses — positions, items, in cabinet, out of
cabinet, curfew, fault — while the model keeps its own precise names. One map translates
between them, and it also decides which style class carries each state, so no view ever names
a colour and both themes follow automatically.

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
