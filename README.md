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

This fork reworks live TV delivery so that channels direct play on the official Jellyfin Android app.
Channels are served from a shared, conditioned MPEG-TS buffer rather than the raw TVHeadend URL, and
broadcasts that signal random access without IDR frames — which common device decoders refuse to start
on, giving audio but a black picture — have their video re-encoded. Every other channel is passed
through untouched. See the [release notes](https://github.com/daniel1v/jellyfin-plugin-tvheadend/releases)
for the full list.

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
