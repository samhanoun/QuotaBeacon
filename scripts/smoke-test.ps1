param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot

if (Get-Process QuotaBeacon -ErrorAction SilentlyContinue) {
    throw "Close QuotaBeacon before running the smoke test."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

function Test-ProviderEnabledInSettings {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$ProviderId
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $true
    }

    $settings = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $disabled = @($settings.disabledProviderIds)
    return $ProviderId -notin $disabled
}

$runner = $null
$application = $null
$settingsPath = $null
$settingsWasPresent = $false
$originalSettingsBytes = $null
try {
    $runner = Start-Process `
        -FilePath dotnet `
        -ArgumentList @(
            "run",
            "--project",
            "QuotaBeacon\QuotaBeacon.csproj",
            "-c",
            $Configuration,
            "--no-build") `
        -WorkingDirectory $repository `
        -WindowStyle Hidden `
        -PassThru

    $deadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $application = Get-Process QuotaBeacon -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } |
            Select-Object -First 1
    } until ($application -or (Get-Date) -gt $deadline)

    if (-not $application) {
        throw "Quota Beacon did not open a top-level window within 30 seconds."
    }

    $requiredNames = @(
        "Usage intelligence",
        "Quota windows",
        "Choose visible providers",
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
        throw "The Quota Beacon package identity was not registered."
    }

    $localStateRoot = Join-Path $env:LOCALAPPDATA (
        "Packages\{0}\LocalState" -f $package.PackageFamilyName)
    $preferredDataRoot = Join-Path $localStateRoot "QuotaBeacon"
    $legacyDataRoot = Join-Path $localStateRoot "SessionWatcher"
    $dataRoot = if ((Test-Path -LiteralPath $preferredDataRoot) -or
        -not (Test-Path -LiteralPath $legacyDataRoot)) {
        $preferredDataRoot
    }
    else {
        $legacyDataRoot
    }
    $settingsPath = Join-Path $dataRoot "settings.json"
    $settingsWasPresent = Test-Path -LiteralPath $settingsPath
    if ($settingsWasPresent) {
        $originalSettingsBytes = [IO.File]::ReadAllBytes($settingsPath)
    }

    $providerButton = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            "Choose visible providers"))
    $invokePattern = $providerButton.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $invokePattern.Invoke()

    $providerNames = @("Claude", "Codex", "Gemini CLI", "Antigravity")
    $deadline = (Get-Date).AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 100
        $processElements = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
                $application.Id))
        $providerItems = @{}
        for ($index = 0; $index -lt $processElements.Count; $index++) {
            $element = $processElements.Item($index)
            $togglePattern = $null
            if ($element.Current.Name -in $providerNames -and
                $element.TryGetCurrentPattern(
                    [System.Windows.Automation.TogglePattern]::Pattern,
                    [ref]$togglePattern)) {
                $providerItems[$element.Current.Name] = $element
            }
        }
        $missingProviders = @($providerNames | Where-Object { -not $providerItems.ContainsKey($_) })
    } until ($missingProviders.Count -eq 0 -or (Get-Date) -gt $deadline)

    if ($missingProviders.Count -gt 0) {
        throw "The Providers menu is missing: $($missingProviders -join ', ')."
    }
    foreach ($providerName in $providerNames) {
        $null = $providerItems[$providerName].GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)
    }

    $testProviderId = "codex"
    $testProviderName = "Codex"
    $testToggle = $providerItems[$testProviderName].GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $initialProviderEnabled =
        $testToggle.Current.ToggleState -eq
        [System.Windows.Automation.ToggleState]::On
    $expectedProviderEnabled = -not $initialProviderEnabled
    $testToggle.Toggle()

    $deadline = (Get-Date).AddSeconds(25)
    do {
        Start-Sleep -Milliseconds 200
        $persistedProviderEnabled = Test-ProviderEnabledInSettings `
            -Path $settingsPath `
            -ProviderId $testProviderId
    } until ($persistedProviderEnabled -eq $expectedProviderEnabled -or
        (Get-Date) -gt $deadline)

    if ($persistedProviderEnabled -ne $expectedProviderEnabled) {
        throw "The overview provider toggle was not persisted."
    }

    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 250
    $settingsItem = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            "Settings"))
    if (-not $settingsItem) {
        throw "The Settings navigation item is missing."
    }

    $selectionPattern = $null
    if ($settingsItem.TryGetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern,
            [ref]$selectionPattern)) {
        $selectionPattern.Select()
    }
    else {
        $settingsInvokePattern = $settingsItem.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)
        $settingsInvokePattern.Invoke()
    }

    $settingsProviderNames = @(
        "Enable Claude",
        "Enable Codex",
        "Enable Gemini CLI",
        "Enable Antigravity")
    $deadline = (Get-Date).AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 100
        $settingsElements = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        $settingsProviderItems = @{}
        $settingsNames = for ($index = 0; $index -lt $settingsElements.Count; $index++) {
            $element = $settingsElements.Item($index)
            $togglePattern = $null
            if ($element.Current.Name -in $settingsProviderNames -and
                $element.TryGetCurrentPattern(
                    [System.Windows.Automation.TogglePattern]::Pattern,
                    [ref]$togglePattern)) {
                $settingsProviderItems[$element.Current.Name] = $element
            }
            $element.Current.Name
        }
        $missingSettingsProviders = @(
            $settingsProviderNames |
                Where-Object { -not $settingsProviderItems.ContainsKey($_) })
    } until ($missingSettingsProviders.Count -eq 0 -or (Get-Date) -gt $deadline)

    if ($missingSettingsProviders.Count -gt 0) {
        throw "The Settings provider controls are missing: $($missingSettingsProviders -join ', ')."
    }
    foreach ($providerName in $settingsProviderNames) {
        $null = $settingsProviderItems[$providerName].GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)
    }
    if ("Save Quota Beacon settings" -notin $settingsNames) {
        throw "The Settings save action is missing from the accessibility surface."
    }

    $settingsTestToggle = $settingsProviderItems["Enable Codex"].GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $settingsShowsEnabled =
        $settingsTestToggle.Current.ToggleState -eq
        [System.Windows.Automation.ToggleState]::On
    if ($settingsShowsEnabled -ne $expectedProviderEnabled) {
        throw "Settings did not reflect the provider change made from the overview."
    }

    $settingsTestToggle.Toggle()
    $saveButton = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            "Save Quota Beacon settings"))
    if (-not $saveButton) {
        throw "The Settings save button could not be invoked."
    }
    $savePattern = $saveButton.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $savePattern.Invoke()

    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $restoredProviderEnabled = Test-ProviderEnabledInSettings `
            -Path $settingsPath `
            -ProviderId $testProviderId
    } until ($restoredProviderEnabled -eq $initialProviderEnabled -or
        (Get-Date) -gt $deadline)

    if ($restoredProviderEnabled -ne $initialProviderEnabled) {
        throw "The Settings provider toggle was not saved."
    }

    $historyPath = Join-Path $dataRoot "history.json"
    if (Test-Path -LiteralPath $historyPath) {
        $history = Get-Content -LiteralPath $historyPath -Raw
        if ($history -match "diagnostic|accessToken|refreshToken|Bearer") {
            throw "History contains a forbidden diagnostic or credential field."
        }
    }

    Write-Output "PASS: packaged launch, persisted overview and Settings provider toggles, accessibility surface, and sanitized history when present"
    Write-Output "Data root: $dataRoot"
}
finally {
    if ($application -and -not $application.HasExited) {
        Stop-Process -Id $application.Id -Force -ErrorAction SilentlyContinue
    }
    if ($runner -and -not $runner.HasExited) {
        Stop-Process -Id $runner.Id -Force -ErrorAction SilentlyContinue
    }
    if ($settingsPath) {
        if ($settingsWasPresent -and $null -ne $originalSettingsBytes) {
            [IO.File]::WriteAllBytes($settingsPath, $originalSettingsBytes)
        }
        elseif (Test-Path -LiteralPath $settingsPath) {
            Remove-Item -LiteralPath $settingsPath -Force
        }
    }
}
