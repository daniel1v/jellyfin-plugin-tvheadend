# Live TV cold start

Why a channel takes as long as it does to appear, what has actually been measured, and what could
be done about it. This is an open investigation, not settled architecture: the durable rules live in
[../architecture.md](../architecture.md), and anything here may still turn out to be wrong.

Every statement below is labelled with what kind of statement it is.

- **Established behaviour** -- read out of the code, ours or Jellyfin's, and true until that code
  changes.
- **Measured observation** -- a number somebody took, with what it was taken on.
- **Current hypothesis** -- believed, not measured.
- **Candidate solution** -- a direction that could be tried.
- **Rejected / low-priority** -- a direction considered and put aside, with the reason.

## The shape of the problem

**Established behaviour.** A channel is delivered directly when the client's profile and the media
source agree, and re-encoded when they do not -- Jellyfin decides which, from the device profile and
`StreamBuilder`. Two things push channels onto the re-encoding side: an H.264 service whose access
points carry no IDR picture, for the Android clients that cannot start on anything else, and a codec
the client's profile does not list at all, MPEG-2 being the common one. Both are described in
[The one thing a client changes](../architecture.md#the-one-thing-a-client-changes).

**Measured observation.** Where no video re-encode is needed -- ZDF is the usual example -- a cold
start lands in the range of a few seconds. Where one is, the observed cold start is currently around
**8 s**; Das Erste and VOX are the examples, for the two different reasons above.

**Current hypothesis.** The difference is the delivery path taken after the playback decision, not
the channel and not the broadcast. Nothing measured so far contradicts that, and nothing so far
proves it either.

### What the older numbers do and do not say

**Measured observation, 2026-08-26, 8097 test server, plugin `04e43c0`.** Cutting `MinSegments` and
`SegmentLength` to one took the video playlist from 9.4 s to 1.9 s on ZDF, 2.5 s on Das Erste and
2.3 s on RTL.

Those numbers were taken on the **copy/remux HLS path**, where FFmpeg was passing the video through
untouched. They are evidence about the playlist gate that was removed and about nothing else. They
are **not** general startup times for this plugin, and quoting them as such is what this section
exists to prevent: a channel that has to be re-encoded does not start in 2 s today.

**Measured observation, same session.** `AnalyzeDurationMs` is a ceiling rather than a wait -- the
first segment was written after 1,772 ms at 100 ms, 1,807 ms at 250 ms, 1,998 ms at 500 ms and
1,481 ms at 1,000 ms, which is noise. Below roughly 250 ms FFmpeg cannot state an AC-3 sample rate
and a copied stream fails to write its muxer header, so the value stays at 2000. See
[What FFmpeg is told](../architecture.md#what-ffmpeg-is-told).

## Where the time might be going

**Established behaviour.** The path a cold start passes through contains the stages below. Their
individual contribution has **not** been measured; this is the sequence they occur in, not a ranking
by cost.

1. TVHeadend HTTP open -- the subscription and the first bytes of `profile=pass`.
2. Plugin bootstrap -- program tables read, a safe entry point found, the ring buffer filled far
   enough to be served.
3. FFmpeg process startup.
4. Input analysis (`-analyzeduration`, bounded as above).
5. Decoder and encoder initialisation, where a re-encode is required -- this stage is absent
   entirely on the direct and copy paths.
6. HLS segment generation.
7. Playlist readiness -- Jellyfin withholds the playlist until `MinSegments` segments exist.
8. Jellyfin's next-segment readiness rule, per segment fetched.
9. Android/Media3 buffering and decoder startup on the client.

**Established behaviour.** Stage 7 is the one already addressed: the plugin sets `MinSegments=1` and
`SegmentLength=1` on its own video playlist requests where the client stated neither, which removed
the original nine-second gate. See
[How long a viewer waits](../architecture.md#how-long-a-viewer-waits).

**Established behaviour.** Stage 8 is `DynamicHlsController` in Jellyfin: a segment that exists on
disk is served only once the transcoding job has exited or the *next* segment also exists, polled
every 100 ms. It is not exposed as a query parameter, so the middleware that reaches `MinSegments`
and `SegmentLength` cannot reach it.

**Established behaviour.** This is not "TS against HLS". The HLS segments here are themselves
MPEG-TS. What differs from a continuous response is segmented delivery with a playlist and a
readiness rule in front of it.

**Current hypothesis.** Stage 5 together with stages 6 to 8 accounts for most of the difference
between the fast and the slow case. Untested. The honest next step is instrumentation per stage,
not a fix.

## Candidate solutions

### Trim what is left of the HLS wait

**Candidate solution.** Shorten or remove the remaining waits, above all the next-segment readiness
rule in stage 8.

Smallest change in scope. Its ceiling is bounded by the segment length, currently one second, so it
cannot by itself account for eight. It also lives in Jellyfin's controller rather than in a request
parameter, so reaching it is not a matter of rewriting a query string.

### Jellyfin's progressive transcoding path

**Established behaviour.** Jellyfin has a progressive transcoding path that streams FFmpeg's output
over a single HTTP response with no segments and no playlist -- `TranscodingJobType.Progressive`,
which is what `VideosController` uses.

**Candidate solution.** Deliver re-encoded live streams over that path instead, which would take the
segment boundary out of the startup. Attractive on the server side for that reason alone.

**Established behaviour.** The current Jellyfin Android client requires HLS for
`PlayMethod.TRANSCODE`: `QueueManager.kt` asserts `require(protocol == MediaStreamProtocol.HLS)` and
throws on anything else. A URL rewrite alone therefore does not reach this path -- the client has to
be told a playback shape it accepts.

**Candidate solution, with a cost.** A middleware bridge could let Jellyfin decide exactly as it does
now and then route live streams onto the progressive transcoder. It would bend the playback
semantics the client was told, and it would additionally have to get the transcoding lifecycle,
playback reporting and `StopTranscoding` right for a job the client believes is something else. Not
a preferred direction as things stand.

### Low-latency HLS

**Established behaviour.** Media3 on Android supports Apple's Low-Latency HLS, including
`EXT-X-PART`.

**Established behaviour.** Jellyfin's current HLS output does not produce Apple LL-HLS parts; its
playlists carry no `EXT-X-PART`.

**Established behaviour.** FFmpeg's `lhls` option is not Apple's LL-HLS. It emits the older
`EXT-X-PREFETCH` style of prefetch hint, which is a different scheme with different client support.

**Candidate solution, larger than it looks.** Real LL-HLS would leave Jellyfin's transcoding decision
completely intact and change only the delivery. But it is not a parameter change: it needs an
additional packaging and playlist path producing partial segments and the playlist updates that
announce them. That makes it a poor first step, whatever its merits as an eventual one.

## Rejected and low-priority directions

**Rejected: transcoding in TVHeadend, or in the plugin itself.** Neither place knows what the
concrete client can decode. Both would have to reproduce part of Jellyfin's device-profile and codec
reasoning, and would then be wrong for every client that does not match the guess. The plugin
describes the broadcast; the server decides what to do with it, and for whom.

**Low priority: lowering `AnalyzeDurationMs` further.** Measured to do nothing for startup, and
measured to break stream copying below roughly 250 ms.

## What would move this forward

Per-stage instrumentation of one slow channel: timestamps at the TVHeadend open, the first byte out
of the ring, FFmpeg start, first frame decoded, first segment written, playlist released, first
segment served, first frame rendered on the client. Until that exists the eight seconds has one
number and no breakdown, and any fix chosen from the list above is chosen on a hypothesis.

The constraint on all of it: Jellyfin keeps deciding, alone, what has to be transcoded for which
client. Only the delivery path taken after that decision is in scope.
