#!/usr/bin/env python3
"""Lists every line of the launcher's interface that languages.csv does not translate into all languages.
usage: l10n_audit.py [--out file]   Exit code 1 when something is missing.
       l10n_audit.py --new <code> <name>   Writes languages-<code>.csv for a new language: every line in English beside an empty column."""
import csv, glob, io, json, os, re, sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
# Literals in these files that are shown to the player go through L.T. Paths, names and code strings are filtered below.
CS_FILES = ['AboutPage.cs', 'InfoPages.cs', 'SettingsPage.cs', 'SettingsView.axaml.cs', 'GameSettingsView.axaml.cs', 'ControlsView.axaml.cs', 'MainWindow.axaml.cs', 'TextPage.cs', 'JacintoLauncher/Theme.cs']
SKIP = re.compile(r'^(https?:|avares:|M\d|mods/|assets/|bg\.jpg|multipatch\.png|.*\.(jpg|png|txt|cs|json|exe|ini|csv)$|[A-Za-z]+Brush$|LeftMouseButton|RightMouseButton|MiddleMouseButton|ThumbMouseButton2?|MouseScroll\w+|SP_\w+)')
# Names that stay as they are in every language: products, key names, console commands and map events.
TECHNICAL = re.compile(r"^(GearGame|Localization|Rabbit77rage|Mark Anthony|Proton, umu-launcher or Wine|GitHub|\.NET|Avalonia.*|SkiaSharp|HarfBuzzSharp|Sentry|Aptabase|HTTP API v0|Steamworks SDK.*|JacintoNet|DXVK|Unreal Engine 3|Gears of War 3 Jacinto 1\.1\.1|\{.*|HD Textures Pack|HD Movies Pack|Türkçe|Português - Brasil| launch started|FXAA / Temporal AA|MSAA \dx|SSD / NVMe|HDD"
                       r"|(Ctrl|Alt|Shift|U|Num|Delete|Page|Insert)\b.*|God|Badass|SuperDamage|Unlimited.*|Loaded|Invis|Fly / Ghost / Walk|slomo .*|SpawnWeapon .*|GiveWeapon .*|kill enemy.*|KillMyTarget|DBNOMyTarget|DebugAI .*|ToggleFlocking|DebugHeadshots|open .*|servertravel .*|ce <.*|HasChapterUnlock .*|UnlockAllChapters|ShowDamage|FF|LaughTrack.*|ShowProfileSettings|ScreenshotMode|stat fps.*|viewmode .*|show collision.*"
                       r"|(?i:jump|load_01|cine_|firstfight|gogo|enable_lift|gotrain|test_xx|chapter1).*)$")


def literals(path):
    text = open(os.path.join(ROOT, path), encoding='utf-8').read()
    # Only what reaches the screen: arguments of L.T / L.F and the data tables of the pages.
    found = []
    for call in re.finditer(r'\bL\.[TF]\((.*?)\)\s*[;,)\]}+:?]', text):
        found += re.findall(r'"((?:[^"\\]|\\.)*)"', call.group(1))
    if os.path.basename(path) in ('InfoPages.cs', 'AboutPage.cs'):
        for line in text.splitlines():
            if re.search(r'^\s*\("|^\s*"|InfoUi\.(Text|Muted|Heading|Open)\(|Section\("|Link\("|\(L\.T|panel\.Children\.Add', line):
                found += re.findall(r'"((?:[^"\\]|\\.)*)"', line)
    if os.path.basename(path) == 'MainWindow.axaml.cs':
        found += re.findall(r'L\.T\((?:[^;]*?)\? "([^"]+)" : "([^"]+)"\)', text) and [x for p in re.findall(r'\? "([^"]+)" : "([^"]+)"', text) for x in p]
    return found


def wanted():
    lines = {}
    for path in CS_FILES:
        for s in literals(path):
            s = s.replace('\\n', '\n').replace('\\"', '"').lstrip('#')
            if s and re.search('[A-Za-z]{2}', s) and not SKIP.match(s) and ('.' in s or ' ' in s or s.upper() == s or s[0].isupper()):
                lines.setdefault(s, path)
    for path in glob.glob(os.path.join(ROOT, '*.axaml')):
        text = open(path, encoding='utf-8').read()
        for s in re.findall(r'\b(?:Text|Content|ToolTip\.Tip|Title)="([^"{]+)"', text):
            s = s.replace('&amp;', '&')
            if re.search('[A-Za-z]{2}', s) and not s.startswith(('1', '3', '5', '7', 'Gears of War 3: Jacinto')) and s not in ('native', 'span', 'GEARS OF WAR 3', 'JACINTO', 'OK', 'FAQ'):
                lines.setdefault(s, os.path.basename(path))
    for block in open(os.path.join(ROOT, 'assets/faq.txt'), encoding='utf-8').read().replace('\r\n', '\n').split('\n\n'):
        for s in block.strip().split('\n', 1):
            if s: lines.setdefault(s, 'faq')
    for line in open(os.path.join(ROOT, 'assets/credits.txt'), encoding='utf-8').read().splitlines():
        if not line.strip(): continue
        parts = line.split(' - ', 1)
        s = parts[1] if len(parts) == 2 and not line.startswith('- ') else line
        lines.setdefault(s, 'credits')
    engine = open(os.path.join(ROOT, 'JacintoLauncher/SettingsEngine.cs'), encoding='utf-8').read()
    for s in re.findall(r'\("GBA_\w+", "([^"]+)", "\w+"\)', engine):
        lines.setdefault(s, 'controls')
    bridge = open(os.path.join(ROOT, 'JacintoLauncher/MultipatchBridge.cs'), encoding='utf-8').read()
    for s in re.findall(r'new ModeStatus\("([^"]+)", "([^"]+)"\)', bridge):
        for x in s: lines.setdefault(x, 'status')
    return lines


def table():
    rows = list(csv.reader(io.open(os.path.join(ROOT, 'languages.csv'), encoding='utf-8-sig', newline='')))
    start = 1 if rows[0][0] == 'key' else 0
    codes = rows[0][start:]
    data = {}
    for row in rows[2:]:
        if len(row) > start and row[start]:
            data[row[start].replace('\\n', '\n')] = dict(zip(codes, [c for c in row[start:]]))
    return codes, data


def new_language(code, name):
    rows = list(csv.reader(io.open(os.path.join(ROOT, 'languages.csv'), encoding='utf-8-sig', newline='')))
    path = os.path.join(ROOT, f'languages-{code}.csv')
    with open(path, 'w', encoding='utf-8', newline='') as out:
        csv.writer(out).writerows([['key', 'en', code], ['language', 'English', name]] + [[r[0], r[1], ''] for r in rows[2:] if r])
    print(f'{path}: fill the {code} column, then put the file beside the launcher to try it.')


if __name__ == '__main__':
    if '--new' in sys.argv:
        at = sys.argv.index('--new')
        new_language(sys.argv[at + 1], sys.argv[at + 2])
        sys.exit(0)
    codes, data = table()
    missing = {}
    for line, source in wanted().items():
        if TECHNICAL.match(line): continue
        row = data.get(line)
        gaps = [c for c in codes[1:] if not row or not row.get(c)]
        if gaps: missing[line] = {'source': source, 'missing': gaps}
    report = json.dumps(missing, ensure_ascii=False, indent=0)
    if '--out' in sys.argv:
        open(sys.argv[sys.argv.index('--out') + 1], 'w', encoding='utf-8').write(report)
    print(f'{len(wanted())} lines, {len(missing)} not fully translated')
    sys.exit(1 if missing else 0)
