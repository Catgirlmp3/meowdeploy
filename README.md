# MeowDeploy

MeowDeploy is a Windows .NET 8 WinForms fleet controller plus a visible Python systemd agent for MeowKit hosts. The agent runs schedules locally, persists temporary overrides, and changes only `/etc/systemd/system/meowkit.service.d/90-meowdeploy.conf`.

This repository does not deploy to real servers automatically. Review `docs/INSTALLATION.md`, configure Tor and authenticated controller transport, then run the deployment helper deliberately.
