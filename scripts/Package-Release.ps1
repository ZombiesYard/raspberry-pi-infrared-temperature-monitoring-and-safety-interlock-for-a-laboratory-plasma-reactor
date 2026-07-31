[CmdletBinding(DefaultParameterSetName = "Package")]
param(
    [Parameter(Mandatory = $true, ParameterSetName = "Package")]
    [string]$ZipPath,
    [Parameter(Mandatory = $true, ParameterSetName = "Validate")]
    [string]$ValidateOnlyDirectory,
    [Parameter(Mandatory = $true, ParameterSetName = "Resolve")]
    [switch]$ResolveDotNetOnly
)

$ErrorActionPreference = "Stop"

function Assert-SafeReleaseDirectory {
    param([Parameter(Mandatory = $true)][string]$Directory)

    $root = (Resolve-Path -LiteralPath $Directory).Path
    if (-not (Test-Path -LiteralPath (Join-Path $root "ReactorSoftInterlock.Wpf.exe"))) {
        throw "The publish directory does not contain ReactorSoftInterlock.Wpf.exe."
    }

    $allowedJson = @(
        "appsettings.json",
        "ReactorSoftInterlock.Wpf.deps.json",
        "ReactorSoftInterlock.Wpf.runtimeconfig.json"
    )
    $allowedText = @(
        "OFFLINE-SETUP.txt",
        "offline-deps\tesseract\README.txt",
        "offline-deps\cp210x-driver\CP210x_Universal_Windows_Driver_ReleaseNotes.txt",
        "offline-deps\cp210x-driver\SLAB_License_Agreement_VCP_Windows.txt"
    )
    $binaryExtensions = @(".dll", ".exe", ".sys")
    $driverExtensions = @(".cat", ".inf", ".reg", ".bat")
    $forbidden = @()
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -Force -File) {
        $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/').Replace('/', '\')
        $extension = $file.Extension.ToLowerInvariant()
        if ($relative -match '(^|\\)(data|experiment-sessions|experiment-bundles|experiment-upload-outbox)(\\|$)') {
            $forbidden += $relative
            continue
        }

        $allowed = $binaryExtensions -contains $extension
        if ($extension -eq ".json") {
            $allowed = $allowedJson -contains $relative
        }
        elseif ($extension -eq ".txt") {
            $allowed = $allowedText -contains $relative
        }
        elseif ($driverExtensions -contains $extension) {
            $allowed = $relative.StartsWith("offline-deps\cp210x-driver\", [StringComparison]::OrdinalIgnoreCase)
        }

        if (-not $allowed) {
            $forbidden += $relative
        }
    }

    $settingsPath = Join-Path $root "appsettings.json"
    if (Test-Path -LiteralPath $settingsPath) {
        $settingsText = Get-Content -LiteralPath $settingsPath -Raw
        if ($settingsText -match '(?i)personal.?access.?token|private.?token|password|client.?secret|api.?key') {
            $forbidden += "appsettings.json contains a credential-like field"
        }
    }

    if ($forbidden.Count -gt 0) {
        throw "Release directory contains files outside the software allowlist:`n$($forbidden -join [Environment]::NewLine)"
    }
}

function Resolve-DotNetExecutable {
    $knownPath = if ([string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $null
    }
    else {
        Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    }
    if ($knownPath -and (Test-Path -LiteralPath $knownPath)) {
        return (Resolve-Path -LiteralPath $knownPath).Path
    }

    $command = Get-Command "dotnet.exe" -ErrorAction SilentlyContinue
    if (-not $command) {
        $command = Get-Command "dotnet" -ErrorAction SilentlyContinue
    }
    if (-not $command) {
        throw "Could not locate dotnet.exe. Install the .NET 8 SDK before packaging."
    }

    return $command.Source
}

if ($PSCmdlet.ParameterSetName -eq "Resolve") {
    Write-Output (Resolve-DotNetExecutable)
    exit 0
}

if ($PSCmdlet.ParameterSetName -eq "Validate") {
    Assert-SafeReleaseDirectory -Directory $ValidateOnlyDirectory
    Write-Output "release-directory-valid"
    exit 0
}

if (Test-Path -LiteralPath $ZipPath) {
    throw "Refusing to overwrite an existing release ZIP. Use a new short output path."
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$publishRoot = Join-Path ([IO.Path]::GetTempPath()) ("reactor-release-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $publishRoot | Out-Null
try {
    $dotnetExecutable = Resolve-DotNetExecutable
    $projectPath = Join-Path $repositoryRoot "src\ReactorSoftInterlock.Wpf\ReactorSoftInterlock.Wpf.csproj"
    $publishArguments = @(
        "publish",
        ('"{0}"' -f $projectPath),
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", ('"{0}"' -f $publishRoot)
    )
    $publishProcess = Start-Process `
        -FilePath $dotnetExecutable `
        -ArgumentList $publishArguments `
        -Wait `
        -PassThru `
        -NoNewWindow
    if ($publishProcess.ExitCode -ne 0) {
        throw "dotnet publish failed with exit code $($publishProcess.ExitCode)."
    }

    Assert-SafeReleaseDirectory -Directory $publishRoot
    Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $ZipPath -CompressionLevel Optimal
    Write-Output (Resolve-Path -LiteralPath $ZipPath).Path
}
finally {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force -ErrorAction SilentlyContinue
}
