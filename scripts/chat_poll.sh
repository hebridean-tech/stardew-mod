#!/bin/bash
# Stardew Chat Poll — called by cron to check for new in-game messages
# Returns exit code 1 if no messages (cron should handle silently)
# Returns exit code 0 and prints messages if there are pending ones

BRIDGE_DIR="$HOME/.openclaw/agents/lyra/agent/stardew_bridge"
PENDING="$BRIDGE_DIR/chat_pending.json"
TRIGGER="$BRIDGE_DIR/chat_trigger.flag"

# Check if trigger exists and is recent (within last 30 seconds)
if [ ! -f "$TRIGGER" ]; then
    exit 1
fi

# Check trigger age
TRIGGER_AGE=$(($(date +%s) - $(stat -c %Y "$TRIGGER" 2>/dev/null || echo 0)))
if [ "$TRIGGER_AGE" -gt 30 ]; then
    # Stale trigger, clean up
    rm -f "$TRIGGER"
    exit 1
fi

# Check pending file
if [ ! -f "$PENDING" ]; then
    exit 1
fi

# Read and output pending messages
python3 -c "
import json, sys
try:
    with open('$PENDING') as f:
        data = json.load(f)
    if isinstance(data, list) and len(data) > 0:
        for msg in data:
            print(f'{msg.get(\"from\", \"?\")}: {msg.get(\"text\", \"\")}')
        sys.exit(0)
    else:
        sys.exit(1)
except Exception as e:
    sys.exit(1)
"

# If we got messages, consume them (clear pending + trigger)
if [ $? -eq 0 ]; then
    echo '[]' > "$PENDING"
    rm -f "$TRIGGER"
fi
