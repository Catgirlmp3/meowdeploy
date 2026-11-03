#!/usr/bin/env python3
"""Deploy the reviewed agent with Paramiko; never executes arbitrary commands."""
import argparse, concurrent.futures, getpass, pathlib, paramiko
ALLOWED = ("install", "verify")
def deploy(host, password, agent, service):
    result={"host":host,"ok":False,"error":None}; client=paramiko.SSHClient(); client.load_system_host_keys()
    try:
        client.connect(host, username="root", password=password, look_for_keys=False, allow_agent=False, timeout=10)
        sftp=client.open_sftp(); sftp.mkdir('/opt/meowdeploy') if not _exists(sftp,'/opt/meowdeploy') else None
        sftp.put(agent, '/opt/meowdeploy/meowdeploy_agent.py'); sftp.put(service, '/etc/systemd/system/meowdeploy-agent.service');
        cmd='chmod 755 /opt/meowdeploy/meowdeploy_agent.py && systemctl daemon-reload && systemctl enable --now meowdeploy-agent.service && systemctl is-active meowdeploy-agent.service'
        _, out, err=client.exec_command(cmd); result["ok"] = out.read().decode().strip() == 'active'; result["error"] = err.read().decode().strip() or None
    except Exception as e: result["error"]=str(e)
    finally: client.close()
    return result
def _exists(sftp,path):
    try:sftp.stat(path);return True
    except IOError:return False
def main():
    p=argparse.ArgumentParser(); p.add_argument('--servers',default='deployment/servers.txt'); p.add_argument('--workers',type=int,default=5); a=p.parse_args(); password=getpass.getpass('Root SSH password (not stored): ')
    hosts=[x.strip() for x in pathlib.Path(a.servers).read_text().splitlines() if x.strip() and not x.startswith('#')]; files=("agent/meowdeploy_agent.py","agent/meowdeploy-agent.service")
    with concurrent.futures.ThreadPoolExecutor(max_workers=min(5,a.workers)) as pool:
        for r in pool.map(lambda h:deploy(h,password,*files),hosts): print(('OK ' if r['ok'] else 'FAIL ')+r['host']+((' '+r['error']) if r['error'] else ''))
if __name__=='__main__': main()
