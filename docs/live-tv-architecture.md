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
- **the codec**, where the table identifies one. MPEG audio (stream types 0x03 and 0x04) is left
  without one on purpose: the table does not say which layer, and FFmpeg reports whichever it
  finds. The medium is certain, so the track is described; the codec is not, so it is left unsaid.
- **the language** and the **hearing-impaired flag**, from the DVB descriptors.

Stream type 0x06 is "private data in PES packets" and is where DVB puts AC-3, E-AC-3, subtitles
and teletext; those are told apart by the descriptors that follow it (`0x6A`, `0x7A`, `0x59`,
`0x56`, `0x7C`, and the registration descriptor `0x05`).

### What it deliberately does not establish

Resolution, frame rate, bit rate, codec profile and level. None of them is in a PMT, none is
needed for the playback decision, and none is worth a second analysis of a stream that is already
playing. They are left unset. Jellyfin treats an absent optional value as unknown and carries on;
it is a *wrong* value that makes it choose badly.

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

## Joining and re-joining the ring buffer

Both go through the same bootstrap index, because they are the same problem: the oldest surviving
byte in a ring is wherever the write head happened to wrap, which is the middle of a picture with
no tables in front of it.

- A reader that **joins** is placed at the most recent confirmed random access point still in the
  window, preceded by PAT and PMT.
- A reader the writer has **lapped** — a client paused for longer than the buffer holds — is
  re-joined the same way, and the tables are delivered before the bytes they describe.
- If no confirmed access point survives, the stream is still delivered from the oldest bytes, with
  the tables in front so the decoder can map the elementary streams once it resynchronises.

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
- delivery began at a signalled access point whose picture was then read to the end and found to
  hold no IDR.

Anything absent or unsettled means no. The conditioner that fills the ring is the one that answers
this, as the packets go past: an IDR the moment its NAL appears, and its absence the moment the
next picture begins. Nothing waits on a timer, and only such a viewer waits at all.

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
