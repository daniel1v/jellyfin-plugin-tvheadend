# TVHeadend stream profiles

The plugin delivers a channel through one of three roles. Each role is backed by a TVHeadend
stream profile that the administrator creates; the plugin never creates or modifies TVHeadend
configuration. Only the native role is required.

Profiles are configured by name in the plugin settings. Where the plugin has permission to read
`/api/profile/list`, the settings offer the existing profiles as a list; otherwise the name can be
typed freely.

## Roles

### Native — required

The broadcast as received, with no re-coding.

| | |
|---|---|
| Default profile name | `pass` |
| Use case | Every channel, always offered first |
| Output | Whatever the broadcast is |
| TVHeadend permission | Streaming |

`pass` forwards the transport stream untouched, which is what makes direct play possible and what
every stored channel description is taken from. Changing this setting invalidates all stored
descriptions, because a different profile can change the container and the elementary streams.

### Mpeg2H264Compatibility — optional

An H.264 rendering of broadcasts whose codec many clients cannot decode. Offered *alongside* the
native stream, never instead of it: a client that can decode MPEG-2 keeps the broadcast.

| | |
|---|---|
| Suggested profile name | `jellyfin-h264` |
| Use case | SD channels carrying MPEG-2 video |
| Container | MPEG-TS |
| Video | H.264 |
| Geometry | Preserve source resolution and frame rate |
| Deinterlacing | Recommended — SD MPEG-2 broadcasts are usually interlaced |
| Audio / subtitles | As configured in the profile; the plugin does not require any particular choice |
| Random access | Short, regular intervals recommended (about 1 s) |
| TVHeadend permission | Streaming, plus transcoding enabled for the user |

### H264IdrNormalization — optional

A genuine H.264 re-encode with real IDR access points, for broadcasts that signal random access
through recovery points and open GOPs without ever sending an IDR. Used only for clients known to
be unable to cold-start such a stream.

| | |
|---|---|
| Suggested profile name | `jellyfin-idr` |
| Use case | DVB broadcasts with recovery-point-only random access |
| Container | MPEG-TS |
| Video | H.264, genuinely re-encoded — a remux does not help, because the source frames are the problem |
| Geometry | Preserve source resolution and frame rate |
| Deinterlacing | Only if the source is interlaced; do not deinterlace progressive HD |
| Audio / subtitles | As configured in the profile |
| Random access | IDR every ~1 s (`keyint` at about the frame rate, closed GOP) |
| TVHeadend permission | Streaming, plus transcoding enabled for the user |

A stream that merely copies the video will not satisfy this role. The plugin validates the output
of an opened compatibility stream and disables the role for that channel if the contract is
violated, falling back to the native stream so Jellyfin can transcode normally.

## Recommended TVHeadend settings

Hardware acceleration is recommended but not required, and no particular implementation is
assumed. VAAPI and Intel Quick Sync are both known to work; software `libx264` is acceptable at
the cost of CPU.

A GOP length of about one second keeps channel changes short. Longer GOPs work but delay the
first picture by up to one GOP on every tune.

## Status shown in the settings

For each role the settings report:

- **configured** — a profile name is set
- **found / not found** — whether TVHeadend reports a profile of that name, where the plugin is
  permitted to list them
- **validated / not validated / invalid** — whether an opened stream of that role has been
  observed to satisfy the contract above

## Fallback behaviour

| Situation | Behaviour |
|---|---|
| Compatibility role not configured | Only the native stream is offered |
| Configured profile not found | Only the native stream is offered; the status shows *not found* |
| Opened output violates the contract | The role is disabled for that channel and the native stream is used |
| `H264IdrNormalization` not yet configured | The plugin's own transitional encoder is used instead |

The plugin-side encoder is a transitional measure. Once a TVHeadend `H264IdrNormalization`
profile has been configured and validated, it can be removed without touching playback policy or
the transport layer.

## Minimal and recommended setups

**Minimal** — native only. Everything plays that the client can decode; anything else is
transcoded by Jellyfin as usual.

**Recommended** — native, plus both compatibility roles. MPEG-2 channels reach H.264-only clients
without a Jellyfin transcode, and recovery-point broadcasts cold-start on clients that need an
IDR.
