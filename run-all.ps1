<#
run-all.ps1
Starts the main services (Orchestrator, Tosin persona) in separate PowerShell windows,
using OPENAI_API_KEY from environment or prompt.
Performs a quick smoke test against Tosin's chat endpoint.

Usage: from repository root
  .\run-all.ps1
#>

# Ensure script runs from repo root
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

function Get-OpenAiKey {
    if ($env:OPENAI_API_KEY -and $env:OPENAI_API_KEY.Trim().Length -gt 0) {
        return $env:OPENAI_API_KEY
    }

    Write-Host "OPENAI_API_KEY not found in environment. Please enter it (won't echo):"
    $secure = Read-Host -AsSecureString "OpenAI API Key"
    if (-not $secure) { Write-Error "No API key provided. Aborting."; exit 1 }
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

$openaiKey = Get-OpenAiKey

Write-Host "Building solution..."
$build = dotnet build .\MusicAI.sln
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed. Check output above."; exit $LASTEXITCODE }

# Helper to start a PowerShell window that sets env vars and runs the project
function Start-ServiceWindow([string]$projectPath, [int]$port) {
    $absProject = Join-Path $scriptDir $projectPath
    $cmd = "$env:OPENAI_API_KEY='$openaiKey'; $env:ASPNETCORE_URLS='http://localhost:$port'; dotnet run --project '$absProject'"

    # Use powershell.exe so script works on Windows PowerShell v5.1
    Start-Process -FilePath "powershell.exe" -ArgumentList ("-NoExit","-Command", $cmd) -WindowStyle Normal
    Start-Sleep -Milliseconds 500
}

Write-Host "Starting Orchestrator (http://localhost:5000)..."
Start-ServiceWindow -projectPath 'MusicAI.Orchestrator\MusicAI.Orchestrator.csproj' -port 5000

Write-Host "Starting Tosin persona (http://localhost:5001)..."
Start-ServiceWindow -projectPath 'MusicAI.Personas.Tosin\MusicAI.Personas.Tosin.csproj' -port 5001

Write-Host "Waiting for services to warm up (10s)..."
Start-Sleep -Seconds 10

Write-Host "Running smoke test against Tosin..."
try {
    $body = @{ UserId = 'local-test'; Message = 'Hello Tosin, please give a short jingle.'; Language = 'en' } | ConvertTo-Json
    $resp = Invoke-RestMethod -Uri 'http://localhost:5001/api/Chat/chat' -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 30
    Write-Host "Smoke test response:" -ForegroundColor Green
    $resp | ConvertTo-Json -Depth 5 | Write-Host
} catch {
    Write-Warning "Smoke test failed: $($_.Exception.Message)"
    Write-Host "You can inspect the two PowerShell windows for full logs."
}

Write-Host "Run script finished. Services are running in new windows (or check processes)."