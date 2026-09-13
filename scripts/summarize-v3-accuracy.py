"""Print the two V3 metrics from one accuracy-samples.csv.

Steady error: signed error over continuous playback (no excludedReason), with
black-sweep dropped because no correct frame exists during a black gap.
Recovery time: first sample in each phase within a tolerance, swept - a tolerance
below the steady error can never be reached, so a single number would mislead.
See the "V3 criteria" section in docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md.
"""
import csv, io, statistics, sys

SEEK = ['seek-a', 'seek-b', 'seek-c', 'seek-back']
GAP = ['black-sweep', 'freeze-sweep']


def pct(values, p):
    if not values:
        return None
    v = sorted(values)
    return v[max(0, min(len(v) - 1, int(round(p / 100 * (len(v) - 1)))))]


def main(path, label):
    rows = list(csv.DictReader(io.open(path, encoding='utf-8')))
    steady = [float(r['signedErrorMs']) for r in rows
              if r['status'] == 'measured' and not r['excludedReason']
              and r['signedErrorMs'] and r['phase'] != 'black-sweep']
    print(f'--- {label}')
    if steady:
        print(f'  steady n={len(steady):<5} mean={statistics.fmean(steady):+8.1f}  '
              f'p50={pct(steady,50):+8.1f}  p5={pct(steady,5):+8.1f}  p95={pct(steady,95):+8.1f}  '
              f'spread(p95-p5)={pct(steady,95)-pct(steady,5):7.1f}ms')
    else:
        print('  steady: no measured samples')
    print(f'  recovery (ms to first |err| <= tol; "-" = not reached in phase)')
    print('    {:>6} | '.format('tol') + ' '.join(f'{p:>12}' for p in SEEK + GAP))
    for tol in (80, 150, 250, 400, 700):
        cells = []
        for ph in SEEK + GAP:
            s = sorted((r for r in rows if r['phase'] == ph and r['status'] == 'measured' and r['signedErrorMs']),
                       key=lambda r: float(r['phaseElapsedMs']))
            hit = next((float(r['phaseElapsedMs']) for r in s if abs(float(r['signedErrorMs'])) <= tol), None)
            cells.append(f'{hit:12.0f}' if hit is not None else f'{"-":>12}')
        print(f'    {tol:>6} | ' + ' '.join(cells))


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else sys.argv[1])
