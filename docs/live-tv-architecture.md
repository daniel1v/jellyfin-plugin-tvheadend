# How live TV works

A live channel is one HTTP request.

TVHeadend serves the broadcast through its `pass` profile — the original MPEG-TS, forwarded
untouched, with its own PCR, program tables and random access points intact. That stream is also
the only description of itself the plugin needs, because the Program Map Table it carries is the
same table libavformat walks to decide what streams the file has and in what order.

```
TVHeadend  ──HTTP profile=pass──▶  conditioner ──▶ ring buffer ──▶ Jellyfin
                                        │
                                     PAT/PMT
                                        │
                                  MediaSourceInfo
```

Nothing else is consulted. No second subscription, no service lookup, no administrative API — so
nothing else can be slow, be refused for want of a permission, or disagree with the bytes.

## What the plugin establishes itself

From the program map of the stream being delivered:

- **the number of elementary streams and their order**, which is what every later `-map`
  argument means;
- **what medium each one carries** — video, audio, subtitle or data;
- **the codec**, where the table identifies one. MPEG audio (stream types 0x03 and 0x04) is named
  `mp2`, which is what DVB mandates for them and what FFmpeg reports. This was once left unnamed,
  on the reasoning that the table does not state the layer — and that turned out to be the worse
  of the two mistakes: Jellyfin reads an unnamed codec as one no device profile matches, picks
  such a track anyway when it chooses for itself, and then refuses the same track when it
  re-checks it, sending the client to a transcode of a channel it could have played.
- **the language** and the **hearing-impaired flag**, from the DVB descriptors;
- **what an audio track is for**, where the descriptors say so -- see [Audio](#audio) below.

Stream type 0x06 is "private data in PES packets" and is where DVB puts AC-3, E-AC-3, subtitles
and teletext; those are told apart by the descriptors that follow it (`0x6A`, `0x7A`, `0x59`,
`0x56`, `0x7C`, and the registration descriptor `0x05`).

### What it deliberately does not establish

Resolution, frame rate, bit rate, codec profile and level. None of them is in a PMT, none is
needed for the playback decision, and none is worth a second analysis of a stream that is already
playing. They are left unset. Jellyfin treats an absent optional value as unknown and carries on;
it is a *wrong* value that makes it choose badly.

## Audio

`IsDefault` on an audio stream is a statement about the broadcast, not about the viewer. It says
the tables did not call this track an addition to the programme -- nothing more. Which track a
given viewer actually hears is Jellyfin's decision, made from the device profile, the account's
language and default-track preferences, and the stream metadata below.

What the tables can say, in order of authority:

- DVB's **supplementary audio descriptor** states a track's editorial role outright. Where it
  appears it settles the question: main audio, an audio description for the visually impaired, a
  clean mix for the hearing impaired, spoken subtitles. Values the standard reserves are not
  interpreted.
- Otherwise the **audio type** of the ISO 639 language descriptor is read, and only its two
  unambiguous values are acted on -- a hearing impaired mix and a commentary for the visually
  impaired. Audio type one is nominally "clean effects" and is *not* treated as an addition,
  because broadcasters use it for ordinary programme sound.
- Where neither says anything, the purpose is unknown, and an unknown track is not withheld.

No track is made the default for being first in the table. That was tried and it is the wrong
statement: the table says which tracks exist, not which one a viewer wants.

The asymmetry -- withhold only on a clear statement, never on silence -- is deliberate, because
the failure it prevents is not obvious. Jellyfin narrows its audio candidates to the tracks marked
default whenever the account prefers default tracks, which is how a new account is created. If
that narrowing leaves nothing, the audio compatibility check is *skipped* rather than failed:
direct play is granted with no transcode reason and labelled with the first track of the map. The
client pins the track it was told, asks again, and this time the check does run -- against that one
track -- and refuses it. Reading silence as "supplementary" would put every channel without audio
descriptors into exactly that state.

## What FFmpeg is told

The opened media source carries `AnalyzeDurationMs`. Without it Jellyfin falls back to its
server-wide default -- 200 seconds -- and passes that to FFmpeg as `-analyzeduration`. On a live
stream that means FFmpeg reads two hundred seconds of broadcast before writing its first HLS
segment, so the client waits, Jellyfin gives up waiting for the playlist, kills FFmpeg and starts
again. Every channel, for ever. It is not a tuning knob; live TV does not play without it.

## Random access

Two different things, kept apart:

- **where delivery begins.** The conditioner withholds the stream until it has both program
  tables and a video packet that starts a payload unit. If the broadcast has not signalled a
  random access point within a couple of seconds, it starts anyway rather than stalling. A
  program with no video the plugin recognised -- a radio service, or a television service whose
  video the table did not identify -- starts as soon as the tables are complete, because there is
  nothing to wait for and withholding it would withhold it for ever.
- **where a decoder may join.** Only a packet whose adaptation field carries the random access
  indicator is recorded in the bootstrap index.

A payload unit start says a picture begins here; it does not say a decoder may begin here. Storing
one as an entry point would hand every later reader a position its decoder cannot start on, for as
long as it stayed inside the buffer window. `StartedOnConfirmedRandomAccessPoint` reports which of
the two happened, for the log.

An entry point also carries *how strong* a guarantee it is, and the two are not interchangeable:

- **`DvbRandomAccess`** -- the broadcast signalled a random access point. That is a legal entry
  for the broadcast, and for H.264 it may be an IDR picture or a recovery point opening a GOP that
  refers backwards.
- **`Idr`** -- the picture at that position was read and found to contain an IDR.

`Idr` is the stricter of the two. Every `Idr` point is also a valid `DvbRandomAccess` point; the
reverse does not hold, which is the whole reason the distinction exists.

Classification does not stop once a stream is running. A position is published as
`DvbRandomAccess` as soon as the broadcast signals it, and the same position is republished as
`Idr` if the picture there turns out to carry one. A point therefore gets stronger over time, never
weaker.

## Joining and re-joining the ring buffer

Both go through the same bootstrap index, because they are the same problem: the oldest surviving
byte in a ring is wherever the write head happened to wrap, which is the middle of a picture with
no tables in front of it.

- A reader that **joins** is placed at the most recent confirmed random access point still in the
  window, preceded by PAT and PMT.
- A reader the writer has **lapped** — a client paused for longer than the buffer holds — is
  re-joined the same way, and the tables are delivered before the bytes they describe.

A reader states the guarantee it needs, and it is honoured on both paths. A reader that requires
`Idr` is never placed on a position known only as `DvbRandomAccess`, however much closer to live
that position is -- it is put further back, onto the most recent point that actually carries the
guarantee. A nearer entry point is not a better one.

What happens when nothing in the window is good enough depends on what was asked for, and the two
answers are different on purpose:

- **`DvbRandomAccess`** falls back to the oldest surviving bytes, with the tables in front, so the
  decoder can map the elementary streams and resynchronise on its own. An ordinary random access
  is all that was wanted, and starting somewhere imperfect beats not starting.
- **`Idr`** does not fall back. There is no weaker position that satisfies it -- not the oldest
  bytes, and not a nearer point that only promises random access -- so the reader is told *not
  yet* and waits for an IDR to be recorded. Falling back here would hand a decoder that cannot
  start without an IDR a position with none, which is the failure the guarantee exists to prevent.

## How long a channel stays open

One subscription serves every viewer of a channel whose stream they can share, and it is closed
when the last of them stops. What counts is viewers, not the number of times playback was
negotiated -- a client whose first attempt fails negotiates again, and Jellyfin answers by asking
for the stream once more. Counting those asks left channels running with nobody watching, because
a client reports one stop and not one per attempt it abandoned.

A viewer is identified by the client name and device id of the authenticated request, which is how
the server keys a session of its own, so the plugin and the server agree on who is watching.

The two directions are not symmetrical, and the asymmetry is Jellyfin's:

- **arriving** is `GetChannelStreamWithDirectStreamProvider`, on the authenticated request, so the
  viewer can be named;
- **leaving** is `ILiveStream.ConsumerCount--`, reached from the session manager, the media info
  controller, the transcode manager and a stream state being disposed. It says how many are left
  and never who, and not every one of those paths is on a request at all.

So a departure does not delete a name -- any of the named viewers could have been the one that
left. It reduces the total and forgets the names, leaving viewers known to be there but no longer
identifiable; a later arrival takes one of those places back instead of adding to the count.

## Trusting the table

The program map is the only description there is, so it is checked rather than trusted: the
MPEG-2 systems CRC-32 must match, and `current_next_indicator` must be set. A section announced
for later describes the program as it *will* be, and acting on it would describe streams that are
not there yet. A section failing either check is discarded and the table already in hand keeps
describing the stream.

When the broadcaster changes the layout mid-stream, every entry point found under the old table
is discarded: a reader sent to one would be given the new tables and the old picture.

## Why the EIT is dropped

libavformat creates an `epg` stream the moment the first EIT packet turns up. Where that lands
relative to the elementary streams depends on how the broadcast interleaves them, so every index
after it shifts unpredictably. Dropping the EIT PID means what remains comes from the program map
alone, and is numbered in program map order every time.

## Permissions

An ordinary TVHeadend **streaming** account. Nothing in the live path calls an administrative API.

The plugin also needs the rights its other features imply — reading the channel list and guide
over HTSP, and the DVR rights for recordings.

## A live stream is never probed

`SupportsProbing` is false on the media source, opened or not. Probing a live channel means
reading a stream that is already being read, to answer a question the program map has already
answered.

What "described" means depends on the channel rather than on the table, because the transport
stream cannot tell the two apart: a program map with no video is a complete radio service and an
incomplete television channel. The channel kind is therefore passed into the open path from the
channel list, which knows it. Television needs one recognised video stream, radio one recognised
audio stream; when it is missing the open fails at once and says which stream was missing and what
the table held. There is no probe to fall back to and no long timeout standing in for one.

An entry the table names but nothing identifies -- stream type 0x06 with no descriptor saying what
is inside it -- keeps its index and is described as data. It blocks nothing and is not guessed at.

## The one thing a client changes

Some DVB H.264 services never transmit an IDR picture: their access points are I-frames marked by
the random access indicator and a recovery point message. FFmpeg starts on those; Android's
MediaCodec does not — it takes the samples at full rate, emits no frame and raises no error, which
reaches the viewer as a spinner that never resolves. Measured on a Pixel 10: over thirty seconds
Das Erste contained no NAL type 5 at all and ZDF contained 48.

Three conditions, all of which have to hold, and they are settled while the stream opens:

- the client Jellyfin authenticated names itself as one of the Android apps;
- the program map says the video is H.264 — the question belongs to no other syntax, and the
  MPEG-2 slice start code for picture row five is byte-for-byte an IDR NAL header;
- the first few signalled access points were classified and none of them carried an IDR.

Anything absent or unsettled means no. The conditioner that fills the ring is the one that answers
this, as the packets go past: an IDR the moment its NAL appears, and its absence the moment the
next picture begins.

**The broadcast is never held up for this.** Bytes run from TVHeadend through the conditioner into
the ring from the first packet, as they always do. What waits is only the playback decision for an
Android H.264 cold start, and only until the first three signalled access points have been
classified -- a statement about how far this open looked, not about the broadcaster. The first of
them to carry an IDR settles it in favour of direct play, and the reader joins on that IDR. Three
classified without one settles it the other way. Classification carries on afterwards for the
readers that join later; it simply no longer decides anything about this open.

When all three hold the media source withdraws `SupportsDirectPlay` and `SupportsDirectStream`,
which puts Jellyfin on its ordinary transcoding path, and one small middleware sets
`allowVideoStreamCopy=false` on the requests naming that live stream — without which Jellyfin
would copy the H.264 video inside the transcode and deliver the same pictures it cannot start on.
**The re-encoding is Jellyfin's own, with the server's configured encoder and hardware
acceleration.** The plugin runs no FFmpeg of its own, and the ring buffer holds the broadcast
exactly as TVHeadend delivered it either way.

The middleware is registered as an ordinary `IStartupFilter` and keys on nothing but the
`LiveStreamId` already in the URL: no client detection, no channel names, no session state, no
cache. Every other request, including every other plugin's, passes through untouched.

Two viewers of such a channel who need different things get two subscriptions, because one media
source cannot both offer and withhold direct play. A channel whose H.264 does carry IDR pictures,
and everything that is not H.264, is shared by everyone as before.

## Recordings

A separate path, and deliberately less clever than it was.

A recording is described from a sample of its opening, because analysing the whole file means
reading gigabytes across the network. That sample establishes what the recording contains — and
nothing else. It is explicitly **not** used to conclude that the recording lacks something: a
bounded probe cannot establish an absence, and an earlier version used exactly that inference to
withhold direct play and serve a re-encode for whole recordings.

HEAD and GET therefore describe the same resource identically: one route, proxying TVHeadend,
advertising the same length and the same range support to both.

## Known external issues

`LiveTvMediaSourceProvider.Normalize` in Jellyfin overwrites `IsInterlaced` to `true` on the video
stream of every external live TV service, whatever the plugin reported. Device profiles keying on
interlacing may therefore choose transcoding unnecessarily. This is a server bug; no workaround is
built here, because a plugin-side hack would only hide it.

Jellyfin for Android 2.7.1 does not complete direct play of a live MPEG-TS stream, and the reason
is in the client rather than in what it is served. For `PlayMethod.DIRECT_PLAY` over
`MediaProtocol.HTTP` it hands Media3 the source path together with a forced MIME type of
`application/x-mpegURL`, whatever the URL ends in. Media3 therefore builds an HLS media source,
fetches our `stream.ts`, and tries to parse a transport stream as a playlist. Playback fails and
the client falls through to its next option.

The measurement matches that exactly rather than contradicting it. On 2026-08-25: negotiation
succeeded twice, both answers `DirectPlay`; the client then fetched the stream itself, sent no
`Range` header, read between half a megabyte and a megabyte in about a tenth of a second, closed
the connection, and asked a third time with direct play switched off. What it was served has been
checked against FFmpeg and is sound -- `video/mp2t`, chunked, program tables followed by an access
unit delimiter, parameter sets and an IDR picture, indices matching the published program map
exactly. The download is real; the parser it was fed to is the wrong one.

Nothing further is to be learned from the server side, so no more tracing. Nor is this a reason to
publish the buffer as a file again, to rename the route, to lie about the content type, or to
build HLS inside the plugin. The file route is closed in any case: the server serves a live stream
from its static video endpoint only when the request carries a `LiveStreamId`, and this client
omits it, so that route delivers the buffer file, which ends.

The remaining cost is startup latency rather than quality. Jellyfin's HLS path waits for several
segments before releasing the playlist, which is several seconds on a live channel; the fallback
itself copies both video and audio, so what the viewer sees is the broadcast unaltered.
