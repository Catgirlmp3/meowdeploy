# Scheduling

Schedules are local JSON state under `/var/lib/meowdeploy/state.json` and use
`Europe/Dublin` by default. Intervals must cover `00:00`-`24:00` exactly, with no gaps
or overlaps. A zero-length interval is rejected.

CPU percentages apply to the host's available CPU count:

```text
CPUQuota = round(percent * available_cores)
```

Examples:

| Host cores | Requested | CPUQuota |
| --- | --- | --- |
| 8 | 30% | 240% |
| 8 | 75% | 600% |
| 20 | 30% | 600% |
| 20 | 75% | 1500% |

The agent uses its CPU affinity mask when available, so a host restricted with
`CPUAffinity` is not over-allocated.

Temporary overrides have explicit UTC expiry timestamps and are cleared automatically.
The agent evaluates both the local schedule and override expiry on every poll cycle,
including when the controller is offline. `apply_now` writes the current effective
quota to `/etc/systemd/system/meowdeploy-miner.service.d/90-meowdeploy.conf`, reloads
systemd, and restarts the miner only if the file actually changed.

`stop_miner`, `start_miner`, and `restart_miner` do not re-apply the schedule
afterwards; a stop is not immediately undone by an automatic restart.
