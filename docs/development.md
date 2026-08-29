# Development

Building, testing and releasing TVHeadend EX. For how the plugin actually works, see
[architecture.md](architecture.md).

## Build

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```sh
dotnet publish TVHeadEnd/TVHeadEnd.csproj --configuration Release --output bin
```

Copy `bin/TVHeadEnd.dll` and `bin/Tvheadend.Htsp.dll` into a folder under `plugins/` in your
Jellyfin install (creating it if it is not there). Only those two assemblies: a publish also emits
the Jellyfin framework assemblies the plugin compiled against, and those belong to the server.

Internal namespaces, classes and project directories are still spelled `TVHeadEnd`. That is
deliberate — the plugin's public identity changed, its source layout did not, and renaming it
would be diff without a reader.

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

### Versions published before the rename

`manifest.json` still lists 14.0.0.1 through 14.0.0.3. Those were built under the old plugin GUID,
so a server that installs one of them from this repository ends up with a plugin that identifies
itself as the old fork and stops being offered TVHeadend EX updates. They are kept because a
published alpha is not withdrawn, not because installing one is a good idea. Install the newest.
