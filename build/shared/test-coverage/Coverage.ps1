# Copyright (c) 2026 Neil Colvin.
# Licensed under the MIT License with Commons Clause. See LICENSE in this directory.
# Pure evidence parsing only. Suite selection, execution and skip policies belong to callers.
function Assert-CoverageTestNames($Expected,$Actual,[string]$Context) {
    $left=@($Expected | Sort-Object -CaseSensitive)
    $right=@($Actual | Sort-Object -CaseSensitive)
    if (!$left.Count -or $left.Count -ne $right.Count) { throw "$Context has empty or missing test results." }
    for ($index=0;$index -lt $left.Count;$index++) {
        if ($left[$index] -cne $right[$index]) { throw "$Context test identities differ: $($left[$index]) / $($right[$index])" }
    }
}
function Read-CoverageTestTree {
    param([string]$Path,[switch]$AdapterDump,[string[]]$ExtraCategories=@(),[scriptblock]$NormalizeName)
    $text=[IO.File]::ReadAllText($Path)
    if ($AdapterDump) {
        $matches=[regex]::Matches($text,'(?s)<test-run\b.*?</test-run>')
        if ($matches.Count -ne 1) { throw "Expected one discovery tree in $Path" }
        $text=$matches[0].Value
    }
    $xml=[Xml.XmlDocument]::new()
    $xml.XmlResolver=$null
    $xml.LoadXml($text)
    if ($xml.SelectNodes("//*[@runstate='NotRunnable']").Count) { throw "Invalid tests in $Path" }
    $cases=@($xml.SelectNodes('//test-case'))
    if (!$cases.Count) { throw "Empty suite in $Path" }
    foreach ($case in $cases) {
        $categories=@($case.SelectNodes("ancestor-or-self::*/properties/property[@name='Category']") | ForEach-Object { $_.GetAttribute('value') })
        $name=$case.GetAttribute('fullname')
        if ($NormalizeName) { $name=& $NormalizeName $name }
        $value=[ordered]@{Name=$name;Live=('Live' -in $categories);Processor=('Processor' -in $categories)}
        foreach ($category in $ExtraCategories) { $value[$category]=($category -in $categories) }
        $value.Result=$case.GetAttribute('result')
        $value.Label=$case.GetAttribute('label')
        $value.Reason=$case.SelectSingleNode('reason/message')
        [pscustomobject]$value
    }
}
function Assert-CoverageTestOutcome {
    param($Case,[bool]$AllowSkip,[string[]]$AllowedSkipReasons=@())
    if ($Case.Result -eq 'Passed') { return }
    if ($AllowSkip -and $Case.Processor -and $Case.Result -in @('Skipped','NotExecuted') -and $null -ne $Case.Reason) {
        foreach ($reason in $AllowedSkipReasons) {
            if ($Case.Reason.InnerText.Contains($reason)) { return }
        }
    }
    throw "Unexpected result for $($Case.Name): $($Case.Result)"
}
