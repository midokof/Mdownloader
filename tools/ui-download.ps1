param(
    [string]$Url = "http://127.0.0.1:8099/test.bin",
    [string]$Out = "D:\Mdownloader\shot-downloads.png",
    [int]$WaitSeconds = 4
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process NovaDownload.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output "no-window"; exit 1 }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

function Find-ByType($root, $type) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
    return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

# focus the New download page
$radioCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::RadioButton)
foreach ($r in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $radioCond)) {
    if ($r.Current.Name -eq "New download") {
        $r.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        break
    }
}
Start-Sleep -Milliseconds 500

# set URL
$edits = Find-ByType $root ([System.Windows.Automation.ControlType]::Edit)
$urlBox = $null
foreach ($e in $edits) { if ($e.Current.AutomationId -eq "UrlBox") { $urlBox = $e; break } }
if (-not $urlBox) { Write-Output "no-urlbox"; exit 2 }
$urlBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Url)
Start-Sleep -Milliseconds 400

# start
$buttons = Find-ByType $root ([System.Windows.Automation.ControlType]::Button)
$start = $null
foreach ($b in $buttons) { if ($b.Current.Name -eq "Start download") { $start = $b; break } }
if (-not $start) { Write-Output "no-start"; exit 3 }
$start.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output "started $Url"

Start-Sleep -Seconds $WaitSeconds
& powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Mdownloader\tools\capture-window.ps1" -Out $Out
