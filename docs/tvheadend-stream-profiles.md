# TVHeadend stream profiles

The plugin delivers a channel in one of two forms. Each is backed by a TVHeadend stream profile
that the administrator creates; the plugin never creates or modifies TVHeadend configuration.
Only the native profile is required.

Profiles are configured by name in the plugin settings. Where the plugin has permission to read
`/api/profile/list`, the settings offer the existing profiles as a list; otherwise the name can be
typed freely.

## How a form is chosen

The plugin does not decide what a client can play. It offers what it has, describes it factually,
and Jellyfin evaluates the offer against the device profile the client sent.

```
TVHeadend profiles → media source candidates → Jellyfin StreamBuilder → the best direct play
```

- The broadcast is always offered, and always first.
- A compatibility rendering is added only for a channel observed to carry MPEG-2 video, and only
  when a profile for it is configured and found.
- If the client can direct play the broadcast, it gets the broadcast — Jellyfin keeps the first
  source when both are equally playable.
- If it cannot, and it can direct play the rendering, it gets the rendering. TVHeadend does the
  encoding; Jellyfin does none.
- If it can play neither, Jellyfin transcodes, exactly as it would for any other library item.

## Roles

### Native — required

The broadcast as received, with no re-coding.

| | |
|---|---|
| Default profile name | `pass` |
| Use case | Every channel, always offered first |
| Container | MPEG-TS |
| Output | Whatever the broadcast is |
| TVHeadend permission | Streaming |

`pass` forwards the transport stream untouched, which is what makes direct play possible and what
every stored channel description is taken from. Changing this setting invalidates all stored
descriptions, because a different profile can change the container and the elementary streams.

### Mpeg2H264Compatibility — optional

An H.264 rendering of broadcasts whose codec many clients cannot decode. Offered *alongside* the
broadcast, never instead of it.

| | |
|---|---|
| Suggested profile name | `jellyfin-h264` |
| Use case | SD channels carrying MPEG-2 video |
| Container | Matroska |
| Video | H.264 |
| Geometry | Preserve source resolution |
| Deinterlacing | Recommended — SD MPEG-2 broadcasts are usually interlaced |
| Audio / subtitles | As configured in the profile; the plugin does not require any particular choice |
| TVHeadend permission | Streaming, plus transcoding enabled for the user |

Matroska because that is what TVHeadend's transcoder can currently produce; its libav muxer does
not emit usable MPEG-TS. The rendering is served through Jellyfin's live stream file endpoint,
which takes its content type from the container in the URL, so Matroska is announced correctly.

The output is checked the first time it is produced. A profile that copies the video, or produces
a different container, is marked invalid, closed without ever reaching the client, and not
offered again until it is corrected.

## Recommended TVHeadend settings

Hardware acceleration is recommended but not required, and no particular implementation is
assumed. VAAPI and Intel Quick Sync are both known to work; software `libx264` is acceptable at
the cost of CPU.

A GOP length of about one second keeps channel changes short.

## Status shown in the settings

For each role the settings report:

- **configured** — a profile name is set
- **found / not found** — whether TVHeadend reports a profile of that name, where the plugin is
  permitted to list them
- **validated / not validated / invalid** — whether an opened stream of that role has been
  observed to satisfy the contract above

Validation survives a restart and is discarded when the configured profile name changes.

## Fallback behaviour

| Situation | Behaviour |
|---|---|
| Compatibility role not configured | Only the broadcast is offered |
| Configured profile not found | Only the broadcast is offered; the status shows *not found* |
| Opened output violates the contract | The role is marked invalid and the stream is closed before publication |
| Client can play neither form | Jellyfin transcodes, as it does for any other item |

## Known client limitation: Jellyfin Android Mobile 2.7.x

Jellyfin Android Mobile 2.7.x currently cannot use the plugin's multi-source direct play
negotiation correctly, because of client-side `MediaSourceId` and `liveStreamId` handling.

Measured on 2.7.1:

- The client sends `MediaSourceId` equal to the channel's item identifier. Jellyfin filters the
  offered sources by that identifier *before* the device profile is evaluated, and no source
  carries it — deliberately, because a source that did would win the choice before any evaluation
  and would break the compatibility path for every other client.
- For a source with `Protocol = File`, the client builds its direct play URL without
  `liveStreamId`. Jellyfin then re-resolves the source from the provider rather than from the
  open stream, and ends up proxying its own root address — an HTML page, which the player reports
  as `UnrecognizedInputFormatException`.
- For a source with `Protocol = Http`, the client forces the MIME type
  `application/x-mpegURL` and parses the body as an HLS playlist, whatever it actually is.

The consequence on that client is that live TV may report no compatible stream, or fall back to a
Jellyfin transcode after two failed attempts. This is not worked around here: every available
workaround needs client detection or identifier tricks that would degrade the negotiation for
every other client.
