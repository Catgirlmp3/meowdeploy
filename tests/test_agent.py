import datetime as dt, json
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).parents[1] / "agent"))
from meowdeploy_agent import Agent, quota, validate_schedule, scheduled_percent

S = {"timezone":"Europe/Dublin","intervals":[{"start":"00:00","end":"06:00","percent":75},{"start":"06:00","end":"17:00","percent":35},{"start":"17:00","end":"18:00","percent":50},{"start":"18:00","end":"00:00","percent":60}]}
def test_quota(): assert quota(75)==1500 and quota(35)==700
def test_schedule_and_midnight():
    validate_schedule(S); assert scheduled_percent(S, dt.datetime(2026,1,1,2)) == 75; assert scheduled_percent(S, dt.datetime(2026,1,1,21)) == 60
def test_override_persists(tmp_path):
    a=Agent(tmp_path/'state.json', True); a.command('set_schedule',S); scheduled=a.effective_percent(); a.command('temporary_override', {'percent':25,'duration_seconds':3600}); b=Agent(tmp_path/'state.json', True); assert b.effective_percent()==25; b.command('clear_override', {}); assert b.effective_percent()==scheduled
def test_reject_gap():
    bad={"intervals":[{"start":"00:00","end":"05:00","percent":1},{"start":"06:00","end":"00:00","percent":1}]}
    try: validate_schedule(bad); assert False
    except ValueError: pass
