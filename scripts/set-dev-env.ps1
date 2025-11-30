param(
    [string]$EnvFile = ".env"
)

if (-not (Test-Path $EnvFile)) {
    Write-Error "Env file '$EnvFile' not found. Copy .env.example to .env and edit values."
    exit 1
}

Write-Host "Loading environment variables from $EnvFile"

Get-Content $EnvFile | ForEach-Object {
    if ($_ -and -not $_.StartsWith('#')) {
        $parts = $_ -split '=', 2
        if ($parts.Length -eq 2) {
            $name = $parts[0].Trim()
            $value = $parts[1].Trim()
            Write-Host "Setting $name"
            [System.Environment]::SetEnvironmentVariable($name, $value, 'Process')
        }
    }
}

Write-Host "Environment loaded into current process. Run your dotnet command in this session."
