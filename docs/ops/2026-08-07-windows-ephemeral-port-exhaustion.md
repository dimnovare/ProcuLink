# A random `*PostgresTests` class fails per full-suite run — on Windows only

**Date:** 2026-08-07
**Applies to:** local development on Windows. **Not** CI (`ubuntu-latest`), which is unaffected.
**Verdict:** host TCP-stack limitation, not a defect in the test suite. No repo code change was warranted.

---

## Symptom

Running `dotnet test ProcuLink.slnx` repeatedly on a Windows host, a *different* `*PostgresTests`
class fails on roughly one run in three to five. Two captured signatures, verbatim:

```
ProcuLink.Api.Tests.Integration.EndToEndPipelineTests.FullPipeline_UploadParseValidateMapTransformDeliver_ProducesPassportWithDeliveryProof [FAIL]
Npgsql.NpgsqlException : Failed to connect to 127.0.0.1:63026
---- System.Net.Sockets.SocketException : Only one usage of each socket address (protocol/network address/port) is normally permitted.
```

```
ProcuLink.Api.Tests.Integration.SupplierMappingEditPostgresTests.SupplierMappingEdit_NeverShipsThePreEditDocument_AndLeavesThePinnedOrderAlone [FAIL]
Npgsql.NpgsqlException : Exception while reading from stream
---- System.IO.IOException : Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host..
-------- System.Net.Sockets.SocketException : An existing connection was forcibly closed by the remote host.
```

Both are the same pressure expressed two ways (`AddressAlreadyInUse` / 10048 and
`ConnectionReset` / 10054). **Every affected class passes when run in isolation.**

This is a different defect from the per-process readiness flag fixed in #175.

## Root cause

Almost every class in the `postgres-container` collection builds its connection string with
`Pooling = false`, so **every** `DbContext` operation opens and closes a fresh physical TCP
connection to the shared container's published loopback port. Each graceful close parks the
client's ephemeral port in `TIME_WAIT`.

On this Windows host, measured:

| Quantity | Measured value | How |
|---|---|---|
| `TIME_WAIT` duration | **119.6 s** | opened a loopback connection, closed it at 15:36:43.621, watched `netstat` until the entry vanished. `TcpTimedWaitDelay` is unset in the registry, so this is the Windows default. |
| Dynamic port range | **49152–65535 (16,384 ports)** | `netsh int ipv4 show dynamicport tcp` |
| Reserved carve-outs | ~460 ports | `netsh int ipv4 show excludedportrange protocol=tcp` — 80, 443, 44300, 49688–49787, 49788–49887, 50000–50059, 53423–53522, 53623–53722 |

Windows will not hand out a local port that is in `TIME_WAIT`, so the supply is bounded by
`usable ports ÷ 120 s`. Sustained connection churn above that rate cannot be serviced, and
`connect()` returns `WSAEADDRINUSE`.

### Why it appeared after #168

Not because the shared container concentrated the load onto one destination — see *Refuted*
below. Because the shared container made the collection roughly **four times faster** (the CI
Test step went 639 s → 157 s). The same number of connections now lands inside a window about a
quarter as long, while `TIME_WAIT` stays a fixed 120 s, so peak concurrent `TIME_WAIT` rose
in proportion. Measured in one local run: **3,410** connections parked against
`127.0.0.1:63026`, 3,892 `TIME_WAIT` total, 4,022 ephemeral ports in use — all inside 75 s.

## Measurements

Full-solution runs on this host, 2026-08-07: **2 of 5 runs affected** (run 1 →
`EndToEndPipelineTests`, `AddressAlreadyInUse`; batch run 1 → two failures in
`SupplierMappingEditPostgresTests`, `ConnectionReset`; three runs clean).

Correlating the `AddressAlreadyInUse` failure against a 1 Hz `netstat` sampler. The failing test
ran 15:36:54.814 → 15:36:55.401 (from the trx), at which point:

```
ts        time_wait_total  established  ephemeral_in_use  top_remote_time_wait
15:36:54  2383             176          2570              127.0.0.1:63026=1908 …
15:36:55  2495             142          2646              127.0.0.1:63026=2021 …
```

The mechanism reproduces with **no test code at all** — a plain connect/close loop against a
Docker-published loopback port throws the identical exception:

```
RESULT: FAILED after 10048 successful connects in 8.1s
exception: System.Net.Sockets.SocketException: Only one usage of each socket address (protocol/network address/port) is normally permitted. 127.0.0.1:5435
SocketErrorCode: AddressAlreadyInUse NativeErrorCode: 10048
```

Repeating that probe from a drained pool gave ceilings of **10,048 / 10,277 / 14,827** connects.
A 16-thread version reached **13,786** in aggregate before six threads failed with the same
error. The ceiling is therefore state-dependent, not a constant.

### Linux does not have this problem

The same connect/close loop inside a Linux container:

```
ip_local_port_range: 32768	60999
tcp_tw_reuse: 2
tcp_fin_timeout: 60
connects_ok=60000 elapsed=38.75s rate=1548/s
time_wait_sockets_now: 6818
error: none (hit MAX)
```

**60,000 connections, zero errors**, versus ~10,000 on Windows. `tcp_tw_reuse=2` lets Linux
recycle loopback `TIME_WAIT` sockets, and the range is 28,232 ports wide. Consistent with the
last 20 `ci.yml` runs: 18 success, 2 cancelled by the concurrency policy, no occurrence.

## Refuted

Three plausible explanations that the measurements rule out. Recorded so they are not re-opened.

1. **"Before #168 the load spread across 56 destination 5-tuples, so each had its own supply."**
   False on Windows. Saturating against `127.0.0.1:5435` and then immediately connecting to a
   *different* destination, `127.0.0.1:15432`, failed on the second attempt with the same
   `AddressAlreadyInUse`. The port is blocked for **every** destination, so destination diversity
   buys nothing — and spreading the container across several published ports would not help.
2. **Concurrent `connect()` collisions.** 16 threads reached 13,786 connections in aggregate,
   no worse per-port than the single-threaded walk.
3. **The allocator colliding with a bound listener in the ephemeral range.** The single-threaded
   probe walked the entire range (49152 → 65534, wrapping) past other processes' listeners and
   only failed on exhaustion.

## Known gap

The suite fails at roughly 2,600 ephemeral ports in use, whereas synthetic churn against a
single destination needs 10,000–15,000. That gap is **not** explained here. The measured ceiling
varied by 50 % across four drained runs, so the allocator's behaviour is state-dependent; the
suite's multi-process, bursty allocation pattern plausibly lowers it further, but that was not
demonstrated. Treat the numbers above as evidence of the mechanism, not as a threshold.

## What to do about it

Nothing in this repository. The mitigations are host settings, and they need an elevated prompt.
**Apply them yourself; they change machine-wide networking behaviour.**

Widen the dynamic port range (takes effect immediately, no reboot):

```
netsh int ipv4 set dynamicport tcp start=10000 num=55535
```

Or shorten `TIME_WAIT` to 30 s — `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters`,
`TcpTimedWaitDelay` (DWORD) = `30`, then reboot.

Neither was applied or verified on this host, for the reason above; the arithmetic is that the
first raises the port supply about 3.4× and the second raises turnover about 4×.

Without changing host settings: re-run the failing class on its own — it passes — or leave a
couple of minutes between full-suite runs so `TIME_WAIT` drains.

## Optional follow-up (not done here)

54 classes in `ProcuLink.Api.Tests/Integration` set `Pooling = false`. Only **10** of them run
genuinely concurrent work (`Task.WhenAll` / `Parallel.` / `Task.Run`):

```
AcceptanceGateEntryPathsPostgresTests   DeliverySlaConcurrencyPostgresTests   S3IngressClaimFirstPostgresTests
AiUsageTrackerPostgresTests             EmailPollClaimFirstPostgresTests      SchemaFingerprintConcurrencyPostgresTests
DeliveryConcurrencyPostgresTests        ReplayReprocessPostgresTests          SftpIngressClaimFirstPostgresTests
DeliveryConcurrentRetryPostgresTests
```

`Pooling = false` is load-bearing in those ten: it is what makes each racing context take its own
physical connection, which is the whole point of the claim tests. In the other 44 it appears to be
inherited by copy-paste, and it is the bulk of the churn.

Enabling pooling there would cut the churn substantially and is the only repo-side change that
would reduce this pressure. It is deliberately **not** bundled into this note: each of the 44
needs its own justification and its assertions re-verified, which is a packet of its own, and CI
does not need it.
