"""LTC の一式の数の合否の範囲を、回ごとの集計から計算する（試験基盤の束 2026-10 の 3）。

規則（TSP-Fable の決定、docs/design/test-infra-2026-10.md の 3 節）:
  数え上げの数（relocate・holdEntries・3 つの和・boundary・pump・assertion など）の範囲は、
  「3 回以上の回の最小〜最大」と「中央 ±2√中央」の広いほう。
  - 広いほう = 下端と上端をそれぞれ広い側にとる（両方を含む範囲）。2 つが入れ子なら、広いほうそのもの
  - 中央 ±2√中央 は整数に外向きに丸める（下端は切り捨てで 0 未満にしない、上端は切り上げ）
  - 回が 3 未満の種類は「回が足りない」とし、±2√中央 を参考に出すだけで範囲には使わない
  - 小数の項目（l2def・l2gpu など）は √ を使わず、最小〜最大だけ
  読み方（上に外れたら止める、下は説明できれば記録）は変えない。このスクリプトは範囲を出すだけで、判定はしない。

入力（位置引数、いくつでも。形は中身で見分ける）:
  (a) run-result.json（scripts/ltc-run-report.ps1 の出力、schema ltc-run-result/1）
      読む数: failed・prores.gpu・l2[0].maxFrameDeficitSeconds・crashes.count。
      "metrics"（または "counts"）の辞書があれば、その数も読む
  (b) check.py の出力のテキスト（「##### <label>」の後に「  reloc: 35+17=52」「  hold: 87」などの行）。
      1 つのファイルに複数の回があってよい
  (c) 手で作った JSON: {"runs": [{"label": "...", "kind": "...", "source": "...", "metrics": {...}}]}
      （トップレベルが回の配列でもよい）。kind があれば種類の対応より優先する

回の名前は「<ファイル名（拡張子なし）>#<label>」。--exclude はこの名前に当てる（fnmatch のワイルドカード）。
failed が 1 以上の回は、数が欠けるので自動で除く（--include-failed で入れる）。除いた回は出力に書く。

使い方:
  python scripts/compute-ltc-ranges.py <入力>... --kinds "std1=std,std2=std,heavy-a=heavy" \\
      [--kinds-file kinds.json] [--exclude "check-a691b58-prores*=落ちで数が欠けた"] \\
      [--title "開発機"] [--out ranges.md] [--dump-runs runs.json]
  種類の対応のキーは label への fnmatch のパターン（そのままの名前でもよい）。
  自己試験: python scripts/tests/test_compute_ltc_ranges.py
"""
import argparse
import fnmatch
import json
import math
import os
import re
import statistics
import sys

# 出力の順。ここに無い項目は後ろに名前の順で並べる。
METRIC_ORDER = ['reloc', 'hold', 'back3', 'bound', 'stale', 'pump', 'assert', 'proresGpu',
                'refStale', 'recap', 'l2def', 'l2gpu']
METRIC_LABEL = {
    'reloc': 'relocate', 'hold': 'holdEntries', 'back3': '3 つの和', 'bound': 'boundary',
    'stale': 'dropping stale', 'pump': 'pump: held', 'assert': 'assertion', 'proresGpu': 'ProRes GPU',
    'refStale': 'reference-stale', 'recap': 'recapture-failed', 'l2def': 'L-2 maxFrameDeficit（秒）',
    'l2gpu': 'L-2 maxGpuDeficit（秒）',
}
# 小数の項目（√ を使わない）。ここに無くても、値に小数があれば小数の項目として扱う。
DECIMAL_METRICS = {'l2def', 'l2gpu'}
MIN_RUNS = 3

# check.py の行の名前 → 項目の名前
CHECK_KEYS = {'reloc': 'reloc', 'hold': 'hold', 'back3': 'back3', 'bound': 'bound', 'stale': 'stale',
              'pump': 'pump', 'assert_': 'assert', 'refStale': 'refStale', 'recap': 'recap',
              'l2def': 'l2def', 'l2gpu': 'l2gpu'}


class Run:
    def __init__(self, name, label, metrics, kind=None, failed=0):
        self.name = name
        self.label = label
        self.metrics = metrics
        self.kind = kind
        self.failed = failed


def _num(text):
    text = text.strip()
    if text in ('None', 'null', ''):
        return None
    try:
        return int(text)
    except ValueError:
        return float(text)


def parse_check_text(text, source):
    runs = []
    cur = None
    for line in text.splitlines():
        m = re.match(r'^#####\s+(\S+)', line)
        if m:
            cur = Run(f'{source}#{m.group(1)}', m.group(1), {})
            runs.append(cur)
            continue
        if cur is None:
            continue
        m = re.match(r'^  (\w+): (.*)$', line)
        if not m:
            continue
        key, val = m.group(1), m.group(2).strip()
        if key == 'result':
            parts = val.split('/')
            if len(parts) >= 2:
                cur.failed = int(parts[1])
        elif key == 'reloc':
            cur.metrics['reloc'] = int(val.split('=')[-1])
        elif key == 'prores':
            g = re.search(r"'gpu':\s*(\d+)", val)
            if g:
                cur.metrics['proresGpu'] = int(g.group(1))
        elif key in CHECK_KEYS:
            v = _num(val)
            if v is not None:
                cur.metrics[CHECK_KEYS[key]] = v
    return runs


def _is_number(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def parse_run_result(obj, source):
    metrics = {}
    pr = obj.get('prores') or {}
    if _is_number(pr.get('gpu')):
        metrics['proresGpu'] = pr['gpu']
    l2 = obj.get('l2') or []
    if isinstance(l2, dict):
        l2 = [l2]
    if l2 and _is_number(l2[0].get('maxFrameDeficitSeconds')):
        metrics['l2def'] = float(l2[0]['maxFrameDeficitSeconds'])
    crashes = obj.get('crashes') or {}
    if isinstance(crashes, dict) and _is_number(crashes.get('count')):
        metrics['crashes'] = crashes['count']
    for k in ('metrics', 'counts'):
        for mk, mv in (obj.get(k) or {}).items():
            if _is_number(mv):
                metrics[mk] = mv
    label = obj.get('label') or source
    return [Run(f'{source}#{label}', label, metrics, failed=int(obj.get('failed') or 0))]


def parse_hand_json(obj, source):
    items = obj['runs'] if isinstance(obj, dict) else obj
    runs = []
    for it in items:
        src = it.get('source') or source
        metrics = {k: v for k, v in (it.get('metrics') or {}).items() if _is_number(v)}
        runs.append(Run(f"{src}#{it['label']}", it['label'], metrics, kind=it.get('kind'),
                        failed=int(it.get('failed') or 0)))
    return runs


def parse_text(text, source):
    stripped = text.lstrip('﻿').lstrip()
    if stripped.startswith('{') or stripped.startswith('['):
        obj = json.loads(stripped)
        if isinstance(obj, dict) and str(obj.get('schema', '')).startswith('ltc-run-result'):
            return parse_run_result(obj, source)
        return parse_hand_json(obj, source)
    return parse_check_text(text, source)


def parse_file(path):
    with open(path, encoding='utf-8-sig', errors='replace') as f:
        text = f.read()
    return parse_text(text, os.path.splitext(os.path.basename(path))[0])


def parse_pairs(text):
    """'a=b,c=d' → [('a','b'),('c','d')]。値に , を使わない。"""
    out = []
    for part in (text or '').split(','):
        if not part.strip():
            continue
        k, _, v = part.partition('=')
        out.append((k.strip(), v.strip()))
    return out


def assign_kinds(runs, kind_map):
    for r in runs:
        if r.kind:
            continue
        for pat, kind in kind_map:
            if fnmatch.fnmatchcase(r.label, pat):
                r.kind = kind
                break


def select_runs(runs, excludes, include_failed):
    """(使う回, 除いた回と理由の一覧) を返す。"""
    used, dropped = [], []
    for r in runs:
        reason = None
        for pat, why in excludes:
            if fnmatch.fnmatchcase(r.name, pat):
                reason = why or '指定で除いた'
                break
        if reason is None and r.failed > 0 and not include_failed:
            reason = f'失敗 {r.failed} 件の回（数が欠ける）'
        if reason is None and not r.kind:
            reason = '種類の対応が無い'
        if reason:
            dropped.append((r, reason))
        else:
            used.append(r)
    return used, dropped


def is_decimal(metric, values):
    return metric in DECIMAL_METRICS or any(isinstance(v, float) and not float(v).is_integer() for v in values)


def sqrt_band(median):
    half = 2 * math.sqrt(max(median, 0))
    return max(0, math.floor(median - half)), math.ceil(median + half)


def compute(values, decimal):
    """1 つの種類・項目の範囲。辞書で返す。"""
    n = len(values)
    lo, hi = min(values), max(values)
    med = statistics.median(values)
    row = {'n': n, 'min': lo, 'max': hi, 'median': med, 'decimal': decimal,
           'band': None, 'range': None, 'source': None, 'nested': True, 'enough': n >= MIN_RUNS}
    if decimal:
        row['range'] = (lo, hi) if row['enough'] else None
        row['source'] = '最小〜最大（小数）' if row['enough'] else None
        return row
    band = sqrt_band(med)
    row['band'] = band
    if not row['enough']:
        return row
    rlo, rhi = min(lo, band[0]), max(hi, band[1])
    row['range'] = (rlo, rhi)
    mm_in_band = band[0] <= lo and hi <= band[1]
    band_in_mm = lo <= band[0] and band[1] <= hi
    row['nested'] = mm_in_band or band_in_mm
    if mm_in_band and band_in_mm:
        row['source'] = '同じ'
    elif mm_in_band:
        row['source'] = '±2√中央'
    elif band_in_mm:
        row['source'] = '最小〜最大'
    else:
        row['source'] = '両方の外側（入れ子でない）'
    return row


def metric_sort_key(m):
    return (METRIC_ORDER.index(m), '') if m in METRIC_ORDER else (len(METRIC_ORDER), m)


def build_table(runs, metrics_filter=None):
    """{kind: {metric: row}} と、種類の並び（最初に出た順）を返す。"""
    kinds = []
    data = {}
    for r in runs:
        if r.kind not in data:
            kinds.append(r.kind)
            data[r.kind] = {}
        for m, v in r.metrics.items():
            if metrics_filter and m not in metrics_filter:
                continue
            data[r.kind].setdefault(m, []).append(v)
    table = {}
    for k in kinds:
        table[k] = {}
        for m in sorted(data[k], key=metric_sort_key):
            vals = data[k][m]
            table[k][m] = compute(vals, is_decimal(m, vals))
            table[k][m]['values'] = vals
    return kinds, table


def _fmt(v, decimal):
    if decimal:
        return f'{v:.3f}'
    if isinstance(v, float):
        return f'{v:g}'
    return str(v)


def _fmt_range(rng, decimal):
    if rng is None:
        return '—'
    a, b = rng
    return _fmt(a, decimal) if a == b else f'{_fmt(a, decimal)}〜{_fmt(b, decimal)}'


def render_markdown(title, kinds, table, used, dropped):
    out = []
    if title:
        out.append(f'### {title}')
        out.append('')
    out.append('範囲 = 「最小〜最大」と「中央 ±2√中央」の広いほう（下端・上端をそれぞれ広い側に）。'
               f'回が {MIN_RUNS} 未満の種類は範囲に使わない。小数の項目は最小〜最大だけ。')
    out.append('')
    out.append('| 種類 | 項目 | 回数 | 値 | 最小〜最大 | 中央 | 中央±2√中央 | 範囲 | 範囲の元 |')
    out.append('|---|---|---|---|---|---|---|---|---|')
    for k in kinds:
        for m, row in table[k].items():
            dec = row['decimal']
            vals = '・'.join(_fmt(v, dec) for v in row['values'])
            band = '—（小数）' if dec else _fmt_range(row['band'], False)
            if row['enough']:
                rng = _fmt_range(row['range'], dec)
                src = row['source']
            else:
                rng = f'回が足りない（{row["n"]} 回、範囲に使わない）'
                src = '—'
            out.append(f'| {k} | {METRIC_LABEL.get(m, m)} | {row["n"]} | {vals} | '
                       f'{_fmt_range((row["min"], row["max"]), dec)} | {_fmt(row["median"], dec)} | '
                       f'{band} | {rng} | {src} |')
    out.append('')
    out.append('使った回: ' + ('、'.join(f'{r.name}（{r.kind}）' for r in used) if used else 'なし'))
    out.append('')
    if dropped:
        out.append('除いた回:')
        for r, why in dropped:
            out.append(f'- {r.name}: {why}')
    else:
        out.append('除いた回: なし')
    out.append('')
    return '\n'.join(out)


def main(argv=None):
    ap = argparse.ArgumentParser(description='LTC の一式の数の合否の範囲を計算する（Markdown の表）')
    ap.add_argument('inputs', nargs='+', help='run-result.json・check.py の出力・手で作った JSON')
    ap.add_argument('--kinds', default='', help='label のパターン=種類 を , で区切る（例 "std*=std,heavy-a=heavy"）')
    ap.add_argument('--kinds-file', help='{"パターン": "種類"} の JSON')
    ap.add_argument('--exclude', action='append', default=[],
                    help='回の名前のパターン=理由（何度でも）。名前は <ファイル名>#<label>')
    ap.add_argument('--include-failed', action='store_true', help='失敗のあった回も使う')
    ap.add_argument('--metrics', default='', help='出す項目を , で区切る（既定は全部）')
    ap.add_argument('--title', default='')
    ap.add_argument('--out', help='Markdown の書き出し先（既定は標準出力）')
    ap.add_argument('--dump-runs', help='読んだ回を手で作る JSON の形で書き出す')
    a = ap.parse_args(argv)

    runs = []
    for p in a.inputs:
        runs.extend(parse_file(p))
    kind_map = parse_pairs(a.kinds)
    if a.kinds_file:
        with open(a.kinds_file, encoding='utf-8-sig') as f:
            kind_map.extend(json.load(f).items())
    assign_kinds(runs, kind_map)
    excludes = []
    for e in a.exclude:
        pat, _, why = e.partition('=')
        excludes.append((pat.strip(), why.strip()))
    used, dropped = select_runs(runs, excludes, a.include_failed)
    metrics_filter = set(x.strip() for x in a.metrics.split(',') if x.strip()) or None
    kinds, table = build_table(used, metrics_filter)
    md = render_markdown(a.title, kinds, table, used, dropped)

    if a.dump_runs:
        dump = {'runs': [{'source': r.name.split('#')[0], 'label': r.label, 'kind': r.kind, 'failed': r.failed,
                          'metrics': r.metrics} for r in runs]}
        with open(a.dump_runs, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(dump, f, ensure_ascii=False, indent=2)
            f.write('\n')
    if a.out:
        with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
            f.write(md)
    else:
        sys.stdout.reconfigure(encoding='utf-8')
        sys.stdout.write(md)
    return 0


if __name__ == '__main__':
    sys.exit(main())
