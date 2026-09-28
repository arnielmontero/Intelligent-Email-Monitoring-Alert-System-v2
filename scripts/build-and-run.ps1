# Builds and starts IEMAS (database, API, CMS, proxy) with Docker, then checks that everything works.
# Started by Build-IEMAS.bat in the project folder; can also be run directly:
#   powershell -ExecutionPolicy Bypass -File scripts\build-and-run.ps1 [-NoBrowser] [-CheckOnly]
# -CheckOnly skips building and starting and only checks the running system.
param([switch]$NoBrowser, [switch]$CheckOnly)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$compose = @('compose', '-f', 'docker-compose.yml', '-f', 'docker-compose.prod.yml')
$logDir = Join-Path $root 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir ("build-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$problems = New-Object System.Collections.Generic.List[string]

function Step($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Ok($text) { Write-Host "   OK    $text" -ForegroundColor Green }
function Warn($text) { Write-Host "   WARN  $text" -ForegroundColor Yellow; $problems.Add("WARN: $text") }
function Fail($text) { Write-Host "   FAIL  $text" -ForegroundColor Red; $problems.Add("FAIL: $text") }
function Stop-WithError($text) {
    Fail $text
    Write-Host ""
    Write-Host "Stopped. Full log: $log" -ForegroundColor Red
    exit 1
}

# Runs docker with the given arguments, copies all output to the log, and returns the exit code.
function Invoke-Docker([string[]]$arguments, [switch]$Quiet) {
    Add-Content -Path $log -Value ("`r`n> docker " + ($arguments -join ' '))
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # docker writes progress to stderr; that is not an error
    & docker @arguments 2>&1 | ForEach-Object {
        $line = "$_"
        Add-Content -Path $log -Value $line
        if (-not $Quiet) { Write-Host "   $line" -ForegroundColor DarkGray }
    }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previous
    return $code
}

function Get-HttpStatus([string]$url) {
    try {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10 -MaximumRedirection 0
        return [int]$response.StatusCode
    } catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        return 0
    }
}

Write-Host "IEMAS - build and start" -ForegroundColor White
Write-Host "Project: $root"
Write-Host "Log:     $log"

# 1. Docker ---------------------------------------------------------------------------------------
Step "Checking Docker"
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Stop-WithError "Docker is not installed. Install Docker Desktop, then run this again."
}
if ((Invoke-Docker @('info') -Quiet) -ne 0) {
    $desktop = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
    if (-not (Test-Path $desktop)) { Stop-WithError "Docker is not running and Docker Desktop was not found. Start Docker, then run this again." }
    Write-Host "   Starting Docker Desktop (this can take a minute)..."
    Start-Process $desktop
    $deadline = (Get-Date).AddMinutes(3)
    do {
        Start-Sleep -Seconds 5
        $ready = (Invoke-Docker @('info') -Quiet) -eq 0
    } until ($ready -or (Get-Date) -gt $deadline)
    if (-not $ready) { Stop-WithError "Docker did not start within 3 minutes. Open Docker Desktop, wait until it says it is running, then run this again." }
}
Ok "Docker is running"

# 2. Settings -------------------------------------------------------------------------------------
Step "Checking settings (.env)"
if (-not (Test-Path (Join-Path $root '.env'))) {
    Copy-Item (Join-Path $root '.env.example') (Join-Path $root '.env')
    Stop-WithError "No .env file was found, so one was created from .env.example. Open .env, fill in the passwords and secrets, then run this again."
}
$envText = Get-Content (Join-Path $root '.env') -Raw
foreach ($key in 'POSTGRES_PASSWORD', 'JWT_SECRET', 'AGENT_JWT_SECRET', 'CREDENTIAL_ENCRYPTION_KEY', 'BOOTSTRAP_ADMIN_EMAIL', 'BOOTSTRAP_ADMIN_PASSWORD') {
    if ($envText -notmatch "(?m)^$key=\S+") { Stop-WithError "$key is empty in .env. Fill it in, then run this again." }
}
if ((Invoke-Docker ($compose + @('config', '-q')) -Quiet) -ne 0) { Stop-WithError "The Docker configuration is invalid - see the log." }
Ok ".env and Docker configuration are valid"

$started = Get-Date
if (-not $CheckOnly) {
    # 3. Build ------------------------------------------------------------------------------------
    Step "Building the API and the CMS (the first build can take several minutes)"
    if ((Invoke-Docker ($compose + @('build', 'api', 'web'))) -ne 0) { Stop-WithError "The build failed - the error is shown above and in the log." }
    Ok "Build succeeded"

    # 4. Start ------------------------------------------------------------------------------------
    Step "Starting database, API, CMS and proxy"
    $started = Get-Date
    if ((Invoke-Docker ($compose + @('up', '-d', '--remove-orphans'))) -ne 0) { Stop-WithError "The containers could not be started - see above and the log." }
    Ok "Containers started"
} else {
    # Look back over the last hour of the API log when only checking.
    $started = (Get-Date).AddHours(-1)
}

# 5. Verify ---------------------------------------------------------------------------------------
Step "Checking that everything works"

foreach ($name in 'iemas-postgres', 'iemas-api', 'iemas-web', 'iemas-proxy') {
    $state = (& docker inspect -f '{{.State.Status}}' $name 2>$null)
    if ($state -eq 'running') { Ok "$name is running" } else { Fail "$name is not running (state: $state). Logs: docker logs $name" }
}

# The API applies database updates when it starts; wait until it answers. The request is made from inside the
# API container (its image has bash but no curl), so the full report is read whatever the HTTP status is.
# Only single quotes inside: Windows PowerShell drops double quotes from arguments given to programs.
$probe = 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf ''GET /health/ready HTTP/1.0\r\nHost: localhost\r\n\r\n'' >&3 && cat <&3'
$health = $null
$deadline = (Get-Date).AddMinutes(3)
do {
    Start-Sleep -Seconds 3
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $raw = (& docker exec iemas-api bash -c $probe 2>$null) -join "`n"
    $ErrorActionPreference = $previous
    if ($raw -match '^HTTP/\S+ (\d{3})' -and $raw.Contains('{')) { $health = $raw }
} until ($health -or (Get-Date) -gt $deadline)

if (-not $health) {
    Fail "The API did not answer within 3 minutes. Logs: docker logs iemas-api"
} else {
    try { $report = $health.Substring($health.IndexOf('{')) | ConvertFrom-Json } catch { $report = $null }
    if ($report -eq $null) {
        Warn "The API answered but its health report could not be read."
    } else {
        foreach ($check in $report.checks) {
            $detail = if ($check.description) { " - $($check.description)" } else { "" }
            switch ($check.status) {
                'Healthy'   { Ok "API check $($check.name)$detail" }
                'Degraded'  { Warn "API check $($check.name) is degraded$detail" }
                default     { Fail "API check $($check.name) is $($check.status)$detail" }
            }
        }
    }
}

$webPort = if ($envText -match '(?m)^WEB_PORT=(\d+)') { $Matches[1] } else { '8091' }
$proxyPort = if ($envText -match '(?m)^PROXY_HTTPS_PORT=(\d+)') { $Matches[1] } else { '8443' }
$cmsUrl = "http://localhost:$webPort"

$code = Get-HttpStatus "$cmsUrl/"
if ($code -eq 200) { Ok "CMS opens at $cmsUrl" } else { Fail "CMS at $cmsUrl answered $code" }

# Without signing in the API must refuse (401) - that proves the CMS reaches the API.
$code = Get-HttpStatus "$cmsUrl/api/v1/dashboard/summary"
if ($code -eq 401) { Ok "CMS reaches the API" } else { Fail "CMS -> API answered $code (expected 401 before sign-in)" }

# The proxy serves HTTPS (plain HTTP is redirected) with a local certificate on this PC, so the certificate isn't
# validated (-k). Windows' own curl.exe is used; Windows PowerShell can't skip the check reliably.
$curl = Join-Path $env:SystemRoot 'System32\curl.exe'
if (Test-Path $curl) {
    $code = (& $curl -sk -o NUL -w '%{http_code}' --max-time 10 "https://localhost:$proxyPort/" 2>$null)
    if ($code -eq '200') { Ok "Proxy answers at https://localhost:$proxyPort" } else { Warn "Proxy at https://localhost:$proxyPort answered $code" }
} else {
    Warn "curl.exe not found, so the proxy at https://localhost:$proxyPort was not checked"
}

# Errors the API logged since this start (a failed database update would show here).
$since = $started.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$apiErrors = @(& docker logs iemas-api --since $since 2>&1 | Where-Object { "$_" -match '\[(ERR|FTL)\]' })
if ($apiErrors.Count -eq 0) {
    Ok "No errors in the API log since start"
} else {
    Warn "$($apiErrors.Count) error line(s) in the API log since start (latest below; full log: docker logs iemas-api)"
    $apiErrors | Select-Object -Last 5 | ForEach-Object { Write-Host "         $_" -ForegroundColor Yellow }
}

# 6. Result ---------------------------------------------------------------------------------------
Write-Host ""
$failures = @($problems | Where-Object { $_ -like 'FAIL*' }).Count
$warnings = @($problems | Where-Object { $_ -like 'WARN*' }).Count
Add-Content -Path $log -Value ("`r`nResult: {0} failure(s), {1} warning(s)`r`n{2}" -f $failures, $warnings, ($problems -join "`r`n"))

if ($failures -gt 0) {
    Write-Host "IEMAS started with $failures problem(s) - see FAIL lines above. Log: $log" -ForegroundColor Red
    exit 1
}
if ($warnings -gt 0) {
    Write-Host "IEMAS is running, with $warnings warning(s) above." -ForegroundColor Yellow
} else {
    Write-Host "IEMAS is running. Everything checked out." -ForegroundColor Green
}
Write-Host "Open the CMS: $cmsUrl"
if (-not $NoBrowser) { Start-Process $cmsUrl }
exit 0
