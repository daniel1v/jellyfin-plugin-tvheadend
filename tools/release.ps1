<#
.SYNOPSIS
    Packages the plugin and adds it to manifest.json, the file this fork is consumed as a
    Jellyfin plugin repository through.

.DESCRIPTION
    Reads every package detail from build.yaml so the zip, the meta.json inside it and the
    manifest entry cannot drift apart. Run it after bumping the version in build.yaml and
    Directory.Build.props:

        & .\tools\release.ps1 -Publish
        git commit -am "Publish <version>" && git push

    The checksum Jellyfin verifies is the MD5 of the zip, so the manifest has to be written
    after the zip is final. Passing -SkipBuild reuses an existing Release build.

    -Publish creates the GitHub release itself. Every release this fork makes is an alpha and
    is marked as a GitHub prerelease, which is the only place the word can live: Jellyfin parses
    a manifest version with Version.Parse, so it cannot carry a "-alpha" suffix. The flag is set
    here rather than typed each time, because a flag that has to be remembered is one that
    eventually is not.

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
    guid        = Get-Scalar 'guid'
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

$zip = Join-Path $dist "tvheadend-ex_$version.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Remove-Item $stage -Recurse -Force

$checksum = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()

$entry = [ordered]@{
    version    = $version
    changelog  = $changelog
    targetAbi  = Get-Scalar 'targetAbi'
    sourceUrl  = "$RepositoryUrl/releases/download/v$version/tvheadend-ex_$version.zip"
    checksum   = $checksum
    timestamp  = $timestamp
}

$manifestPath = Join-Path $repo 'manifest.json'

# Earlier versions are carried over; republishing one replaces its entry rather than listing it
# twice. The plugin object is rebuilt from build.yaml every time, so the two cannot drift apart.
$previous = @()
if (Test-Path $manifestPath) {
    # Explicitly UTF-8, for the same reason build.yaml is: read in the ANSI code page, the
    # changelogs already in the manifest come back mangled and are written back out worse.
    $existing = [System.IO.File]::ReadAllText($manifestPath, (New-Object System.Text.UTF8Encoding($false))) | ConvertFrom-Json
    foreach ($plugin in @($existing)) {
        foreach ($v in @($plugin.versions)) {
            if ($v -and $v.version -ne $version) { $previous += $v }
        }
    }
}

$package = [ordered]@{
    guid        = Get-Scalar 'guid'
    name        = Get-Scalar 'name'
    description = Get-Scalar 'description'
    overview    = Get-Scalar 'overview'
    owner       = Get-Scalar 'owner'
    category    = Get-Scalar 'category'
}
if ($imageUrl) { $package['imageUrl'] = $imageUrl }
$package['versions'] = @([PSCustomObject]$entry) + $previous

$manifest = @($package)

Write-Json $manifestPath $manifest -AsArray

Write-Output "Paket:    $zip"
Write-Output "MD5:      $checksum"
Write-Output "Manifest: $manifestPath (Version $version an erster Stelle)"

if ($Publish) {
    $repoSlug = ($RepositoryUrl -replace '^https://github\.com/', '')

    # Through a file, not an argument. Windows PowerShell re-splits the arguments it hands a
    # native program, so the quotation marks inside a changelog arrive as argument boundaries and
    # gh is asked to do something nobody wrote.
    $notes = Join-Path $dist "notes_$version.md"
    [System.IO.File]::WriteAllText($notes, $changelog, (New-Object System.Text.UTF8Encoding($false)))

    # Always a prerelease. This fork ships alphas, and the manifest cannot say so itself.
    & gh release create "v$version" $zip `
        --repo $repoSlug `
        --title "TVHeadend EX $version (alpha)" `
        --notes-file $notes `
        --prerelease
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }

    Write-Output "Release:  $RepositoryUrl/releases/tag/v$version (alpha)"
}


