# Threat model

**Status: the mitigations below describe the design. Almost none are implemented yet** — the
README capability table says what is actually built. This document is written first so the
controls are designed in rather than added afterwards.

## What is being protected

| Asset | Why it matters |
| ----- | -------------- |
| Physical keys and the items they open | The point of the system. A key issued to the wrong person is the primary failure. |
| The audit trail | The record of who held what, when. Worthless if it can be altered or is incomplete. |
| Holder identities and credentials | Personal data, and the means to impersonate a holder. |
| The custody database | Contains both of the above in one file. |
| Cabinet control | Whoever can command a cabinet can open it. |

## Attacker model

| Attacker | Capability assumed |
| -------- | ------------------ |
| Opportunist holder | A valid account, physical access to a cabinet, no special tools |
| Malicious insider | A valid account and a workstation they fully control |
| Network attacker on the site LAN | Can observe and inject traffic between cabinets and the server |
| Thief with the database file | A copy of the SQLite file and unlimited time |
| Attacker with server host access | Out of scope. The host is the trust anchor. |

## Threats and mitigations

**T1 · A holder takes an asset they are not authorized for.**
Authorization is evaluated server-side per request and scoped by asset group. The cabinet is
only asked to unlock after the decision is made and recorded. The client cannot reach the
database or the device link, so a modified client can request but never grant.

**T2 · A client is modified to bypass the interface.**
The client holds only a bearer token, and has no database connection and no device protocol
reference. Every control is server-side; a hidden button is presentation. A rebuilt client can
request anything it likes and be refused by the same rules.

**T3 · Credentials are stolen or guessed.**
Passwords and PINs are hashed with PBKDF2-HMAC-SHA512 and never stored or logged in the clear.
Failed authentication is recorded and rate-limited, and returns the same response and timing
for an unknown account as for a wrong password.

**T4 · Tokens are replayed.**
Short-lived access tokens, refresh tokens that are revocable and stored hashed. Tokens are
filtered out of logs.

**T5 · Device messages are forged, replayed, or reordered.**
Per-cabinet monotonic sequence numbers mean a replayed event is discarded rather than applied
twice. Correlation ids mean a replayed command is recognised as the same command.
**Currently insufficient:** until the transport-security sprint the link is plaintext and a
network attacker can forge a cabinet. This is the largest open weakness in the design and is
the reason transport security is not deferred to the end.

**T6 · A cabinet reports state the server did not authorize.**
The cabinet is not the authority. A slot change with no matching authorized command is
recorded and raises an alarm rather than updating custody. The device never decides.

**T7 · The audit trail is altered to hide an action.**
Audit records are append-only from the application layer, written in the same transaction as
the state change they describe, and correlated by id. No API path updates or deletes one.
**Known limitation:** anyone with the database file can rewrite it. Tamper-evident audit
(hash chaining) is not implemented, and file permissions are the only control today.

**T8 · The database file is stolen.**
Credentials in it are hashed. Everything else — holder names, asset descriptions, the full
custody history — is readable. **Not mitigated.** Encryption at rest is a deployment concern
today (disk encryption), not an application feature.

**T9 · Custody state and audit disagree after a crash.**
One transaction covers both. A crash between them is not possible; a crash before them loses
the command, which the correlation id makes visible.

**T10 · A cabinet goes offline and its state drifts.**
Missed heartbeats mark it `Offline` and its slots `Unknown`. Buffered events replay from the
last acknowledged sequence on reconnect. A sequence gap triggers a full snapshot, because a
gap means events were lost rather than delayed. Uncertain state is recorded as uncertain.

**T11 · A denial-of-service against the device gateway.**
Frames above a fixed maximum are rejected before allocation, connections are bounded, and an
unauthenticated connection is dropped quickly. Not a primary concern on a site LAN.

## Known limitations

Stated plainly, because a security document that reads as complete is worse than one that
does not:

1. **The device link is plaintext until the transport-security sprint**, and the cabinet
   credential is a shared secret. A network attacker on the site LAN can impersonate a
   cabinet.
2. **The audit trail is not tamper-evident.** Append-only is enforced by the application, not
   by the storage.
3. **The database is not encrypted at rest.** Disk encryption is the only answer today.
4. **The server host is the trust anchor.** An attacker with host access has everything.
5. **No multi-factor authentication.** Password or PIN only.
6. **Single site.** No federation, no cross-site custody, no multi-writer story.
