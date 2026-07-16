param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot

if (Get-Process SessionWatcher -ErrorAction SilentlyContinue) {
    throw "Close SessionWatcher before running the smoke test."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$runner = $null
$application = $null
try {
    $runner = Start-Process `
        -FilePath dotnet `
        -ArgumentList @(
            "run",
            "--project",
            "SessionWatcher\SessionWatcher.csproj",
            "-c",
            $Configuration,
            "--no-build") `
        -WorkingDirectory $repository `
        -WindowStyle Hidden `
        -PassThru

    $deadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $application = Get-Process SessionWatcher -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } |
            Select-Object -First 1
    } until ($application -or (Get-Date) -gt $deadline)

    if (-not $application) {
        throw "SessionWatcher did not open a top-level window within 30 seconds."
    }

    $requiredNames = @(
        "Usage overview",
        "Claude",
        "Codex",
        "Refresh provider usage")
    $deadline = (Get-Date).AddSeconds(30)
    do {
        $window = [System.Windows.Automation.AutomationElement]::FromHandle(
            $application.MainWindowHandle)
        $elements = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        $names = for ($index = 0; $index -lt $elements.Count; $index++) {
            $elements.Item($index).Current.Name
        }
        $missingNames = @($requiredNames | Where-Object { $_ -notin $names })
        if ($missingNames.Count -gt 0) {
            Start-Sleep -Milliseconds 250
        }
    } until ($missingNames.Count -eq 0 -or (Get-Date) -gt $deadline)

    if ($missingNames.Count -gt 0) {
        throw "The accessibility surface is missing: $($missingNames -join ', ')."
    }

    $package = Get-AppxPackage |
        Where-Object { $_.InstallLocation -like "*$repository*" } |
        Select-Object -First 1
    if (-not $package) {
        throw "The SessionWatcher debug package identity was not registered."
    }

    $dataRoot = Join-Path $env:LOCALAPPDATA (
        "Packages\{0}\LocalState\SessionWatcher" -f $package.PackageFamilyName)
    $historyPath = Join-Path $dataRoot "history.json"
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path -LiteralPath $historyPath) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }

    if (-not (Test-Path -LiteralPath $historyPath)) {
        throw "The app did not persist a sanitized history snapshot."
    }

    $history = Get-Content -LiteralPath $historyPath -Raw
    if ($history -match "diagnostic|accessToken|refreshToken|Bearer") {
        throw "History contains a forbidden diagnostic or credential field."
    }

    Write-Output "PASS: packaged launch, accessibility surface, and sanitized history"
    Write-Output "Data root: $dataRoot"
}
finally {
    if ($application -and -not $application.HasExited) {
        Stop-Process -Id $application.Id -Force -ErrorAction SilentlyContinue
    }
    if ($runner -and -not $runner.HasExited) {
        Stop-Process -Id $runner.Id -Force -ErrorAction SilentlyContinue
    }
}
