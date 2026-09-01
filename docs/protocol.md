# Cabinet protocol

**Status: specification. Not implemented yet** — see the capability table in the README.

The link between the server and a cabinet. Original to this project; it is not compatible with
any commercial cabinet and does not attempt to be.

## Transport

Long-lived TCP. **The cabinet dials the server**, not the reverse: cabinets sit behind the
site firewall and the server is the reachable party. This puts reconnect and backoff in the
cabinet and offline detection in the server, and means no inbound rule is needed per cabinet.

Default port 5610. The codec is written against `Stream`, so TLS is a wrapper rather than a
change to message handling.

## Framing

TCP is a byte stream with no message boundaries, so frames are explicit:

```
┌────────────┬────────┬──────────────────────┐
│ length (4) │ type(1)│ payload (length-1)   │
│ big-endian │        │ UTF-8 JSON           │
└────────────┴────────┴──────────────────────┘
```

`length` covers the type byte and the payload. A partial read is a normal condition, not a
corruption — the decoder buffers until a full frame is available. Frames above a fixed maximum
are rejected and the connection closed, so a bad length cannot make the server allocate
arbitrarily.

An explicit length rather than a newline delimiter, because delimiter framing means scanning
for the delimiter and a payload that contains one becomes a framing bug. JSON rather than a
packed binary encoding, because a frame that is readable in a packet capture is worth more
here than the bytes it costs — the payloads are small and the link is a building LAN. A binary
encoding stays available behind the same codec interface if that ever stops being true.

## Messages

| Type | Direction | Purpose |
| ---- | --------- | ------- |
| `Hello` | cabinet → server | Identify, authenticate, declare protocol version and last sequence sent |
| `HelloAck` | server → cabinet | Session id, and the last sequence the server actually applied |
| `Heartbeat` | cabinet → server | Liveness, every 5 s |
| `Ping` | server → cabinet | Liveness probe from the other direction |
| `SlotStateChanged` | cabinet → server | A key was inserted, removed, or a slot faulted |
| `CommandResult` | cabinet → server | Outcome of a command, echoing its correlation id |
| `EventBatch` | cabinet → server | Buffered events replayed after a reconnect |
| `UnlockSlot` | server → cabinet | Release the asset in a slot, carrying a correlation id |
| `RequestSnapshot` | server → cabinet | Report the state of every slot |

## Lifecycle

```
cabinet                                    server
  │  connect ─────────────────────────────▶│
  │  Hello(cabinet, credential, lastSeq) ─▶│  authenticate, load session
  │◀────────── HelloAck(sessionId, ackSeq) │
  │  EventBatch(events after ackSeq) ─────▶│  apply in order, ignore ≤ lastApplied
  │  Heartbeat ───────────────────────────▶│  every 5 s
  │◀─────────── UnlockSlot(correlationId)  │
  │  CommandResult(correlationId, outcome)▶│
  │  SlotStateChanged(seq, slot, state) ──▶│  reconcile against what was authorized
```

## Sequence numbers and correlation ids

**Sequence numbers** are per cabinet, monotonic, and assigned by the cabinet. The server
records the highest applied and discards anything at or below it. This is what makes duplicate
and out-of-order delivery harmless: the same event applied twice is applied once.

**Correlation ids** are per command, assigned by the server, and echoed in `CommandResult`. A
command retried after a timeout carries the same id, so the cabinet can recognise it and the
server can match a late result to the request that caused it.

**Resume-from-sequence** is the reconciliation path. The cabinet buffers events it has not had
acknowledged. On reconnect, `Hello` carries the last sequence it sent and `HelloAck` carries
the last the server applied; the cabinet replays the difference. Everything that happened while
the link was down arrives in order, once.

## Failure handling

| Condition | Behaviour |
| --------- | --------- |
| Three missed heartbeats | Cabinet marked `Offline`, its slots `Unknown` |
| Reconnect | Backoff with jitter, then `Hello` with the last sequence sent |
| Command timeout | Retried with the same correlation id, bounded attempts |
| Unknown message type | Ignored and logged, so a newer cabinet does not break an older server |
| Sequence gap on replay | `RequestSnapshot`, because a gap means events were lost, not delayed |
| Frame over the maximum | Connection closed |

A slot the server cannot account for is `Unknown` until a snapshot resolves it. It is never
assumed `Available`.

## Authentication

Cabinets present a per-cabinet credential in `Hello`. **The link is plaintext and the
credential is a shared secret until the transport-security sprint**, at which point TLS with
mutual certificates replaces both. This is a real weakness and is recorded as one in
[`threat-model.md`](threat-model.md) rather than described as sufficient.
