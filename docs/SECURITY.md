# Security

The deployment helper uses Paramiko's known-host database and does not auto-add unknown keys. Passwords are held only in memory for the session. The agent accepts an allowlist of typed operations and never executes received shell commands. Configure Tor as a separate onion service and preserve existing `torrc` entries; the agent should use the local SOCKS5 proxy at `127.0.0.1:9050`.
