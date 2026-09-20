param(
    [Parameter(Mandatory=$true)][string]$Scenario,
    [Parameter(Mandatory=$true)][int]$Port,
    [Parameter(Mandatory=$true)][string]$Tfm,
    [switch]$Normalize
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'NestedMapEvidence.csproj'
$outDir = Join-Path $PSScriptRoot "out-$Scenario-$Tfm-$($Normalize.IsPresent)"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

dotnet build $project -c Release -p:EvidenceTfm=$Tfm

$oldScenario = $env:SCENARIO
$oldPort = $env:PORT
$oldNormalize = $env:NORMALIZE_BACKSLASH
$env:SCENARIO = $Scenario
$env:PORT = "$Port"
$env:NORMALIZE_BACKSLASH = if ($Normalize) { '1' } else { '0' }

$stdout = Join-Path $outDir 'server.log'
$stderr = Join-Path $outDir 'server.err'
$proc = Start-Process dotnet -ArgumentList @(
    'run','--no-build','-c','Release',
    '--project',$project,
    "-p:EvidenceTfm=$Tfm"
) -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru

function Invoke-Raw([string]$Target, [bool]$Auth) {
    $client = [System.Net.Sockets.TcpClient]::new('127.0.0.1',$Port)
    try {
        $stream = $client.GetStream()
        $extra = if ($Auth) { "X-Admin-Key: research-only`r`n" } else { "" }
        $req = "GET $Target HTTP/1.1`r`nHost: localhost:$Port`r`n" + $extra + "Connection: close`r`n`r`n"
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($req)
        $stream.Write($bytes,0,$bytes.Length)
        $stream.Flush()

        $ms = [System.IO.MemoryStream]::new()
        $buffer = New-Object byte[] 8192
        do {
            $n = $stream.Read($buffer,0,$buffer.Length)
            if ($n -gt 0) { $ms.Write($buffer,0,$n) }
        } while ($n -gt 0)
        $rawBytes = $ms.ToArray()
        $rawText = [System.Text.Encoding]::Latin1.GetString($rawBytes)
        $sep = $rawText.IndexOf("`r`n`r`n")
        if ($sep -ge 0) {
            $headerText = $rawText.Substring(0,$sep)
            $headerByteCount = [System.Text.Encoding]::Latin1.GetByteCount($headerText) + 4
            $body = New-Object byte[] ($rawBytes.Length - $headerByteCount)
            [Array]::Copy($rawBytes,$headerByteCount,$body,0,$body.Length)
        } else {
            $headerText = $rawText
            $body = @()
        }

        $headers = @{}
        $headerLines = $headerText -split "`r`n"
        for ($i=1; $i -lt $headerLines.Length; $i++) {
            $idx = $headerLines[$i].IndexOf(':')
            if ($idx -gt 0) {
                $headers[$headerLines[$i].Substring(0,$idx).Trim().ToLowerInvariant()] = $headerLines[$i].Substring($idx+1).Trim()
            }
        }

        $sha = if ($body.Length -gt 0) {
            [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($body))
        } else { '' }

        return [ordered]@{
            target = $Target
            auth = $Auth
            status = $headerLines[0]
            content_length = $headers['content-length']
            etag = $headers['etag']
            body_sha256 = $sha
            contains_secret = $rawText.Contains('TOP_SECRET_7B4F')
            contains_auth_required = $rawText.Contains('AUTH_REQUIRED')
            raw = $rawText
        }
    } finally {
        $client.Dispose()
    }
}

try {
    $ready = $false
    for ($i=0; $i -lt 80; $i++) {
        try {
            $tcp = [System.Net.Sockets.TcpClient]::new('127.0.0.1',$Port)
            $tcp.Dispose()
            $ready = $true
            break
        } catch {
            Start-Sleep -Milliseconds 250
        }
    }
    if (-not $ready) { throw 'Server did not become ready' }

    $results = [ordered]@{
        scenario = $Scenario
        normalize = $Normalize.IsPresent
        tfm = $Tfm
        os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        normal_no_auth = Invoke-Raw '/api/admin/secret.txt' $false
        normal_with_auth = Invoke-Raw '/api/admin/secret.txt' $true
        bypass_full_backslash_no_auth = Invoke-Raw '/api%5Cadmin%5Csecret.txt' $false
        bypass_boundary_only_no_auth = Invoke-Raw '/api%5Cadmin/secret.txt' $false
        negative_after_admin_no_auth = Invoke-Raw '/api/admin%5Csecret.txt' $false
    }

    $json = $results | ConvertTo-Json -Depth 8
    $resultPath = Join-Path $outDir 'results.json'
    $json | Set-Content $resultPath
    Write-Host $json
    Write-Host '=== SERVER LOG ==='
    Get-Content $stdout
    Write-Host '=== SERVER ERR ==='
    Get-Content $stderr

    if ($Scenario -eq 'map' -and -not $Normalize -and $Tfm -eq 'net11.0' -and $IsWindows) {
        if (-not $results.normal_no_auth.status.Contains('401')) { throw 'normal_no_auth did not fail closed' }
        if (-not $results.normal_with_auth.status.Contains('200') -or -not $results.normal_with_auth.contains_secret) { throw 'authenticated control did not serve secret' }
        if (-not $results.bypass_full_backslash_no_auth.status.Contains('200') -or -not $results.bypass_full_backslash_no_auth.contains_secret) { throw 'expected bypass not reproduced' }
        if ($results.normal_with_auth.body_sha256 -ne $results.bypass_full_backslash_no_auth.body_sha256) { throw 'bypass did not return identical bytes' }
        if (-not $results.negative_after_admin_no_auth.status.Contains('401')) { throw 'negative control unexpectedly bypassed' }
    }
}
finally {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    $env:SCENARIO = $oldScenario
    $env:PORT = $oldPort
    $env:NORMALIZE_BACKSLASH = $oldNormalize
}
