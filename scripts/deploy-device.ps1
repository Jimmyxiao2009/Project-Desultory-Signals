# deploy-device.ps1 -- install this appx on a Windows 10 Mobile device over Device Portal.
#
# Why a bespoke script instead of `Invoke-WebRequest -Form`: PowerShell's -Form does not
# produce the multipart shape WDP requires. WDP identifies the payload by the
# *filename* in each part's Content-Disposition, and it rejects the whole upload with
# "Missing .appx (or .eappx) file in uploaded files" when it cannot find one. So each
# part must carry name = filename = filename and Content-Type: application/octet-stream,
# which is exactly what the curl invocation in Apotheosis's tools/wdp.sh does:
#
#     -F "${NAME}=@${APPX};type=application/octet-stream"
#
# MultipartFormDataContent gives that level of control, so this script drives HttpClient
# directly. Two smaller things it also has to get right, both learned the hard way:
#
#   * WDP requires an X-CSRF-Token header on every state-changing request. The token
#     arrives as a Set-Cookie on any GET, so one GET is done first and the token is read
#     out of the cookie container.
#   * The device's certificate is self-signed, so validation is skipped.
#
# Usage:
#   pwsh -NoProfile -File scripts\deploy-device.ps1 -DeviceIp 192.168.3.159
#   pwsh -NoProfile -File scripts\deploy-device.ps1 -DeviceIp <ip> -Launch -PullDiagnostics
#   pwsh -NoProfile -File scripts\deploy-device.ps1 -DeviceIp <ip> -List
#   pwsh -NoProfile -File scripts\deploy-device.ps1 -DeviceIp <ip> -Uninstall

param(
    [string]$DeviceIp = '192.168.3.159',
    [int]$Port = 443,
    [ValidateSet('https', 'http')]
    [string]$Scheme = 'https',

    [string]$Appx,
    [string[]]$Dependencies = @(),
    [string]$Certificate,

    [switch]$List,
    [switch]$Uninstall,
    [switch]$Launch,
    [switch]$PullDiagnostics,
    # Skip the upload entirely. Combined with -Launch / -PullDiagnostics this turns the
    # script into a control tool for an already-installed package, which is what is
    # wanted most of the time after the first deploy.
    [switch]$SkipDeploy,
    # Reuse frameworks already confirmed by a successful launch on this device.
    [switch]$SkipDependencies,
    # Do not cross-check the package list when waiting for a deployment to finish.
    # Needed for framework packages: the device accepts and installs them, but
    # /api/app/packagemanager/packages does not enumerate them, so a list-based wait
    # would run until it times out even on success. The state endpoint is the only
    # signal available for those, and the dependent app launching is the real proof.
    [switch]$NoPackageCheck,

    [string]$PackageMatch = 'DesultorySignals',
    [string]$ResultFile = 'startup-report.txt',
    [int]$TimeoutSec = 600,
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$base = "{0}://{1}:{2}" -f $Scheme, $DeviceIp, $Port

# ---------------------------------------------------------------------------
# client: skip the self-signed cert; do NOT let HttpClient manage cookies
# ---------------------------------------------------------------------------
# UseCookies must be false. With it true, .NET consumes every Set-Cookie into the
# CookieContainer and *removes it from the response headers*, so the CSRF token becomes
# invisible -- and the CookieContainer will not hand it back either, because its
# domain-matching rules are meant for host names and this is an IP address. Disabling
# cookie management keeps Set-Cookie readable in the headers, which is where the token
# is taken from. The token is only ever sent back in the X-CSRF-Token request header,
# so no cookie jar is needed at all.
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.ServerCertificateCustomValidationCallback = [System.Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
$handler.UseCookies = $false
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds([Math]::Min($TimeoutSec, 60))

function Get-CsrfToken {
    # Any GET makes WDP set a CSRF-Token cookie, but the token is read from the raw
    # Set-Cookie response header rather than from the CookieContainer: CookieContainer
    # is unreliable for IP-address hosts (it applies domain-matching rules meant for
    # names), so the cookie silently never lands and every later POST is rejected.
    # The header is the authoritative source, and the value is what WDP validates in
    # the X-CSRF-Token request header anyway.
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $response = $client.GetAsync("$base/api/os/info").GetAwaiter().GetResult()
            $null = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

            $headers = New-Object System.Collections.Generic.List[string]
            $values = $null
            if ($response.Headers.TryGetValues('Set-Cookie', [ref]$values)) {
                foreach ($value in $values) { $headers.Add($value) }
            }
            foreach ($value in $headers) {
                if ($value -match 'CSRF-Token=([^;,\s]+)') { return $Matches[1] }
            }
        } catch {
            if ($attempt -eq 3) { throw }
        }
        Start-Sleep -Seconds 2
    }
    throw "could not obtain a CSRF token from $base (no CSRF-Token in any Set-Cookie header)"
}

function Get-DeviceInfo {
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            $response = $client.GetAsync("$base/api/os/info").GetAwaiter().GetResult()
            $response.EnsureSuccessStatusCode() | Out-Null
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            return $text | ConvertFrom-Json
        } catch {
            if ($attempt -eq 5) { throw }
            Write-Host "    device connection attempt $attempt failed; retrying" -ForegroundColor DarkYellow
            Start-Sleep -Seconds 2
        }
    }
}

function Get-Packages {
    $response = $client.GetAsync("$base/api/app/packagemanager/packages").GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return ($text | ConvertFrom-Json).InstalledPackages
}

function Wait-Install {
    <#
        Poll until the deployment reports a terminal state.

        Two details matter, and getting either wrong makes the script lie about success:

        * An empty or 204 state is NOT success. Immediately after WDP answers 202
          ("accepted and being processed") the state endpoint is briefly empty, so
          treating that as "done" reports a successful install for a deployment that has
          not started yet. The first observation must instead be the initial
          "InProgress" state, or the package must actually appear in the package list.
        * The authoritative completion signal is the package appearing in
          /api/app/packagemanager/packages. Success in the state endpoint plus a missing
          package is a real failure mode (an untrusted certificate, for instance), and
          only the package list catches it.
    #>
    param([int]$Timeout, [string]$ExpectPackage, [switch]$AllowHigherVersion)

    $deadline = (Get-Date).AddSeconds($Timeout)
    $lastLine = ''
    $sawProgress = $false

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3

        # Completion check first: this is the signal that cannot be faked.
        if ($ExpectPackage) {
            try {
                $packages = Get-Packages
                if ($packages | Where-Object { $_.PackageFullName -like "$ExpectPackage*" }) {
                    Write-Host "    package is registered" -ForegroundColor DarkGray
                    return $true
                }
            } catch {
                # Transient; keep polling.
            }
        }

        try {
            $response = $client.GetAsync("$base/api/app/packagemanager/state").GetAwaiter().GetResult()
            if ([int]$response.StatusCode -eq 204) { continue }
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([string]::IsNullOrWhiteSpace($text)) { continue }
            $state = $text | ConvertFrom-Json
        } catch {
            continue
        }

        $percent = ''
        if ($null -ne $state.PercentComplete) { $percent = " $($state.PercentComplete)%" }
        $line = "[{0}]{1} {2}" -f $state.CodeText, $percent, $state.Reason
        if ($line -ne $lastLine) {
            Write-Host "    $line" -ForegroundColor DarkGray
            $lastLine = $line
        }

        if ($state.Success -eq $true) {
            # Confirmed success. Without an expected package name there is nothing to
            # cross-check, so accept it.
            if (-not $ExpectPackage) { return $true }
            $sawProgress = $true
            continue
        }

        # ERROR_INSTALL_PACKAGE_DOWNGRADE means the same framework identity is
        # already available at a higher version. Keep the newer runtime installed.
        if ($AllowHigherVersion -and $state.Code -eq -2147009274) {
            Write-Host '    a newer framework is already installed; using it' -ForegroundColor DarkGray
            return $true
        }

        if ($state.Success -eq $false -and $state.Code -and $state.Code -ne 0) {
            if ($state.CodeText -match 'InProgress|Installing|Processing') { $sawProgress = $true; continue }
            throw "install failed: Code=$($state.Code) $($state.CodeText) - $($state.Reason)"
        }

        $sawProgress = $true
    }

    if ($ExpectPackage -and $sawProgress) {
        throw "the deployment reported progress but $ExpectPackage never appeared in the package list"
    }
    throw "install timed out after ${Timeout}s"
}

function New-MultipartBody {
    <#
        Build the multipart body by hand, byte for byte the way curl does it.

        MultipartFormDataContent cannot be used here. Its Content-Disposition omits the
        quotes around name/filename whenever the value is a valid HTTP token, and
        "Microsoft.NET.CoreRuntime.1.1.appx" qualifies (dots are token characters), so
        .NET emits:

            Content-Disposition: form-data; name=X.appx; filename=X.appx

        while WDP's parser requires curl's quoted form:

            Content-Disposition: form-data; name="X.appx"; filename="X.appx"

        .NET also quotes the boundary in the Content-Type. Either difference is enough
        for WDP to answer "Missing .appx (or .eappx) file in uploaded files", because it
        identifies the payload by the filename parameter. Emitting the bytes directly
        removes the ambiguity entirely.
    #>
    param([System.IO.FileInfo[]]$Files, [string]$Boundary)

    $stream = New-Object System.IO.MemoryStream
    $crlf = [Text.Encoding]::ASCII.GetBytes("`r`n")

    foreach ($file in $Files) {
        $header = "--$Boundary`r`n" +
                  "Content-Disposition: form-data; name=""$($file.Name)""; filename=""$($file.Name)""`r`n" +
                  "Content-Type: application/octet-stream`r`n`r`n"
        $headerBytes = [Text.Encoding]::UTF8.GetBytes($header)
        $stream.Write($headerBytes, 0, $headerBytes.Length)

        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Write($crlf, 0, $crlf.Length)
    }

    $tail = [Text.Encoding]::ASCII.GetBytes("--$Boundary--`r`n")
    $stream.Write($tail, 0, $tail.Length)

    $body = $stream.ToArray()
    $stream.Dispose()
    return $body
}

function Invoke-Deploy {
    <#
        Upload one package. Frameworks are installed separately before the app;
        placing them in additional multipart parts does not register them on Mobile.
    #>
    param([System.IO.FileInfo[]]$Files, [string]$PrimaryName, [string]$Csrf)

    $boundary = [Guid]::NewGuid().ToString('N')
    $body = New-MultipartBody -Files $Files -Boundary $boundary

    $content = New-Object System.Net.Http.ByteArrayContent(,$body)
    # TryAddWithoutValidation keeps the boundary exactly as written; MediaTypeHeaderValue
    # would re-serialise and may add quotes.
    $null = $content.Headers.TryAddWithoutValidation('Content-Type', "multipart/form-data; boundary=$boundary")

    $url = "$base/api/app/packagemanager/package?package=$([uri]::EscapeDataString($PrimaryName))"

    $totalMb = [math]::Round((($Files | Measure-Object -Property Length -Sum).Sum)/1MB, 1)
    Write-Host "==> uploading $($Files.Count) file(s), $totalMb MB total" -ForegroundColor Cyan
    foreach ($file in $Files) {
        Write-Host "    + $($file.Name) ($([math]::Round($file.Length/1MB,2)) MB)" -ForegroundColor DarkGray
    }

    # The LAN link to a phone drops mid-upload often enough that a single attempt is not
    # acceptable; the request is idempotent from WDP's point of view.
    $lastError = $null
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, $url)
        $request.Headers.Add('X-CSRF-Token', $Csrf)
        $request.Content = $content

        try {
            $response = $client.SendAsync($request).GetAwaiter().GetResult()
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        } catch {
            $lastError = $_.Exception.Message
            Write-Host "    attempt $attempt/4: $lastError; retrying in 3s" -ForegroundColor DarkYellow
            Start-Sleep -Seconds 3
            continue
        }

        if ($response.IsSuccessStatusCode) {
            Write-Host "    HTTP $([int]$response.StatusCode) $text" -ForegroundColor DarkGray
            return
        }

        $lastError = "HTTP $([int]$response.StatusCode) $text"
        if ([int]$response.StatusCode -eq 409) {
            # Consume the preceding deployment's state before retrying. Mobile can
            # keep its deployment slot occupied after returning the final status.
            try { $null = $client.GetAsync("$base/api/app/packagemanager/state").GetAwaiter().GetResult() } catch {}
        }
        if ($attempt -lt 4) {
            Write-Host "    attempt $attempt/4 rejected: $lastError; retrying in 3s" -ForegroundColor DarkYellow
            Start-Sleep -Seconds 3
        }
    }
    throw "deploy rejected: $lastError"
}

function Get-AppxIdentityName {
    <#
        Read the Identity Name out of an appx's own manifest.

        The wait-for-install check keys on this, and it cannot be derived from the file
        name: the app's file is DesultorySignals_0.1.0.0_ARM_Debug.appx while its identity
        is DesultorySignals.DebugShell, and a framework package's file name happens to
        match but that is a coincidence. The manifest is the only authoritative source,
        and it also makes the script work for any appx rather than just this one.
    #>
    param([string]$Path, [switch]$RegistrationPrefix)
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq 'AppxManifest.xml' } | Select-Object -First 1
        if (-not $entry) { return $null }
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try { $xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        [xml]$manifest = $xml
        $identity = $manifest.Package.Identity
        if ($RegistrationPrefix) {
            return "$($identity.Name)_$($identity.Version)_$($identity.ProcessorArchitecture)_"
        }
        if ($identity) { return [string]$identity.Name }
        return $null
    } finally {
        $zip.Dispose()
    }
}

# ---------------------------------------------------------------------------
# entry
# ---------------------------------------------------------------------------

Write-Host "WDP target: $base" -ForegroundColor Yellow

$info = Get-DeviceInfo
Write-Host "==> connected: $($info.ComputerName)  OS $($info.OsVersion)" -ForegroundColor Green

if ($List) {
    $packages = Get-Packages
    Write-Host "`ninstalled packages: $($packages.Count)" -ForegroundColor Cyan
    $packages | Where-Object { $_.PackageFullName -match $PackageMatch } |
        ForEach-Object { "  $($_.PackageFullName)" }
    Write-Host "`nframework packages:" -ForegroundColor Cyan
    $packages | Where-Object { $_.PackageFullName -match 'NET\.CoreRuntime|NET\.Native|VCLibs' } |
        ForEach-Object { "  $($_.PackageFullName)" }
    exit 0
}

$csrf = Get-CsrfToken
Write-Host "==> CSRF token acquired" -ForegroundColor DarkGray

if ($Uninstall) {
    $packages = Get-Packages
    $targets = @($packages | Where-Object { $_.PackageFullName -match $PackageMatch })
    if ($targets.Count -eq 0) { Write-Host "==> nothing installed matching '$PackageMatch'"; exit 0 }
    foreach ($target in $targets) {
        Write-Host "==> uninstalling $($target.PackageFullName)" -ForegroundColor Cyan
        $url = "$base/api/app/packagemanager/package?package=$([uri]::EscapeDataString($target.PackageFullName))"
        $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Delete, $url)
        $request.Headers.Add('X-CSRF-Token', $csrf)
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        Write-Host "    HTTP $([int]$response.StatusCode)"
    }
    exit 0
}

# --- resolve what to deploy -------------------------------------------------
if (-not $SkipDeploy) {
    if (-not $Appx) {
        $Appx = Get-ChildItem (Join-Path $RepoRoot 'app\DesultorySignals\AppPackages') -Recurse -Filter '*.appx' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notmatch 'CoreRuntime|VCLibs' } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $Appx -or -not (Test-Path -LiteralPath $Appx)) {
        throw "no appx found; build one with scripts\build-app.ps1 or pass -Appx"
    }
    $appxItem = Get-Item -LiteralPath $Appx

    # A framework package must be deployed as its own package, before the app that
    # depends on it. Uploading it as an extra part of the app's deploy request does NOT
    # register it: the device accepts the request, reports success, and then the app
    # fails to launch with 0x8027025B ("The app didn't start") because its declared
    # framework dependency is absent. That cost a debugging round; hence the explicit
    # ordered loop below.
    if (-not $SkipDependencies -and $Dependencies.Count -eq 0 -and $appxItem.Name -notmatch 'CoreRuntime|VCLibs') {
        $dependencyDir = Join-Path $appxItem.DirectoryName 'Dependencies\ARM'
        $Dependencies = @(Get-ChildItem -LiteralPath $dependencyDir -Filter '*.appx' -ErrorAction SilentlyContinue |
            Sort-Object Name | Select-Object -ExpandProperty FullName)
    }

    if (-not $Certificate) {
        $candidate = Join-Path $RepoRoot 'app\DesultorySignals\DesultorySignals_TemporaryKey.cer'
        if (Test-Path $candidate) { $Certificate = $candidate }
    }

    # An update cannot replace a framework while this app holds its runtime.
    # Close only the exact application identity being deployed, keeping LocalState.
    $appxIdentity = Get-AppxIdentityName -Path $appxItem.FullName
    $running = ($client.GetAsync("$base/api/resourcemanager/processes").GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).Processes
    foreach ($target in (Get-Packages | Where-Object { $_.PackageFullName.StartsWith($appxIdentity + '_') })) {
        if (-not ($running | Where-Object { $_.PackageFullName -eq $target.PackageFullName })) { continue }
        $encodedTarget = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($target.PackageFullName))
        $url = "$base/api/taskmanager/app?package=$([uri]::EscapeDataString($encodedTarget))&forcestop=true"
        $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Delete, $url)
        $request.Headers.Add('X-CSRF-Token', $csrf)
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw "could not close app for update: HTTP $([int]$response.StatusCode)" }
        Write-Host "==> closed $($target.PackageFullName) for update" -ForegroundColor DarkGray
        Start-Sleep -Seconds 2
    }

    foreach ($dependency in $Dependencies) {
        $dependencyItem = Get-Item -LiteralPath $dependency
        $dependencyIdentity = Get-AppxIdentityName -Path $dependencyItem.FullName
        Write-Host "==> framework dependency: $dependencyIdentity" -ForegroundColor Cyan
        Invoke-Deploy -Files @($dependencyItem) -PrimaryName $dependencyItem.Name -Csrf $csrf
        # Mobile's package list hides framework packages; installation state is
        # the completion signal for every framework, regardless of NoPackageCheck.
        Wait-Install -Timeout $TimeoutSec -AllowHigherVersion | Out-Null
        Write-Host "    installed" -ForegroundColor Green
    }

    $appxIdentity = Get-AppxIdentityName -Path $appxItem.FullName
    Invoke-Deploy -Files @($appxItem) -PrimaryName $appxItem.Name -Csrf $csrf
    if ($NoPackageCheck) {
        Wait-Install -Timeout $TimeoutSec | Out-Null
    } else {
        Wait-Install -Timeout $TimeoutSec -ExpectPackage (Get-AppxIdentityName -Path $appxItem.FullName -RegistrationPrefix) | Out-Null
    }
    Write-Host "==> installed $appxIdentity" -ForegroundColor Green
}

$packages = Get-Packages
$installed = @($packages | Where-Object { $_.PackageFullName -match $PackageMatch })
foreach ($package in $installed) { Write-Host "    $($package.PackageFullName)" }

if ($Launch -and $installed.Count -gt 0) {
    $package = $installed[0]
    $fullName = $package.PackageFullName

    # Both arguments go in the *query string* and both are base64-encoded:
    #   appid   = base64(PackageRelativeId), i.e. "<FamilyName>!App"
    #   package = base64(PackageFullName)
    # Sending them as a form body, or sending the literal string "App" as the app id,
    # produces "Missing app id argument." The PackageRelativeId is read from the device
    # rather than assembled, because the publisher hash in the family name is not
    # something a caller can predict.
    $appId = $package.PackageRelativeId
    if ([string]::IsNullOrEmpty($appId)) {
        Write-Host "    (the device did not report a PackageRelativeId; cannot launch)" -ForegroundColor Yellow
    } else {
        $encodedAppId = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($appId))
        $encodedPackage = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($fullName))
        $url = "$base/api/taskmanager/app?appid=$([uri]::EscapeDataString($encodedAppId))" `
             + "&package=$([uri]::EscapeDataString($encodedPackage))"

        Write-Host "==> launching $appId" -ForegroundColor Cyan
        $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, $url)
        $request.Headers.Add('X-CSRF-Token', $csrf)
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        Write-Host "    HTTP $([int]$response.StatusCode) $text"
        if (-not $response.IsSuccessStatusCode) {
            throw "launch rejected: HTTP $([int]$response.StatusCode) $text"
        }
    }
}

if ($PullDiagnostics -and $installed.Count -gt 0) {
    $fullName = $installed[0].PackageFullName
    Write-Host "==> waiting for the app to write $ResultFile" -ForegroundColor Cyan

    # The Device Portal file API takes the folder and the file name as *separate*
    # parameters: path=\LocalState and filename=<name>. Passing the file as part of
    # `path` answers 400 "Missing known folder id", which reads like a missing
    # knownfolderid even though it was supplied.
    $destination = Join-Path $RepoRoot $ResultFile
    $deadline = (Get-Date).AddSeconds(120)
    $fetched = $false
    while ((Get-Date) -lt $deadline -and -not $fetched) {
        Start-Sleep -Seconds 5
        $url = "$base/api/filesystem/apps/file?knownfolderid=LocalAppData" `
            + "&packagefullname=$([uri]::EscapeDataString($fullName))" `
            + "&path=%5CLocalState" `
            + "&filename=$([uri]::EscapeDataString($ResultFile))"
        try {
            $response = $client.GetAsync($url).GetAwaiter().GetResult()
            if ($response.IsSuccessStatusCode) {
                $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                Set-Content -Path $destination -Value $text -Encoding utf8
                Write-Host "==> pulled to $destination ($($text.Length) chars)" -ForegroundColor Green
                $fetched = $true
            }
        } catch {
            # Not written yet; keep waiting.
        }
    }
    if (-not $fetched) {
        Write-Host "==> $ResultFile was not produced within 120s" -ForegroundColor Yellow
        Write-Host "    (the app must be launched and must reach the report-writing step)" -ForegroundColor DarkGray
    }
}

exit 0
