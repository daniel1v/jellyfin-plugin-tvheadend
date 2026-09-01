# Development

Building, testing and releasing TVHeadend EX. For how the plugin actually works, see
[architecture.md](architecture.md).

## Build

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```sh
dotnet publish TVHeadEnd/TVHeadEnd.csproj --configuration Release --output bin
```

Copy `bin/TVHeadEnd.dll`, `bin/TVHeadEnd.Core.dll` and `bin/Tvheadend.Htsp.dll` into a folder under
`plugins/` in your Jellyfin install (creating it if it is not there). Only those three assemblies:
a publish also emits the Jellyfin framework assemblies the plugin compiled against, and those
belong to the server.

## Three projects, and the direction between them

```
TVHeadEnd  ──>  TVHeadEnd.Core      what a broadcast, a recording and a transport stream are
     └────────>  Tvheadend.Htsp     how to talk to a TVHeadend server
```

`TVHeadEnd.Core` has no project reference and no runtime package: there is nothing in it to reach
a Jellyfin type, an HTSP message or an ASP.NET request with, which is what makes "the core does not
know about the host" a fact rather than an intention. `Tvheadend.Htsp` does not reference the core
either, so it stays extractable on its own. `TVHeadEnd` is the only project that knows Jellyfin
exists, and it is where the two halves are joined — the HTSP wire format becomes a core `DvrEntry`
in `Tvheadend/Mapping`, and a core `DvrEntry` becomes a Jellyfin timer or recording in `LiveTv`.

Internal namespaces, classes and project directories are still spelled `TVHeadEnd`. That is
deliberate — the plugin's public identity changed, its source layout did not, and renaming it
would be diff without a reader.

## Architecture boundaries

Inside `TVHeadEnd` the same direction is drawn again, between areas rather than projects. Each one
answers a different question, and the whole point is that the answers do not mix.

```
TVHeadEnd.Core                  what a broadcast, a recording and a transport stream are
Tvheadend.Htsp                  how to talk to a TVHeadend server

TVHeadEnd/Tvheadend             the TVHeadend adapter: HTSP, TVHeadend's HTTP, catalogs, mapping
TVHeadEnd/LiveTv                Jellyfin's live TV vocabulary
TVHeadEnd/Recordings            Jellyfin's recording vocabulary
TVHeadEnd/Playback              opening and describing what is played
TVHeadEnd/Compatibility         playback rules that are about clients rather than about Jellyfin
TVHeadEnd/Compatibility/Jellyfin12   this server version's names, workarounds and hooks
TVHeadEnd/Infrastructure        technical machinery, principally the live ring buffer
TVHeadEnd/Configuration         the only bridge to the plugin's stored settings
TVHeadEnd/ServiceRegistrator    the composition root
```

The TVHeadend adapter turns wire messages into core facts and never learns what a host wanted; the
Jellyfin-facing areas turn core facts into what Jellyfin asked for and never speak HTSP. Where a
rule exists only because of *this* server version — a codec spelling, a container name, an MVC
filter, a date that makes the channel manager rewrite an item — it lives under `Jellyfin12`, so
that the next version's differences have somewhere to go.

### The rules that are tested

These are not conventions. `TVHeadEnd.Tests/Architecture` fails the build if one is crossed:

- **The core is built on the base class library and nothing else.** No project reference, no
  runtime package, and no compiled reference to Jellyfin, ASP.NET, SkiaSharp or the HTSP client.
- **The core does not speak the host's codec vocabulary.** `h264`, `mp2`, `mpegts` and the rest are
  FFmpeg's names for things; the core carries an `ElementaryStreamCodec` and
  `Compatibility/Jellyfin12/JellyfinCodecNames` is the one place that translates. The four-character
  identifiers a registration descriptor puts *in* the stream — `HEVC`, `AC-3`, `DTS1` — are
  broadcast facts and stay in the core.
- **Those names have one owner.** A second copy is a second answer waiting to disagree.
- **`TVHeadEnd/Tvheadend` does not reference Jellyfin**, the Jellyfin-facing areas, or the plugin's
  configuration object.
- **Only `Plugin.cs` and `Configuration/` touch `Plugin.Instance`.** Everything else is handed what
  it needs, which is what lets any of it be stood up in a test.
- **The two Jellyfin entry points do not hold each other.** `RecordingsChannel` once needed
  `LiveTvService` to list a recording; they answer different interfaces and share only what is
  behind them.
- **Everything the composition root registers can be constructed**, and all of it is a singleton.
  A missing registration otherwise surfaces as an empty channel list on somebody's server.
- **`Tests/Core` tests the core** — no Jellyfin, no HTSP, no adapter.

The rules forbid dependencies, not arrangements: moving a type between folders breaks none of them.

## Test and check

The build enforces the shared Jellyfin analyzer set (StyleCop, .NET code analysis,
SerilogAnalyzer and MultithreadingAnalyzer) with `TreatWarningsAsErrors`, so warnings fail the
build. Before pushing:

```sh
dotnet format TVHeadEnd.slnx --verify-no-changes
dotnet build TVHeadEnd.slnx -c Release
dotnet test TVHeadEnd.slnx -c Release
```

`dotnet format TVHeadEnd.slnx` without the flag applies the fixes it can.

## Deploying to a local test server

`tools/deploy-dev.ps1` publishes into a local Jellyfin 12 instance, restarts it and confirms
through the API that the plugin loaded. The script's own header explains the two things it exists
to get right; both have broken an instance before.

## Releasing

This repository packages and publishes itself. Bump the version in `build.yaml` and
`Directory.Build.props`, describe the release in the `changelog` block of `build.yaml`, then:

```powershell
& .\tools\release.ps1 -Publish
git commit -am "Publish 14.0.0.4" && git push
```

`-Publish` creates the GitHub release as well as the package. Without it the script only leaves
the zip in `dist/` and updates `manifest.json`, which is what you want when checking what a
release would contain.

`tools/release.ps1` takes every package detail from `build.yaml`, so the zip, the `meta.json`
inside it and the `manifest.json` entry cannot drift apart. The checksum Jellyfin verifies is the
MD5 of the zip, which is why the manifest is written after the zip is final — and why the script
uploads exactly the file it produced rather than leaving that to be done by hand.

Both `build.yaml` and `manifest.json` are read as UTF-8 explicitly. Windows PowerShell 5.1 reads
in the system ANSI code page by default, which turned every em dash into `â€”` on its way into the
manifest, and then into something worse the next time that entry was carried forward.

`build.yaml` has no `imageUrl`. The plugin has no artwork of its own, and the one it used to point
at is the Jellyfin project's own TVHeadend logo — borrowing that back would undo the point of
having a separate identity. The key is optional; add it if artwork ever exists.

## Releases are alphas

Every release is an alpha, and the script marks it as a GitHub prerelease itself rather than
leaving the flag to be remembered. That flag and the release title are the only places the word
can appear: Jellyfin parses a manifest version with `Version.Parse`, so a version cannot carry an
`-alpha` suffix. Use the fourth component for the alpha number.

Alphas accumulate. A published one stays published when the next supersedes it, and a defect found
in one afterwards is not a reason to remove it — being unproven is what the label already says.
Each release adds an entry to `manifest.json` alongside the ones before it, which the script does
on its own.

Release notes are written for the person installing the plugin, not for the person who wrote it:
what changed for them, what they have to do, a few short paragraphs. Being funny is fine. Internal
mechanics belong in the commit message and in [architecture.md](architecture.md), which stays as
thorough as it is.

### A version history belongs to the plugin that made it

`manifest.json` lists nothing before 14.0.0.4, because nothing before it was TVHeadend EX.
14.0.0.1 through 14.0.0.3 were built and published while this fork still carried the official
plugin's GUID; carrying them into the EX manifest offered them under an identity they were never
built with. A server installing one would get an assembly naming the old GUID, drop out of this
plugin's update path, and report itself as a different plugin.

The GitHub releases for those versions stay published — a published alpha is not withdrawn, and
they are still what was released, under the name they were released as. Only the EX update feed
does not claim them.

`tools/release.ps1` carries earlier versions forward **only from a manifest with the same GUID**,
and warns when it skips one. A GUID change now starts a fresh history rather than adopting
somebody else's.
