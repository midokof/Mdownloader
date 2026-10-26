Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process NovaDownload.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

function Dump($root, $controlType) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) |
        ForEach-Object { "{0} | name='{1}' auto='{2}'" -f $controlType.ProgrammaticName, $_.Current.Name, $_.Current.AutomationId }
}

Dump $root ([System.Windows.Automation.ControlType]::Button)
Dump $root ([System.Windows.Automation.ControlType]::Edit)
Dump $root ([System.Windows.Automation.ControlType]::RadioButton)
