#!/usr/bin/env python3
"""
Lyra's Virtual Keyboard — uinput-based keyboard injector for Stardew Valley on Linux.

SDL2 reads from /dev/input/* directly, bypassing X11/Wayland. xdotool doesn't work.
This creates a virtual keyboard via uinput using python3-evdev.

Usage:
    python3 uinput_keyboard.py key <key_name>              # Press and release a key
    python3 uinput_keyboard.py keys <key1> <key2> ...     # Press and release multiple in sequence
    python3 uinput_keyboard.py type "Hello World"         # Type a string
    python3 uinput_keyboard.py hold <key_name>            # Press key (no release)
    python3 uinput_keyboard.py release <key_name>         # Release key
    python3 uinput_keyboard.py combo <key1>+<key2>        # Press combo (e.g., shift+a)
    python3 uinput_keyboard.py test                       # Send test keystroke 'a'

Key names: a-z, 0-9, enter, escape, tab, space, backspace, delete, up, down,
left, right, shift, ctrl, alt, f1-f12, home, end, pageup, pagedown, etc.
"""

import sys
import time
from evdev import uinput, ecodes as e

# Friendly name -> evdev KEY_* constant
KEY_MAP = {
    'a': e.KEY_A, 'b': e.KEY_B, 'c': e.KEY_C, 'd': e.KEY_D, 'e': e.KEY_E,
    'f': e.KEY_F, 'g': e.KEY_G, 'h': e.KEY_H, 'i': e.KEY_I, 'j': e.KEY_J,
    'k': e.KEY_K, 'l': e.KEY_L, 'm': e.KEY_M, 'n': e.KEY_N, 'o': e.KEY_O,
    'p': e.KEY_P, 'q': e.KEY_Q, 'r': e.KEY_R, 's': e.KEY_S, 't': e.KEY_T,
    'u': e.KEY_U, 'v': e.KEY_V, 'w': e.KEY_W, 'x': e.KEY_X, 'y': e.KEY_Y,
    'z': e.KEY_Z,
    '0': e.KEY_0, '1': e.KEY_1, '2': e.KEY_2, '3': e.KEY_3, '4': e.KEY_4,
    '5': e.KEY_5, '6': e.KEY_6, '7': e.KEY_7, '8': e.KEY_8, '9': e.KEY_9,
    'enter': e.KEY_ENTER, 'return': e.KEY_ENTER, 'escape': e.KEY_ESC, 'esc': e.KEY_ESC,
    'tab': e.KEY_TAB, 'space': e.KEY_SPACE, 'backspace': e.KEY_BACKSPACE, 'delete': e.KEY_DELETE,
    'shift': e.KEY_LEFTSHIFT, 'leftshift': e.KEY_LEFTSHIFT, 'rightshift': e.KEY_RIGHTSHIFT,
    'ctrl': e.KEY_LEFTCTRL, 'leftctrl': e.KEY_LEFTCTRL, 'rightctrl': e.KEY_RIGHTCTRL,
    'alt': e.KEY_LEFTALT, 'leftalt': e.KEY_LEFTALT, 'rightalt': e.KEY_RIGHTALT,
    'up': e.KEY_UP, 'down': e.KEY_DOWN, 'left': e.KEY_LEFT, 'right': e.KEY_RIGHT,
    'f1': e.KEY_F1, 'f2': e.KEY_F2, 'f3': e.KEY_F3, 'f4': e.KEY_F4,
    'f5': e.KEY_F5, 'f6': e.KEY_F6, 'f7': e.KEY_F7, 'f8': e.KEY_F8,
    'f9': e.KEY_F9, 'f10': e.KEY_F10, 'f11': e.KEY_F11, 'f12': e.KEY_F12,
    'home': e.KEY_HOME, 'end': e.KEY_END, 'pageup': e.KEY_PAGEUP, 'pagedown': e.KEY_PAGEDOWN,
    'insert': e.KEY_INSERT, 'minus': e.KEY_MINUS, 'equals': e.KEY_EQUAL,
    'backslash': e.KEY_BACKSLASH, 'semicolon': e.KEY_SEMICOLON, 'quote': e.KEY_APOSTROPHE,
    'comma': e.KEY_COMMA, 'period': e.KEY_DOT, 'slash': e.KEY_SLASH,
    'leftbracket': e.KEY_LEFTBRACE, 'rightbracket': e.KEY_RIGHTBRACE,
    'leftbrace': e.KEY_LEFTBRACE, 'rightbrace': e.KEY_RIGHTBRACE,
    'grave': e.KEY_GRAVE, 'capslock': e.KEY_CAPSLOCK, 'numlock': e.KEY_NUMLOCK,
}

# Characters requiring shift -> (base key code, needs_shift)
SHIFT_MAP = {
    '!': (e.KEY_1, True), '@': (e.KEY_2, True), '#': (e.KEY_3, True),
    '$': (e.KEY_4, True), '%': (e.KEY_5, True), '^': (e.KEY_6, True),
    '&': (e.KEY_7, True), '*': (e.KEY_8, True), '(': (e.KEY_9, True),
    ')': (e.KEY_0, True),
    '_': (e.KEY_MINUS, True), '+': (e.KEY_EQUAL, True),
    '{': (e.KEY_LEFTBRACE, True), '}': (e.KEY_RIGHTBRACE, True),
    '|': (e.KEY_BACKSLASH, True), ':': (e.KEY_SEMICOLON, True),
    '"': (e.KEY_APOSTROPHE, True),
    '<': (e.KEY_COMMA, True), '>': (e.KEY_DOT, True), '?': (e.KEY_SLASH, True),
    '~': (e.KEY_GRAVE, True),
}
for c in 'ABCDEFGHIJKLMNOPQRSTUVWXYZ':
    SHIFT_MAP[c] = (KEY_MAP[c.lower()], True)

# Build the full set of capabilities (all keys we might use)
ALL_KEYS = set(KEY_MAP.values()) | {v for v, _ in SHIFT_MAP.values()}
CAPABILITIES = {e.EV_KEY: list(ALL_KEYS)}

# Persistent device — created once, reused
_device = None


def get_device():
    global _device
    if _device is None:
        _device = uinput.UInput(events=CAPABILITIES, name='Lyra Virtual Keyboard')
        time.sleep(0.1)
    return _device


def close_device():
    global _device
    if _device is not None:
        _device.close()
        _device = None


def key_press(key_name):
    get_device().write(e.EV_KEY, _resolve_key(key_name), 1)
    get_device().syn()


def key_release(key_name):
    get_device().write(e.EV_KEY, _resolve_key(key_name), 0)
    get_device().syn()


def key_tap(key_name, delay=0.03):
    key_press(key_name)
    time.sleep(delay)
    key_release(key_name)
    time.sleep(delay)


def type_string(text, delay=0.05):
    dev = get_device()
    for ch in text:
        if ch in SHIFT_MAP:
            base_code, needs_shift = SHIFT_MAP[ch]
            if needs_shift:
                dev.write(e.EV_KEY, e.KEY_LEFTSHIFT, 1)
                dev.syn()
                time.sleep(0.01)
            dev.write(e.EV_KEY, base_code, 1)
            dev.syn()
            time.sleep(0.02)
            dev.write(e.EV_KEY, base_code, 0)
            dev.syn()
            if needs_shift:
                dev.write(e.EV_KEY, e.KEY_LEFTSHIFT, 0)
                dev.syn()
                time.sleep(0.01)
        elif ch in KEY_MAP:
            dev.write(e.EV_KEY, KEY_MAP[ch], 1)
            dev.syn()
            time.sleep(0.02)
            dev.write(e.EV_KEY, KEY_MAP[ch], 0)
            dev.syn()
        elif ch == '\n':
            key_tap('enter')
        elif ch == '\t':
            key_tap('tab')
        elif ch == ' ':
            key_tap('space')
        else:
            continue
        time.sleep(delay)


def _resolve_key(name):
    name_lower = name.lower().strip()
    if name_lower in KEY_MAP:
        return KEY_MAP[name_lower]
    raise KeyError(f"Unknown key: {name}")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    action = sys.argv[1].lower()

    try:
        if action == 'key':
            key_tap(sys.argv[2])

        elif action == 'keys':
            for k in sys.argv[2:]:
                key_tap(k)
                time.sleep(0.03)

        elif action == 'type':
            type_string(' '.join(sys.argv[2:]))

        elif action == 'hold':
            key_press(sys.argv[2])

        elif action == 'release':
            key_release(sys.argv[2])

        elif action == 'combo':
            keys = sys.argv[2].split('+')
            for k in keys:
                key_press(k)
                time.sleep(0.02)
            time.sleep(0.02)
            for k in reversed(keys):
                key_release(k)
                time.sleep(0.02)

        elif action == 'test':
            print("Sending test key 'a' in 2 seconds...")
            time.sleep(2)
            key_tap('a')
            print("Done!")

        elif action == 'close':
            close_device()
            print("Virtual keyboard closed.")

        else:
            print(f"Unknown action: {action}")
            sys.exit(1)

    except PermissionError:
        print("Permission denied: /dev/uinput")
        print("Run: sudo chmod 660 /dev/uinput && sudo chgrp input /dev/uinput")
        sys.exit(1)
    except Exception as ex:
        print(f"Error: {ex}")
        import traceback
        traceback.print_exc()
        sys.exit(1)


if __name__ == '__main__':
    main()
