# Tasky Web & Mobile Preview Server
# Runs a lightweight HTTP server on port 5500 serving docs/
# Automatically launches the interactive dual-view test lab

param(
    [int]$Port = 5500,
    [switch]$NoBrowser
)

$docsPath = Join-Path $PSScriptRoot "docs"
if (-not (Test-Path $docsPath)) {
    Write-Error "Docs directory not found at $docsPath"
    exit 1
}

$prefix = "http://localhost:$Port/"
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add($prefix)

try {
    $listener.Start()
} catch {
    Write-Error "Could not start HTTP listener on $prefix : $_"
    exit 1
}

# Determine local IP addresses for physical device testing
$localIps = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | 
            Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" } | 
            Select-Object -ExpandProperty IPAddress

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   🚀 Tasky Web & Mobile Preview Server Running" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [Desktop & Mobile Test Lab]  : http://localhost:$Port/test-preview.html" -ForegroundColor Yellow
Write-Host " [Direct Web/Mobile App]     : http://localhost:$Port/index.html" -ForegroundColor White
if ($localIps) {
    foreach ($ip in $localIps) {
        Write-Host " [On-Phone Wi-Fi Testing]   : http://$($ip):$Port/index.html" -ForegroundColor Magenta
    }
}
Write-Host " Press Ctrl+C in this window to stop the server." -ForegroundColor Gray
Write-Host "==========================================================" -ForegroundColor Cyan

# Open default browser
if (-not $NoBrowser) {
    Start-Process "http://localhost:$Port/test-preview.html"
}

$mimeMap = @{
    ".html" = "text/html; charset=utf-8"
    ".htm"  = "text/html; charset=utf-8"
    ".css"  = "text/css; charset=utf-8"
    ".js"   = "application/javascript; charset=utf-8"
    ".json" = "application/json; charset=utf-8"
    ".png"  = "image/png"
    ".jpg"  = "image/jpeg"
    ".jpeg" = "image/jpeg"
    ".gif"  = "image/gif"
    ".svg"  = "image/svg+xml"
    ".ico"  = "image/x-icon"
    ".woff2"= "font/woff2"
}

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $request = $context.Request
        $response = $context.Response

        $rawUrl = $request.Url.LocalPath.TrimStart('/')
        if ([string]::IsNullOrWhiteSpace($rawUrl)) {
            $rawUrl = "test-preview.html"
        }

        # URL decode and map to local path
        $decodedUrl = [System.Uri]::UnescapeDataString($rawUrl).Replace('/', '\')
        $filePath = Join-Path $docsPath $decodedUrl

        # Security check: ensure path stays within docs
        $fullPath = [System.IO.Path]::GetFullPath($filePath)
        $docsFullPath = [System.IO.Path]::GetFullPath($docsPath)

        if ($fullPath.StartsWith($docsFullPath, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path $fullPath -PathType Leaf)) {
            $ext = [System.IO.Path]::GetExtension($fullPath).ToLower()
            $mime = if ($mimeMap.ContainsKey($ext)) { $mimeMap[$ext] } else { "application/octet-stream" }
            $response.ContentType = $mime
            
            $bytes = [System.IO.File]::ReadAllBytes($fullPath)
            $response.ContentLength64 = $bytes.Length
            $response.OutputStream.Write($bytes, 0, $bytes.Length)
            $response.StatusCode = 200
        } else {
            $response.StatusCode = 404
            $msg = [System.Text.Encoding]::UTF8.GetBytes("404 Not Found: $rawUrl")
            $response.ContentLength64 = $msg.Length
            $response.ContentType = "text/plain"
            $response.OutputStream.Write($msg, 0, $msg.Length)
        }

        $response.OutputStream.Close()
    }
} finally {
    $listener.Stop()
    $listener.Close()
    Write-Host "Server stopped." -ForegroundColor Yellow
}
