<#
.SYNOPSIS
    Packages the plugin and adds it to manifest.json, the file this fork is consumed as a
    Jellyfin plugin repository through.

.DESCRIPTION
    Reads every package detail from build.yaml so the zip, the meta.json inside it and the
    manifest entry cannot drift apart. Run it after bumping the version in build.yaml and
    Directory.Build.props, then create the GitHub release with the zip it leaves in dist/:

        pwsh tools/release.ps1
        gh release create v<version> dist/tvheadend_<version>.zip --repo <owner>/<repo>
        git commit -am "Publish <version>" && git push

    The checksum Jellyfin verifies is the MD5 of the zip, so the manifest has to be written
    after the zip is final. Passing -SkipBuild reuses an existing Release build.
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
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

$yaml = Get-Content (Join-Path $repo 'build.yaml')
function Get-Scalar([string]$key) {
    $line = $yaml | Where-Object { $_ -match "^$key\s*:" } | Select-Object -First 1
    if (-not $line) { throw "build.yaml has no '$key'" }
    return ($line -replace "^$key\s*:\s*", '').Trim('"', ' ')
}

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
Write-Json (Join-Path $stage 'meta.json') ([ordered]@{
    category    = Get-Scalar 'category'
    changelog   = $changelog
    description = Get-Scalar 'description'
    guid        = Get-Scalar 'guid'
    imageUrl    = Get-Scalar 'imageUrl'
    name        = Get-Scalar 'name'
    overview    = Get-Scalar 'overview'
    owner       = Get-Scalar 'owner'
    targetAbi   = Get-Scalar 'targetAbi'
    timestamp   = $timestamp
    version     = $version
})

foreach ($artifact in ($yaml | Select-String -Pattern '^\s+-\s+"(.+)"$' | ForEach-Object { $_.Matches[0].Groups[1].Value })) {
    Copy-Item (Join-Path $repo "TVHeadEnd\bin\Release\net10.0\$artifact") $stage
}

$zip = Join-Path $dist "tvheadend_$version.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Remove-Item $stage -Recurse -Force

$checksum = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()

$entry = [ordered]@{
    version    = $version
    changelog  = $changelog
    targetAbi  = Get-Scalar 'targetAbi'
    sourceUrl  = "$RepositoryUrl/releases/download/v$version/tvheadend_$version.zip"
    checksum   = $checksum
    timestamp  = $timestamp
}

$manifestPath = Join-Path $repo 'manifest.json'

# Earlier versions are carried over; republishing one replaces its entry rather than listing it
# twice. The plugin object is rebuilt from build.yaml every time, so the two cannot drift apart.
$previous = @()
if (Test-Path $manifestPath) {
    $existing = Get-Content $manifestPath -Raw | ConvertFrom-Json
    foreach ($plugin in @($existing)) {
        foreach ($v in @($plugin.versions)) {
            if ($v -and $v.version -ne $version) { $previous += $v }
        }
    }
}

$manifest = @([ordered]@{
    guid        = Get-Scalar 'guid'
    name        = Get-Scalar 'name'
    description = Get-Scalar 'description'
    overview    = Get-Scalar 'overview'
    owner       = Get-Scalar 'owner'
    category    = Get-Scalar 'category'
    imageUrl    = Get-Scalar 'imageUrl'
    versions    = @([PSCustomObject]$entry) + $previous
})

Write-Json $manifestPath $manifest -AsArray

Write-Output "Paket:    $zip"
Write-Output "MD5:      $checksum"
Write-Output "Manifest: $manifestPath (Version $version an erster Stelle)"
