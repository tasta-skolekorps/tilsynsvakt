<#
.SYNOPSIS
  Imports guards (data/guards.json) and shift assignments (data/roster-import.json) into the backend.
.DESCRIPTION
  Uses the admin API to create or reactivate guards, then signs them up for their shifts.
  Shifts in the past are skipped because the API rejects them. Shifts already held by another guard are reported.
  Secrets are read from the environment and never stored in the repo.
.EXAMPLE
  $env:TILSYNSVAKT_API_URL = "https://<backend>"; $env:TILSYNSVAKT_ADMIN_KEY = "<key>"; ./scripts/import-roster.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ApiUrl = $env:TILSYNSVAKT_API_URL,
    [string]$AdminKey = $env:TILSYNSVAKT_ADMIN_KEY
)

$ErrorActionPreference = "Stop"
if (-not $ApiUrl -or -not $AdminKey) { throw "Set TILSYNSVAKT_API_URL and TILSYNSVAKT_ADMIN_KEY." }
$ApiUrl = $ApiUrl.TrimEnd("/")
$root = Split-Path $PSScriptRoot -Parent
$guards = Get-Content "$root/data/guards.json" -Raw -Encoding utf8 | ConvertFrom-Json
$roster = Get-Content "$root/data/roster-import.json" -Raw -Encoding utf8 | ConvertFrom-Json
$headers = @{ Authorization = "Bearer $AdminKey" }
$today = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTime]::UtcNow, "Central European Standard Time").ToString("yyyy-MM-dd")

function Invoke-Api($method, $path, $body) {
    $args = @{ Method = $method; Uri = "$ApiUrl$path"; Headers = $headers; ContentType = "application/json; charset=utf-8" }
    if ($body) { $args.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress)) }
    for ($attempt = 1; ; $attempt++) {
        try { return Invoke-RestMethod @args }
        catch {
            if ($_.Exception.Response.StatusCode.value__ -ne 429 -or $attempt -ge 10) { throw }
            Start-Sleep -Seconds 15
        }
    }
}

$existing = @{}
Invoke-Api GET "/api/admin/guards" | ForEach-Object { $_ } | ForEach-Object { $existing[$_.name.ToLowerInvariant()] = $_ }

$ids = @{}
foreach ($guard in $guards) {
    $found = $existing[$guard.name.ToLowerInvariant()]
    if (-not $found) {
        if ($PSCmdlet.ShouldProcess($guard.name, "Create guard")) {
            $found = Invoke-Api POST "/api/admin/guards" @{ name = $guard.name; phone = $guard.phone }
        }
    } elseif (-not $found.active) {
        if ($PSCmdlet.ShouldProcess($guard.name, "Reactivate guard")) {
            $found = Invoke-Api PUT "/api/admin/guards/$($found.id)" @{ name = $found.name; phone = $found.phone; active = $true }
        }
    }
    if ($found) { $ids[$guard.name] = $found.id }
}

foreach ($shift in $roster) {
    if ($shift.date -lt $today) { Write-Host "Skipped past shift $($shift.date) ($($shift.guard))"; continue }
    $id = $ids[$shift.guard]
    if (-not $id) { Write-Warning "No guard id for $($shift.guard); skipped $($shift.date)"; continue }
    if ($PSCmdlet.ShouldProcess("$($shift.date)", "Sign up $($shift.guard)")) {
        try {
            $result = Invoke-Api POST "/api/shifts/$($shift.date)/signup" @{ guardId = $id }
            if ($result.guard.id -ne $id) { Write-Warning "$($shift.date) is held by $($result.guard.name), not $($shift.guard)" }
            else { Write-Host "OK $($shift.date) $($shift.guard)" }
        } catch { Write-Warning "$($shift.date) $($shift.guard): $($_.Exception.Message)" }
    }
}
