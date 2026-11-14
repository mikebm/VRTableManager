<#
.SYNOPSIS
  Builds VR Table Manager for Windows and publishes it as a GitHub release.

.DESCRIPTION
  1. Checks the working tree is clean, HEAD is pushed and the tag is new.
  2. Runs the smoke test (skip with -SkipTests).
  3. Publishes the manager self-contained for win-x64 (no .NET install needed to run it).
  4. Exports the VR lobby with Godot next to the app (VrTableManager\VrLobby\VrLobby.exe), so players need
     neither the Godot editor nor the .NET SDK, and runs its self-test from the export. Needs the Godot .NET
     editor (-Godot, else the one the app downloads) and its export templates installed.
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
    # Godot .NET editor (the _console.exe) that exports the lobby; defaults to the one the app downloads.
    [string] $Godot,
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
    # Judged by the exit code alone: Windows PowerShell turns a tool's stderr (Godot's "OpenXR … failed to start"
    # with no headset) into errors when output is redirected, which 'Stop' would end the script on.
    $ErrorActionPreference = 'Continue'
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

Step "Exporting the VR lobby"
$lobbySrc = Join-Path $root 'VrLobby'
# The Godot version the lobby is built for, from the project's SDK (Godot.NET.Sdk/<version>).
if ((Get-Content (Join-Path $lobbySrc 'VrLobby.csproj') -Raw) -notmatch 'Godot\.NET\.Sdk/(?<v>\d+\.\d+(\.\d+)?)') { throw "No Godot.NET.Sdk version in VrLobby.csproj" }
$godotVersion = $Matches.v
if (-not $Godot) {
    # The editor VR Table Manager downloads to run the lobby from source.
    $Godot = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'VrTableManager\godot') -Recurse -Filter "Godot_v$godotVersion-stable_mono_win64_console.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Godot -or -not (Test-Path $Godot)) { throw "No Godot $godotVersion .NET editor found. Pass -Godot <path to Godot_v$godotVersion-stable_mono_win64_console.exe>, or launch the VR lobby from a source build of the app once (it downloads it)." }
$templates = Join-Path $env:APPDATA "Godot\export_templates\$godotVersion.stable.mono"
if (-not (Test-Path (Join-Path $templates 'windows_release_x86_64.exe'))) {
    throw "Godot $godotVersion .NET export templates aren't installed ($templates). Install them from the editor (Editor > Manage Export Templates) or unzip Godot_v$godotVersion-stable_mono_export_templates.tpz's 'templates' folder there."
}
$lobbyOut = Join-Path $stage 'VrLobby'
New-Item -ItemType Directory -Force $lobbyOut | Out-Null
# Builds the lobby's C# in Release (with the .NET SDK) and packs it with its assets: VrLobby.exe, VrLobby.pck, its
# console wrapper (PREPARE TABLES reads its output) and data_VrLobby_windows_x86_64 (its code and its own .NET).
Run $Godot @('--headless', '--path', $lobbySrc, '--export-release', 'Windows', (Join-Path $lobbyOut 'VrLobby.exe'))
# Godot can report success after an export that wrote nothing, so check.
foreach ($f in @('VrLobby.exe', 'VrLobby.pck', 'VrLobby.console.exe', 'data_VrLobby_windows_x86_64\coreclr.dll', 'data_VrLobby_windows_x86_64\VrLobby.dll')) {
    if (-not (Test-Path (Join-Path $lobbyOut $f))) { throw "The lobby export didn't write $f" }
}
# The lobby's DOF helper (DofBridge), once per DOF bitness: VrLobby\dof\x64 and \x86. It runs the player's own DOF, so
# nothing of DOF goes in the release.
foreach ($arch in @(@{ Name = 'x64'; Platform = 'AnyCPU' }, @{ Name = 'x86'; Platform = 'x86' })) {
    $dofOut = Join-Path $lobbyOut "dof\$($arch.Name)"
    Run dotnet @('build', (Join-Path $root 'DofBridge\DofBridge.csproj'), '-c', 'Release', "-p:Platform=$($arch.Platform)",
        "-p:Version=$Version", '-o', $dofOut, '-nologo')
    Remove-Item (Join-Path $dofOut '*.pdb') -ErrorAction SilentlyContinue
    if (-not (Test-Path (Join-Path $dofOut 'DofBridge.exe'))) { throw "DofBridge wasn't built to $dofOut" }
}
Step "Checking the exported lobby"
# Its in-engine self-test, run from the export itself: the default cabinet, fonts and code all come from the .pck.
Run (Join-Path $lobbyOut 'VrLobby.console.exe') @('--headless', '--', '--selftest')

Copy-Item (Join-Path $root 'README.md') $stage
# The MIT license and the third-party notices (MPL-2.0 OpenMcdf, ODbL maps, OFL fonts) go with every copy.
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $stage
@"
VR Table Manager $Version
=========================

Run VrTableManager.exe. Keep the VrLobby folder next to it (the VR lobby runs from there).

Needs: Windows 10/11 x64, a web browser (table sites are downloaded from in it), a Quest with Meta Quest Link
or Virtual Desktop. Nothing else to install: the app and the VR lobby both run on the .NET included here.

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
    # Written straight to git's stdin: piping the request from Windows PowerShell reaches git
    # mangled ("credential missing protocol field"), and its stderr would stop the script.
    $psi = New-Object Diagnostics.ProcessStartInfo 'git', 'credential fill'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables['GCM_INTERACTIVE'] = 'never'; $psi.EnvironmentVariables['GIT_TERMINAL_PROMPT'] = '0'
    $git = [Diagnostics.Process]::Start($psi)
    $git.StandardInput.Write("protocol=https`nhost=github.com`n`n"); $git.StandardInput.Close()
    $cred = $git.StandardOutput.ReadToEnd() -split "`r?`n"
    $git.WaitForExit()
    $token = ($cred | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
}
if (-not $token) { throw "No GitHub token found. Set GITHUB_TOKEN (a token with contents: write on $Repo) or sign in with 'gh auth login'." }
$headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }

Step "Tagging $tag"
Run git @('-C', $root, 'tag', '-a', $tag, '-m', "VR Table Manager $Version", $commit)
Run git @('-C', $root, 'push', 'origin', $tag)

Step "Creating the GitHub release"
# Read as UTF-8: Windows PowerShell 5.1 would read a file without a BOM as ANSI and mangle "→", "…".
if ($NotesFile) { $Notes = [IO.File]::ReadAllText((Resolve-Path $NotesFile), [Text.Encoding]::UTF8) + "`n`n" + $Notes }
$requirements = @"
**Download** ``$(Split-Path $zip -Leaf)``, unzip it anywhere and run ``VrTableManager.exe``.

Needs Windows 10/11 x64. No .NET install needed: the app and the VR lobby run on the .NET included in the zip.
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
    # Sent as UTF-8 bytes: 5.1 sends a string body as ISO-8859-1, which loses anything outside it.
    $release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repo/releases" -Headers $headers `
        -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json; charset=utf-8'
}
catch {
    $reason = $_.ErrorDetails.Message
    if (-not $reason -and $_.Exception.Response) {
        try { $reason = (New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd() } catch { }
    }
    Write-Warning "Creating the release failed: $($_.Exception.Message) $reason"
    Write-Warning "Removing tag $tag so you can try again."
    # git writes progress to stderr, which would stop the script here (ErrorActionPreference Stop) before the local tag goes.
    $ErrorActionPreference = 'Continue'
    & git -C $root push --delete origin $tag 2>&1 | Out-Null
    & git -C $root tag -d $tag 2>&1 | Out-Null
    throw "Creating the GitHub release failed. $reason"
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
