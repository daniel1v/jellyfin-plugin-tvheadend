<#
.SYNOPSIS
    Packages the plugin and adds it to manifest.json, the file this fork is consumed as a
    Jellyfin plugin repository through.

.DESCRIPTION
    Two phases, and the order between them is the point.

        & .\tools\release.ps1                     # prepare: build, pack, write the manifest
        # test the artefact in dist\, then:
        git commit -am "Publish <version>" ; git push
        # wait for CI to go green on that commit, then:
        & .\tools\release.ps1 -Publish            # publish what was prepared

    Preparing writes dist\tvheadend-ex_<version>.zip and updates manifest.json. Publishing
    uploads that zip and nothing else: it does not build, does not repack, does not re-stamp the
    timestamp and does not touch the manifest. Whatever was tested is what is released.

    That separation is not tidiness. The old script created the GitHub release before the
    manifest commit was pushed, so GitHub tagged whatever the remote happened to be at -- and
    v14.0.0.4's tag points at the commit before the one that describes it. Publishing now names
    the current HEAD as the release target explicitly, after checking that HEAD is pushed, so the
    tag, the commit carrying build.yaml and manifest.json, and the source of the published build
    are one thing.

    Everything about the package is read from build.yaml, so the zip, the meta.json inside it and
    the manifest entry cannot drift apart. Bump the version in build.yaml and
    Directory.Build.props first. -SkipBuild reuses an existing Release build.

    Every release this fork makes is an alpha and is marked as a GitHub prerelease, which is the
    only place the word can live: Jellyfin parses a manifest version with Version.Parse, so it
    cannot carry a "-alpha" suffix. The flag is set here rather than typed each time, because a
    flag that has to be remembered is one that eventually is not.

#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$Publish,
    [string]$RepositoryUrl = 'https://github.com/daniel1v/jellyfin-plugin-tvheadend'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $repo 'dist'

# Windows PowerShell writes UTF-8 with a byte order mark, which JSON parsers reject.
function Write-Json([string]$path, $value, [switch]$AsArray) {
    $json = $value | ConvertTo-Json -Depth 6
    if ($AsArray -and -not $json.TrimStart().StartsWith('[')) { $json = "[$json]" }
    [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Read-Utf8([string]$path) {
    return [System.IO.File]::ReadAllText($path, (New-Object System.Text.UTF8Encoding($false)))
}

# Explicitly UTF-8. Windows PowerShell 5.1 reads files in the system ANSI code page by default,
# which turned every em dash in a changelog into mojibake on its way into the manifest.
$yaml = [System.IO.File]::ReadAllLines((Join-Path $repo 'build.yaml'), (New-Object System.Text.UTF8Encoding($false)))
function Get-Scalar([string]$key, [switch]$Optional) {
    $line = $yaml | Where-Object { $_ -match "^$key\s*:" } | Select-Object -First 1
    if (-not $line) {
        if ($Optional) { return $null }
        throw "build.yaml has no '$key'"
    }
    return ($line -replace "^$key\s*:\s*", '').Trim('"', ' ')
}

# Optional, and currently absent: the plugin has no artwork of its own since it stopped
# presenting itself as the official plugin, and borrowing that one back would undo the point.
$imageUrl = Get-Scalar 'imageUrl' -Optional

$changelogStart = ($yaml | Select-String -Pattern '^changelog\s*:').LineNumber
$changelog = (($yaml[$changelogStart..($yaml.Count - 1)] | ForEach-Object { $_ -replace '^  ', '' }) -join "`n").Trim()

$version = Get-Scalar 'version'
$guid = Get-Scalar 'guid'
$zip = Join-Path $dist "tvheadend-ex_$version.zip"
$assetName = "tvheadend-ex_$version.zip"
$sourceUrl = "$RepositoryUrl/releases/download/v$version/$assetName"
$manifestPath = Join-Path $repo 'manifest.json'

# ---------------------------------------------------------------------------------------------
# Publish: release exactly what was prepared, or refuse.
# ---------------------------------------------------------------------------------------------
if ($Publish) {
    $repoSlug = ($RepositoryUrl -replace '^https://github\.com/', '')

    # 1. The artefact exists. Nothing is built or packed here -- a publish that can produce a zip
    #    is a publish that can produce a different one from the one that was tested.
    if (-not (Test-Path $zip)) {
        throw "No prepared package at $zip. Run .\tools\release.ps1 first, and test what it produces."
    }

    # 2. One version number. The assembly is built from Directory.Build.props and the package
    #    from build.yaml; two numbers for one release is two releases to everything reading them.
    $props = Read-Utf8 (Join-Path $repo 'Directory.Build.props')
    foreach ($element in @('Version', 'AssemblyVersion', 'FileVersion')) {
        if ($props -notmatch "<$element>$([regex]::Escape($version))</$element>") {
            throw "Directory.Build.props does not say <$element>$version</$element>."
        }
    }

    # 3, 4, 5. The manifest describes this package: this version first, this file's checksum, and
    #    the address the asset will actually have.
    $manifest = Read-Utf8 $manifestPath | ConvertFrom-Json
    $plugin = @($manifest) | Where-Object { $_.guid -eq $guid } | Select-Object -First 1
    if (-not $plugin) { throw "manifest.json has no entry for $guid." }

    $entry = @($plugin.versions) | Where-Object { $_.version -eq $version } | Select-Object -First 1
    if (-not $entry) { throw "manifest.json does not list version $version." }

    $checksum = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()
    if ($entry.checksum -ne $checksum) {
        throw "manifest.json records $($entry.checksum) for $version; $zip hashes to $checksum. Prepare again."
    }

    if ($entry.sourceUrl -ne $sourceUrl) {
        throw "manifest.json points $version at $($entry.sourceUrl); this release will publish $sourceUrl."
    }

    # 6, 7. The tag is about to be created on HEAD, so HEAD has to be what the manifest describes
    #    and has to already exist upstream. This is the check the old script did not make, and
    #    not making it is how v14.0.0.4 came to be tagged at the commit before its own manifest.
    $dirty = & git -C $repo status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'git status failed' }
    if ($dirty) { throw "The working tree has uncommitted changes:`n$($dirty -join "`n")" }

    $head = (& git -C $repo rev-parse HEAD).Trim()
    & git -C $repo fetch origin --quiet
    if ($LASTEXITCODE -ne 0) { throw 'git fetch failed' }
    $upstream = (& git -C $repo rev-parse '@{upstream}').Trim()
    if ($LASTEXITCODE -ne 0) { throw 'no upstream branch to compare against' }
    if ($head -ne $upstream) {
        throw "HEAD is $head and its upstream is $upstream. Push the release commit first."
    }

    # 8. Not already released. gh would refuse anyway, but it would refuse after uploading.
    & gh release view "v$version" --repo $repoSlug 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { throw "A release v$version already exists." }

    # Through a file, not an argument. Windows PowerShell re-splits the arguments it hands a
    # native program, so the quotation marks inside a changelog arrive as argument boundaries and
    # gh is asked to do something nobody wrote.
    $notes = Join-Path $dist "notes_$version.md"
    [System.IO.File]::WriteAllText($notes, $changelog, (New-Object System.Text.UTF8Encoding($false)))

    # 9. The target is named. Left to itself gh tags the remote's default branch as it finds it,
    #    which is only the right commit by luck.
    & gh release create "v$version" $zip `
        --repo $repoSlug `
        --target $head `
        --title "TVHeadend EX $version (alpha)" `
        --notes-file $notes `
        --prerelease
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }

    Write-Output "Release:  $RepositoryUrl/releases/tag/v$version (alpha)"
    Write-Output "Tag auf:  $head"
    Write-Output "Asset:    $assetName"
    Write-Output "MD5:      $checksum"
    return
}

# ---------------------------------------------------------------------------------------------
# Prepare: build, pack, write the manifest. Nothing is published.
# ---------------------------------------------------------------------------------------------
$timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

if (-not $SkipBuild) {
    & dotnet build (Join-Path $repo 'TVHeadEnd\TVHeadEnd.csproj') -c Release -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}

$stage = Join-Path $dist 'stage'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# meta.json is what Jellyfin reads out of the installed plugin folder.
$meta = [ordered]@{
    category    = Get-Scalar 'category'
    changelog   = $changelog
    description = Get-Scalar 'description'
    guid        = $guid
    name        = Get-Scalar 'name'
    overview    = Get-Scalar 'overview'
    owner       = Get-Scalar 'owner'
    targetAbi   = Get-Scalar 'targetAbi'
    timestamp   = $timestamp
    version     = $version
}
if ($imageUrl) { $meta['imageUrl'] = $imageUrl }
Write-Json (Join-Path $stage 'meta.json') $meta

foreach ($artifact in ($yaml | Select-String -Pattern '^\s+-\s+"(.+)"$' | ForEach-Object { $_.Matches[0].Groups[1].Value })) {
    Copy-Item (Join-Path $repo "TVHeadEnd\bin\Release\net10.0\$artifact") $stage
}

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Remove-Item $stage -Recurse -Force

$checksum = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()

$entry = [ordered]@{
    version    = $version
    changelog  = $changelog
    targetAbi  = Get-Scalar 'targetAbi'
    sourceUrl  = $sourceUrl
    checksum   = $checksum
    timestamp  = $timestamp
}

# Earlier versions are carried over; republishing one replaces its entry rather than listing it
# twice. The plugin object is rebuilt from build.yaml every time, so the two cannot drift apart.
#
# Only versions of the same plugin. A version entry says nothing about which plugin built it, so
# a manifest whose guid has changed carries a history that belongs to the plugin it used to be:
# Jellyfin would offer those packages under the new identity, and installing one hands the server
# an assembly that names the old guid, drops out of the new plugin's update path, and reports
# itself as a different plugin entirely. That is what happened when this fork took its own guid.
$previous = @()
if (Test-Path $manifestPath) {
    # Explicitly UTF-8, for the same reason build.yaml is: read in the ANSI code page, the
    # changelogs already in the manifest come back mangled and are written back out worse.
    $existing = Read-Utf8 $manifestPath | ConvertFrom-Json
    foreach ($plugin in @($existing)) {
        if ($plugin.guid -ne $guid) {
            Write-Warning "Skipping the version history of $($plugin.name) ($($plugin.guid)): a different plugin."
            continue
        }

        foreach ($v in @($plugin.versions)) {
            if ($v -and $v.version -ne $version) { $previous += $v }
        }
    }
}

$package = [ordered]@{
    guid        = $guid
    name        = Get-Scalar 'name'
    description = Get-Scalar 'description'
    overview    = Get-Scalar 'overview'
    owner       = Get-Scalar 'owner'
    category    = Get-Scalar 'category'
}
if ($imageUrl) { $package['imageUrl'] = $imageUrl }
$package['versions'] = @([PSCustomObject]$entry) + $previous

Write-Json $manifestPath @($package) -AsArray

Write-Output "Paket:    $zip"
Write-Output "MD5:      $checksum"
Write-Output "Manifest: $manifestPath (Version $version an erster Stelle)"
Write-Output ''
Write-Output "Vorbereitet, nicht veroeffentlicht. Artefakt testen, dann committen und pushen,"
Write-Output "CI abwarten und erst danach: .\tools\release.ps1 -Publish"
