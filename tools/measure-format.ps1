#Requires -Version 7
<#
Windows-only: measures the dotnet-format subprocess's peak working set and CPU
(user + kernel), and those of its descendants, during generate --verify.
Use a disposable clone already prepared with the submodule, pinned installs and
one tool build. Generation rewrites its generated tree with identical bytes.
Without -Output, no report file is written. Every figure is a lower bound read
up to one poll interval (the report's LastGapMs approximates it) before exit;
BuildHost's few reads give a weak one.
Usage: pwsh -File tools/measure-format.ps1 -Clone <path> [-Runs 5] [-Output <csv path>]
#>
param(
    [Parameter(Mandatory)] [string] $Clone,
    [ValidateRange(1, 50)] [int] $Runs = 5,
    [string] $Output
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) {
    Write-Output 'Windows-only measurement procedure'
    exit 1
}
$topLevel = git -C $Clone rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Output 'Clone is not a git work tree'
    exit 1
}
if ((Resolve-Path -LiteralPath $topLevel).Path.TrimEnd('\', '/') -ne
    (Resolve-Path -LiteralPath $Clone).Path.TrimEnd('\', '/')) {
    Write-Output 'Clone must be the git work tree root'
    exit 1
}
$status = git -C $Clone status --porcelain
if ($LASTEXITCODE -ne 0) {
    Write-Output 'Cannot read Clone git status'
    exit 1
}
if ($status) {
    Write-Output 'Clone is not clean (including untracked files and submodule changes)'
    exit 1
}
function New-TrackedEntry($entry, [string] $role) {
    $trackedEntry = @{
        Name = $entry.Name
        Role = $role
        Reads = [System.Collections.Generic.List[object]]::new()
        Process = $null
        Finished = $false
    }
    $process = $null
    try {
        $process = [System.Diagnostics.Process]::GetProcessById([int]$entry.ProcessId)
        $null = $process.get_Handle()
        $start = $process.get_StartTime()
        $created = $entry.CreationDate
        if ([math]::Abs(($start - $created).TotalSeconds) -le 1) {
            $trackedEntry.Process = $process
        } else {
            # A reused PID belongs to a different process than the snapshot captured.
            $trackedEntry.Finished = $true
        }
    } catch [System.ArgumentException] {
        # The process is no longer running.
        $trackedEntry.Finished = $true
    } catch [System.InvalidOperationException] {
        # The process exited before its handle was taken.
        $trackedEntry.Finished = $true
    } finally {
        if ($null -eq $trackedEntry.Process -and $null -ne $process) {
            $process.Dispose()
        }
    }
    return $trackedEntry
}
function Get-Descendants([int] $rootId, [datetime] $rootCreated, $byParent) {
    $queue = [System.Collections.Generic.Queue[object]]::new()
    $queue.Enqueue(@{
        ProcessId = $rootId
        CreationDate = $rootCreated
    })
    $visited = [System.Collections.Generic.HashSet[int]]::new()
    $null = $visited.Add($rootId)
    while ($queue.Count -gt 0) {
        $parent = $queue.Dequeue()
        $parentId = $parent.ProcessId
        foreach ($child in $byParent["$parentId"]) {
            if ($child.CreationDate -ge $parent.CreationDate -and $visited.Add([int]$child.ProcessId)) {
                $child
                $queue.Enqueue($child)
            }
        }
    }
}
$rows = [System.Collections.Generic.List[object]]::new()
for ($run = 1; $run -le $Runs; $run++) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new('dotnet', 'run --file tools/opencode-tool.cs -- generate --verify')
    $startInfo.WorkingDirectory = (Resolve-Path -LiteralPath $Clone).Path
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $root = [System.Diagnostics.Process]::Start($startInfo)
    $stdout = $root.StandardOutput.ReadToEndAsync()
    $stderr = $root.StandardError.ReadToEndAsync()
    $tracked = @{}
    $formatId = $null
    try {
        $rootCreated = $root.get_StartTime()
        while (-not $root.get_HasExited()) {
            $snapshot = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CommandLine, Name, CreationDate)
            $byParent = $snapshot | Group-Object ParentProcessId -AsHashTable -AsString
            if ($null -eq $formatId) {
                $format = Get-Descendants $root.Id $rootCreated $byParent |
                    Where-Object { $_.CommandLine -like '*dotnet-format*' } | Select-Object -First 1
                if ($null -ne $format) {
                    $formatId = [int]$format.ProcessId
                    $formatCreated = $format.CreationDate
                    $tracked[$formatId] = New-TrackedEntry $format 'format'
                }
            }
            if ($null -ne $formatId) {
                $formatProcess = $tracked[$formatId].Process
            }
            if ($null -ne $formatId -and $null -ne $formatProcess -and -not $formatProcess.get_HasExited()) {
                $subtree = @($snapshot | Where-Object { $_.ProcessId -eq $formatId }) +
                    @(Get-Descendants $formatId $formatCreated $byParent)
                foreach ($entry in $subtree) {
                    $processId = [int]$entry.ProcessId
                    if (-not $tracked.ContainsKey($processId)) {
                        $role = if ($processId -eq $formatId) {
                            'format'
                        } elseif ($entry.CommandLine -like '*BuildHost*') {
                            'buildhost'
                        } else {
                            'descendant'
                        }
                        $tracked[$processId] = New-TrackedEntry $entry $role
                    }
                }
            }
            foreach ($processId in $tracked.Keys) {
                $entry = $tracked[$processId]
                $process = $entry.Process
                if ($null -eq $process -or $entry.Finished) {
                    continue
                }
                if ($process.get_HasExited()) {
                    $entry.Finished = $true
                    continue
                }
                try {
                    $process.Refresh()
                    $peak = $process.get_PeakWorkingSet64()
                    $cpu = $process.get_TotalProcessorTime().TotalSeconds
                } catch [System.InvalidOperationException] {
                    # The process exited between the get_HasExited() check and the read.
                    if ($_.Exception.InnerException -is [System.ComponentModel.Win32Exception]) {
                        throw
                    }
                    continue
                }
                $entry.Reads.Add(@{
                    Time = Get-Date
                    Peak = $peak
                    Cpu = $cpu
                })
            }
        }
    } finally {
        try {
            if (-not $root.get_HasExited()) {
                $root.Kill($true)
            }
            $root.WaitForExit()
            $outputText = $stdout.GetAwaiter().GetResult()
            $errorText = $stderr.GetAwaiter().GetResult()
            $exitCode = $root.ExitCode
        } finally {
            foreach ($entry in $tracked.Values) {
                if ($null -ne $entry.Process) {
                    $entry.Process.Dispose()
                }
            }
            $root.Dispose()
        }
    }
    if ($exitCode -ne 0) {
        Write-Output "run ${run}: generate --verify exited $exitCode"
        $outputText -split '\r?\n' | Select-Object -Last 20 | Write-Output
        $errorText -split '\r?\n' | Select-Object -Last 20 | Write-Output
        exit 1
    }
    if ($null -eq $formatId) {
        Write-Output "run ${run}: no format process found"
        exit 1
    }
    foreach ($processId in $tracked.Keys) {
        $entry = $tracked[$processId]
        $reads = $entry.Reads
        $last = if ($reads.Count -gt 0) { $reads[$reads.Count - 1] }
        $rows.Add([pscustomobject]@{
            Run = $run
            Pid = $processId
            Role = $entry.Role
            Name = $entry.Name
            Reads = $reads.Count
            PeakMiB = if ($last) { ($last.Peak / 1MB).ToString('F1', [cultureinfo]::InvariantCulture) }
            CpuSeconds = if ($last) { $last.Cpu.ToString('F1', [cultureinfo]::InvariantCulture) }
            LastGapMs = if ($reads.Count -gt 1) {
                [math]::Round(($last.Time - $reads[$reads.Count - 2].Time).TotalMilliseconds)
            }
        })
    }
}
$ordered = $rows | Sort-Object Run, { @('format', 'buildhost', 'descendant').IndexOf($_.Role) }, Pid
$ordered | Format-Table -AutoSize
if ($Output) {
    $ordered | Export-Csv -LiteralPath $Output -NoTypeInformation
}
exit 0
