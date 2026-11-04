<#
.SYNOPSIS
Publishes Nax Explorer as a self-contained x64 app and builds the installer with Inno Setup 7.

.DESCRIPTION
The installer is written to artifacts\installer. The version comes from Directory.Build.props unless -Version is given.
Inno Setup 7 is expected at its standard per-user location, or at the path given with -IsccPath.
#>
#Requires -Version 7
param(
    [string]$Version,
    [string]$IsccPath = (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe')
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\NaxExplorer\NaxExplorer.csproj'

if (-not (Test-Path $IsccPath)) {
    throw "Inno Setup 7 was not found at $IsccPath. Install it with: winget install JRSoftware.InnoSetup.7"
}
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw "Directory.Build.props has no <Version>." }

$publishDir = Join-Path $root 'artifacts\publish\win-x64'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

Write-Host "Publishing Nax Explorer $Version"
dotnet publish $project -c Release -r win-x64 --self-contained -p:Platform=x64 -o $publishDir -p:Version=$Version -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
# Without these the app would only start on PCs that happen to have the same .NET installed, or AI search would fail.
foreach ($required in 'coreclr.dll', 'Nax-Explorer.exe', 'onnxruntime.dll', 'DirectML.dll', 'e_sqlite3.dll', 'Microsoft.WindowsAppRuntime.dll') {
    if (-not (Test-Path (Join-Path $publishDir $required))) { throw "The published app lacks $required." }
}

# The bundled .NET runtime, Windows App SDK, ONNX Runtime, DirectML, SQLite and other packages stay under their own
# licenses, which require their notices to travel with every copy. Their license and notice files are copied from the
# packages the build used.
Write-Host "Collecting third-party notices"
$deps = Get-Content (Join-Path $publishDir 'Nax-Explorer.deps.json') -Raw | ConvertFrom-Json
$assets = Get-Content (Join-Path $root 'src\NaxExplorer\obj\project.assets.json') -Raw | ConvertFrom-Json
# The folder the restore actually used, which a NuGet.Config can move away from the default.
$packages = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
# Packages that deps.json names are part of the app. The Windows App SDK packages copy their native files without a
# deps.json entry, so a package known only from the restore counts when one of its program files is in the publish
# folder. Packages used only while building (analyzers, source generators, build tools) have none and are left out.
$published = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
Get-ChildItem $publishDir -Recurse -File | ForEach-Object { [void]$published.Add($_.Name) }
$fromDeps = @($deps.libraries.PSObject.Properties | Where-Object { $_.Value.type -in 'package', 'runtimepack' } | ForEach-Object { $_.Name -replace '^runtimepack\.', '' })
$fromRestore = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' -and $_.Name -notin $fromDeps } | ForEach-Object { $_.Name })
$shipped = foreach ($component in $fromRestore) {
    $id, $componentVersion = $component.Split('/')
    $folder = Join-Path $packages "$($id.ToLowerInvariant())\$($componentVersion.ToLowerInvariant())"
    if (-not (Test-Path $folder)) { continue }
    $files = Get-ChildItem $folder -Recurse -File -Include *.dll, *.exe, *.winmd, *.msix -ErrorAction SilentlyContinue
    # The self-contained build unpacks the Windows App SDK runtime from the .msix files it carries into the app folder.
    if ($files | Where-Object { $_.Extension -eq '.msix' -or $published.Contains($_.Name) } | Select-Object -First 1) { $component }
    else { Write-Host "  Not bundled, left out of the notices: $component" }
}
$components = @($fromDeps) + @($shipped) | Sort-Object -Unique

# One file with every component's name, version, copyright and full license and notice texts. It is installed next to
# the app and kept in the repository root, where a change in the bundled packages shows up as a change to the file.
$notices = [System.Collections.Generic.List[string]]::new()
$notices.Add('Third-party components in Nax Explorer')
$notices.Add('')
$notices.Add('Nax Explorer includes the following components from other authors. As section 8 of the Nax Source-Available License says, they are not covered by that license; each stays under its own license, whose text follows. The list is taken from the published app and the texts from the packages the build used.')
$notices.Add('')
$notices.Add('The AI model multilingual-e5-small (Microsoft, MIT license) is not part of the installation. Nax Explorer downloads it from https://huggingface.co/intfloat/multilingual-e5-small when the user turns on AI search.')
$rule = '=' * 100
foreach ($component in $components) {
    $id, $componentVersion = $component.Split('/')
    $folder = Join-Path $packages "$($id.ToLowerInvariant())\$($componentVersion.ToLowerInvariant())"
    if (-not (Test-Path $folder)) { throw "The package folder of $component was not found at $folder." }
    $metadata = ([xml](Get-Content -Encoding utf8 (Get-ChildItem $folder -Filter *.nuspec | Select-Object -First 1).FullName)).package.metadata
    $notices.Add('')
    $notices.Add($rule)
    $notices.Add("$id $componentVersion")
    if ($metadata.copyright) { $notices.Add($metadata.copyright) }
    $notices.Add($rule)
    $files = Get-ChildItem $folder -File | Where-Object { $_.Name -match '^(license|notice|third-?party-?notices)' } | Sort-Object Name
    if ($files) {
        foreach ($file in $files) {
            $notices.Add('')
            $notices.Add("--- $($file.Name) ---")
            $notices.Add('')
            $notices.Add(((Get-Content $file.FullName -Raw -Encoding utf8) -replace "`r`n", "`n").TrimEnd())
        }
    }
    else {
        # The package's own description names its license, by expression or by address.
        $notices.Add('')
        $notices.Add('This package contains no license text of its own.')
        if ($metadata.license) { $notices.Add("License: $($metadata.license.'#text') (https://spdx.org/licenses/$($metadata.license.'#text').html)") }
        elseif ($metadata.licenseUrl) { $notices.Add("License: $($metadata.licenseUrl)") }
        else { throw "The package $component names no license." }
    }
}
$noticeText = (($notices -join "`n") -replace "`n", "`r`n") + "`r`n"
[System.IO.File]::WriteAllText((Join-Path $publishDir 'THIRD-PARTY-NOTICES.txt'), $noticeText, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText((Join-Path $root 'THIRD-PARTY-NOTICES.txt'), $noticeText, [System.Text.UTF8Encoding]::new($false))

Write-Host "Building the installer"
& $IsccPath /Q "/DAppVersion=$Version" "/DPublishDir=$publishDir" (Join-Path $root 'installer\Nax-Explorer.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

$setup = Join-Path $root "artifacts\installer\Nax-Explorer-$Version-setup.exe"
Write-Host "Installer written to $setup"
