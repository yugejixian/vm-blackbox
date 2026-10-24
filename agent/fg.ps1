Add-Type -Name W -Namespace N -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
'@
$h = [N.W]::GetForegroundWindow()
$sb = New-Object System.Text.StringBuilder 256
[void][N.W]::GetWindowText($h, $sb, 256)
$pid2 = 0
[void][N.W]::GetWindowThreadProcessId($h, [ref]$pid2)
$p = Get-Process -Id $pid2 -ErrorAction SilentlyContinue
"foreground title = " + $sb.ToString()
"foreground pid   = $pid2"
"foreground proc  = " + $(if ($p) { $p.Name } else { '?' })
