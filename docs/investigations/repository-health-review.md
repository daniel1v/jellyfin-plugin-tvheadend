# Repository health review

## Status

This document is a point-in-time review of the repository, not a backlog and not an architectural contract.

Reviewed commit: `d743e91b5427f82e978a73d5f809244b62951fe0`

The playback-compatibility work tracked in issue #1 is intentionally excluded from the findings below. That issue already defines a concrete behavioral change, implementation scope and acceptance criteria. This document records the state of the rest of the repository.

## Overall assessment

Outside the playback-compatibility work in issue #1, the repository is in a good structural state. No second large architectural problem, broad refactoring need, substantial block of dead code or obvious security defect was found.

The remaining findings are local correctness and diagnostics issues rather than signs of a wrong overall architecture.

A broad Clean Architecture or DDD refactor is not recommended. The current boundaries are already explicit and, importantly, tested.

## Confirmed strengths

### Architecture boundaries

The architecture tests actively prevent several failure modes that previously existed:

- `TVHeadEnd.Core` cannot depend on Jellyfin, MediaBrowser, ASP.NET, SkiaSharp or the TVHeadend adapter.
- The TVHeadend adapter cannot depend on Jellyfin-facing playback, recordings or compatibility code.
- Host-specific codec vocabulary has one owner in the Jellyfin compatibility layer.
- Access to the plugin singleton is constrained to the configuration bridge.
- `LiveTvService` and `RecordingsChannel` are kept independent rather than holding each other.
- Tests under the Core test area are prevented from quietly becoming integration tests against outer layers.

These tests make the intended dependency direction executable rather than merely documented.

### Build and static analysis

The main project enables nullable reference types, treats warnings as errors and runs the .NET analyzers together with additional style and multithreading analyzers.

CI builds, runs tests and verifies formatting on pushes and pull requests. The reviewed commit completed that CI successfully.

### Live-buffer and concurrency design

The ring buffer and late-join code is backed by dedicated concurrency, late-join, layout-change and rejoin tests. Apart from the per-reader guarantee issue already covered by issue #1, no second structural problem was found in the buffer design.

### HTSP connection model

The HTSP layer has a clear ownership model: one read loop, serialized writes, sequence-correlated requests and explicit completion/failure of pending requests. No sync-over-async workaround or obvious request-correlation race was found.

### Recording delivery

Recordings are proxied through the plugin rather than exposing TVHeadend credentials or relying on the remote recording endpoint's incomplete seek semantics. Range requests are forwarded as independent upstream requests and the plugin advertises byte ranges to Jellyfin.

### Anonymous proxy endpoints

The anonymous recording and TVHeadend-artwork endpoints are protected by HMAC-derived, unguessable tokens. The token identifies only a resource/path that is subsequently resolved against configured TVHeadend state; callers cannot freely choose an upstream host. No obvious SSRF or credential-leak path was found in this design.

### Release consistency

The release script validates that assembly version, package version, manifest entry, checksum, source URL, Git HEAD and upstream state agree before publishing. This is a strong guard against release metadata drifting from the tested artifact.

## Findings

### 1. Channel number removal can preserve a stale number

Severity: medium

Location: `TVHeadEnd/Tvheadend/Catalogs/ChannelCatalog.cs`

`AddOrUpdate()` currently computes the channel number using:

```csharp
var number = ReadNumber(message) ?? existing?.Number;
```

`ReadNumber()` returns `null` both when the `channelNumber` field is absent and when its value is zero or otherwise not positive.

Those two states have different meanings for a partial update:

- field absent: keep the existing number;
- field present with zero: TVHeadend is explicitly saying the channel is now unnumbered.

TVHeadend writes `channelNumber` into its channel description even when the major number is zero. Therefore an existing numbered channel can incorrectly retain its old number after TVHeadend removes the number.

Recommended fix:

- distinguish `message.Contains("channelNumber")` from the parsed value;
- absent field -> retain the previous number;
- present and zero/non-positive -> remove the channel from the numbered catalogue or otherwise represent it as unnumbered according to the existing catalogue policy;
- present and positive -> replace the number.

The existing tag handling already makes the equivalent distinction between "field absent" and "present but empty" and is a useful model for this fix.

### 2. Settings cache invalidation has a read/invalidate race

Severity: medium

Location: `TVHeadEnd/Tvheadend/TvheadendConnection.cs`

The cached settings are currently read through:

```csharp
public TvheadendSettings Settings => _settings ??= _settingsSource.Current;
```

`ApplyConfiguration()` reads `_settings`, sets it to `null`, and then may read `Settings` again to obtain the new value.

Because the read-compute-write operation behind `??=` is not synchronized with invalidation, a concurrent reader can:

1. observe `_settings == null`;
2. read an older/current source snapshot;
3. race with `ApplyConfiguration()` invalidating the cache;
4. publish the stale snapshot back into `_settings` after invalidation.

The practical window is small and configuration changes are rare, but this is still a real cache-coherency race.

Recommended fix:

Make snapshot publication and invalidation part of the same synchronization strategy. A `volatile` field alone is not sufficient because the compound `??=` operation must not race with invalidation.

### 3. Valid recording priority 0 is displayed as 5 in the settings UI

Severity: low

Location: `TVHeadEnd/Web/tvheadend.js`

The settings page currently contains:

```javascript
page.querySelector('#txtPriority').value = config.Priority || '5';
```

Priority `0` is valid and means "important", but JavaScript treats `0` as falsy. Opening the settings page therefore displays `5` for a stored priority of `0`, and a subsequent save can unintentionally overwrite the configured priority.

Recommended fix:

Use a nullish fallback rather than a truthiness fallback, for example:

```javascript
page.querySelector('#txtPriority').value = config.Priority ?? 5;
```

Other numeric settings should be reviewed for the same pattern whenever zero is a valid value.

### 4. HTSP lifetime cancellation can be reported as a request timeout

Severity: low

Location: `Tvheadend.Htsp/HtspConnection.cs`

`SendRequestAsync()` waits with a linked token containing:

- caller cancellation;
- connection lifetime cancellation;
- request timeout.

The `OperationCanceledException` handler distinguishes only caller cancellation. If the connection lifetime ends, the code can therefore report:

> TVHeadend did not answer '<method>' within ... seconds.

although the actual reason was that the connection closed.

This does not appear to corrupt request state, but it makes diagnostics misleading and can obscure connection-loss root causes.

Recommended fix:

Distinguish the three cancellation causes before translating the exception. Preserve caller cancellation, report connection shutdown as connection/lifetime failure, and reserve the timeout message for the timeout source.

## Things specifically not found

Within the reviewed scope, no evidence was found of:

- a second large architecture violation;
- a need for a repository-wide Clean Architecture or DDD rewrite;
- meaningful dead-code accumulation;
- sync-over-async or `Task.Run` workarounds in normal request paths;
- an obvious ring-buffer disposal or ownership defect beyond the issue already captured in #1;
- an obvious SSRF path in the anonymous proxy endpoints;
- TVHeadend credentials being exposed in published media or artwork URLs;
- a second broad playback-policy problem outside issue #1.

This is not a proof that none can exist; it records the result of the targeted review of the stated commit.

## Recommended order

1. Fix channel-number partial-update semantics.
2. Fix settings cache synchronization.
3. Fix priority `0` rendering in the settings page.
4. Improve HTSP cancellation diagnostics.

The first two are correctness fixes worth explicit regression tests. The latter two are small enough to fix directly with focused tests unless they grow in scope.

## Tracking policy

This document should remain an investigation snapshot, not become a permanent TODO list.

When a finding becomes concrete work:

- create an issue when it has meaningful scope, trade-offs, multiple affected components or acceptance criteria;
- fix small, obvious defects directly with tests when a separate issue would add no useful coordination value;
- update this document only to record that a finding was fixed, rejected or superseded, preferably with the issue or commit reference.

Issue #1 is deliberately an issue rather than only an investigation note because it already represents a concrete cross-cutting behavior change: client identity and playback requirements, Android TV IDR handling, live-stream sharing, opening-vs-late-join semantics, buffer reader guarantees, recording compatibility, tests and real-device acceptance criteria all have to move together.