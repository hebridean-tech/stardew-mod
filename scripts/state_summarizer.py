#!/usr/bin/env python3
"""
LyraAI State Summarizer
Dramatically reduces token usage for the AI agent by turning raw state.json
into a compact, high-signal summary.

Usage examples:
    python state_summarizer.py
    python state_summarizer.py --focus navigation
    python state_summarizer.py --focus crops
    python state_summarizer.py --compact
"""

import json
import sys
import argparse
from pathlib import Path
from typing import Any, Dict, List, Optional


BRIDGE_DIR = Path(__file__).parent
STATE_FILE = BRIDGE_DIR / "state.json"
CHAT_FILE = BRIDGE_DIR / "chat_in.json"


def load_state() -> Dict[str, Any]:
    if not STATE_FILE.exists():
        return {}
    try:
        with open(STATE_FILE, "r") as f:
            return json.load(f)
    except Exception:
        return {}


def load_recent_chat(limit: int = 6) -> List[Dict[str, Any]]:
    if not CHAT_FILE.exists():
        return []
    try:
        with open(CHAT_FILE, "r") as f:
            data = json.load(f)
            if isinstance(data, list):
                return data[-limit:]
            return []
    except Exception:
        return []


def summarize_crops(farm: Dict[str, Any], focus: str) -> str:
    total = farm.get("cropCount", 0)
    if total == 0:
        return "No unwatered crops detected."

    unwatered = farm.get("unwateredCrops", [])
    if not unwatered:
        return f"{total} unwatered crops (exact positions not available)."

    # Simple clustering by proximity
    clusters: List[Dict[str, Any]] = []
    used = set()

    for i, crop in enumerate(unwatered):
        if i in used:
            continue
        cx, cy = crop["x"], crop["y"]
        cluster = [(cx, cy)]
        used.add(i)

        for j, other in enumerate(unwatered):
            if j in used:
                continue
            ox, oy = other["x"], other["y"]
            if abs(ox - cx) <= 3 and abs(oy - cy) <= 3:
                cluster.append((ox, oy))
                used.add(j)

        if len(cluster) >= 2:
            avg_x = sum(p[0] for p in cluster) / len(cluster)
            avg_y = sum(p[1] for p in cluster) / len(cluster)
            clusters.append({
                "count": len(cluster),
                "center": (round(avg_x), round(avg_y)),
                "positions": cluster[:5]  # limit for tokens
            })

    # Sort clusters by size
    clusters.sort(key=lambda c: c["count"], reverse=True)

    parts = [f"Total unwatered: {total}"]
    for c in clusters[:3]:  # top 3 clusters only
        cx, cy = c["center"]
        parts.append(f"Cluster of {c['count']} around ({cx},{cy})")

    remaining = total - sum(c["count"] for c in clusters[:3])
    if remaining > 0:
        parts.append(f"+ {remaining} scattered")

    # Add nearby detail if focused on crops
    if focus in ("crops", "full"):
        nearby = [c for c in unwatered 
                  if abs(c["x"] - (farm.get("lyra", {}).get("tileX", 0))) < 5 and 
                     abs(c["y"] - (farm.get("lyra", {}).get("tileY", 0))) < 5][:4]
        if nearby:
            parts.append("Nearby unwatered: " + ", ".join(f"({c['x']},{c['y']})" for c in nearby))

    return ". ".join(parts) + "."


def summarize_navigation(farm: Dict[str, Any], lyra: Dict[str, Any]) -> str:
    exits = farm.get("exits", [])
    if not exits:
        return "No exits available in current location."

    current_x = lyra.get("tileX", 0)
    current_y = lyra.get("tileY", 0)

    # Sort exits by distance
    def dist(e):
        return ((e.get("x", 0) - current_x) ** 2 + (e.get("y", 0) - current_y) ** 2) ** 0.5

    sorted_exits = sorted(exits, key=dist)[:4]  # top 4 closest

    parts = []
    for e in sorted_exits:
        d = round(dist(e), 1)
        target = e.get("targetLocation", "?")
        parts.append(f"{target} ({e.get('x')},{e.get('y')}) — {d} tiles")

    summary = "Closest exits: " + "; ".join(parts)

    # Add transition awareness
    status = farm.get("transitionStatus", "")
    stuck = farm.get("isStuckOnTransition", False)
    dist_to_exit = farm.get("distanceToNearestExit", -1)

    if stuck:
        summary += f". WARNING: Stuck near exit for {farm.get('transitionStuckTicks', 0)} ticks."
    elif dist_to_exit > 0 and dist_to_exit < 6:
        summary += f". You are only {round(dist_to_exit, 1)} tiles from nearest exit."

    return summary


def build_summary(state: Dict[str, Any], focus: str = "full", compact: bool = False) -> Dict[str, Any]:
    lyra = state.get("lyra", {})
    farm = state.get("farm", {})
    world = state.get("world", {})
    nearby = state.get("nearbyPlayers", [])

    summary: Dict[str, Any] = {
        "location": lyra.get("location"),
        "position": [lyra.get("tileX"), lyra.get("tileY")],
        "following": lyra.get("following"),
        "stamina": f"{lyra.get('stamina')}/{lyra.get('maxStamina')}",
        "time": f"{world.get('time', 0)} ({world.get('season')} {world.get('day')})",
        "weather": world.get("weather"),
    }

    if nearby:
        summary["nearby"] = [p.get("name") for p in nearby]

    # Navigation focus or full
    if focus in ("navigation", "full"):
        summary["navigation"] = summarize_navigation(farm, lyra)

    # Crops focus or full
    if focus in ("crops", "full"):
        summary["crops"] = summarize_crops(farm, focus)

    # Transition / stuck status (always useful)
    if farm.get("isStuckOnTransition"):
        summary["stuck"] = {
            "status": "STUCK",
            "ticks": farm.get("transitionStuckTicks"),
            "suggestion": "Consider different exit or temporary warp"
        }
    elif farm.get("transitionStatus") == "exits_available":
        summary["transition"] = {
            "status": "ready",
            "nearest_exit_dist": round(farm.get("distanceToNearestExit", -1), 1)
        }

    # Recent chat - high quality, low token conversation context (crucial for natural play)
    recent_chat = load_recent_chat(10)
    if recent_chat:
        # Produce a clean, recent conversation transcript (max ~4 turns)
        chat_lines = []
        for msg in recent_chat[-7:]:
            speaker = msg.get("from", "Unknown")
            text = msg.get("text", "").strip()[:140]
            if text:
                chat_lines.append(f"{speaker}: {text}")

        # Keep it very compact but readable
        summary["chat"] = chat_lines[-5:] if chat_lines else []

        # Add a tiny "last thing said to you" for quick context
        if chat_lines:
            last = chat_lines[-1]
            if "Raphtalia" in last or "you" in last.lower():
                summary["last_message_to_you"] = last

    # High-signal situation report (very useful for the agent)
    situation_parts = []
    if summary.get("following"):
        situation_parts.append(f"Following {summary['following']}")
    if "navigation" in summary:
        situation_parts.append(summary["navigation"][:180])
    if "crops" in summary and "Cluster" in summary["crops"]:
        situation_parts.append("Crops need attention")

    if situation_parts:
        summary["situation"] = ". ".join(situation_parts) + "."

    # Tie chat into situation if relevant (helps the agent stay conversational)
    if summary.get("chat") and "social" not in [s.lower() for s in summary.get("suggested_focus", [])]:
        summary["situation"] = (summary.get("situation", "") + " Recent conversation happening.").strip()

    # Suggested focus for the agent (helps it decide what to think about)
    suggestions = []
    if farm.get("isStuckOnTransition"):
        suggestions.append("navigation")
    elif farm.get("cropCount", 0) > 8:
        suggestions.append("crops")
    if summary.get("following"):
        suggestions.append("social")
    if farm.get("nearbyChests"):
        suggestions.append("inventory")
    if summary.get("chat"):
        suggestions.append("social")
    summary["suggested_focus"] = suggestions or ["navigation"]

    # Chest summary (very token efficient)
    chests = farm.get("nearbyChests", [])
    if chests:
        summary["chests"] = [
            f"{c.get('summary', 'unknown')} chest {round(c.get('distance', 0), 1)} tiles away at ({c.get('x')},{c.get('y')})"
            for c in chests[:3]
        ]

    # Foraging summary (minimal tokens, high value)
    forage = farm.get("forage", {})
    if forage and forage.get("count", 0) > 0:
        summary["forage"] = forage.get("summary", f"{forage.get('count')} forageables nearby")

    # Autonomy opportunities (very token-light, intelligence lives here in Python)
    autonomy = farm.get("autonomy", {})
    if autonomy:
        summary["autonomy"] = {
            "mode": "on" if autonomy.get("autonomy_mode") else "off",
            "opportunities": autonomy.get("opportunities", []),
            "suggestion": autonomy.get("primary_suggestion", "idle"),
            "idle_level": autonomy.get("idle_level", "medium")
        }

    # Ultra compact mode for very low token use
    if compact:
        return {
            "loc": summary.get("location"),
            "pos": summary.get("position"),
            "sit": summary.get("situation"),
            "nav": summary.get("navigation"),
            "crops": summary.get("crops"),
            "chat": summary.get("chat"),
            "focus": summary.get("suggested_focus"),
        }

    return summary


def get_agent_perception(focus: str = "full", compact: bool = False) -> Dict[str, Any]:
    """
    Primary function for the AI agent to call.
    Returns a highly optimized, low-token perception packet.
    This is the recommended interface for the controlling agent.
    """
    state = load_state()
    if not state:
        return {"error": "No state available"}

    return build_summary(state, focus=focus, compact=compact)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--focus", choices=["full", "navigation", "crops"], default="full")
    parser.add_argument("--compact", action="store_true", help="Return ultra-minimal output")
    parser.add_argument("--json", action="store_true", help="Output as JSON instead of text")
    args = parser.parse_args()

    state = load_state()
    if not state:
        print("No valid state.json found.")
        sys.exit(1)

    summary = build_summary(state, focus=args.focus, compact=args.compact)

    if args.json:
        print(json.dumps(summary, indent=2))
    else:
        # Human-readable compact report (very token efficient)
        lines = []
        lines.append(f"Location: {summary.get('location')} @ {summary.get('position')}")
        if summary.get("following"):
            lines.append(f"Following: {summary['following']}")
        if "navigation" in summary:
            lines.append(f"Nav: {summary['navigation']}")
        if "crops" in summary:
            lines.append(f"Crops: {summary['crops']}")
        if "stuck" in summary:
            lines.append(f"STUCK: {summary['stuck']}")
        if summary.get("chat"):
            lines.append("Chat: " + " | ".join(summary["chat"]))
        if summary.get("situation"):
            lines.append(f"Situation: {summary['situation']}")

        print("\n".join(lines))


if __name__ == "__main__":
    main()