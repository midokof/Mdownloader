param([string]$Name = "Downloads", [string]$Out = "D:\Mdownloader\shot.png")

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process NovaDownload.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output "no-window"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
if (-not $root) { Write-Output "no-root"; exit 1 }

$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::RadioButton)

$radios = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
$target = $null
foreach ($r in $radios) { if ($r.Current.Name -eq $Name) { $target = $r; break } }

if (-not $target) {
    $names = ($radios | ForEach-Object { $_.Current.Name }) -join ", "
    Write-Output "not-found ($names)"
    exit 2
}

$pattern = $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
$pattern.Select()
Start-Sleep -Milliseconds 700
Write-Output "selected $Name"

& powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Mdownloader\tools\capture-window.ps1" -Out $Out
