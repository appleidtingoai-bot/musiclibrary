# Run SQL migration files against Postgres
# Usage: set env POSTGRES_URL (e.g. Host=localhost;Username=postgres;Password=postgres;Database=musicai)
# Requires psql in PATH. You can install psql via Postgres installer or psql client tools.

$pg = $Env:POSTGRES_URL
if ([string]::IsNullOrEmpty($pg)) {
    Write-Host "POSTGRES_URL environment variable not set. Falling back to default connection string." -ForegroundColor Yellow
    $pg = "Host=localhost;Username=postgres;Password=postgres;Database=musicai"
}

# Convert connection string to psql connection params
function To-PsqlArgs($conn) {
    # crude parser for typical Npgsql connection string
    $parts = $conn -split ';' | Where-Object { $_ -match '=' }
    $h = @{}
    foreach ($p in $parts) {
        $kv = $p -split '=',2
        $h[$kv[0].Trim()] = $kv[1].Trim()
    }
    $pgHostname = $h['Host']
    if (-not $pgHostname -or [string]::IsNullOrWhiteSpace($pgHostname)) { $pgHostname = $h['Server'] }
    if (-not $pgHostname -or [string]::IsNullOrWhiteSpace($pgHostname)) { $pgHostname = 'localhost' }

    $pgUser = $h['Username']
    if (-not $pgUser -or [string]::IsNullOrWhiteSpace($pgUser)) { $pgUser = $h['User Id'] }
    if (-not $pgUser -or [string]::IsNullOrWhiteSpace($pgUser)) { $pgUser = 'postgres' }

    $pgPass = $h['Password']
    if ($null -eq $pgPass) { $pgPass = '' }

    $pgDb = $h['Database']
    if (-not $pgDb -or [string]::IsNullOrWhiteSpace($pgDb)) { $pgDb = 'postgres' }

    return @{ Host=$pgHostname; User=$pgUser; Password=$pgPass; Database=$pgDb }
}

$p = To-PsqlArgs $pg

# Ensure psql exists
if (-not (Get-Command psql -ErrorAction SilentlyContinue)) {
    Write-Error "psql not found in PATH. Please install the PostgreSQL client tools or run the SQL files manually via your DB admin tool."
    exit 1
}

$env:PGPASSWORD = $p.Password
$files = Get-ChildItem -Path $(Join-Path $PSScriptRoot 'db_migrations') -Filter '*.sql' | Sort-Object Name
foreach ($f in $files) {
    Write-Host "Applying $($f.Name)..."
    psql -h $p.Host -U $p.User -d $p.Database -f $f.FullName
    if ($LASTEXITCODE -ne 0) { Write-Error "Failed to apply $($f.Name)"; exit $LASTEXITCODE }
}
Write-Host "Migrations applied successfully." -ForegroundColor Green
