# Scheduling

Schedules are local JSON state and use `Europe/Dublin` by default. Intervals must cover 00:00–24:00 exactly with no gaps or overlaps. CPU percentages apply to all 20 cores; the agent translates them to systemd quota by multiplying by 20 (75% becomes `CPUQuota=1500%`). Overrides have explicit UTC expiry timestamps and are cleared automatically after expiry.
