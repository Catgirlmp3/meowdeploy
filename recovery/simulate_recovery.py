#!/usr/bin/env python3
"""Offline-only recovery policy simulator. Never sends packets or runs commands."""
from dataclasses import dataclass
from enum import Enum
import unittest

class State(str, Enum):
    HEALTHY="healthy"; SUSPECTED="suspected"; UNREACHABLE="unreachable"
    SERVICE_FAILED="service_failed"; OS_FAILED="os_failed"; POWERED_OFF="powered_off"; ESCALATED="escalated"

@dataclass
class Node:
    heartbeat_age:int=0
    ssh_ok:bool=True
    service_ok:bool=True
    amt_power:str="unknown"
    wol_enabled:bool=False
    attempts:int=0
    last_action_at:int=-10000
    state:State=State.HEALTHY

def evaluate(n:Node, now:int, cooldown:int=300, max_attempts:int=2)->str:
    """Return a proposed action only; never performs a real recovery operation."""
    if n.heartbeat_age<30: n.state=State.HEALTHY; return "none"
    if n.heartbeat_age<60: n.state=State.SUSPECTED; return "probe_again"
    if n.ssh_ok and not n.service_ok:
        n.state=State.SERVICE_FAILED
        if now-n.last_action_at<cooldown:return "cooldown"
        if n.attempts>=max_attempts:n.state=State.ESCALATED;return "alert"
        n.attempts+=1;n.last_action_at=now;return "restart_service"
    if n.ssh_ok and n.service_ok:
        n.state=State.HEALTHY;return "heartbeat_path_issue"
    if n.amt_power=="off":
        n.state=State.POWERED_OFF
        if not n.wol_enabled:return "alert"
        if now-n.last_action_at<cooldown:return "cooldown"
        if n.attempts>=max_attempts:n.state=State.ESCALATED;return "alert"
        n.attempts+=1;n.last_action_at=now;return "wake_on_lan"
    if n.amt_power=="on":
        n.state=State.OS_FAILED
        if now-n.last_action_at<cooldown:return "cooldown"
        if n.attempts>=max_attempts:n.state=State.ESCALATED;return "alert"
        n.attempts+=1;n.last_action_at=now;return "amt_power_cycle_requires_policy"
    n.state=State.UNREACHABLE;return "alert_unknown_power_state"

class PolicyTests(unittest.TestCase):
    def test_short_miss_never_reboots(self): self.assertEqual(evaluate(Node(heartbeat_age=45),100),"probe_again")
    def test_service_failure_restarts_service_first(self): self.assertEqual(evaluate(Node(heartbeat_age=90,service_ok=False),100),"restart_service")
    def test_unknown_power_never_blind_wakes(self): self.assertEqual(evaluate(Node(heartbeat_age=90,ssh_ok=False,wol_enabled=True),100),"alert_unknown_power_state")
    def test_confirmed_off_can_wake(self): self.assertEqual(evaluate(Node(heartbeat_age=90,ssh_ok=False,amt_power="off",wol_enabled=True),100),"wake_on_lan")
    def test_wol_disabled_alerts(self): self.assertEqual(evaluate(Node(heartbeat_age=90,ssh_ok=False,amt_power="off"),100),"alert")
    def test_cooldown_blocks_repeat(self): self.assertEqual(evaluate(Node(heartbeat_age=90,service_ok=False,attempts=1,last_action_at=100),150),"cooldown")
    def test_retry_limit_escalates(self):
        n=Node(heartbeat_age=90,service_ok=False,attempts=2,last_action_at=0)
        self.assertEqual(evaluate(n,1000),"alert");self.assertEqual(n.state,State.ESCALATED)
    def test_ssh_ok_means_heartbeat_path_issue(self): self.assertEqual(evaluate(Node(heartbeat_age=90),100),"heartbeat_path_issue")

if __name__=="__main__": unittest.main(verbosity=2)
