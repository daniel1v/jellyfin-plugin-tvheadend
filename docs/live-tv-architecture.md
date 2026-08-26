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

It is a ceiling and not a wait, which is worth knowing before anyone tries to shorten it. FFmpeg
returns as soon as it has described the streams, so the two seconds are spent only when it cannot.
Measured on 2026-08-26: the first HLS segment was written after 1,772 ms at 100 ms, 1,807 ms at
250 ms, 1,998 ms at 500 ms and 1,481 ms at 1,000 ms -- noise, with no trend in it.

Lowering it does have an effect, just not that one. Below roughly a quarter of a second FFmpeg has
not seen an AC-3 frame and cannot state its sample rate, and a stream it is asked to *copy* rather
than re-encode has no parameters to write a header from: `sample rate not set`, then `Could not
write header (incorrect codec parameters ?)`, and the client is handed a 500 instead of a
playlist. The threshold moves by channel -- ZDF failed at 50 ms and worked at 100, Das Erste
failed at 100 and worked at 250 -- so it is not a number to sail close to.

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
cache. Every other request, including every other plugin's, passes through untouched. It answers
one other question as well -- see [How long a viewer waits](#how-long-a-viewer-waits) -- which is
why it is named for the requests it adjusts rather than for either rule.

Two viewers of such a channel who need different things get two subscriptions, because one media
source cannot both offer and withhold direct play. A channel whose H.264 does carry IDR pictures,
and everything that is not H.264, is shared by everyone as before.

## How long a viewer waits

Where direct play is not possible Jellyfin remuxes to HLS, and it holds the playlist back until a
minimum number of segments have been written. For a segmented live stream being copied its
defaults are three segments of three seconds: nine seconds of broadcast before anything appears.
That is not a guess -- a cold start measured 9.2 s, and a request stating those values explicitly
still measures 9.4 s today.

Both are ordinary query parameters of Jellyfin's own HLS controller, `MinSegments` and
`SegmentLength`. So on a video playlist request naming a live stream this plugin opened, the same
middleware sets them to one. Measured after the change: 1.9 s on ZDF, 2.5 s on Das Erste, 2.3 s on
RTL, against 9.4 s before.

Only where the client said nothing. A client that states either value is stating it for a reason
-- Jellyfin gives Apple devices six-second segments by its own rules -- and trading one client's
playback for another's startup is not this plugin's decision. An explicit value is left exactly as
it arrived, and a client that states one of the two is not assumed to have meant the other.

Only the playlists, too. Segment requests are not adjusted: by the time one is fetched the playlist
naming it has already been released. Radio takes Jellyfin's audio route and is left alone, because
the nine seconds being cut here is the wait for video segments.

## Artwork

Three kinds — a channel logo, an EPG programme image, a recording's poster — and one problem
between them. TVHeadend serves its own images from `/imagecache/N`, behind the same authentication
as everything else it serves. Jellyfin is handed an image URL and fetches it with an HTTP client of
its own, which knows nothing of TVHeadend, so it received 401 and the item was blank. Measured
against a real server: anonymous 401, authenticated 200 and 4,971 bytes for the same path.

Credentials in the URL do not fix it, although an earlier version tried. **`HttpClient` ignores the
userinfo component of a URI** and sends no `Authorization` header for it, so
`http://user:pass@host/imagecache/1` fails with exactly the same 401. What it did achieve was
writing the TVHeadend password into Jellyfin's database as an image path, and into the log on every
failed fetch.

So an image that lives on TVHeadend is fetched here instead, and published as
`/TVHeadend/Artwork/{token}` — an address on Jellyfin, which needs no credentials. Because the
published address differs from the stored one, Jellyfin replaces the old path on the next refresh,
which is also what clears the stored passwords.

### Where each one comes from

| | Source | Notes |
|---|---|---|
| Channel | `channelIcon` on the HTSP channel | Kept in the channel catalog the connection maintains. |
| Programme | `image` on the EPG event | Read as the guide is read; the guide keeps no catalog of its own. |
| Recording | `image` and `fanartImage` on the DVR entry | Taken from the entry, not rebuilt from the event it was made from: TVHeadend copies the artwork onto the entry when it schedules the recording, so it survives the event ageing out of the guide — which for a recording is most of its life. |

All three go through the same publisher, and all three are decided by the same rule.

### When there is none

Broadcast DVB EIT has no field for a picture, so on an over-the-air guide there is nothing to
publish -- measured against a real server: 84 DVR entries and 300 EPG events, none carrying an
image. A recording and a guide entry fall back to the logo of the channel they came from, which is
at least true in that it says which broadcaster it was. A setting turns that off.

**The logo is padded into a square before it is served.** Jellyfin draws an item's picture at
whatever shape the view wants -- a tall poster here, a wide thumbnail there -- and fills the frame
with it, so a 400x240 logo handed over as it stands is enlarged to the size of the tile. A square
survives both: a 2:3 crop keeps the middle two-thirds of the width, a 16:9 crop the middle
nine-sixteenths of the height, and the logo is drawn well inside both. It uses 55 per cent of the
width and 45 per cent of the height at most, so a 400x240 logo becomes a 727x727 square occupying
18 per cent of its area. Nothing is enlarged; the margin grows and Jellyfin scales the whole down.

All 124 channel logos on the test server are exactly 400x240. The wide and tall pictures a user
sees are the frames, not the logos, which is why no single aspect ratio could have worked.

That is what the second route is for. `/TVHeadend/Artwork/{token}` serves a picture as it is, which
is what a broadcaster's own artwork wants; `/TVHeadend/Artwork/{token}/poster` pads it into the
square, which is what a logo wants -- including a channel's own, since Jellyfin draws that edge to
edge in its tile and a logo filling its frame reads as a mistake rather than as a logo.

The path still says "poster" from when padding meant a 2:3 frame. Changing it would change every
published address, and a recording keeps the first picture it is given, so the rename would cost
everybody a reset to buy a better word.

### Which slots get filled

| | Slots available | What is published |
|---|---|---|
| Channel | `ImageUrl` | the logo, padded |
| Guide entry | `ImageUrl`, `ThumbImageUrl`, `LogoImageUrl`, `BackdropImageUrl` | its own artwork as primary; failing that the padded square as **both primary and thumb** |
| Recording | `ImageUrl` only | its own artwork; failing that the padded square |

The thumb matters. Jellyfin's live TV cards are built with `preferThumb: "auto"`, which the card
builder resolves to `shape === "backdrop" || shape === "overflowBackdrop"` -- true for the wide
cards those galleries use. So a programme carrying only a primary image still shows the
placeholder in "On Now", which is what happened when only the logo slot was filled. Both slots are
filled, because that holds whichever way the option resolves.

The logo slot is deliberately left empty: that is where a programme's *own* logo belongs, and the
channel's is not that.

A recording has one slot and no choice, which is also why `fanartImage` is read from the DVR entry
and goes nowhere.

### Getting a changed picture to an item that has one

A guide entry needs nothing. `GuideManager.UpdateImage` compares the stored path against the new
address, replaces it when they differ, and removes it when the plugin stops publishing one, so
channels and programmes correct themselves on the next refresh.

A recording cannot. `ChannelManager` gives a channel item an image only when it has none, so the
first picture a recording is given is the one it keeps. The settings page has a button that forgets
them, which also discards the cached listing -- without that, Jellyfin serves the listing from
cache for up to three hours, never asks the plugin, and the recordings sit there with no picture
at all.


### The credential rule

**TVHeadend credentials only ever reach the configured TVHeadend endpoint.** That is a property of
how the address is built rather than a check that could be forgotten: the token carries a *path*,
the controller puts the configured base URL in front of it, and there is no input that makes it
produce a different host.

A reference that points somewhere else — an EPG provider's own artwork, on some host of its own — is
published unchanged and fetched by Jellyfin directly. It needs no credentials, and this is what
keeps it from being sent any.

The token names a path below the TVHeadend web root and nothing else. It is not a URL, and it is
signed with a secret only this server knows, so nobody can mint one. The path is checked again as
it comes back out — absolute references, `..`, and anything carrying a query or a fragment are
refused — because that is the line that decides where the credentials go, and it should not depend
on every caller elsewhere having got it right.

The route is anonymous, as the recordings route is, because Jellyfin's image pipeline carries no
session. An unguessable address is what stands in for one.

## Recordings

A separate path, and deliberately less clever than it was.

A recording is described from a sample of its opening, because analysing the whole file means
reading gigabytes across the network. That sample establishes what the recording contains — and
nothing else. It is explicitly **not** used to conclude that the recording lacks something: a
bounded probe cannot establish an absence, and an earlier version used exactly that inference to
withhold direct play and serve a re-encode for whole recordings.

HEAD and GET therefore describe the same resource identically: one method, proxying TVHeadend,
advertising the same length, the same range support and the same content type to both.

The address a recording is served from says nothing about its container -- `/TVHeadend/Recordings/{token}/stream`. What a recording actually is follows TVHeadend's DVR profile, which a WebTV
setting makes Matroska, and the answer arrives with the analysis long after the address is built.
The older `.../stream.ts` spelling still answers, on the same method rather than a second one,
because it is written into media sources people already have.

The content type is TVHeadend's own where it states one, and `application/octet-stream` where it
does not. `video/mp2t` unconditionally was the same overstatement the `.ts` was.

## One contract for live TV and recordings

Both publish the same shape:

| | Live TV | Recording |
|---|---|---|
| `Protocol` | `File` | `File` |
| `Path` | the ring buffer file | `TVHeadend/Recordings/{id}`, a name for a file nobody opens |
| `EncoderProtocol` | `Http` | `Http` |
| `EncoderPath` | Jellyfin's own live stream URL | this plugin's recording proxy |
| `Container` | `ts` | whatever the analysis found, `ts` for MPEG-TS |

The split is what lets one media source say two true things at once. A client is told the plainest
thing there is -- a whole, seekable file it may play as it stands -- while the server reaches the
bytes over HTTP. `EncodingHelper.AttachMediaSourceInfo` prefers `EncoderPath` and `EncoderProtocol`
whenever both are set, so `state.InputProtocol` becomes HTTP and Jellyfin never tries to open the
path itself.

Saying `File` is not decoration. `StreamBuilder.SortMediaSources` ranks a direct-played file above
everything else -- *"nothing beats direct playing a file"* is the comment in Jellyfin's own source
-- and this is what puts a channel and a recording on the same footing as any other item in the
library.

For a recording the request lands in `GetStaticRemoteStreamResult`, which forwards the client's
`Range` header upstream and returns the upstream status, `Content-Range`, `Content-Length` and
`Accept-Ranges` unaltered, so seeking behaves exactly as it did. For live TV it lands one branch
earlier, on `ProgressiveFileStream(liveStreamInfo.GetStream())` -- see [Naming the stream a client
did not](#naming-the-stream-a-client-did-not).

The path a recording carries is deliberately not shaped like a real one. Nothing on this server
reads it, but a client configured for direct file access resolves what it is given against its own
filesystem, and a plausible-looking path is the one that could accidentally resolve to something
else.

## Naming the stream a client did not

A static video request reaches `ProgressiveFileStream` only when `state.DirectStreamProvider` is
set, and `StreamingHelpers.GetStreamingState` sets that in one branch only: the one taken when the
request names a `LiveStreamId`. Some clients send only the media source. Without the identifier
Jellyfin has no provider, falls through, and serves the ring buffer file directly -- which ends at
whatever had been written when it was opened.

So the middleware supplies it, from a registry of which live stream a media source identifier
stands for, written when the stream is opened or reused. Two things have to agree before anything
is added: the registry has to hold a stream for that media source, and
`mediaSourceManager.GetLiveStreamInfo` has to return *the same object* for the identifier that
stream carries. Not an equal one -- the same one. Nothing is inferred from a client name, a user
agent, a channel name or the order things happened in, and the references are weak, so an entry
never keeps a tuner open.

## Two spellings of one container

MPEG-TS is `ts` to Jellyfin's probe normaliser and to Android TV, and `mpegts` to FFprobe and to
the mobile app. The comparison between a device profile and a media source is a literal string one,
and a source can carry only one name -- so the profile is the side that has to say both.

On a `PlaybackInfo` request for one of this plugin's own items, the device profile the client sent
gains the missing spelling: direct play profiles, container profiles and the container-bound codec
profiles. Transcoding profiles are left alone, because there the container names what the client
wants produced rather than what it can read. A list already naming both, naming neither, or written
as a negative list is untouched, and every other item in the library passes through with the profile
exactly as its client sent it.

Whose an item is comes from the library -- a live channel records the service that produced it --
and never from the request. A display name, a path fragment or a prefix would all be coincidences
waiting to happen.

The source itself is `ts` and stays `ts`. Whenever the server has hardware acceleration configured
the container is passed to FFmpeg as `-f`, but never verbatim: `EncodingHelper.GetInputFormat`
translates on the way, so `ts` arrives as **`-f mpegts`**. `-f ts` is not what is produced and not
what FFmpeg is asked for -- it has no demuxer by that name. That translation is the whole reason
the canonical name can be the one clients use.

Naming both spellings at once was tried and broke playback outright on such a server, because
`mpegts,ts` is not in the translation table and so reached FFmpeg unchanged: `-f mpegts,ts` is not
a demuxer either.



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

The remaining cost is startup latency rather than quality: the fallback copies both video and
audio, so what the viewer sees is the broadcast unaltered. Most of that latency has since been
taken out of the HLS path -- see [How long a viewer waits](#how-long-a-viewer-waits).
