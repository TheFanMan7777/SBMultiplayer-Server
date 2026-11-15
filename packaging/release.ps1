# Builds the release packages, pushes the branch and tag, and creates the GitHub release with the two zips.
# Run from the repository root in PowerShell, after committing everything:
#   .\packaging\release.ps1 -Repo TheFanMan7777/SBMultiplayer-Server
# Needs: .NET 8 SDK, git, and the GitHub CLI (gh) logged in (gh auth login).
# GitHub adds the source code .zip and .tar.gz for the tag by itself; they can't be uploaded.
param(
    [Parameter(Mandatory = $true)] [string] $Repo,
    [string] $Remote = "https://github.com/$Repo.git",
    [switch] $Draft
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)

$version = [regex]::Match((Get-Content Directory.Build.props -Raw), "<Version>([^<]+)</Version>").Groups[1].Value
$tag = "v$version"
$branch = git rev-parse --abbrev-ref HEAD
if ($LASTEXITCODE -ne 0) { throw "git failed" }
if (git status --porcelain) { throw "Uncommitted changes. Commit or stash them first." }
if (git tag -l $tag) { throw "Tag $tag already exists. Bump the version in Directory.Build.props first." }

Write-Host "Releasing $tag from branch $branch to $Repo"

# 1) Mod package: the five files the mod needs, plus the install notes. No settings file, so players keep theirs.
dotnet build src\SBMultiplayer.Client -c Release -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Client build failed" }
$out = "src\SBMultiplayer.Client\bin\Release\net8.0"
Remove-Item dist\pkg -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force dist\pkg\SBMultiplayer | Out-Null
foreach ($f in "SBMultiplayer.Client.dll", "0Harmony.dll", "LiteNetLib.dll", "SBMultiplayer.Protocol.dll", "SBMultiplayer.Server.dll") {
    Copy-Item (Join-Path $out $f) dist\pkg\SBMultiplayer\
}
Copy-Item packaging\INSTALL.txt dist\pkg\
$clientZip = "dist\SBMultiplayer-$version.zip"
Remove-Item $clientZip -ErrorAction SilentlyContinue
Compress-Archive -Path dist\pkg\* -DestinationPath $clientZip

# 2) Dedicated server: a framework-dependent publish (needs the .NET 8 runtime).
Remove-Item dist\server -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src\SBMultiplayer.Server -c Release -o dist\server\SBMultiplayer.Server -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Server publish failed" }
Remove-Item dist\server\SBMultiplayer.Server\*.pdb -ErrorAction SilentlyContinue
Copy-Item docs\hosting.md dist\server\
$serverZip = "dist\SBMultiplayer-Server-$version.zip"
Remove-Item $serverZip -ErrorAction SilentlyContinue
Compress-Archive -Path dist\server\* -DestinationPath $serverZip

# 3) Push the branch and the tag, then create the release.
git push $Remote "HEAD:refs/heads/$branch"
if ($LASTEXITCODE -ne 0) { throw "Pushing the branch failed" }
git tag -a $tag -m "SBMultiplayer $tag"
git push $Remote $tag
if ($LASTEXITCODE -ne 0) { throw "Pushing the tag failed" }

$ghArgs = @("release", "create", $tag, $clientZip, $serverZip, "--repo", $Repo, "--title", "SBMultiplayer $tag", "--generate-notes")
if ($Draft) { $ghArgs += "--draft" }
gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
Write-Host "Done: https://github.com/$Repo/releases/tag/$tag"
