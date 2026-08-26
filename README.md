<h1 align="center">Jellyfin TVHeadend Plugin</h1>
<h3 align="center">An alpha fork of the <a href="https://github.com/jellyfin/jellyfin-plugin-tvheadend">Jellyfin project's plugin</a></h3>

<p align="center">
<img alt="Plugin Banner" src="https://raw.githubusercontent.com/jellyfin/jellyfin-ux/master/plugins/SVG/jellyfin-plugin-tvheadend.svg?sanitize=true"/>
<br/>
<br/>
<a href="https://github.com/daniel1v/jellyfin-plugin-tvheadend/releases">
<img alt="Latest alpha" src="https://img.shields.io/github/v/release/daniel1v/jellyfin-plugin-tvheadend?include_prereleases&amp;label=alpha"/>
</a>
<a href="https://github.com/daniel1v/jellyfin-plugin-tvheadend/blob/master/LICENSE">
<img alt="MIT License" src="https://img.shields.io/github/license/daniel1v/jellyfin-plugin-tvheadend.svg"/>
</a>
</p>

## About

This plugin allows you to manage TVHeadend from Jellyfin.

**This is a fork, and it publishes alphas.** Live TV and recording delivery have been reworked so
that both play on the official Jellyfin Android app; see [How live TV works](#how-live-tv-works)
and [docs/live-tv-architecture.md](docs/live-tv-architecture.md) for what that changed and why.
Releases are marked as GitHub prereleases and are not proven — read the changelog before
installing one.

Add it to Jellyfin as a plugin repository:

```
https://raw.githubusercontent.com/daniel1v/jellyfin-plugin-tvheadend/master/manifest.json
```


## How live TV works

A live channel is one HTTP request. TVHeadend serves the broadcast through its `pass` profile —
the original MPEG-TS, forwarded untouched — and that same stream is the only description of itself
the plugin needs.

The Program Map Table the broadcast carries is the table libavformat walks to decide what streams
the file has and in what order, so reading it here is not a second opinion about the stream; it is
the same source FFmpeg will use, read earlier. From it the plugin takes the stream order, what
medium each stream carries, the codec, the language and the hearing-impaired flag.

The plugin then:

- conditions the transport stream only as far as safety requires — it drops the DVB EIT, waits for
  a point a decoder can start at, and captures PAT/PMT so a viewer joining a channel already
  running gets the tables it needs;
- describes the result to Jellyfin as facts, leaving unknown fields unset.

Resolution, frame rate, bit rate and codec profile are **not** established. None of them is in a
PMT, none is needed for the playback decision, and an absent optional value is something Jellyfin
handles — a wrong one is not.

**Jellyfin** then decides — from the device profile the client sent — whether to direct play, remux
or transcode. The plugin expresses no opinion about clients, and offers exactly one media source per
channel.

There is no FFprobe in the live path, no second subscription, and no service or PID lookup.

### TVHeadend permissions

An ordinary **streaming** account. Live TV touches no administrative API.

Recordings additionally need whatever DVR rights the operations you use require.

### Known Jellyfin issues

- Jellyfin overwrites `IsInterlaced` to `true` on the video stream of **every** external live TV
  service, in `LiveTvMediaSourceProvider.Normalize`, regardless of what the plugin reported. Device
  profiles that key on interlacing may therefore choose transcoding unnecessarily. This is a server
  bug and is deliberately **not** worked around here; a plugin-side hack would only hide it.

For the details, see [docs/live-tv-architecture.md](docs/live-tv-architecture.md).

## Installation

Add this fork as a plugin repository in Jellyfin under *Dashboard → Plugins → Repositories*:

```
https://raw.githubusercontent.com/daniel1v/jellyfin-plugin-tvheadend/master/manifest.json
```

TVHeadend then appears in the plugin catalogue. It carries the same plugin GUID as the official
plugin, so it replaces rather than accompanies it — uninstall the official one first if it is
present. Requires Jellyfin 12 (`targetAbi` 12.0.0.0).

For the general mechanics, [see the official documentation](https://jellyfin.org/docs/general/server/plugins/index.html#installing).

## Build

1. To build this plugin you will need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

2. Build the plugin with the following command:

  ```sh
  dotnet publish TVHeadEnd/TVHeadEnd.csproj --configuration Release --output bin
  ```

3. Place `bin/TVHeadEnd.dll` in the `plugins/tvheadend` folder (you might need to create the folders) of your Jellyfin install.

## Development

The build enforces the shared Jellyfin analyzer set (StyleCop, .NET code analysis, SerilogAnalyzer and MultithreadingAnalyzer) with `TreatWarningsAsErrors`, so warnings fail the build.
Before pushing, check formatting and analyzer compliance:

```sh
dotnet format TVHeadEnd.slnx --verify-no-changes
dotnet build TVHeadEnd.slnx -c Release
```

`dotnet format TVHeadEnd.slnx` (without the flag) applies the fixes it can automatically.

## Releasing

Upstream publishes through the Jellyfin project's own infrastructure, which a fork cannot use, so
this repository packages and publishes itself. Bump the version in `build.yaml` and
`Directory.Build.props`, describe the release in the `changelog` block of `build.yaml`, then:

```powershell
& .\tools\release.ps1 -Publish
git commit -am "Publish 14.0.0.0" && git push
```

`-Publish` creates the GitHub release as well as the package. Without it the script only leaves the
zip in `dist/` and updates `manifest.json`, which is what you want when checking what a release would
contain.

`tools/release.ps1` takes every package detail from `build.yaml`, so the zip, the `meta.json` inside
it and the `manifest.json` entry cannot drift apart. The checksum Jellyfin verifies is the MD5 of the
zip, which is why the manifest is written after the zip is final — and why the script uploads exactly
the file it produced rather than leaving that to be done by hand. Upstream's route via
[JPRM](https://github.com/oddstr13/jellyfin-plugin-repository-manager) remains an alternative.

### Alpha releases

Every release this fork makes is an alpha, and the script marks it as a GitHub prerelease itself
rather than leaving the flag to be remembered. That flag, the plugin name and the release title are
the only places the word can appear: Jellyfin parses a manifest version with `Version.Parse`, so a
version cannot carry a `-alpha` suffix. Use the fourth component for the alpha number.

Alphas accumulate. A published one stays published when the next supersedes it, and a defect found
in one afterwards is not a reason to remove it — being unproven is what the label already says. Each
release adds an entry to `manifest.json` alongside the ones before it, which the script does on its
own.

The exception was a one-off: everything up to and including 14.0.0.0 was withdrawn when the alpha
line started, because each of those was broken in a way only found after publishing and none was
worth installing. The tags remain, so nothing is lost from the history; only the downloads are gone.
That was a clean slate, not a policy.



## Contributing

This is a personal fork. Anything that is not about the changes described above belongs upstream,
at [jellyfin/jellyfin-plugin-tvheadend](https://github.com/jellyfin/jellyfin-plugin-tvheadend),
under the Jellyfin project's
[contributing guidelines](https://github.com/jellyfin/.github/blob/master/CONTRIBUTING.md) — the
plugin is theirs and fixes are worth more there.

Issues about what this fork changed are welcome here. If you are working on the live TV or
recording path, read [docs/live-tv-architecture.md](docs/live-tv-architecture.md) first: most of
it records a measurement or a failure that a reasonable-looking change would reintroduce.


## Licence

This plugins code and packages are distributed under the MIT License. See [LICENSE](./LICENSE) for more information.
