# Helper: register/login and call /api/payment/initiate
# Usage: run in PowerShell while the app is running on http://localhost:5000

param(
    [string]$BaseUrl = $(if ($env:BASE_URL) { $env:BASE_URL } else { "http://localhost:5000" }),
    [string]$Email = $(if ($env:PAYMENT_LOGIN_EMAIL) { $env:PAYMENT_LOGIN_EMAIL } else { $null }),
    [string]$Password = $(if ($env:PAYMENT_LOGIN_PASSWORD) { $env:PAYMENT_LOGIN_PASSWORD } else { $null }),
    [string]$Phone = "",
    [string]$Address = ""
)

if (-not $Email -or -not $Password) {
    Write-Host "ERROR: Provide login credentials via args or environment variables PAYMENT_LOGIN_EMAIL and PAYMENT_LOGIN_PASSWORD"
    exit 1
}

function Login-GetToken {
    param($baseUrl, $email, $password)
    $body = @{ Email = $email; Password = $password } | ConvertTo-Json
    try {
        $resp = Invoke-RestMethod -Uri "$baseUrl/api/onboarding/login" -Method Post -Body $body -ContentType "application/json" -ErrorAction Stop
        return $resp.token
    } catch {
        Write-Host "Login failed: $($_.Exception.Message)"
        if ($_.Exception.Response) {
            $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
            $text = $reader.ReadToEnd(); Write-Host "Response body:`n$text"
        }
        return $null
    }
}

Write-Host "Logging in to obtain JWT from $BaseUrl..."
$token = Login-GetToken -baseUrl $BaseUrl -email $Email -password $Password
if (-not $token) { Write-Host "Could not obtain token; aborting"; exit 1 }
Write-Host "Token obtained (length $($token.Length))"

# Prepare initiate payload (only phone/address accepted from client)
$initBody = @{ customerPhone = $Phone; customerAddress = $Address } | ConvertTo-Json
$headers = @{ Authorization = "Bearer $token" }
try {
    Write-Host "Calling /api/payment/initiate..."
    $resp = Invoke-RestMethod -Uri "$BaseUrl/api/payment/initiate" -Method Post -Body $initBody -ContentType "application/json" -Headers $headers -ErrorAction Stop
    Write-Host "Response:`n" (ConvertTo-Json $resp -Depth 5)
} catch {
    Write-Host "Payment initiate failed: $($_.Exception.Message)"
    if ($_.Exception.Response) {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        $text = $reader.ReadToEnd(); Write-Host "Response body:`n$text"
    }
    exit 1
}
