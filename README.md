.kl# key-management-system

An on-premises key and asset custody system. Physical keys are held in electronic cabinets,
issued to authorized holders, and every issue and return is recorded in an append-only audit
trail.

On-premises means the system of record runs on the customer's own hardware. There is no cloud
dependency and no third-party service in the custody path.

> **Early development.** Custody works end to end across the desktop client, the API and the
> database: sign in, see the position board, request an item, be refused if you may not have it,
> return it, and watch the activity arrive live. Cabinet communication does not exist yet, so
> the step where a cabinet confirms an item was physically taken has no hardware behind it. The
> capability table below is exact — it says what works, not what is planned.

## The problem

An organisation with a hundred keys and forty people who might need them has three bad
options: a pegboard and a paper sheet, a locked drawer and one person who holds it, or a
spreadsheet nobody updates. All three share a failure — when something goes wrong, nobody can
say with confidence who had the key and when.

Electronic cabinets fix the physical half. This is the other half: deciding who may take
what, recording it so the record can be trusted afterwards, and staying correct when a
cabinet drops off the network mid-transaction.

Typical use: a facilities team issuing plant-room keys, a fleet operator handing out vehicle
keys per shift, a data centre controlling cage and rack access, a property manager issuing
unit keys to contractors.

## How it works

```
┌──────────────────────┐   HTTPS REST + SignalR    ┌───────────────────────────┐
│  Desktop client      │ ────────────────────────▶ │  Server                   │
│  Avalonia, MVVM      │ ◀──── live events ─────── │  REST + SignalR           │        ┌──────────┐
└──────────────────────┘                           │  Device gateway           │───────▶│  SQLite  │
                                                   └─────────────┬─────────────┘        └──────────┘
                                                        TCP :5610│ (cabinet dials in)
                                                   ┌─────────────┴─────────────┐
                                                   │  Cabinet simulator        │
                                                   └───────────────────────────┘
```

A holder asks for an asset. The server decides whether they may have it, records the decision,
and only then tells the cabinet to unlock. The cabinet reports back what actually happened,
and the server reconciles that against what it authorized.

Three properties are worth calling out, because they are what makes the difference between
this and a form over a database:

**The device is never the authority.** A cabinet reports state and executes commands. If a
slot changes with no matching authorized command, that raises an alarm — it does not quietly
update custody.

**Uncertain state is recorded as uncertain.** A cabinet that misses three heartbeats is
`Offline` and its slots become `Unknown`, not `Available`. An audit trail that reads as
confident and is wrong is worse than one that admits a gap.

**Disconnection is expected, not exceptional.** Cabinets buffer events while offline and
replay them from the last acknowledged sequence on reconnect. Per-cabinet sequence numbers
make a duplicated event harmless; correlation ids make a retried command the same command.

## What works today

| Capability | Status |
| ---------- | ------ |
| Solution structure, layered projects, CI | **works** |
| Architecture, protocol, API and threat-model documentation | **works** |
| Custody domain model and state machine | **works** |
| SQLite persistence, migrations and seed data | **works** |
| Password and PIN hashing | **works** |
| Authentication, refresh tokens, role-based authorization | **works** |
| Checkout and return over the HTTP API | **works** |
| Audit search and correlation | **works** |
| Desktop client: position board, items, activity | **works** |
| Live event stream to connected clients | **works** |
| Cabinet protocol and device gateway | not implemented |
| Cabinet simulator | not implemented |
| Overdue detection and alarms | not implemented |
| Audit CSV export | not implemented |
| TLS and mutual authentication on the device link | not implemented |
| Windows Service hosting | not implemented |

## Limitations

Current and by design, stated up front rather than discovered later. The full analysis is in
[`docs/threat-model.md`](docs/threat-model.md).

- **The device link will be plaintext when it first ships**, with a shared secret per cabinet.
  TLS with mutual certificates replaces both in the transport-security sprint. Until then a
  network attacker on the site LAN can impersonate a cabinet.
- **The audit trail is append-only by application policy, not tamper-evident.** Anyone with
  the database file can rewrite it.
- **The database is not encrypted at rest.** Disk encryption is the answer today.
- **Single site, single writer.** SQLite suits one site well and would not suit several.
- **No offline operation.** A custody decision made without the system of record cannot be
  audited, so the client does not make one.
- **No multi-factor authentication.**

The cabinet protocol is original to this project. It is not compatible with any commercial
cabinet and does not attempt to be; the simulator is the reference device.

## Building

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes
```

To run it, the server needs a signing key and an initial administrator password. Neither has a
built-in default, because a default would be the same secret on every deployment.

```bash
cd src/KeyManagement.Server
dotnet user-secrets set "Jwt:SigningKey" "<at least 32 bytes>"
dotnet user-secrets set "Seed:AdministratorPassword" "<initial password>"
dotnet run

# in another shell
dotnet run --project src/KeyManagement.Desktop -- --server https://localhost:7183
```

The database is created and seeded on first start: four roles, two item groups, five items and
a ten-position cabinet.

## Layout

| Path | Contents |
| ---- | -------- |
| `src/KeyManagement.Domain` | Entities and the custody state machine. References nothing. |
| `src/KeyManagement.Application` | Use cases and the ports they depend on. |
| `src/KeyManagement.Infrastructure` | EF Core, SQLite, repositories, password hashing. |
| `src/KeyManagement.Contracts` | DTOs shared by server and client. |
| `src/KeyManagement.Devices.Protocol` | Device link frames and codec. |
| `src/KeyManagement.Server` | REST API, SignalR hub, device gateway. |
| `src/KeyManagement.Desktop` | Avalonia MVVM client. |
| `src/KeyManagement.DeviceSimulator` | Standalone cabinet simulator. |
| `tests/` | One test project per source project. |
| `docs/` | Architecture, protocol, API surface, threat model. |

## Documentation

- [`docs/architecture.md`](docs/architecture.md) — components, trust boundaries, data flow
- [`docs/protocol.md`](docs/protocol.md) — device link framing and lifecycle
- [`docs/api.md`](docs/api.md) — HTTP surface and authorization
- [`docs/threat-model.md`](docs/threat-model.md) — assets, attackers, known limitations

## License

Apache-2.0. See [`LICENSE`](LICENSE).
