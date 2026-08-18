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
- **the codec**, where the table identifies one;
- **the language** and the **hearing-impaired flag**, from the DVB descriptors.

Stream type 0x06 is "private data in PES packets" and is where DVB puts AC-3, E-AC-3, subtitles
and teletext; those are told apart by the descriptors that follow it (`0x6A`, `0x7A`, `0x59`,
`0x56`, `0x7C`, and the registration descriptor `0x05`).

### What it deliberately does not establish

Resolution, frame rate, bit rate, codec profile and level. None of them is in a PMT, none is
needed for the playback decision, and none is worth a second analysis of a stream that is already
playing. They are left unset. Jellyfin treats an absent optional value as unknown and carries on;
it is a *wrong* value that makes it choose badly.

## Random access

Two different things, kept apart:

- **where delivery begins.** The conditioner withholds the stream until it has both program
  tables and a video packet that starts a payload unit. If the broadcast has not signalled a
  random access point within a couple of seconds, it starts anyway rather than stalling.
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

## Why the EIT is dropped

libavformat creates an `epg` stream the moment the first EIT packet turns up. Where that lands
relative to the elementary streams depends on how the broadcast interleaves them, so every index
after it shifts unpredictably. Dropping the EIT PID means what remains comes from the program map
alone, and is numbered in program map order every time.

## Permissions

An ordinary TVHeadend **streaming** account. Nothing in the live path calls an administrative API.

The plugin also needs the rights its other features imply — reading the channel list and guide
over HTSP, and the DVR rights for recordings.

## When Jellyfin probes the stream itself

Only when the plugin cannot honestly describe it: the program map named no video stream, or none
arrived before the stream was published. In that case the media source is published with
`SupportsProbing = true` and no streams, and Jellyfin establishes what is in it. A description
that is merely missing optional fields is *not* such a case, and never suppresses a probe
incorrectly, because a description is only offered as complete when it has a video stream.

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
