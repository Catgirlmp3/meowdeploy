# MeowDeploy Windows overnight Wake-on-LAN scheduler.
[CmdletBinding()]
param(
  [ValidateSet("wake-all","install-task","remove-task","test")][string]$Action="test",
  [string]$ConfigPath="$env:ProgramData\MeowDeploy\RecoveryRelay\relay.json",
  [string]$TaskName="MeowDeploy Wake all workers"
)
$ErrorActionPreference="Stop"; Set-StrictMode -Version Latest
function Read-Hosts {
  if(!(Test-Path -LiteralPath $ConfigPath)){throw "Missing relay config: $ConfigPath"}
  $c=Get-Content -LiteralPath $ConfigPath -Raw|ConvertFrom-Json
  $hosts=@($c.hosts|Where-Object{$_.enabled -and $_.role -ne "proxmox" -and $_.mac})
  if(!$hosts.Count){throw "No enabled worker hosts with MAC addresses in $ConfigPath"}
  return $hosts
}
function Send-Wake($h) {
  $m=([string]$h.mac -replace "[:-]","")
  if($m -notmatch "^[0-9A-Fa-f]{12}$"){throw "Invalid MAC for $($h.id)"}
  $macBytes=for($i=0;$i -lt 6;$i++){[Convert]::ToByte($m.Substring($i*2,2),16)}
  $packet=[byte[]](@(255,255,255,255,255,255)+($macBytes*16))
  $udp=[Net.Sockets.UdpClient]::new()
  try{$udp.EnableBroadcast=$true;[void]$udp.Send($packet,$packet.Length,[Net.IPEndPoint]::new([Net.IPAddress]::Broadcast,9))}
  finally{$udp.Dispose()}
  Write-Host "WOL packet sent to $($h.id) ($($h.mac)); boot is not yet verified."
}
if($Action -eq "remove-task"){Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue;Write-Host "Removed task $TaskName";exit}
if($Action -eq "install-task"){
  $script=(Resolve-Path -LiteralPath $PSCommandPath).Path
  $args="-NoProfile -ExecutionPolicy Bypass -File `"$script`" -Action wake-all -ConfigPath `"$ConfigPath`""
  $action=New-ScheduledTaskAction -Execute "powershell.exe" -Argument $args
  $trigger=New-ScheduledTaskTrigger -Daily -At "7:00PM"
  $principal=New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
  Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Description "Send Wake-on-LAN packets to enabled MeowDeploy workers daily at 19:00." -Force|Out-Null
  Write-Host "Installed daily 19:00 task running as SYSTEM; it runs even when nobody is logged on. Ensure Windows is set to the Europe/Dublin time zone."
  exit
}
$hosts=Read-Hosts
foreach($h in $hosts){Send-Wake $h}
