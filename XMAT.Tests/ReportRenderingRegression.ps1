# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
# SPDX-License-Identifier: MIT
# Requires PowerShell 7 (pwsh) and Microsoft Edge. Not run by CI.

[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 30
)

function Find-RepositoryRoot {
    $directory = [System.IO.DirectoryInfo]::new($PSScriptRoot)
    while ($directory) {
        if (Test-Path -LiteralPath (
            Join-Path $directory.FullName 'XboxMultiplayerAnalysisTool.sln') -PathType Leaf) {
            return $directory.FullName
        }
        $directory = $directory.Parent
    }
    throw 'Could not locate XboxMultiplayerAnalysisTool.sln.'
}

function New-OwnedScratchRoot {
    param(
        [string]$RepositoryRoot
    )

    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        $path = Join-Path $RepositoryRoot (
            '.xmat-render-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        if ($path.Length -gt 120) {
            throw "The repository path leaves insufficient renderer fixture budget: $path"
        }
        try {
            New-Item -ItemType Directory -Path $path -ErrorAction Stop |
                Out-Null
            return $path
        }
        catch {
            if (Test-Path -LiteralPath $path -PathType Container) {
                continue
            }
            throw
        }
    }

    throw 'Could not reserve a unique renderer scratch directory.'
}

$ErrorActionPreference = 'Stop'
$repoRoot = Find-RepositoryRoot
$scratchRoot = $null
$reportDirectory = $null
$profileDirectory = $null
$labelMarkerId = 'xmat-label-marker'
$uriMarkerId = 'xmat-uri-marker'
$cMarkerId = 'xmat-c-marker'
$markerIds = @($labelMarkerId, $uriMarkerId, $cMarkerId)
$edgeProcess = $null
$exitCode = 0

function Resolve-EdgePath {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe')
    )

    $command = Get-Command msedge.exe -ErrorAction SilentlyContinue
    if ($command) {
        $candidates += $command.Source
    }

    $edgePath = $candidates |
        Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
        Select-Object -First 1
    if (-not $edgePath) {
        throw 'Microsoft Edge was not found.'
    }
    return $edgePath
}

function Stop-OwnedEdgeProcesses {
    param(
        [string]$OwnedRoot,
        [string]$UserDataDirectory
    )

    $fullOwnedRoot = [System.IO.Path]::GetFullPath($OwnedRoot)
    $fullUserDataDirectory =
        [System.IO.Path]::GetFullPath($UserDataDirectory)
    $expectedProfile = Join-Path $fullOwnedRoot 'edge-profile'
    if (-not [string]::Equals(
        $fullUserDataDirectory,
        $expectedProfile,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to stop Edge for an unowned profile: $UserDataDirectory"
    }

    $escapedProfile = [Regex]::Escape($fullUserDataDirectory)
    $dedicatedArgumentPattern =
        '(?i)(?:^|\s)(?:"--user-data-dir=' + $escapedProfile +
        '"|--user-data-dir="' + $escapedProfile +
        '"|--user-data-dir=' + $escapedProfile + ')(?=\s|$)'
    $ownedProcesses = Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'" |
        Where-Object {
            $_.CommandLine -and
            [Regex]::IsMatch(
                $_.CommandLine,
                $dedicatedArgumentPattern)
        }
    foreach ($process in $ownedProcesses) {
        $processId = [int]$process.ProcessId
        if (Get-Process -Id $processId -ErrorAction SilentlyContinue) {
            Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-RenderSnapshot {
    param(
        [string]$Dom,
        [string]$Id
    )

    $pattern = '<pre\b[^>]*\bid="' +
        [Regex]::Escape($Id) +
        '"[^>]*>(?<json>.*?)</pre>'
    $match = [Regex]::Match(
        $Dom,
        $pattern,
        [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) {
        throw "The renderer snapshot '$Id' was not captured."
    }
    $json = [Net.WebUtility]::HtmlDecode(
        $match.Groups['json'].Value)
    return ConvertFrom-Json -InputObject $json
}

function Assert-RenderSnapshot {
    param(
        [object]$Snapshot,
        [string]$ExpectedText,
        [string]$Name,
        [string[]]$ExpectedEndpointIds
    )

    foreach ($sink in @('table', 'legends', 'ticks')) {
        $values = @($Snapshot.$sink)
        if ($values.Count -ne 2) {
            throw "The $Name $sink sink rendered $($values.Count) endpoints instead of 2."
        }
        if (@($values | Where-Object { $_ -cne $ExpectedText }).Count) {
            throw "The $Name $sink sink did not render the expected endpoint text literally."
        }
    }

    if ([int]$Snapshot.barCount -ne 2) {
        throw "The $Name count graph rendered $($Snapshot.barCount) bars instead of 2."
    }

    foreach ($identitySet in @('barIds', 'timelineIds')) {
        $actualIds = @($Snapshot.$identitySet | Sort-Object)
        $expectedIds = @($ExpectedEndpointIds | Sort-Object)
        if (Compare-Object `
            -ReferenceObject $expectedIds `
            -DifferenceObject $actualIds) {
            throw "The $Name $identitySet did not preserve distinct endpoint identities."
        }
    }
}

try {
    $scratchRoot = New-OwnedScratchRoot -RepositoryRoot $repoRoot
    $reportDirectory = Join-Path $scratchRoot 'report with spaces # percent% unicode-雪'
    $profileDirectory = Join-Path $scratchRoot 'edge-profile'
    $resourceDirectory = Join-Path $repoRoot 'XMAT\Resources\html\xlta'
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $profileDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $resourceDirectory -File |
        Copy-Item -Destination $reportDirectory

    $hostileLabel = (
        '<img id="{0}" src="x" onerror="document.body.setAttribute(''data-xmat-injected'',''label'')">Label' -f $labelMarkerId)
    $hostileUri = (
        '<img id="{0}" src="x" onerror="document.body.setAttribute(''data-xmat-injected'',''uri'')">Uri' -f $uriMarkerId)
    $hostileC = (
        '<img id="{0}" src="x" onerror="document.body.setAttribute(''data-xmat-injected'',''c'')">C' -f $cMarkerId)
    $endpointIds = @(
        'Captured:colliding-endpoint',
        'SyntheticTelemetry:colliding-endpoint'
    )
    $stats = @(
        [ordered]@{
            EndpointId = $endpointIds[0]
            Label = $hostileLabel
            Uri = $hostileUri
            C = $hostileC
            'Call Count' = 1
            'Average Time Between Calls' = 1000
        },
        [ordered]@{
            EndpointId = $endpointIds[1]
            Label = $hostileLabel
            Uri = $hostileUri
            C = $hostileC
            'Call Count' = 2
            'Average Time Between Calls' = 2000
        }
    )
    $calls = [ordered]@{
        'Start Time' = 0
        'End Time' = 10000
        'Call List' = @(
            [ordered]@{
                EndpointId = $endpointIds[0]
                Label = $hostileLabel
                Uri = $hostileUri
                C = $hostileC
                Calls = @(
                    [ordered]@{
                        Id = 1
                        ReqTime = 1000
                        Uri = 'https://example.xboxlive.com/first'
                        C = $hostileC
                        'Request Body' = ''
                    }
                )
            },
            [ordered]@{
                EndpointId = $endpointIds[1]
                Label = $hostileLabel
                Uri = $hostileUri
                C = $hostileC
                Calls = @(
                    [ordered]@{
                        Id = 2
                        ReqTime = 2000
                        Uri = 'https://example.xboxlive.com/second'
                        C = $hostileC
                        'Request Body' = ''
                    }
                )
            }
        )
        AverageTimeLabel = 'Average Time'
        CallsPerSecondLabel = 'Calls Per Second'
        CallsPerEndpointLabel = 'Calls Per Endpoint'
        CallCountLabel = 'Call Count'
        EndpointLabel = 'Endpoint'
        SecondsLabel = ' seconds'
    }
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    $statsJson = ConvertTo-Json -InputObject $stats -Compress -Depth 10
    $callsJson = ConvertTo-Json -InputObject $calls -Compress -Depth 10
    [System.IO.File]::WriteAllText(
        (Join-Path $reportDirectory 'stats.js'),
        'stats = ' + $statsJson + ';',
        $utf8NoBom)
    [System.IO.File]::WriteAllText(
        (Join-Path $reportDirectory 'calls.js'),
        'calls = ' + $callsJson + ';',
        $utf8NoBom)

    $indexPath = Join-Path $reportDirectory 'index.html'
    $probeScript = @'
<script>
$(function() {
    function textValues(elements) {
        return elements.map(function() {
            return $(this).text();
        }).get();
    }

    function capture(name) {
        var snapshot = {
            table: textValues(
                statsPage.statDetails.find("tr").slice(1).find("td.endpoint")),
            legends: textValues(
                statsPage.timelineGraph.find(".legendLabel")),
            ticks: textValues(
                statsPage.callCountgraph.find(
                    ".flot-y-axis .flot-tick-label")),
            barCount: statsPage.callCountGraphData.length,
            barIds: $.map(
                statsPage.callCountGraphData,
                function(point) {
                    return point[1];
                }),
            timelineIds: $.map(
                statsPage.timelineGraphData,
                function(series) {
                    return series.id;
                })
        };
        $("<pre>")
            .attr("id", name)
            .text(JSON.stringify(snapshot))
            .appendTo(document.body);
    }

    statsPage = new StatsPage();
    statsPage.build(stats, calls);
    statsPage.show($("#displayArea").empty());
    statsPage._buildCountsGraph();
    statsPage._buildTimelineGraph();
    capture("xmat-uri-initial");
    statsPage.changeAPI("C");
    capture("xmat-c");
    statsPage.changeAPI("Uri");
    capture("xmat-uri-final");
});
</script>
'@
    $index = [System.IO.File]::ReadAllText($indexPath)
    if (-not $index.Contains('</body>')) {
        throw 'The report fixture has no closing body element.'
    }
    [System.IO.File]::WriteAllText(
        $indexPath,
        $index.Replace(
            '</body>',
            $probeScript + [Environment]::NewLine + '</body>'),
        $utf8NoBom)
    $reportUri = [System.Uri]::new($indexPath).AbsoluteUri
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = Resolve-EdgePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    @(
        '--headless=new',
        '--disable-gpu',
        '--no-first-run',
        '--no-default-browser-check',
        '--run-all-compositor-stages-before-draw',
        '--virtual-time-budget=5000',
        '--dump-dom',
        "--user-data-dir=$profileDirectory",
        $reportUri
    ) | ForEach-Object {
        [void]$startInfo.ArgumentList.Add($_)
    }

    $edgeProcess = [System.Diagnostics.Process]::new()
    $edgeProcess.StartInfo = $startInfo
    if (-not $edgeProcess.Start()) {
        throw 'Microsoft Edge did not start.'
    }
    $stdoutTask = $edgeProcess.StandardOutput.ReadToEndAsync()
    $stderrTask = $edgeProcess.StandardError.ReadToEndAsync()
    if (-not $edgeProcess.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-OwnedEdgeProcesses `
            -OwnedRoot $scratchRoot `
            -UserDataDirectory $profileDirectory
        throw "Microsoft Edge exceeded the ${TimeoutSeconds}-second timeout."
    }
    $dom = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($edgeProcess.ExitCode -ne 0) {
        throw "Microsoft Edge exited with code $($edgeProcess.ExitCode).`n$stderr"
    }

    if ($dom -notmatch 'class="stats-table"') {
        throw 'The statistics table did not render.'
    }
    if ($dom -notmatch '<canvas') {
        throw 'The Flot canvases did not render.'
    }
    if ($dom -notmatch 'class="legendLabel"') {
        throw 'The Flot timeline legend did not render.'
    }

    $initialUriSnapshot = Get-RenderSnapshot `
        -Dom $dom `
        -Id 'xmat-uri-initial'
    $cSnapshot = Get-RenderSnapshot `
        -Dom $dom `
        -Id 'xmat-c'
    $finalUriSnapshot = Get-RenderSnapshot `
        -Dom $dom `
        -Id 'xmat-uri-final'
    Assert-RenderSnapshot `
        -Snapshot $initialUriSnapshot `
        -ExpectedText $hostileLabel `
        -Name 'initial Uri' `
        -ExpectedEndpointIds $endpointIds
    Assert-RenderSnapshot `
        -Snapshot $cSnapshot `
        -ExpectedText $hostileC `
        -Name 'C' `
        -ExpectedEndpointIds $endpointIds
    Assert-RenderSnapshot `
        -Snapshot $finalUriSnapshot `
        -ExpectedText $hostileLabel `
        -Name 'final Uri' `
        -ExpectedEndpointIds $endpointIds

    foreach ($markerId in $markerIds) {
        if ($dom -match (
            '<img\b[^>]*\bid="' + [Regex]::Escape($markerId) + '"')) {
            throw "Capture-derived endpoint text created the injected IMG '$markerId'."
        }
    }
    if ($dom -match 'data-xmat-injected=') {
        throw 'Hostile endpoint text executed an onerror handler.'
    }
    if ($dom -match '&amp;lt;img') {
        throw 'Hostile endpoint text was double escaped.'
    }
    foreach ($markerId in @($labelMarkerId, $cMarkerId)) {
        if ($dom -notmatch (
            '&lt;img[^<]*' + [Regex]::Escape($markerId))) {
            throw "The hostile endpoint text '$markerId' was not visible literally."
        }
    }
    if ($dom -match [Regex]::Escape($uriMarkerId)) {
        throw 'The Uri field was shown instead of the endpoint Label.'
    }

    Write-Host 'Report rendering regression passed.'
}
catch {
    Write-Error $_ -ErrorAction Continue
    $exitCode = 1
}
finally {
    if ($edgeProcess -and $profileDirectory) {
        try {
            Stop-OwnedEdgeProcesses `
                -OwnedRoot $scratchRoot `
                -UserDataDirectory $profileDirectory
        }
        catch {
            Write-Warning "Failed to stop an owned Edge process: $_"
        }
        $edgeProcess.Dispose()
    }

    for ($attempt = 1;
        $scratchRoot -and $attempt -le 3;
        $attempt++) {
        if (-not (Test-Path -LiteralPath $scratchRoot)) {
            break
        }
        try {
            Remove-Item -LiteralPath $scratchRoot -Recurse -Force
        }
        catch {
            if ($attempt -eq 3) {
                Write-Warning "Failed to clean report rendering scratch directory: $scratchRoot"
            }
            else {
                Start-Sleep -Milliseconds 200
            }
        }
    }
}

exit $exitCode
