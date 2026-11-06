#Requires -Version 7
param(
    [string]$IsccPath = (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe')
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = Join-Path $repository 'artifacts\installer-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $IsccPath /Q ('/O' + $output) (Join-Path $PSScriptRoot 'CleanupTests.iss')
if ($LASTEXITCODE -ne 0) { throw 'Compiling cleanup regression tests failed' }

foreach ($keep in @($false, $true)) {
    $fixture = Join-Path ([IO.Path]::GetTempPath()) ('NaxExplorerCleanupTests-' + [guid]::NewGuid().ToString('N'))
    $outside = Join-Path $fixture 'outside'
    $junction = Join-Path $fixture 'junction'
    New-Item -ItemType Directory -Path $outside, $junction | Out-Null
    [IO.File]::WriteAllText((Join-Path $outside 'sentinel.txt'), 'outside survives')
    New-Item -ItemType Junction -Path (Join-Path $junction 'index') -Target $outside | Out-Null
    New-Item -ItemType Junction -Path (Join-Path $junction 'models') -Target $outside | Out-Null
    $log = Join-Path $output ('cleanup-' + $keep.ToString().ToLowerInvariant() + '.log')
    try {
        $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/FixtureRoot="' + $fixture + '"'), ('/LOG="' + $log + '"'))
        if ($keep) { $arguments += @('/keepdata', '/ExpectedKeep=1') }
        $process = Start-Process -FilePath (Join-Path $output 'CleanupTests.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
        if ($process.ExitCode -ne 0) { throw "Cleanup regressions failed: exit $($process.ExitCode), see $log" }
        $passes = @(Select-String -LiteralPath $log -Pattern 'PASS ')
        if ($passes.Count -ne 7) { throw "Expected seven passing scenarios, got $($passes.Count)" }
        $passes | ForEach-Object { $_.Line }
    }
    finally {
        # The generated absolute path must remain under the temporary directory before recursive cleanup.
        $resolved = [IO.Path]::GetFullPath($fixture)
        $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolved) -notlike 'NaxExplorerCleanupTests-*') { throw 'Unsafe fixture cleanup path' }
        # Remove any links left by a failed assertion before recursing through the fixture tree.
        foreach ($name in @('index', 'models')) {
            $link = Join-Path $junction $name
            if (Test-Path -LiteralPath $link) { [IO.Directory]::Delete($link) }
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
