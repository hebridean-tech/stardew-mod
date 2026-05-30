#!/usr/bin/env python3
"""
Stardew In-Game Chat → OpenClaw Wake Bridge

Watches chat_in.json for new in-game messages. When new messages arrive,
it writes a compact summary to chat_wake_message.txt that a cron job's
agentTurn prompt can source. This keeps the agentTurn prompt simple:
just read the wake file and respond if non-empty.

Flow:
  1. SMAPI mod captures chat → chat_in.json  
  2. This watcher detects new → chat_wake_message.txt (compact text)
  3. Cron fires agentTurn, reads wake file, responds as Lyra
  4. Lyra uses bridge chat command → commands.json → in-game chat
"""

import json
import time
from pathlib import Path
from datetime import datetime

BRIDGE_DIR = Path(__file__).parent
CHAT_IN = BRIDGE_DIR / "chat_in.json"
WAKE_MSG = BRIDGE_DIR / "chat_wake_message.txt"
SEEN_FILE = BRIDGE_DIR / ".chat_seen.json"

POLL_INTERVAL = 3.0


def load_seen():
    if SEEN_FILE.exists():
        try:
            with open(SEEN_FILE, "r") as f:
                return set(json.load(f))
        except Exception:
            pass
    return set()


def save_seen(seen):
    try:
        with open(SEEN_FILE, "w") as f:
            json.dump(list(seen), f)
    except Exception:
        pass


def make_msg_key(msg, index):
    text = msg.get("text", "")
    from_name = msg.get("from", "?")
    return f"{from_name}:{text[:40]}:{index}"


def main():
    print(f"[ChatBridge] Starting — watching {CHAT_IN}")
    seen = load_seen()

    while True:
        try:
            if CHAT_IN.exists():
                with open(CHAT_IN, "r") as f:
                    data = json.load(f)

                if isinstance(data, list) and len(data) > 0:
                    new_messages = []

                    for i, msg in enumerate(data):
                        key = make_msg_key(msg, i)
                        if key not in seen:
                            seen.add(key)
                            from_name = msg.get("from", "?")
                            text = msg.get("text", "").strip()
                            if text:
                                new_messages.append({
                                    "from": from_name,
                                    "text": text
                                })

                    if new_messages:
                        # Build a compact wake message for the cron agentTurn
                        lines = []
                        for m in new_messages:
                            lines.append(f"[{m['from']} via Stardew chat]: {m['text']}")

                        wake_text = "\n".join(lines)

                        with open(WAKE_MSG, "w") as f:
                            f.write(wake_text)

                        save_seen(seen)

                        for m in new_messages:
                            print(f"[ChatBridge] NEW from {m['from']}: {m['text'][:80]}")

        except json.JSONDecodeError:
            pass
        except Exception as e:
            print(f"[ChatBridge] Error: {e}")

        time.sleep(POLL_INTERVAL)


if __name__ == "__main__":
    main()
