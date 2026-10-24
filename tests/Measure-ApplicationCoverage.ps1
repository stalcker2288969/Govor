[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [string]$AssemblyName = 'Govor.Application',
    [switch]$ExcludeGeneratedSources,
    [double]$MinimumLineCoverage = 98,
    [double]$MinimumBranchCoverage = 92
)

$ErrorActionPreference = 'Stop'
$lines = @{}
$branches = @{}
$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter coverage.json)
if ($reports.Count -eq 0) { throw "No Coverlet JSON reports found in $ResultsDirectory." }

foreach ($report in $reports) {
    $coverage = Get-Content -Raw -LiteralPath $report.FullName | ConvertFrom-Json -AsHashtable
    foreach ($module in $coverage.Keys) {
        if ([IO.Path]::GetFileName($module) -ne "$AssemblyName.dll") { continue }
        foreach ($document in $coverage[$module].Keys) {
            $file = $document.Replace('\', '/')
            $marker = $file.IndexOf("$AssemblyName/", [StringComparison]::Ordinal)
            if ($marker -ge 0) { $file = $file.Substring($marker) }
            if (!$lines.ContainsKey($file)) { $lines[$file] = @{}; $branches[$file] = @{} }
            foreach ($type in $coverage[$module][$document].Keys) {
                foreach ($methodName in $coverage[$module][$document][$type].Keys) {
                    $method = $coverage[$module][$document][$type][$methodName]
                    foreach ($line in $method.Lines.Keys) {
                        $lines[$file][$line] = ($method.Lines[$line] -gt 0) -or $lines[$file][$line]
                    }
                    foreach ($branch in $method.Branches) {
                        $key = "$type|$methodName|$($branch.Line)|$($branch.Offset)|$($branch.EndOffset)|$($branch.Path)"
                        if (!$branches[$file].ContainsKey($key)) {
                            $branches[$file][$key] = @{ Line = $branch.Line; Covered = $false }
                        }
                        $branches[$file][$key].Covered = ($branch.Hits -gt 0) -or $branches[$file][$key].Covered
                    }
                }
            }
        }
    }
}
if ($lines.Count -eq 0) { throw "$AssemblyName.dll was not instrumented." }

$files = @(foreach ($file in ($lines.Keys | Sort-Object)) {
    $missingLines = @($lines[$file].Keys | Where-Object { !$lines[$file][$_] } | ForEach-Object { [int]$_ } | Sort-Object)
    $missingBranches = @($branches[$file].Values | Where-Object { !$_.Covered } | ForEach-Object { [int]$_.Line } | Sort-Object -Unique)
    [pscustomobject]@{
        File = $file
        Lines = $lines[$file].Count
        CoveredLines = @($lines[$file].Values | Where-Object { $_ }).Count
        Branches = $branches[$file].Count
        CoveredBranches = @($branches[$file].Values | Where-Object { $_.Covered }).Count
        MissingLines = $missingLines
        MissingBranchLines = $missingBranches
    }
})
$assemblyFiles = $files
$generatedFiles = @($files | Where-Object { $_.File -match '/obj/' })
if ($ExcludeGeneratedSources) {
    $files = @($files | Where-Object { $_.File -notmatch '/obj/' })
}
$totalLines = ($files | Measure-Object Lines -Sum).Sum
$coveredLines = ($files | Measure-Object CoveredLines -Sum).Sum
$totalBranches = ($files | Measure-Object Branches -Sum).Sum
$coveredBranches = ($files | Measure-Object CoveredBranches -Sum).Sum
$lineCoverage = 100 * $coveredLines / [Math]::Max(1, $totalLines)
$branchCoverage = 100 * $coveredBranches / [Math]::Max(1, $totalBranches)
$summary = [ordered]@{
    Assembly = $AssemblyName
    ExcludeGeneratedSources = [bool]$ExcludeGeneratedSources
    ExcludedFiles = @(if ($ExcludeGeneratedSources) { $generatedFiles.File })
    EntireAssembly = [ordered]@{
        CoveredLines = ($assemblyFiles | Measure-Object CoveredLines -Sum).Sum
        TotalLines = ($assemblyFiles | Measure-Object Lines -Sum).Sum
        CoveredBranches = ($assemblyFiles | Measure-Object CoveredBranches -Sum).Sum
        TotalBranches = ($assemblyFiles | Measure-Object Branches -Sum).Sum
    }
    LineCoverage = [Math]::Round($lineCoverage, 2)
    BranchCoverage = [Math]::Round($branchCoverage, 2)
    CoveredLines = $coveredLines
    TotalLines = $totalLines
    CoveredBranches = $coveredBranches
    TotalBranches = $totalBranches
    Files = $files
}
$summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ResultsDirectory "$($AssemblyName.Split('.')[-1].ToLowerInvariant())-coverage.json")
Write-Output ("${AssemblyName}: {0:F2}% lines ({1}/{2}), {3:F2}% branches ({4}/{5})." -f
    $lineCoverage, $coveredLines, $totalLines, $branchCoverage, $coveredBranches, $totalBranches)
$files | Where-Object { $_.MissingLines.Count -gt 0 -or $_.MissingBranchLines.Count -gt 0 } |
    Sort-Object { $_.Lines - $_.CoveredLines } -Descending |
    Select-Object File, @{ Name = 'MissedLines'; Expression = { $_.MissingLines -join ',' } },
        @{ Name = 'MissedBranchLines'; Expression = { $_.MissingBranchLines -join ',' } } | Format-Table -Wrap

if ($lineCoverage -lt $MinimumLineCoverage -or $branchCoverage -lt $MinimumBranchCoverage) {
    throw "$AssemblyName coverage is below $MinimumLineCoverage% lines / $MinimumBranchCoverage% branches."
}
