# TVHeadend EX

**An independent, unofficial TVHeadend integration for Jellyfin.**

[![Latest alpha](https://img.shields.io/github/v/release/daniel1v/jellyfin-plugin-tvheadend?include_prereleases&label=alpha)](https://github.com/daniel1v/jellyfin-plugin-tvheadend/releases)
[![MIT License](https://img.shields.io/github/license/daniel1v/jellyfin-plugin-tvheadend.svg)](LICENSE)

TVHeadend EX started life as a fork of the official
[jellyfin/jellyfin-plugin-tvheadend](https://github.com/jellyfin/jellyfin-plugin-tvheadend) and has
since gone its own way: its own playback architecture, its own release line, and — as of this
version — its own plugin GUID, so it installs and updates as a plugin in its own right.

It is **not** supported by, endorsed by, or affiliated with the Jellyfin project or the TVHeadend
project. It is not an official successor to the plugin it came from.

## Why TVHeadend EX

Live TV worked, in the sense that it usually started eventually and usually on one of your devices.
The differences are mostly about the gap between "usually" and "reliably":

- **Faster cold start.** Tuning a channel takes a couple of seconds rather than the best part of
  ten.
- **Direct play or a remux wherever possible.** The plugin describes the stream honestly enough
  that Jellyfin can copy it through instead of re-encoding a broadcast it did not need to touch.
- **The awkward DVB and Android cases handled on purpose.** Broadcasters that never send the frame
  a phone waits for, streams a client will not accept as described — these are dealt with
  deliberately rather than left to chance.
- **Artwork that shows up.** Channel logos, programme images and recording artwork are fetched with
  the credentials TVHeadend actually wants, and a guide entry or recording with no picture of its
  own can fall back to its channel's logo. There is a switch if you would rather it did not.
- **Recordings that play properly**, seeking included.

If you want to know how any of that works, it is written down in [docs/architecture.md](docs/architecture.md).

## Project status

This is a personal hobby project, built first and foremost for the author's own Jellyfin and
TVHeadend setup. Please take that at face value:

- Every release is an **alpha**. They are marked as prereleases and they mean it.
- There is no support commitment, no LTS, no promised maintenance window, no roadmap and no release
  schedule.
- There is no claim to cover every TVHeadend configuration, every tuner or every Jellyfin client.
  Plenty of them have never been near this code.

A significant part of the design, implementation and review here is done with **AI-assisted
development tools**. That is worth saying plainly rather than hiding. There is a test suite, changes
are reviewed, and decisions that cost something to get wrong are written down in the architecture
notes — but none of that turns a hobby project into a professionally supported product, and it
should not be mistaken for one.

Bug reports and fixes are welcome. Guarantees are not on offer.

## Tested setup

Regularly exercised against:

- Jellyfin Android
- Jellyfin Android TV
- Jellyfin Web
- mostly **German free-to-air DVB channels**

That is the testing matrix, not a claim of coverage: not every German free-to-air channel is
tested either. Other clients, countries, broadcasters and DVB or IPTV configurations may well work
— several probably do — but they are not part of what gets checked before a release.

## Installation

Requires Jellyfin 12 (`targetAbi` 12.0.0.0).

Add this repository in Jellyfin under *Dashboard → Plugins → Repositories*:

```
https://raw.githubusercontent.com/daniel1v/jellyfin-plugin-tvheadend/master/manifest.json
```

*TVHeadend EX* then appears in the plugin catalogue. Install it, restart Jellyfin, and configure it
under *Dashboard → Plugins → TVHeadend EX*.

**Upgrading from an earlier build of this fork.** Versions up to 14.0.0.3 shipped under the official
TVHeadend plugin's GUID, so Jellyfin cannot see the new plugin as an update of the old one.
Uninstall the old *TVHeadend* plugin first, then install *TVHeadend EX*. Your settings survive: they
live in a configuration file named after the assembly, which did not change.

Both plugins also carry an assembly called `TVHeadEnd.dll`, and Jellyfin refuses to load two copies
of the same assembly — so TVHeadend EX and the official plugin cannot be installed side by side on
one server, even though they are now separate entries in the catalogue.

For the general mechanics, [see the Jellyfin documentation](https://jellyfin.org/docs/general/server/plugins/index.html#installing).

## TVHeadend permissions

An ordinary **streaming** account is enough for live TV. Nothing here touches an administrative API.

Recordings additionally need whatever DVR rights the operations you use require.

## Origin and credits

TVHeadend EX exists because of the official
[jellyfin/jellyfin-plugin-tvheadend](https://github.com/jellyfin/jellyfin-plugin-tvheadend), which
is the foundation this was built on and remains the work of the Jellyfin project and its
contributors. Thank you.

It is now developed and released independently, and it is a different plugin, not a newer version of
that one. Two things follow from that:

- **Do not report TVHeadend EX problems to the official plugin's maintainers.** They did not write
  this and cannot fix it. Report them [here](https://github.com/daniel1v/jellyfin-plugin-tvheadend/issues).
- Jellyfin and TVHeadend are independent projects and carry no responsibility for TVHeadend EX.

If your issue is with the official plugin rather than this one, it belongs
[upstream](https://github.com/jellyfin/jellyfin-plugin-tvheadend), under the Jellyfin project's
[contributing guidelines](https://github.com/jellyfin/.github/blob/master/CONTRIBUTING.md).

## Contributing

Issues and pull requests about TVHeadend EX are welcome here. Before touching the live TV or
recording path, read [docs/architecture.md](docs/architecture.md) first: most of it records a
measurement or a failure that a reasonable-looking change would reintroduce.

## Development

Build, test and release instructions live in [docs/development.md](docs/development.md).
How the plugin works lives in [docs/architecture.md](docs/architecture.md).

## Licence

MIT. See [LICENSE](LICENSE).
