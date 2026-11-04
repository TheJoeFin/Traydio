<#
.SYNOPSIS
Builds and signs Traydio's x64/ARM64 GitHub bundle and uploads a draft release.
.DESCRIPTION
Reads the version from Package.appxmanifest and the GitHub identity from Trdo.csproj.
The Store manifest is restored after building, including when a build fails.
Signing uses the local sign-msix PowerShell profile function.
.EXAMPLE
.\build-msix-for-gh.ps1
.EXAMPLE
.\build-msix-for-gh.ps1 -SkipRelease
.EXAMPLE
.\build-msix-for-gh.ps1 -VerifyPublished
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # Root of the repository to build. Defaults to the folder this script lives in.
    [string]$RepoRoot,

    # The packaged app project, as a .csproj path or a project name. Only needed when
    # the repo has more than one project with a Package.appxmanifest next to it.
    [string]$Project,

    # Release notes for the GitHub release. When omitted, GitHub generates notes
    # from the commits/PRs since the previous release.
    [string]$Notes,

    # Mark the GitHub release as a pre-release.
    [switch]$Prerelease,

    # Build and sign only; skip tagging and the GitHub release.
    [switch]$SkipRelease,

    # Allow releasing with uncommitted changes (they would be in the build but not in the tag).
    [switch]$AllowDirty,

    # Leave the bundle unsigned (requires -SkipRelease). For checking the build, feed and bundle
    # where the sign-msix profile function isn't available; the result can't be installed.
    [switch]$NoSign,

    # Where the builds and release files go. Defaults to <project>\AppPackages\GitHub.
    [string]$OutputRoot,

    # Build nothing: after the draft is published, check that the stable latest/download link
    # serves this version's feed and that every address in it downloads.
    [switch]$VerifyPublished
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# A packaged app project is a .csproj with a Package.appxmanifest next to it
# (test and library projects have none). Returns the matching .csproj files under $Root.
function Find-PackagedProject {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    Get-ChildItem -LiteralPath $Root -Filter '*.csproj' -File -Recurse -Depth 3 -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|AppPackages)\\' } |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.DirectoryName 'Package.appxmanifest') } |
        Where-Object { -not $Project -or $_.BaseName -eq $Project }
}

# An explicit .csproj path pins both the project and (via git) the repo root.
$projectFile = $null
if ($Project -and $Project.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) {
    if (-not (Test-Path -LiteralPath $Project)) {
        throw "Project not found: $Project"
    }
    $projectFile = Get-Item -LiteralPath $Project
    if (-not $RepoRoot) {
        $RepoRoot = & git -C $projectFile.DirectoryName rev-parse --show-toplevel 2>$null
        if (-not $RepoRoot) {
            $RepoRoot = $projectFile.DirectoryName
        }
    }
}

# $PSScriptRoot is not reliable in every host (e.g. ISE/VS Code run-selection or
# dot-sourcing report the caller's folder), so resolve the root explicitly: an
# explicit -RepoRoot wins, then the script's own folder, then the git repo of the
# current directory. Only accept a candidate that actually contains a packaged project.
if (-not $RepoRoot) {
    $candidates = @(
        $(if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path }),
        $PSScriptRoot,
        $(& git rev-parse --show-toplevel 2>$null),
        (Get-Location).ProviderPath
    ) | Where-Object { $_ }

    $RepoRoot = $candidates |
        Where-Object { Find-PackagedProject -Root $_ } |
        Select-Object -First 1

    if (-not $RepoRoot) {
        throw "Could not locate a repository with a packaged app project (tried: $($candidates -join ', ')). Pass -RepoRoot or run from inside the repo."
    }
}

if (-not (Test-Path -LiteralPath $RepoRoot)) {
    throw "Repository root not found: $RepoRoot"
}

$repoRoot = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath

if (-not $projectFile) {
    $found = @(Find-PackagedProject -Root $repoRoot)
    if ($found.Count -eq 0) {
        $what = if ($Project) { "named '$Project'" } else { 'with a Package.appxmanifest next to it' }
        throw "No project $what was found under $repoRoot."
    }
    if ($found.Count -gt 1) {
        throw "Found more than one packaged project; pass -Project with one of: $(($found | ForEach-Object BaseName) -join ', ')."
    }
    $projectFile = $found[0]
}

$appName = $projectFile.BaseName
$projectDir = $projectFile.DirectoryName
$projectPath = $projectFile.FullName
$manifestPath = Join-Path $projectDir 'Package.appxmanifest'
$outputRoot = if ($OutputRoot) {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
} else {
    Join-Path $projectDir 'AppPackages\GitHub'
}
$publishProfilesRoot = Join-Path $projectDir 'Properties\PublishProfiles'

# Signing is done by the maintainer's local 'sign-msix' profile function, so the
# signing configuration stays out of the repo.
$signCommand = 'sign-msix'

$builds = @(
    @{
        Name = 'x64'
        Platform = 'x64'
        RuntimeIdentifier = 'win-x64'
        PublishProfile = 'win-x64.pubxml'
        OutputDir = (Join-Path $outputRoot 'x64')
    },
    @{
        Name = 'arm64'
        Platform = 'ARM64'
        RuntimeIdentifier = 'win-arm64'
        PublishProfile = 'win-arm64.pubxml'
        OutputDir = (Join-Path $outputRoot 'arm64')
    }
)

function Get-ReleaseTag {
    param(
        [Parameter(Mandatory)]
        [string]$Version
    )

    # 0.1.0.0 -> v0.1.0 (matches existing tags); keep a non-zero fourth part.
    $parts = $Version.Split('.')
    if ($parts.Count -eq 4 -and $parts[3] -eq '0') {
        $parts = $parts[0..2]
    }
    return 'v' + ($parts -join '.')
}

function Get-GitHubRepository {
    $url = & git -C $repoRoot remote get-url origin 2>$null
    if ($LASTEXITCODE -ne 0 -or "$url" -notmatch 'github\.com[:/](?<repo>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(\.git)?/?$') {
        throw "Could not work out the GitHub owner/repository from the 'origin' remote ($url)."
    }
    return $Matches['repo']
}

# The identity the GitHub packages carry. "Associate App with the Store" gives a Store app's manifest
# the Partner Center identity (Publisher="CN=<GUID>"), which the maintainer's certificate can't sign.
# Such an app names the identity its GitHub releases use in the csproj: <GitHubIdentityName>,
# <GitHubIdentityPublisher> and, optionally, <GitHubPublisherDisplayName>. The build swaps them into
# the manifest (Set-ReleaseIdentity) and restores the manifest afterwards. Without them, the packages
# keep the manifest's identity.
function Get-ReleaseIdentity {
    param(
        [Parameter(Mandatory)]
        [xml]$Manifest
    )

    $identity = $Manifest.Package.Identity
    $manifestName = $identity.GetAttribute('Name')
    $manifestPublisher = $identity.GetAttribute('Publisher')

    $query = & dotnet msbuild $projectPath -getProperty:GitHubIdentityName -getProperty:GitHubIdentityPublisher -getProperty:GitHubPublisherDisplayName
    if ($LASTEXITCODE -ne 0) {
        throw "Could not evaluate $projectPath."
    }
    $properties = ($query | Out-String | ConvertFrom-Json).Properties
    $name = "$($properties.GitHubIdentityName)".Trim()
    $publisher = "$($properties.GitHubIdentityPublisher)".Trim()
    $publisherDisplayName = "$($properties.GitHubPublisherDisplayName)".Trim()

    if (-not $name -and -not $publisher) {
        if ($manifestPublisher -match '^CN=[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}$') {
            throw "Package.appxmanifest has the Store identity ($manifestName, $manifestPublisher). Set <GitHubIdentityName> and <GitHubIdentityPublisher> in $($projectFile.Name) to the published GitHub identity; the publisher must match the sign-msix certificate subject."
        }
        $name = $manifestName
        $publisher = $manifestPublisher
    }
    elseif (-not $name -or -not $publisher) {
        throw "Set both <GitHubIdentityName> and <GitHubIdentityPublisher> in $($projectFile.Name), or neither."
    }

    [pscustomobject]@{
        Name = $name
        Publisher = $publisher
        PublisherDisplayName = $publisherDisplayName
        Swapped = ($name -cne $manifestName -or $publisher -cne $manifestPublisher -or
            ($publisherDisplayName -and $publisherDisplayName -cne $Manifest.Package.Properties.PublisherDisplayName))
    }
}

function Read-ZipEntryText {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $entry = $Archive.GetEntry($Name)
    if (-not $entry) {
        throw "Missing package entry: $Name."
    }
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

# A framework-dependent app needs the Windows App Runtime framework package on the machine, and
# App Installer installs missing dependencies listed in the feed. The feed is written before the
# build (every package embeds it), so find the framework MSIX in the restored NuGet packages: the
# one the build copies to Dependencies\. This works for the Microsoft.WindowsAppSDK metapackage and
# for component references alike, pinned or floating. Test-ReleaseBundle then checks the feed
# against the PackageDependency elements the build actually wrote. Returns nothing for a
# self-contained app.
function Get-FrameworkPackages {
    & dotnet restore $projectPath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed for $projectPath."
    }

    $first = $builds[0]
    $query = & dotnet msbuild $projectPath -getProperty:ProjectAssetsFile -getProperty:WindowsAppSDKSelfContained `
        "-p:Configuration=$Configuration" "-p:Platform=$($first.Platform)" "-p:RuntimeIdentifier=$($first.RuntimeIdentifier)"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not evaluate $projectPath."
    }
    $properties = ($query | Out-String | ConvertFrom-Json).Properties
    if ($properties.WindowsAppSDKSelfContained -eq 'true') {
        return
    }
    if (-not $properties.ProjectAssetsFile -or -not (Test-Path -LiteralPath $properties.ProjectAssetsFile)) {
        throw "The restore wrote no assets file for $projectPath."
    }

    $assets = Get-Content -LiteralPath $properties.ProjectAssetsFile -Raw | ConvertFrom-Json
    $packageFolders = @($assets.packageFolders.PSObject.Properties.Name)

    foreach ($build in $builds) {
        # Microsoft.WindowsAppRuntime.2.msix, .1.6.msix or .2-experimentalF.msix; not the .Main, .DDLM or .Singleton packages.
        $pattern = "^tools/MSIX/win10-$($build.Name)/Microsoft\.WindowsAppRuntime\.\d+(\.\d+)?(-[A-Za-z0-9]+)?\.msix$"
        $sources = foreach ($library in $assets.libraries.PSObject.Properties) {
            foreach ($file in @(@($library.Value.files) -match $pattern)) {
                $packageFolders |
                    ForEach-Object { Join-Path (Join-Path $_ $library.Value.path) $file } |
                    Where-Object { Test-Path -LiteralPath $_ } |
                    Select-Object -First 1
            }
        }

        $candidates = foreach ($source in $sources) {
            $zip = [System.IO.Compression.ZipFile]::OpenRead($source)
            try {
                [xml]$frameworkManifest = Read-ZipEntryText -Archive $zip -Name 'AppxManifest.xml'
            }
            finally {
                $zip.Dispose()
            }
            $identity = $frameworkManifest.Package.Identity
            [pscustomobject]@{
                Name = $identity.Name
                Publisher = $identity.Publisher
                Version = $identity.Version
                Architecture = $identity.ProcessorArchitecture
                Source = $source
                AssetName = "$($identity.Name)_$($identity.Version)_$($identity.ProcessorArchitecture).msix"
            }
        }

        $framework = $candidates | Sort-Object { [version]$_.Version } -Descending | Select-Object -First 1
        if (-not $framework) {
            throw "No Windows App Runtime framework package for $($build.Name) was found in the restored packages ($($properties.ProjectAssetsFile))."
        }
        $framework
    }
}

# The App Installer feed. Linking users to .../releases/latest/download/<App>.appinstaller makes
# Windows install the right architecture from the bundle and keep the app updated from the
# newest stable release. Only the changed blocks are downloaded on update.
function New-AppInstallerFile {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Repository,
        [Parameter(Mandatory)] [string]$BundleName,
        [object[]]$FrameworkPackages = @()
    )

    $namespace = 'http://schemas.microsoft.com/appx/appinstaller/2021'
    $baseUri = "https://github.com/$Repository/releases"
    $assetBase = "$baseUri/download/$([uri]::EscapeDataString($tag))"

    $document = New-Object System.Xml.XmlDocument
    [void]$document.AppendChild($document.CreateXmlDeclaration('1.0', 'utf-8', $null))
    $installer = $document.CreateElement('AppInstaller', $namespace)
    $installer.SetAttribute('Version', $version)
    $installer.SetAttribute('Uri', "$baseUri/latest/download/$feedName")
    [void]$document.AppendChild($installer)

    $bundle = $document.CreateElement('MainBundle', $namespace)
    $bundle.SetAttribute('Name', $identityName)
    $bundle.SetAttribute('Publisher', $identityPublisher)
    $bundle.SetAttribute('Version', $version)
    $bundle.SetAttribute('Uri', "$assetBase/$([uri]::EscapeDataString($BundleName))")
    [void]$installer.AppendChild($bundle)

    if ($FrameworkPackages.Count -gt 0) {
        $dependencies = $document.CreateElement('Dependencies', $namespace)
        foreach ($framework in $FrameworkPackages) {
            $package = $document.CreateElement('Package', $namespace)
            $package.SetAttribute('Name', $framework.Name)
            $package.SetAttribute('Publisher', $framework.Publisher)
            $package.SetAttribute('Version', $framework.Version)
            $package.SetAttribute('ProcessorArchitecture', $framework.Architecture)
            $package.SetAttribute('Uri', "$assetBase/$([uri]::EscapeDataString($framework.AssetName))")
            [void]$dependencies.AppendChild($package)
        }
        [void]$installer.AppendChild($dependencies)
    }

    # Check on launch (silently, never blocking) and in the background roughly every eight hours.
    $settings = $document.CreateElement('UpdateSettings', $namespace)
    $onLaunch = $document.CreateElement('OnLaunch', $namespace)
    $onLaunch.SetAttribute('HoursBetweenUpdateChecks', '0')
    $onLaunch.SetAttribute('ShowPrompt', 'false')
    $onLaunch.SetAttribute('UpdateBlocksActivation', 'false')
    [void]$settings.AppendChild($onLaunch)
    [void]$settings.AppendChild($document.CreateElement('AutomaticBackgroundTask', $namespace))
    [void]$installer.AppendChild($settings)

    $document.Save($Path)
}

# Points the package at the Update.appinstaller packaged beside it (uap13:AutoUpdate), so even a
# package installed by double-clicking the bundle keeps updating. Edits the manifest in place;
# the caller restores the original bytes afterwards.
function Add-AutoUpdateReference {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $namespace = 'http://schemas.microsoft.com/appx/manifest/uap/windows10/13'
    $document = New-Object System.Xml.XmlDocument
    $document.PreserveWhitespace = $true
    $document.Load($Path)
    $root = $document.DocumentElement
    $properties = $root.Properties
    if ($properties.GetElementsByTagName('AutoUpdate', $namespace).Count -gt 0) {
        return
    }

    $root.SetAttribute('xmlns:uap13', $namespace)
    $root.SetAttribute('IgnorableNamespaces', "$($root.GetAttribute('IgnorableNamespaces')) uap13".Trim())
    $autoUpdate = $document.CreateElement('uap13', 'AutoUpdate', $namespace)
    $appInstaller = $document.CreateElement('uap13', 'AppInstaller', $namespace)
    $appInstaller.SetAttribute('File', 'Update.appinstaller')
    [void]$autoUpdate.AppendChild($appInstaller)
    [void]$properties.AppendChild($autoUpdate)
    $document.Save($Path)
}

# Gives the release packages the GitHub identity from Get-ReleaseIdentity. Edits the manifest in
# place; the caller restores the original bytes afterwards, so the committed manifest keeps the
# Store identity.
function Set-ReleaseIdentity {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [object]$Identity
    )

    $document = New-Object System.Xml.XmlDocument
    $document.PreserveWhitespace = $true
    $document.Load($Path)
    $root = $document.DocumentElement
    $root['Identity'].SetAttribute('Name', $Identity.Name)
    $root['Identity'].SetAttribute('Publisher', $Identity.Publisher)
    if ($Identity.PublisherDisplayName) {
        $root['Properties']['PublisherDisplayName'].InnerText = $Identity.PublisherDisplayName
    }
    $document.Save($Path)
}

function Find-MakeAppx {
    $found = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Directory.Parent.Name) } -Descending |
        Select-Object -First 1
    if (-not $found) {
        throw 'MakeAppx (Windows SDK) was not found.'
    }
    return $found.FullName
}

# Proves the release does what the user experience depends on, before anything is uploaded:
# one bundle carrying both architectures (Windows installs the matching one), each package
# embedding the same feed and a block map (updates download only the changed blocks), and the
# feed naming the bundle and the framework packages it needs.
function Test-ReleaseBundle {
    param(
        [Parameter(Mandatory)] [string]$BundlePath,
        [Parameter(Mandatory)] [string]$FeedPath,
        [object[]]$FrameworkPackages = @()
    )

    [xml]$feed = Get-Content -LiteralPath $FeedPath -Raw
    $main = $feed.AppInstaller.MainBundle
    if (-not $main -or $feed.AppInstaller.MainPackage) {
        throw 'The feed must describe a MainBundle.'
    }
    if ($main.Version -cne $feed.AppInstaller.Version) {
        throw 'The feed and bundle versions must match.'
    }
    if ([uri]::UnescapeDataString(([uri]$main.Uri).Segments[-1]) -cne [System.IO.Path]::GetFileName($BundlePath)) {
        throw 'The feed URI must name the published bundle.'
    }
    $feedText = [System.IO.File]::ReadAllText($FeedPath)
    $dependencyNodes = @($feed.AppInstaller.Dependencies.Package)

    $zip = [System.IO.Compression.ZipFile]::OpenRead($BundlePath)
    try {
        [xml]$bundleManifest = Read-ZipEntryText -Archive $zip -Name 'AppxMetadata/AppxBundleManifest.xml'
        foreach ($attribute in 'Name', 'Publisher', 'Version') {
            if ($bundleManifest.Bundle.Identity.GetAttribute($attribute) -cne $main.GetAttribute($attribute)) {
                throw "Bundle identity does not match the feed ($attribute)."
            }
        }

        $architectures = @()
        $applicationPackages = @($bundleManifest.Bundle.Packages.Package | Where-Object Type -eq 'application')
        foreach ($entry in $applicationPackages) {
            $architecture = $entry.Architecture
            if ($architecture -notin 'x64', 'arm64' -or $architecture -in $architectures) {
                throw "Unexpected or duplicate application architecture: $architecture."
            }
            $architectures += $architecture

            $inner = $zip.GetEntry($entry.FileName)
            if (-not $inner) {
                throw "Missing bundled package: $($entry.FileName)."
            }
            $stream = New-Object System.IO.MemoryStream
            try {
                $source = $inner.Open()
                try { $source.CopyTo($stream) } finally { $source.Dispose() }
                $stream.Position = 0
                $package = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Read, $true)
                try {
                    [xml]$manifest = Read-ZipEntryText -Archive $package -Name 'AppxManifest.xml'
                    foreach ($attribute in 'Name', 'Publisher', 'Version') {
                        if ($manifest.Package.Identity.GetAttribute($attribute) -cne $main.GetAttribute($attribute)) {
                            throw "$architecture package identity does not match the feed ($attribute)."
                        }
                    }
                    if ($manifest.Package.Identity.ProcessorArchitecture -cne $architecture) {
                        throw "$architecture package declares architecture $($manifest.Package.Identity.ProcessorArchitecture)."
                    }
                    if ((Read-ZipEntryText -Archive $package -Name 'Update.appinstaller') -cne $feedText) {
                        throw "The Update.appinstaller embedded in the $architecture package differs from the published feed."
                    }
                    $namespaces = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
                    $namespaces.AddNamespace('p', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
                    $namespaces.AddNamespace('uap13', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/13')
                    $reference = $manifest.SelectSingleNode('/p:Package/p:Properties/uap13:AutoUpdate/uap13:AppInstaller', $namespaces)
                    if (-not $reference -or $reference.GetAttribute('File') -cne 'Update.appinstaller') {
                        throw "The $architecture manifest must reference the embedded Update.appinstaller."
                    }
                    if (-not $package.GetEntry('AppxBlockMap.xml')) {
                        throw "The $architecture package has no block map, so updates could not be differential."
                    }
                    # A machine without a framework the package declares could not install it.
                    foreach ($dependency in $manifest.SelectNodes('/p:Package/p:Dependencies/p:PackageDependency', $namespaces)) {
                        $listed = $dependencyNodes |
                            Where-Object { $_.Name -ceq $dependency.Name -and $_.Publisher -ceq $dependency.Publisher -and $_.ProcessorArchitecture -ceq $architecture } |
                            Select-Object -First 1
                        if (-not $listed -or [version]$listed.Version -lt [version]$dependency.MinVersion) {
                            throw "The $architecture package depends on $($dependency.Name) $($dependency.MinVersion) or later, which the feed does not provide."
                        }
                    }
                }
                finally { $package.Dispose() }
            }
            finally { $stream.Dispose() }
        }
        foreach ($required in 'x64', 'arm64') {
            if ($required -notin $architectures) {
                throw "The bundle is missing the $required application package."
            }
        }
    }
    finally {
        $zip.Dispose()
    }

    foreach ($framework in $FrameworkPackages) {
        $node = $dependencyNodes | Where-Object { $_.Name -ceq $framework.Name -and $_.ProcessorArchitecture -ceq $framework.Architecture }
        if (-not $node -or $node.Version -cne $framework.Version -or $node.Publisher -cne $framework.Publisher) {
            throw "The feed does not list $($framework.Name) $($framework.Version) for $($framework.Architecture)."
        }
        if ([uri]::UnescapeDataString(([uri]$node.Uri).Segments[-1]) -cne $framework.AssetName) {
            throw "The feed URI for $($framework.Name) $($framework.Architecture) does not match its release asset."
        }
    }
    Write-Host "  Verified bundle ($($architectures -join ', ')), embedded feed, block maps and dependencies."
}

# Publishing the draft is manual, so check the live release afterwards: the stable link must serve
# this version's feed (not an older release, a pre-release or an unpublished draft), and every
# address in that feed must download. A tag changed while publishing shows up here as a failure.
function Test-PublishedRelease {
    $feedUri = "https://github.com/$repository/releases/latest/download/$feedName"
    $download = Join-Path ([System.IO.Path]::GetTempPath()) "$([guid]::NewGuid()).appinstaller"
    $feed = New-Object System.Xml.XmlDocument
    try {
        try {
            Invoke-WebRequest -Uri $feedUri -OutFile $download -UseBasicParsing
        }
        catch {
            throw "The stable link $feedUri does not download ($($_.Exception.Message)). Is the release published, and not as a pre-release?"
        }
        $feed.Load($download)
    }
    finally {
        Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue
    }

    $served = $feed.AppInstaller.Version
    if ($served -cne $version) {
        throw "The latest release serves version $served, not $version. Publish the $tag draft as the latest release (not a pre-release)."
    }

    $uris = @($feed.AppInstaller.Uri, $feed.AppInstaller.MainBundle.Uri) +
        @($feed.AppInstaller.Dependencies.Package | ForEach-Object { $_.Uri }) |
        Where-Object { $_ }
    $failed = @()
    foreach ($uri in $uris) {
        try {
            $response = Invoke-WebRequest -Uri $uri -Method Head -UseBasicParsing
            Write-Host "  $($response.StatusCode) $uri"
        }
        catch {
            Write-Host "  FAILED $uri ($($_.Exception.Message))" -ForegroundColor Red
            $failed += $uri
        }
    }
    if ($failed) {
        throw "$($failed.Count) address(es) in the published feed do not download. Releases are immutable, so fix it with a new release under a higher version."
    }
    Write-Host "Verified the published release: $feedUri serves $version and every address in it downloads."
}

function Assert-ReleaseReady {
    param(
        [Parameter(Mandatory)]
        [string]$Tag
    )

    foreach ($tool in 'git', 'gh') {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            throw "$tool is required but was not found on PATH."
        }
    }

    & gh auth status *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'gh is not authenticated. Run "gh auth login" first.'
    }

    $branch = (& git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -eq 'HEAD') {
        throw 'Could not determine the current branch (detached HEAD?).'
    }

    if (-not $AllowDirty) {
        $status = & git -C $repoRoot status --porcelain
        if ($status) {
            throw "Working tree has uncommitted changes; commit/stash them or pass -AllowDirty.`n$($status -join "`n")"
        }
    }

    & git -C $repoRoot fetch origin --quiet
    if ($LASTEXITCODE -ne 0) {
        throw 'git fetch origin failed.'
    }

    $upstream = & git -C $repoRoot rev-parse --abbrev-ref '@{u}' 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Branch '$branch' has no upstream. Push it first (git push -u origin $branch)."
    }
    $ahead = [int](& git -C $repoRoot rev-list --count '@{u}..HEAD')
    if ($ahead -gt 0) {
        throw "Branch '$branch' is $ahead commit(s) ahead of $upstream. Push before releasing."
    }

    & git -C $repoRoot rev-parse -q --verify "refs/tags/$Tag" *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "Tag $Tag already exists locally. Bump the version in Package.appxmanifest."
    }
    $remoteTag = & git -C $repoRoot ls-remote --tags origin "refs/tags/$Tag"
    if ($remoteTag) {
        throw "Tag $Tag already exists on origin. Bump the version in Package.appxmanifest."
    }

    # Drafts have no tag on origin yet, so also check for an existing (draft) release.
    Push-Location -LiteralPath $repoRoot
    try {
        # Filter in PowerShell rather than --jq: Windows PowerShell 5.1 strips embedded quotes from native args.
        $releasesJson = & gh release list --limit 100 --json tagName
        if ($LASTEXITCODE -ne 0) {
            throw 'gh release list failed.'
        }
    }
    finally {
        Pop-Location
    }
    $existingRelease = ($releasesJson | Out-String | ConvertFrom-Json) | Where-Object { $_.tagName -eq $Tag }
    if ($existingRelease) {
        throw "A release for $Tag already exists on GitHub (possibly a draft). Delete it or bump the version."
    }

    Assert-PublishedIdentity

    return $branch
}

# Installed copies only update to a package with the identity they were installed with, so every
# release must keep the identity of the published one. Compares the build's identity with the feed
# the stable link serves; a 404 means no published release has a feed yet.
function Assert-PublishedIdentity {
    $feedUri = "https://github.com/$repository/releases/latest/download/$feedName"
    $download = Join-Path ([System.IO.Path]::GetTempPath()) "$([guid]::NewGuid()).appinstaller"
    $feed = New-Object System.Xml.XmlDocument
    try {
        try {
            Invoke-WebRequest -Uri $feedUri -OutFile $download -UseBasicParsing
        }
        catch {
            if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) {
                return
            }
            throw "Could not read the published feed $feedUri to compare identities ($($_.Exception.Message))."
        }
        $feed.Load($download)
    }
    finally {
        Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue
    }

    $published = $feed.AppInstaller.MainBundle
    if ($published.GetAttribute('Name') -cne $identityName -or $published.GetAttribute('Publisher') -cne $identityPublisher) {
        throw "The published release is $($published.GetAttribute('Name')) ($($published.GetAttribute('Publisher'))), but this build would be $identityName ($identityPublisher), which installed copies can't update to. Set <GitHubIdentityName> and <GitHubIdentityPublisher> in $($projectFile.Name) to the published identity."
    }
    Write-Host "  The published release has the same identity ($identityName)."
}

function Invoke-GitHubMsixBuild {
    param(
        [Parameter(Mandatory)]
        [hashtable]$Build
    )

    $appxPackageDir = (Join-Path $Build.OutputDir '')

    if (Test-Path -LiteralPath $Build.OutputDir) {
        Remove-Item -LiteralPath $Build.OutputDir -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Build.OutputDir -Force | Out-Null

    $arguments = @(
        'publish'
        $projectPath
        '-c'
        $Configuration
        "-p:Platform=$($Build.Platform)"
        "-p:RuntimeIdentifier=$($Build.RuntimeIdentifier)"
        "-p:PublishProfile=$(Join-Path $publishProfilesRoot $Build.PublishProfile)"
        '-p:GenerateAppxPackageOnBuild=true'
        '-p:UapAppxPackageBuildMode=SideloadOnly'
        '-p:AppxBundle=Never'
        '-p:AppxPackageSigningEnabled=false'
        '-p:PackageCertificateThumbprint='
        "-p:AppxPackageDir=$appxPackageDir"
        # Packages the feed as Update.appinstaller (see the Content item in the csproj).
        "-p:ReleaseAppInstallerPath=$script:embeddedFeedPath"
    )

    Write-Host "Building GitHub MSIX for $($Build.Name)..."
    & dotnet @arguments

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $($Build.Name)."
    }
}

function Invoke-MsixSigning {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo[]]$Files
    )

    & $signCommand -Path $Files.FullName

    # Don't rely on the signing command's error reporting: confirm every package is signed.
    foreach ($file in $Files) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.Status -ne 'Valid') {
            throw "$($file.Name) is not validly signed (status: $($signature.Status))."
        }
        Write-Host "  Verified signature on $($file.Name): $($signature.SignerCertificate.Subject)"
    }
}

$scriptFailed = $false
try {
    foreach ($requiredFile in $projectPath, $manifestPath) {
        if (-not (Test-Path -LiteralPath $requiredFile)) {
            throw "Required file not found: $requiredFile"
        }
    }

    $manifestContent = [System.IO.File]::ReadAllText($manifestPath)
    $versionMatch = [regex]::Match($manifestContent, '(?s)<Identity\b[^>]*?\bVersion="([^"]+)"')
    if (-not $versionMatch.Success) {
        throw "Could not find Version attribute on <Identity> in $manifestPath."
    }
    $version = $versionMatch.Groups[1].Value
    $tag = Get-ReleaseTag -Version $version
    $versionedOutputDir = Join-Path $outputRoot "$version-gh"
    $manifestXml = [xml]$manifestContent
    $feedName = "$appName.appinstaller"
    $bundleName = "${appName}_$($tag.Substring(1)).msixbundle"
    $repository = Get-GitHubRepository

    if ($VerifyPublished) {
        Write-Host "Checking the published release of $appName $version ($repository)..."
        Test-PublishedRelease
        return
    }

    if ($NoSign -and -not $SkipRelease) {
        throw '-NoSign builds an unsigned bundle, which cannot be released. Add -SkipRelease.'
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'The dotnet SDK is required but was not found on PATH.'
    }

    # The publish profiles are committed (win-x64.pubxml, win-arm64.pubxml); an older clone may lack them.
    foreach ($build in $builds) {
        $profilePath = Join-Path $publishProfilesRoot $build.PublishProfile
        if (-not (Test-Path -LiteralPath $profilePath)) {
            throw "Publish profile not found: $profilePath"
        }
    }

    # Without this item MakeAppx fails late, after a full build, with a manifest validation error.
    if ([System.IO.File]::ReadAllText($projectPath) -notmatch '\$\(ReleaseAppInstallerPath\)') {
        throw "$projectPath has no ReleaseAppInstallerPath Content item. Add one linked as Update.appinstaller with CopyToOutputDirectory and CopyToPublishDirectory set to PreserveNewest."
    }

    if (-not $NoSign -and -not (Get-Command $signCommand -ErrorAction SilentlyContinue)) {
        throw "'$signCommand' was not found. It is defined in the maintainer's PowerShell profile; run this script from a pwsh session that loaded it, or pass -SkipRelease -NoSign to check the build only."
    }

    # The feed and every package carry the release identity: for a Store app, its GitHub identity.
    $releaseIdentity = Get-ReleaseIdentity -Manifest $manifestXml
    $identityName = $releaseIdentity.Name
    $identityPublisher = $releaseIdentity.Publisher

    Write-Host "Project: $appName ($projectPath), version $version"
    if ($releaseIdentity.Swapped) {
        Write-Host "Packaging as the GitHub identity $identityName ($identityPublisher); Package.appxmanifest keeps $($manifestXml.Package.Identity.GetAttribute('Name')) for the Store."
    }

    # Check git/gh state before the (slow) build so problems surface immediately.
    if (-not $SkipRelease) {
        $branch = Assert-ReleaseReady -Tag $tag
        Write-Host "Releasing $tag from branch '$branch'."
    }

    if (Test-Path -LiteralPath $versionedOutputDir) {
        Get-ChildItem -LiteralPath $versionedOutputDir -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in '.msix', '.msixbundle', '.appinstaller' } |
            Remove-Item -Force
    }
    else {
        New-Item -ItemType Directory -Path $versionedOutputDir -Force | Out-Null
    }

    # The feed is written first because every package embeds a copy of it.
    $frameworkPackages = @(Get-FrameworkPackages)
    $feedPath = Join-Path $versionedOutputDir $feedName
    $script:embeddedFeedPath = $feedPath
    New-AppInstallerFile -Path $feedPath -Repository $repository -BundleName $bundleName -FrameworkPackages $frameworkPackages
    Write-Host "Created App Installer feed $feedName for $repository ($(@($frameworkPackages).Count) framework package(s) listed)."

    # Only release builds get the GitHub identity and the Update.appinstaller reference; restore the
    # manifest afterwards. Abrupt process termination may require restoring it manually.
    $originalManifest = [System.IO.File]::ReadAllBytes($manifestPath)
    try {
        if ($releaseIdentity.Swapped) {
            Set-ReleaseIdentity -Path $manifestPath -Identity $releaseIdentity
        }
        Add-AutoUpdateReference -Path $manifestPath
        foreach ($build in $builds) {
            Invoke-GitHubMsixBuild -Build $build
        }
    }
    finally {
        [System.IO.File]::WriteAllBytes($manifestPath, $originalManifest)
    }

    Write-Host 'Collecting MSIX packages...'
    $stagingDir = Join-Path $outputRoot 'bundle-staging'
    if (Test-Path -LiteralPath $stagingDir) {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    foreach ($build in $builds) {
        # Exclude Dependencies\* (e.g. Microsoft.WindowsAppRuntime.2.msix) - only collect the app's own package.
        $msixFiles = @(Get-ChildItem -LiteralPath $build.OutputDir -Recurse -Filter '*.msix' -File |
            Where-Object { $_.FullName -notmatch '\\Dependencies\\' })
        if (-not $msixFiles) {
            throw "No .msix file was found under $($build.OutputDir) for $($build.Name)."
        }
        if ($msixFiles.Count -gt 1) {
            throw "Expected one app package under $($build.OutputDir) for $($build.Name), found: $(($msixFiles | ForEach-Object Name) -join ', ')."
        }
        foreach ($msix in $msixFiles) {
            Copy-Item -LiteralPath $msix.FullName -Destination (Join-Path $stagingDir $msix.Name) -Force
            Write-Host "  Staged $($msix.Name)"
        }
    }

    # One bundle holds both architectures: Windows installs the matching one, and updates are
    # applied per package block, so a small change downloads only the changed blocks.
    $bundlePath = Join-Path $versionedOutputDir $bundleName
    & (Find-MakeAppx) bundle /d $stagingDir /p $bundlePath /bv $version /o | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'makeappx bundle failed.'
    }

    # Only the bundle is signed; the framework packages are Microsoft's and keep their signature.
    if ($NoSign) {
        Write-Host 'Leaving the bundle unsigned (-NoSign): it is for checking the build only and cannot be installed.'
    }
    else {
        Invoke-MsixSigning -Files @(Get-Item -LiteralPath $bundlePath)
    }

    $assets = @(Get-Item -LiteralPath $bundlePath, $feedPath)
    foreach ($framework in $frameworkPackages) {
        $assets += Copy-Item -LiteralPath $framework.Source -Destination (Join-Path $versionedOutputDir $framework.AssetName) -Force -PassThru
    }

    Write-Host 'Verifying the release...'
    Test-ReleaseBundle -BundlePath $bundlePath -FeedPath $feedPath -FrameworkPackages $frameworkPackages
    Write-Host "Release files are under $versionedOutputDir"

    if (-not $SkipRelease) {
        $commit = (& git -C $repoRoot rev-parse HEAD).Trim()

        # Local-only tag; GitHub creates the remote tag at $commit when the draft is published.
        Write-Host "Creating local tag $tag at HEAD of '$branch' ($commit)..."
        & git -C $repoRoot tag $tag $commit
        if ($LASTEXITCODE -ne 0) {
            throw "git tag $tag failed."
        }

        # Always a draft: review and publish it manually on GitHub.
        $ghArguments = @('release', 'create', $tag) + $assets.FullName + @('--title', $tag, '--target', $commit, '--draft')
        $notesFile = $null
        if ($Notes) {
            # Pass notes via a file so quotes/newlines survive Windows PowerShell 5.1 native arg passing.
            $notesFile = [System.IO.Path]::GetTempFileName()
            [System.IO.File]::WriteAllText($notesFile, $Notes, [System.Text.UTF8Encoding]::new($false))
            $ghArguments += @('--notes-file', $notesFile)
        }
        else {
            $ghArguments += '--generate-notes'
        }
        if ($Prerelease) {
            $ghArguments += '--prerelease'
        }
        Write-Host "Creating draft GitHub release $tag..."
        Push-Location -LiteralPath $repoRoot
        try {
            & gh @ghArguments
            if ($LASTEXITCODE -ne 0) {
                throw "gh release create failed. Remove the local tag before retrying: git tag -d $tag"
            }
        }
        finally {
            Pop-Location
            if ($notesFile) {
                Remove-Item -LiteralPath $notesFile -Force -ErrorAction SilentlyContinue
            }
        }

        Write-Host "Done. Draft release $tag is saved; publishing it on GitHub will create the tag at $commit."
        Write-Host "Publish it as the latest release without changing the tag (the feed's addresses use $tag), then run: .\build-msix-for-gh.ps1 -VerifyPublished"
    }
    else {
        Write-Host 'Done.'
    }
}
catch {
    $scriptFailed = $true
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
}

if ($scriptFailed -and $Host.Name -eq 'ConsoleHost' -and -not [Console]::IsInputRedirected) {
    Write-Host ''
    Read-Host 'Build failed. Press Enter to close this window'
}

if ($scriptFailed) {
    exit 1
}
