# Drives the running NovaDownload window through UI Automation by AutomationId, so the
# harness keeps working after the UI language changes.
#
#   ui-act.ps1 -List                  dump every named element (control type | name | auto id)
#   ui-act.ps1 -Id ClearFinished -Action invoke
#   ui-act.ps1 -Id UrlBox -Action value -Value "http://127.0.0.1:8099/test.bin"
param(
    [string]$Id,
    [ValidateSet("invoke", "select", "value", "expand")]
    [string]$Action = "invoke",
    [string]$Value,
    [switch]$List,
    [int]$DelayMs = 350,
    [switch]$Wait
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process NovaDownload.App -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output "no-window"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
if (-not $root) { Write-Output "no-root"; exit 1 }

$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)

if ($List) {
    foreach ($e in $all) {
        $c = $e.Current
        if ([string]::IsNullOrWhiteSpace($c.Name) -and [string]::IsNullOrWhiteSpace($c.AutomationId)) { continue }
        "{0} | name='{1}' | id='{2}'" -f $c.ControlType.ProgrammaticName, $c.Name, $c.AutomationId
    }
    exit 0
}

if (-not $Id) { Write-Output "need -Id (or -List)"; exit 2 }

$hit = $null
foreach ($e in $all) { if ($e.Current.AutomationId -eq $Id) { $hit = $e; break } }
if (-not $hit) { Write-Output "not-found $Id"; exit 3 }

switch ($Action) {
    "invoke" {
        $hit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    "select" {
        $hit.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }
    "expand" {
        $hit.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    }
    "value" {
        $hit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
    }
}

Start-Sleep -Milliseconds $DelayMs
Write-Output "$Action $Id"
