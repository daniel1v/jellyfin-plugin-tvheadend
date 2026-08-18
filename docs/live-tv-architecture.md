# How live TV works

TVHeadend already parses every broadcast it tunes, in order to feed its own muxers. This plugin
uses that analysis rather than making a second one, which is why nothing in the live path runs
FFprobe.

## The two halves

A running channel is two connections to the same TVHeadend service.

```
                         TVHeadend
                             |
                      the same service
                    /                  \
        HTTP profile=pass          HTSP subscription
               |                          |
      original MPEG-TS           parser / tsfix / globalheaders
               |                          |
               |                  subscriptionStart
               |                          |
               |                  stream metadata
               |                          |
               |            A/V muxpkt filtered out entirely
               |                          |
               |            further start/stop/status events
                    \                  /
                     Jellyfin plugin
                             |
                      MediaSourceInfo
                             |
                          Jellyfin
```

**HTTP `pass`** is the media path and the only one. TVHeadend forwards the original transport
stream untouched, with its own PCR, program tables and random access points intact. No TVHeadend
transcoding and no compatibility profile is ever requested.

**HTSP** is the description. The plugin subscribes to the same channel and immediately sends
`subscriptionFilterStream` disabling indices 0–511. TVHeadend applies that filter at the point
where it would serialise a packet, so its parser, timestamp fixer and global header collector all
keep running and every `subscriptionStart`, status and stop message still arrives — only the audio
and video payload never reaches the socket.

The subscription lives as long as the stream. A broadcast that changes shape produces a fresh
`subscriptionStart`, and the media source is corrected to match.

## Making the two halves the same service

The HTTP subscription is opened **first**, which is what makes TVHeadend choose and start a
service; the HTSP subscription then attaches to what is already running.

That ordering is not trusted on its own. A channel can map to several services, and combining one
service's description with another's video would be wrong in a way nothing downstream could
detect. So the agreement is proven:

1. HTSP reports `sourceinfo`, carrying the multiplex UUID and the DVB service name.
2. The channel's UUID (`channelIdStr`, sent with every `channelAdd`) resolves through
   `api/idnode/load` to the services it maps to. One service is the whole answer; several are
   narrowed by matching `multiplex_uuid` and `svcname` against `sourceinfo`.
3. `api/service/streams` gives that service's components, each with its `es_index` **and** its PID.
4. Every PID in the program map of the bytes actually arriving must be one that service carries.

If step 4 fails, the HTSP description is discarded and Jellyfin is left to inspect the stream
itself. Nothing is published that mixes the two.

## Stream indices

`es_index` in HTSP is TVHeadend's own per-service counter — assigned in discovery order, in
`elementary_stream_create_parent`. It is **not** a PID, not a position, and not Jellyfin's
`MediaStream.Index`. Assigning it directly is what once made FFmpeg's `-map` arguments land on the
wrong tracks.

The real chain:

```
HTSP es_index
    → api/service/streams          (es_index → PID)
    → PMT of the delivered stream  (PID → position)
    → Jellyfin MediaStream.Index
```

libavformat creates one stream per PMT entry as it walks the table, so an entry's position in the
delivered PMT is the index every later `-map` argument means. The PMT is parsed from the bytes
that arrive rather than from anything TVHeadend reports, because the `pass` muxer rewrites the
table down to the subscription's components when configured to and leaves the broadcaster's own in
place when not.

A PMT entry that nothing describes still occupies its index. Leaving a gap would shift everything
after it — the same failure mode as counting the EIT.

### Why the EIT is dropped

libavformat creates an `epg` stream the moment the first EIT packet turns up. Where that lands
relative to the elementary streams depends on how the broadcast interleaves them, so every index
after it shifts unpredictably. Dropping the EIT PID means what remains comes from the program map
alone, and is numbered in program map order every time.

## Fields worth being careful about

Two HTSP fields are routinely misread, and both are named for what they are in the client library:

- `rate` is `es_sri`, an **index** into the MPEG-4 sampling frequency table, not a frequency.
  Reporting it directly gives an audio track claiming to be sampled at 4 Hz.
- `duration` is the duration of one frame in the subscription's time base, which is 90 kHz because
  the plugin always subscribes with `90khz=1`. The frame rate is `90000 / duration` — 3600 is
  25 fps, 1800 is 50 fps. There is no halving rule; applying one is how a 50 fps broadcast was once
  published as 100 fps.

`meta` is deliberately **not** read. TVHeadend adds it to the outer message rather than to the
stream it belongs to, from inside the loop over the streams, so it is the global header of
whichever stream happened to be described last and there is no way to tell which. A field whose
owner cannot be determined is worse than no field.

## What the transport conditioner does

Three things, and deliberately no fourth:

- drops the DVB EIT PID;
- withholds the stream until a random access point, so the first sample a decoder sees is one it
  may begin at (bounded by time and by volume, so a broadcaster that never sets the indicator does
  not stall for ever);
- captures PAT and PMT — reassembling multi-packet sections properly — so a viewer joining a
  channel already running is given the tables its decoder needs.

It does not judge the video and does not describe the media. That comes from HTSP.

## Permissions

The configured TVHeadend account should have **administrator** rights: `service/streams` is
restricted to administrators, and it is the only source of the `es_index` → PID mapping. Without
it live TV still plays, but the stream is published undescribed and Jellyfin probes it on every
tune.

An account carrying TVHeadend's *anonymise* right cannot identify the service behind a channel
that maps to more than one, with the same consequence.

## Known external issues

`LiveTvMediaSourceProvider.Normalize` in Jellyfin overwrites `IsInterlaced` to `true` on the video
stream of every external live TV service, whatever the plugin reported. Device profiles keying on
interlacing may therefore choose transcoding unnecessarily. This is a server bug; no workaround is
built here, because a plugin-side hack would only hide it.
