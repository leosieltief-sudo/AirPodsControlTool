# Lists running AirPods Control processes (diagnostics).
Get-CimInstance Win32_Process -Filter "Name LIKE '%AirPods%'" | Select-Object ProcessId, Name, SessionId, CommandLine | Format-List
