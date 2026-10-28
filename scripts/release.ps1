<#
.SYNOPSIS
  Builds VR Table Manager for Windows and publishes it as a GitHub release.

.DESCRIPTION
  1. Checks the working tree is clean, HEAD is pushed and the tag is new.
  2. Runs the smoke test (skip with -SkipTests).
  3. Publishes the manager self-contained for win-x64 (no .NET install needed to run it).
  4. Builds the VR lobby and bundles it next to the app (VrTableManager\VrLobby), with its compiled
     C# so the app doesn't need the .NET SDK to start it. The app downloads Godot itself on first use.
  5. Zips it (plus a .sha256), tags v<Version>, pushes the tag and creates the GitHub release with
     the zip attached.

  Use -NoPublish to stop after the zip (nothing is tagged, pushed or uploaded).

  GitHub sign-in, first one found: $env:GITHUB_TOKEN, $env:GH_TOKEN, the GitHub CLI (gh auth token),
  then Git's credential helper for github.com (what git push already uses). The token needs
  "contents: write" on the repository.

.EXAMPLE
  .\scripts\release.ps1 -Version 1.0.0 -NoPublish
  .\scripts\release.ps1 -Version 1.0.0
  .\scripts\release.ps1 -Version 1.1.0-beta.1 -Prerelease -NotesFile notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')] [string] $Version,
    # Text added above GitHub's generated list of changes.
    [string] $Notes = "",
    [string] $NotesFile,
    [switch] $Draft,
    [switch] $Prerelease,
    [switch] $SkipTests,
    [switch] $NoPublish,
    # owner/name; defaults to the "origin" remote.
    [string] $Repo
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path $PSScriptRoot -Parent
$tag = "v$Version"
$out = Join-Path $root "artifacts\release"
$stage = Join-Path $out "VrTableManager"
$zip = Join-Path $out "VrTableManager-$Version-win-x64.zip"

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Run($exe, [string[]] $arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed (exit $LASTEXITCODE)" }
}
function GitOut([string[]] $arguments) {
    $result = & git -C $root @arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($arguments -join ' ') failed" }
    $result
}

# ---- Checks ---------------------------------------------------------------------------------

Step "Checking the repository"
if (-not $Repo) {
    $url = GitOut @('remote', 'get-url', 'origin')
    if ($url -notmatch 'github\.com[:/](?<r>[^/]+/[^/]+?)(\.git)?$') { throw "origin isn't a GitHub repository ($url). Pass -Repo owner/name." }
    $Repo = $Matches.r
}
if (GitOut @('status', '--porcelain')) {
    if (-not $NoPublish) { throw "The working tree has uncommitted changes. Commit or stash them first." }
    Write-Warning "Uncommitted changes are included in this local build (-NoPublish)."
}
$commit = (GitOut @('rev-parse', 'HEAD')).Trim()
$branch = (GitOut @('rev-parse', '--abbrev-ref', 'HEAD')).Trim()
if (GitOut @('tag', '--list', $tag)) { throw "Tag $tag already exists." }
if (-not $NoPublish) {
    GitOut @('fetch', '--quiet', 'origin') | Out-Null
    if (-not (GitOut @('branch', '-r', '--contains', $commit))) { throw "HEAD ($($commit.Substring(0,7))) isn't on GitHub yet. Push $branch first." }
}
Write-Host "Releasing $tag of $Repo from $branch @ $($commit.Substring(0,7))"

# ---- Tests ----------------------------------------------------------------------------------

if (-not $SkipTests) {
    Step "Running the smoke test"
    Run dotnet @('run', '--project', (Join-Path $root 'SmokeTest'), '-c', 'Release')
}

# ---- Build ----------------------------------------------------------------------------------

Step "Publishing the manager (self-contained, win-x64)"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
Run dotnet @('publish', (Join-Path $root 'VrTableManager\VrTableManager.csproj'), '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'true', "-p:Version=$Version", "-p:SourceRevisionId=$commit", '-o', $stage, '-nologo')

Step "Building the VR lobby"
$lobbySrc = Join-Path $root 'VrLobby'
# Debug is where Godot looks for a project's assemblies when it runs the project (LobbyRunner does the same).
Run dotnet @('build', (Join-Path $lobbySrc 'VrLobby.csproj'), '-c', 'Debug', '-nologo', '-v', 'q')
$lobbyOut = Join-Path $stage 'VrLobby'
New-Item -ItemType Directory -Force $lobbyOut | Out-Null
# The project and its assets; not editor state, build intermediates or the README's screenshots.
Get-ChildItem $lobbySrc -Force | Where-Object { $_.Name -notin @('.godot', 'bin', 'obj', '.vs') -and $_.Name -notlike 'screenshot*' } |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $lobbyOut $_.Name) -Recurse -Force }
$binSrc = Join-Path $lobbySrc '.godot\mono\temp\bin\Debug'
$binOut = Join-Path $lobbyOut '.godot\mono\temp\bin\Debug'
New-Item -ItemType Directory -Force $binOut | Out-Null
Copy-Item (Join-Path $binSrc '*') $binOut -Recurse -Force
# Newer than every shipped source file, so the app sees the lobby as built and doesn't need the SDK.
Get-ChildItem $binOut -File | ForEach-Object { $_.LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(1) }

Copy-Item (Join-Path $root 'README.md') $stage
@"
VR Table Manager $Version
=========================

Run VrTableManager.exe. Keep the VrLobby folder next to it (the VR lobby runs from there).

Needs: Windows 10/11 x64, Microsoft Edge (signs in to table sites), a Quest with Meta Quest Link
or Virtual Desktop. The VR lobby also needs the .NET 10 Runtime (https://dotnet.microsoft.com/download/dotnet/10.0);
the app itself does not.

Settings, favourites and play stats live in %APPDATA%\VrTableManager, so a new version can be
unzipped over or beside an old one.

Built from $Repo @ $commit
"@ | Set-Content (Join-Path $stage 'README.txt') -Encoding UTF8

Step "Zipping"
# Entry by entry: in Windows PowerShell both Compress-Archive and ZipFile.CreateFromDirectory write
# '\' in entry names, which other unzip tools treat as part of the file name.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    $base = Split-Path $stage -Parent
    foreach ($file in Get-ChildItem $stage -Recurse -File -Force) {
        $entry = $file.FullName.Substring($base.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $archive.Dispose() }
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ASCII
Write-Host ("{0} ({1:N0} MB)`nSHA-256 {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $hash)

if ($NoPublish) { Write-Host "`n-NoPublish: stopped before tagging and uploading." -ForegroundColor Yellow; return }

# ---- Publish --------------------------------------------------------------------------------

Step "Signing in to GitHub"
$token = $env:GITHUB_TOKEN
if (-not $token) { $token = $env:GH_TOKEN }
if (-not $token -and (Get-Command gh -ErrorAction SilentlyContinue)) { $token = (& gh auth token 2>$null) }
if (-not $token) {
    # The same credential git push uses (Git Credential Manager), asked for without prompting.
    $env:GCM_INTERACTIVE = 'never'; $env:GIT_TERMINAL_PROMPT = '0'
    $cred = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
    $token = ($cred | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
}
if (-not $token) { throw "No GitHub token found. Set GITHUB_TOKEN (a token with contents: write on $Repo) or sign in with 'gh auth login'." }
$headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }

Step "Tagging $tag"
Run git @('-C', $root, 'tag', '-a', $tag, '-m', "VR Table Manager $Version", $commit)
Run git @('-C', $root, 'push', 'origin', $tag)

Step "Creating the GitHub release"
if ($NotesFile) { $Notes = (Get-Content $NotesFile -Raw) + "`n`n" + $Notes }
$requirements = @"
**Download** ``$(Split-Path $zip -Leaf)``, unzip it anywhere and run ``VrTableManager.exe``.

Needs Windows 10/11 x64 and Microsoft Edge. The VR lobby also needs the [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
SHA-256: ``$hash``
"@
$body = @{
    tag_name               = $tag
    target_commitish       = $commit
    name                   = "VR Table Manager $Version"
    body                   = ($Notes.Trim() + "`n`n" + $requirements).Trim()
    draft                  = [bool]$Draft
    prerelease             = [bool]$Prerelease
    generate_release_notes = $true
} | ConvertTo-Json
try {
    $release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repo/releases" -Headers $headers -Body $body -ContentType 'application/json'
}
catch {
    Write-Warning "Creating the release failed; removing tag $tag so you can try again."
    git -C $root push --delete origin $tag 2>$null; git -C $root tag -d $tag | Out-Null
    throw
}

Step "Uploading the zip"
$uploadBase = $release.upload_url -replace '\{.*\}$', ''
foreach ($file in @($zip, "$zip.sha256")) {
    $name = [Uri]::EscapeDataString((Split-Path $file -Leaf))
    $type = if ($file.EndsWith('.zip')) { 'application/zip' } else { 'text/plain' }
    Invoke-RestMethod -Method Post -Uri "$uploadBase`?name=$name" -Headers $headers -InFile $file -ContentType $type | Out-Null
    Write-Host "Uploaded $(Split-Path $file -Leaf)"
}

Write-Host "`nDone: $($release.html_url)" -ForegroundColor Green
