"""Check invariant I13 in the GStreamer shim.

I13: never call gst_element_set_state / gst_element_seek / gst_element_send_event
while holding a lock that the streaming thread can need.

frame_lock IS taken by the streaming thread (on_new_sample), so a state change
under frame_lock can deadlock: set_state waits for the streaming thread, which
is parked on frame_lock. Those are hard failures.

state_mutex is currently NOT taken by the streaming thread - only by the API
thread and the dedicated bus thread - so holding it across set_state does not
close a cycle. That is a property of the code, not of the mutex, and it stops
being true the moment a new call site appears. So this script pins the set of
functions that acquire state_mutex; adding one fails the check and forces a
human to redo the reasoning.

Usage:
    python scripts/check-shim-lock-rule.py [path-to-tcs_gstreamer.cpp]

Exit code 0 = clean, 1 = violation.
"""
import io
import os
import re
import sys

LOCK = re.compile(
    r'\b(?:std::)?(?:lock_guard|unique_lock|scoped_lock)\s*<[^>]*>\s*(\w+)\s*\(\s*([A-Za-z_][\w>\-.:]*)')
UNLOCK = re.compile(r'\b(\w+)\s*\.\s*unlock\s*\(')
STATE_CALL = re.compile(r'\bgst_element_(set_state|seek|send_event)\s*\(')
STR = re.compile(r'"(?:\\.|[^"\\])*"')
# This file puts the return type on its own line, so a definition header is a
# bare identifier at column 0 followed by the parameter list.
FUNC = re.compile(r'^([A-Za-z_]\w*)\s*\([^;]*$')

# Functions allowed to hold state_mutex. Keep in sync with the reasoning in
# docs/OUTPUT-GPU-INVARIANTS.md (I13). None of these run on the streaming
# thread: pump_arm / tcs_player_set_paused are API-thread, pump_preroll_tick is
# the dedicated bus thread (bus_loop), and the sync bus handler takes no lock.
STATE_MUTEX_HOLDERS = {
    'pump_arm',
    'pump_preroll_tick',
    'tcs_player_set_paused',
}

STREAMING_LOCKS = {'frame_lock'}


def strip_noise(lines):
    """Blank out comments and string literals so brace counting is reliable."""
    out = []
    in_block = False
    for ln in lines:
        s = ln
        if in_block:
            if '*/' in s:
                s = s[s.index('*/') + 2:]
                in_block = False
            else:
                out.append('')
                continue
        s = re.sub(r'//.*', '', s)
        while '/*' in s:
            a = s.index('/*')
            b = s.find('*/', a)
            if b >= 0:
                s = s[:a] + ' ' + s[b + 2:]
            else:
                s = s[:a]
                in_block = True
                break
        s = STR.sub('""', s)
        out.append(s)
    return out


def enclosing_function(clean, idx):
    """Nearest preceding line that looks like a function definition header."""
    for k in range(idx, max(idx - 80, -1), -1):
        m = FUNC.match(clean[k].rstrip())
        if m and m.group(1) not in ('if', 'for', 'while', 'switch', 'return'):
            return m.group(1)
    return '?'


def held_region(clean, start, var):
    """Lines over which the lock taken at `start` is held."""
    rel = 0
    end = None
    j = start
    while j < len(clean) and end is None:
        for ch in clean[j]:
            if ch == '{':
                rel += 1
            elif ch == '}':
                rel -= 1
                if rel < 0:
                    end = j
                    break
        j += 1
    if end is None:
        end = len(clean) - 1
    for k in range(start + 1, end + 1):
        u = UNLOCK.search(clean[k])
        if u and u.group(1) == var:
            return start + 1, k
    return start + 1, end


def main(argv):
    path = argv[1] if len(argv) > 1 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), '..',
        'native', 'gst-shim', 'src', 'tcs_gstreamer.cpp')
    raw = io.open(path, encoding='utf-8', errors='replace').read().split('\n')
    clean = strip_noise(raw)

    hard = []
    soft = []
    holders = {}

    for i, s in enumerate(clean):
        m = LOCK.search(s)
        if not m:
            continue
        var, mutex_expr = m.group(1), m.group(2)
        mutex = mutex_expr.split('>')[-1].split('.')[-1]
        fn = enclosing_function(clean, i)
        if mutex == 'state_mutex':
            holders.setdefault(fn, i + 1)
        lo, hi = held_region(clean, i, var)
        for k in range(lo, hi + 1):
            c = STATE_CALL.search(clean[k])
            if not c:
                continue
            rec = (k + 1, mutex, c.group(1), raw[k].strip()[:88], i + 1, fn)
            (hard if mutex in STREAMING_LOCKS else soft).append(rec)

    print('checking %s' % os.path.normpath(path))

    failed = False

    if hard:
        failed = True
        print('\nFAIL: state change while holding a lock the streaming thread needs')
        for ln, mx, fn_, txt, lk, owner in hard:
            print('  %s:%d  gst_element_%s under %s (taken at line %d, in %s)'
                  % (os.path.basename(path), ln, fn_, mx, lk, owner))
            print('      %s' % txt)
    else:
        print('\nOK: no state change under %s' % '/'.join(sorted(STREAMING_LOCKS)))

    unexpected = set(holders) - STATE_MUTEX_HOLDERS
    missing = STATE_MUTEX_HOLDERS - set(holders)
    if unexpected:
        failed = True
        print('\nFAIL: new function acquires state_mutex; redo the I13 reasoning')
        for fn in sorted(unexpected):
            print('  %s (line %d) - can it run on the streaming thread?'
                  % (fn, holders[fn]))
        print('  If it cannot, add it to STATE_MUTEX_HOLDERS in this script and')
        print('  to the I13 note in docs/OUTPUT-GPU-INVARIANTS.md.')
    if missing:
        print('\nNOTE: expected state_mutex holder(s) no longer present: %s'
              % ', '.join(sorted(missing)))
        print('  Remove them from STATE_MUTEX_HOLDERS once confirmed intentional.')

    if soft:
        print('\nallowed (state_mutex is not taken by the streaming thread):')
        for ln, mx, fn_, txt, lk, owner in soft:
            print('  %s:%d  gst_element_%s under %s, in %s'
                  % (os.path.basename(path), ln, fn_, mx, owner))

    print('\nresult: %s' % ('FAIL' if failed else 'PASS'))
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
