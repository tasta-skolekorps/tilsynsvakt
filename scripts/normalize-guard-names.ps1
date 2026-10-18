<#
.SYNOPSIS
  One-off cleanup: makes every active guard use a child's last name (the canonical guard list).
.DESCRIPTION
  Reads all guards through the admin API and compares them with the canonical list of children's last names.
  For each active guard whose name is not canonical:
    - phone matches a canonical entry and an active guard with that canonical name exists:
      future duties (>= today, Europe/Oslo) are moved to the canonical guard, then the guard is deactivated;
    - phone matches but no active canonical guard exists: the guard is renamed (id and phone are kept).
      If an inactive guard already owns the canonical name, that guard is reactivated instead and the
      duties are moved to it, because guard names are unique;
    - no (or ambiguous) phone match: left untouched and reported as "needs manual handling".
  Only /api/admin/* endpoints are used. Credentials come from the environment and are never printed.
.PARAMETER CanonicalPath
  JSON file with [{ "name": "...", "phone": "..." }]. Defaults to `git show ee780fd~1:data/guards.json`.
.EXAMPLE
  $env:TILSYNSVAKT_API_URL = "https://<backend>"; $env:TILSYNSVAKT_ADMIN_KEY = "<key>"
  ./scripts/normalize-guard-names.ps1 -WhatIf
  ./scripts/normalize-guard-names.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ApiUrl = $env:TILSYNSVAKT_API_URL,
    [string]$AdminKey = $env:TILSYNSVAKT_ADMIN_KEY,
    [string]$AdminUsername = $env:TILSYNSVAKT_ADMIN_USERNAME,
    [string]$AdminPassword = $env:TILSYNSVAKT_ADMIN_PASSWORD,
    [string]$CanonicalPath,
    # Test hook: replaces the HTTP call with a script block taking ($Method, $Path, $Body).
    [scriptblock]$ApiInvoker
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

function Get-NameKey([string]$name) {
    if (-not $name) { return "" }
    (($name.Trim() -replace " {2,}", " ").Normalize([Text.NormalizationForm]::FormC)).ToUpperInvariant()
}

function Get-PhoneKey([string]$phone) {
    if (-not $phone) { return "" }
    $digits = $phone -replace "[^\d]", ""
    if ($digits.StartsWith("0047")) { $digits = $digits.Substring(4) }
    elseif ($digits.Length -eq 10 -and $digits.StartsWith("47")) { $digits = $digits.Substring(2) }
    $digits
}

function Get-CanonicalJson {
    if ($CanonicalPath) { return Get-Content $CanonicalPath -Raw -Encoding utf8 }
    $previous = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $json = (& git -C $root show "ee780fd~1:data/guards.json") -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "git show ee780fd~1:data/guards.json failed; pass -CanonicalPath." }
        return $json
    } finally { [Console]::OutputEncoding = $previous }
}

if (-not $ApiInvoker) {
    if (-not $ApiUrl) { throw "Set TILSYNSVAKT_API_URL." }
    $ApiUrl = $ApiUrl.TrimEnd("/")
    if ($AdminKey) { $authorization = "Bearer $AdminKey" }
    elseif ($AdminUsername -and $AdminPassword) {
        $authorization = "Basic " + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${AdminUsername}:${AdminPassword}"))
    } else { throw "Set TILSYNSVAKT_ADMIN_KEY, or TILSYNSVAKT_ADMIN_USERNAME and TILSYNSVAKT_ADMIN_PASSWORD." }
    $headers = @{ Authorization = $authorization }
}

function Invoke-Api([string]$Method, [string]$Path, $Body) {
    if ($ApiInvoker) { return & $ApiInvoker $Method $Path $Body }
    $request = @{ Method = $Method; Uri = "$ApiUrl$Path"; Headers = $headers; ContentType = "application/json; charset=utf-8" }
    if ($null -ne $Body) { $request.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Compress)) }
    for ($attempt = 1; ; $attempt++) {
        try { return Invoke-RestMethod @request }
        catch {
            $status = $_.Exception.Response.StatusCode.value__
            if ($status -ne 429 -or $attempt -ge 10) { throw }
            Write-Verbose "429 from $Method $Path; retrying in 15 s (attempt $attempt)"
            Start-Sleep -Seconds 15
        }
    }
}

function Get-OsloToday {
    $zone = try { [TimeZoneInfo]::FindSystemTimeZoneById("Europe/Oslo") } catch { [TimeZoneInfo]::FindSystemTimeZoneById("W. Europe Standard Time") }
    [TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::UtcNow, $zone).ToString("yyyy-MM-dd")
}

$canonical = @(Get-CanonicalJson | ConvertFrom-Json)
if ($canonical.Count -eq 0) { throw "The canonical guard list is empty." }
$canonicalByName = @{}
$canonicalByPhone = @{}
foreach ($entry in $canonical) {
    $canonicalByName[(Get-NameKey $entry.name)] = $entry
    $phoneKey = Get-PhoneKey $entry.phone
    if (-not $phoneKey) { continue }
    if (-not $canonicalByPhone.ContainsKey($phoneKey)) { $canonicalByPhone[$phoneKey] = [System.Collections.Generic.List[object]]::new() }
    $canonicalByPhone[$phoneKey].Add($entry)
}

$guards = [System.Collections.Generic.List[object]]::new()
Invoke-Api GET "/api/admin/guards" $null | ForEach-Object { $_ } | ForEach-Object { $guards.Add($_) }
$activeGuards = @($guards | Where-Object { $_.active } | Sort-Object id)

$today = Get-OsloToday
$year = [int]$today.Substring(0, 4)
$futureDuties = [System.Collections.Generic.List[object]]::new()
$seenDates = @{}
foreach ($query in @(@("spring", $year), @("autumn", $year), @("spring", ($year + 1)), @("autumn", ($year + 1)))) {
    $list = Invoke-Api GET "/api/admin/duties?season=$($query[0])&year=$($query[1])" $null
    foreach ($duty in @($list.duties)) {
        if ($duty.date -ge $today -and $duty.guard -and -not $seenDates.ContainsKey($duty.date)) {
            $seenDates[$duty.date] = $true
            $futureDuties.Add($duty)
        }
    }
}

function Find-Guard([string]$nameKey, [bool]$active) {
    $guards | Where-Object { (Get-NameKey $_.name) -eq $nameKey -and [bool]$_.active -eq $active } | Select-Object -First 1
}

function Move-FutureDuties($from, $to) {
    $moved = 0
    foreach ($duty in @($futureDuties | Where-Object { $_.guard.id -eq $from.id })) {
        if ($PSCmdlet.ShouldProcess("$($duty.date)", "Move duty from '$($from.name)' (#$($from.id)) to '$($to.name)' (#$($to.id))")) {
            $checkOut = if ($duty.hasRecordedCheckOut) { $duty.endTime } else { $null }
            Invoke-Api PUT "/api/admin/duties/$($duty.date)" @{ guardId = $to.id; checkOutTime = $checkOut } | Out-Null
        }
        $duty.guard = [pscustomobject]@{ id = $to.id; name = $to.name; phone = $to.phone }
        $moved++
    }
    $moved
}

$merged = [System.Collections.Generic.List[string]]::new()
$renamed = [System.Collections.Generic.List[string]]::new()
$manual = [System.Collections.Generic.List[string]]::new()
$failed = [System.Collections.Generic.List[string]]::new()
$alreadyOk = 0

foreach ($guard in $activeGuards) {
    if ($canonicalByName.ContainsKey((Get-NameKey $guard.name))) { $alreadyOk++; continue }

    $label = "'$($guard.name)' (#$($guard.id), $($guard.phone))"
    $phoneMatches = $canonicalByPhone[(Get-PhoneKey $guard.phone)]
    if (-not $phoneMatches) { $manual.Add("$label - no phone match in the canonical list"); continue }
    $targetNames = @($phoneMatches | ForEach-Object { $_.name } | Sort-Object -Unique)
    if ($targetNames.Count -gt 1) { $manual.Add("$label - phone matches several children: $($targetNames -join ', ')"); continue }

    $targetName = $targetNames[0]
    $targetKey = Get-NameKey $targetName
    try {
        $target = Find-Guard $targetKey $true
        if (-not $target) {
            $inactive = Find-Guard $targetKey $false
            if ($inactive) {
                if ($PSCmdlet.ShouldProcess("'$($inactive.name)' (#$($inactive.id))", "Reactivate canonical guard")) {
                    Invoke-Api PUT "/api/admin/guards/$($inactive.id)" @{ name = $inactive.name; phone = $inactive.phone; active = $true } | Out-Null
                }
                $inactive.active = $true
                $target = $inactive
            }
        }

        if ($target) {
            $count = Move-FutureDuties $guard $target
            if ($PSCmdlet.ShouldProcess($label, "Deactivate guard")) {
                Invoke-Api DELETE "/api/admin/guards/$($guard.id)" $null | Out-Null
            }
            $guard.active = $false
            $merged.Add("$label -> '$($target.name)' (#$($target.id)), $count future duties moved")
        } else {
            if ($PSCmdlet.ShouldProcess($label, "Rename guard to '$targetName'")) {
                Invoke-Api PUT "/api/admin/guards/$($guard.id)" @{ name = $targetName; phone = $guard.phone; active = $true } | Out-Null
            }
            $guard.name = $targetName
            $renamed.Add("$label -> '$targetName'")
        }
    } catch {
        $failed.Add("$label - $($_.Exception.Message)")
    }
}

$mode = if ($WhatIfPreference) { " (dry run, nothing changed)" } else { "" }
Write-Host ""
Write-Host "Guard name cleanup summary$mode"
Write-Host "  Canonical names: $($canonicalByName.Count); active guards checked: $($activeGuards.Count); already canonical: $alreadyOk; future duties scanned: $($futureDuties.Count)"
foreach ($section in @(@("Merged into canonical guard", $merged), @("Renamed", $renamed), @("Needs manual handling", $manual), @("Failed", $failed))) {
    Write-Host "  $($section[0]): $($section[1].Count)"
    foreach ($line in $section[1]) { Write-Host "    - $line" }
}
if ($failed.Count -gt 0) { exit 1 }
