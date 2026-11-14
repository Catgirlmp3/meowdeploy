# MeowDeploy Windows Recovery Relay MVP. Requires Windows OpenSSH Client.
[CmdletBinding()]
param([ValidateSet("list","probe","wake","open","ssh","restart-xmrig","restart-tor","reboot","watch")][string]$Action="list",[string]$HostId,[string]$ConfigPath="$env:ProgramData\MeowDeploy\RecoveryRelay\relay.json")
$ErrorActionPreference="Stop"; Set-StrictMode -Version Latest
function Load-Config { if(!(Test-Path -LiteralPath $ConfigPath)){throw "Missing config: $ConfigPath"}; $c=Get-Content -LiteralPath $ConfigPath -Raw|ConvertFrom-Json; if(!$c.hosts){throw "Config hosts array is required"}; return $c }
function Audit($c,$id,$op,$result){$p=[Environment]::ExpandEnvironmentVariables([string]$c.logPath);$d=Split-Path -Parent $p;if(!(Test-Path $d)){New-Item -ItemType Directory -Path $d -Force|Out-Null};[ordered]@{utc=(Get-Date).ToUniversalTime().ToString("o");host=$id;operation=$op;result=$result;actor=[Environment]::UserName}|ConvertTo-Json -Compress|Add-Content -LiteralPath $p -Encoding UTF8}
function Get-Target($c,$id){$h=@($c.hosts|Where-Object{$_.id -eq $id -and $_.enabled})|Select-Object -First 1;if(!$h){throw "Enabled host not found: $id"};if($h.address -notmatch "^[0-9a-fA-F:.]+$"){throw "Address must be an IP literal"};return $h}
function Wake($mac){$m=$mac -replace "[^0-9A-Fa-f]","";if($m.Length -ne 12){throw "Invalid MAC: $mac"};$b=for($i=0;$i -lt 6;$i++){[Convert]::ToByte($m.Substring($i*2,2),16)};$packet=[byte[]](@(255,255,255,255,255,255)+($b*16));$u=[Net.Sockets.UdpClient]::new();try{$u.EnableBroadcast=$true;$u.Send($packet,$packet.Length,[Net.IPEndPoint]::new([Net.IPAddress]::Broadcast,9))|Out-Null}finally{$u.Dispose()}}
function Reachable($h){return Test-Connection -ComputerName $h.address -Count 1 -Quiet -ErrorAction SilentlyContinue}
function Ssh($h,$cmd){$exe=(Get-Command ssh.exe -ErrorAction Stop).Source;$target="$($h.sshUser)@$($h.address)";$args=@("-p",[string]$h.sshPort,"-o","BatchMode=yes","-o","ConnectTimeout=8","-o","StrictHostKeyChecking=yes",$target,$cmd);$p=Start-Process -FilePath $exe -ArgumentList $args -NoNewWindow -Wait -PassThru;if($p.ExitCode -ne 0){throw "SSH failed (exit $($p.ExitCode)); verify known_hosts, keys and sudo permissions"}}
$cfg=Load-Config
if($Action -eq "list"){foreach($h in $cfg.hosts){$up=$false;if($h.enabled){$up=Reachable $h};[pscustomobject]@{Id=$h.id;Name=$h.name;IP=$h.address;Enabled=$h.enabled;Reachable=$up;Role=$h.role}};exit}
if($Action -eq "watch"){Write-Warning "Probe/WOL only. No automatic reboot or service restart. ICMP failure is not proof of power-off.";while($true){foreach($h in $cfg.hosts|Where-Object enabled){if(!(Reachable $h) -and $cfg.autoWake -eq $true -and $h.mac){Wake ([string]$h.mac);Audit $cfg $h.id "wake-auto" "packet sent; boot unverified";Write-Host "WOL sent: $($h.id)"}};Start-Sleep -Seconds ([Math]::Max(10,[int]$cfg.pollSeconds))}}
$h=Get-Target $cfg $HostId
try { switch($Action){
"probe" {$ok=Reachable $h;Audit $cfg $HostId "probe" $(if($ok){"reachable"}else{"no ICMP response"});[pscustomobject]@{Host=$HostId;Reachable=$ok;Note="ICMP failure does not prove power-off"}}
"wake" {if(!$h.mac){throw "No MAC configured"};Wake ([string]$h.mac);Audit $cfg $HostId "wake" "packet sent; boot unverified";"Magic packet sent; verify boot separately."}
"open" {if(!$h.proxmoxUrl){throw "No Proxmox URL configured"};Start-Process ([string]$h.proxmoxUrl);Audit $cfg $HostId "open" "browser launched"}
"ssh" {Ssh $h "hostname; uptime";Audit $cfg $HostId "ssh" "completed"}
"restart-xmrig" {Ssh $h "sudo -n systemctl restart meowdeploy-miner.service";Audit $cfg $HostId "restart-xmrig" "requested"}
"restart-tor" {Ssh $h "sudo -n systemctl restart tor.service";Audit $cfg $HostId "restart-tor" "requested"}
"reboot" {$answer=Read-Host "Type REBOOT $HostId to confirm";if($answer -cne "REBOOT $HostId"){throw "Cancelled"};Ssh $h "sudo -n systemctl reboot";Audit $cfg $HostId "reboot" "requested"}
} } catch {Audit $cfg $HostId $Action ("failed: "+$_.Exception.Message);throw}
