#Requires -Version 7
<#
Windows-only: measures the dotnet-format subprocess's peak working set and CPU
(user + kernel), and those of its descendants, during generate --verify.
Use a disposable clone already prepared with the submodule, pinned installs and
one tool build. Generation rewrites its generated tree with identical bytes.
Without -Output, no report file is written. Every figure is a lower bound read
up to one poll interval (the report's LastGapMs) before exit; BuildHost's few
reads give a weak one.
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
if ((Resolve-Path $topLevel).Path -ne (Resolve-Path $Clone).Path) {
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
function Get-Descendants([int] $rootId, $byParent) {
    $queue = [System.Collections.Generic.Queue[int]]::new()
    $queue.Enqueue($rootId)
    while ($queue.Count -gt 0) {
        $parentId = $queue.Dequeue()
        foreach ($child in $byParent["$parentId"]) {
            $child
            $queue.Enqueue([int]$child.ProcessId)
        }
    }
}
$rows = [System.Collections.Generic.List[object]]::new()
for ($run = 1; $run -le $Runs; $run++) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new('dotnet', 'run --file tools/opencode-tool.cs -- generate --verify')
    $startInfo.WorkingDirectory = (Resolve-Path $Clone).Path
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $root = [System.Diagnostics.Process]::Start($startInfo)
    $stdout = $root.StandardOutput.ReadToEndAsync()
    $stderr = $root.StandardError.ReadToEndAsync()
    $tracked = @{}
    $formatId = $null
    try {
        while (-not $root.HasExited) {
            $snapshot = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CommandLine, Name)
            $byParent = $snapshot | Group-Object ParentProcessId -AsHashTable -AsString
            if ($null -eq $formatId) {
                $format = Get-Descendants $root.Id $byParent |
                    Where-Object { $_.CommandLine -like '*dotnet-format*' } | Select-Object -First 1
                if ($null -ne $format) {
                    $formatId = [int]$format.ProcessId
                }
            }
            if ($null -ne $formatId) {
                $subtree = @($snapshot | Where-Object { $_.ProcessId -eq $formatId }) +
                    @(Get-Descendants $formatId $byParent)
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
                        $tracked[$processId] = @{
                            Name = $entry.Name
                            Role = $role
                            Reads = [System.Collections.Generic.List[object]]::new()
                        }
                    }
                }
            }
            foreach ($processId in $tracked.Keys) {
                $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
                if ($null -eq $process) {
                    continue
                }
                try {
                    try {
                        $peak = $process.PeakWorkingSet64
                        $cpu = $process.TotalProcessorTime.TotalSeconds
                    } catch [System.InvalidOperationException] {
                        # The process exited between the lookup and the read.
                        continue
                    }
                    $tracked[$processId].Reads.Add(@{
                        Time = Get-Date
                        Peak = $peak
                        Cpu = $cpu
                    })
                } finally {
                    $process.Dispose()
                }
            }
        }
    } finally {
        try {
            if (-not $root.HasExited) {
                $root.Kill($true)
            }
            $root.WaitForExit()
            $null = $stdout.GetAwaiter().GetResult()
            $errorText = $stderr.GetAwaiter().GetResult()
            $exitCode = $root.ExitCode
        } finally {
            $root.Dispose()
        }
    }
    if ($exitCode -ne 0) {
        Write-Output "run ${run}: generate --verify exited $exitCode"
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
    $ordered | Export-Csv -Path $Output -NoTypeInformation
}
exit 0
