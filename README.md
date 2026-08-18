<h1 align="center">Jellyfin TVHeadend Plugin</h1>
<h3 align="center">Part of the <a href="https://jellyfin.org">Jellyfin Project</a></h3>

<p align="center">
<img alt="Plugin Banner" src="https://raw.githubusercontent.com/jellyfin/jellyfin-ux/master/plugins/SVG/jellyfin-plugin-tvheadend.svg?sanitize=true"/>
<br/>
<br/>
<a href="https://github.com/jellyfin/jellyfin-plugin-tvheadend/actions/workflows/build.yaml">
<img alt="GitHub Workflow Status" src="https://img.shields.io/github/actions/workflow/status/jellyfin/jellyfin-plugin-tvheadend/build.yaml?branch=master"/>
</a>
<a href="https://github.com/jellyfin/jellyfin-plugin-tvheadend">
<img alt="MIT License" src="https://img.shields.io/github/license/jellyfin/jellyfin-plugin-tvheadend.svg"/>
</a>
<a href="https://github.com/jellyfin/jellyfin-plugin-tvheadend/releases">
<img alt="Current Release" src="https://img.shields.io/github/release/jellyfin/jellyfin-plugin-tvheadend.svg"/>
</a>
</p>

## About

This plugin allows you to manage TVHeadend from Jellyfin.

## How live TV works

TVHeadend already parses every broadcast it tunes. This plugin uses that analysis instead of making
a second one of its own, which is why live TV needs no FFprobe at all.

A running channel has two halves, both on the same TVHeadend service:

- **HTTP, `profile=pass`** is the media path. TVHeadend forwards the original MPEG-TS untouched,
  with its own PCR, program tables and random access points intact. No TVHeadend transcoding and no
  compatibility profile is ever requested.
- **HTSP** is the description. The plugin subscribes to the same channel and immediately filters out
  every stream index, so TVHeadend keeps parsing and keeps describing the stream but never puts a
  frame of audio or video on that second socket. The subscription stays open for the life of the
  stream, so a broadcast that changes shape is re-described and the media source corrected.

The plugin then:

- conditions the transport stream only as far as safety requires — it drops the DVB EIT, waits for a
  random access point so a decoder can start, and captures PAT/PMT so a viewer joining a channel
  already running gets the tables it needs;
- places each elementary stream at the index FFmpeg will give it, by going from HTSP's `es_index`
  through the service's PID table to the position the delivered PMT puts it at;
- describes the result to Jellyfin as facts, leaving unknown fields unset.

**Jellyfin** then decides — from the device profile the client sent — whether to direct play, remux
or transcode. The plugin expresses no opinion about clients, and offers exactly one media source per
channel.

### TVHeadend permissions

The account configured here should have **administrator** rights.

Live TV plays without them, but the mapping from TVHeadend's `es_index` to a transport stream PID
comes from TVHeadend's `service/streams` API, which is restricted to administrators. Without it the
plugin cannot say which track sits at which index, so rather than guess it leaves the stream
undescribed and lets Jellyfin inspect it — costing a probe on every tune and losing the correct
track ordering. An account carrying TVHeadend's *anonymise* right is also unable to identify the
service behind a channel that maps to more than one, with the same consequence.

### Known Jellyfin issues

- Jellyfin overwrites `IsInterlaced` to `true` on the video stream of **every** external live TV
  service, in `LiveTvMediaSourceProvider.Normalize`, regardless of what the plugin reported. Device
  profiles that key on interlacing may therefore choose transcoding unnecessarily. This is a server
  bug and is deliberately **not** worked around here; a plugin-side hack would only hide it.

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
pwsh tools/release.ps1
gh release create v13.1.0.0 dist/tvheadend_13.1.0.0.zip --repo daniel1v/jellyfin-plugin-tvheadend
git commit -am "Publish 13.1.0.0" && git push
```

`tools/release.ps1` takes every package detail from `build.yaml`, so the zip, the `meta.json` inside
it and the `manifest.json` entry cannot drift apart. The checksum Jellyfin verifies is the MD5 of the
zip, which is why the manifest is written after the zip is final — upload exactly the file the script
produced. Upstream's route via [JPRM](https://github.com/oddstr13/jellyfin-plugin-repository-manager)
remains an alternative.

## Contributing

We welcome all contributions and pull requests! If you have a larger feature in mind please open an issue so we can discuss the implementation before you start.
In general refer to our [contributing guidelines](https://github.com/jellyfin/.github/blob/master/CONTRIBUTING.md) for further information.

## Licence

This plugins code and packages are distributed under the MIT License. See [LICENSE](./LICENSE) for more information.
