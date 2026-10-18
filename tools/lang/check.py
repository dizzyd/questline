#!/usr/bin/env python3
"""Checks Questline's translations against en.json.

    python3 tools/lang/check.py            # every language
    python3 tools/lang/check.py de fr      # just these

A translation must have every key en.json has and no others, the same {0}-style
arguments, the same handbook links (the href kept, the words translated) and no
markup English doesn't have. Long lines left identical to the English are warned
about, not failed: a name can rightly stay as it is.
"""
import collections, json, pathlib, re, sys

LANG = pathlib.Path(__file__).resolve().parents[2] / "questline/assets/questline/lang"
ARG = re.compile(r"\{p?\d+[:}]")
HREF = re.compile(r'<a\s+href="([^"]*)"\s*>')
TAG = re.compile(r"</?([a-zA-Z]+)")


def problems(code, english):
    path = LANG / f"{code}.json"
    try:
        text = json.loads(path.read_text(encoding="utf-8"))
    except Exception as e:
        return [f"can't read {path.name}: {e}"], []
    errors, warnings = [], []
    if not isinstance(text, dict):
        return [f"{path.name} is not an object"], []
    for key in english.keys() - text.keys():
        errors.append(f"{key}: missing")
    for key in text.keys() - english.keys():
        errors.append(f"{key}: not in en.json")
    for key, en in english.items():
        tr = text.get(key)
        if tr is None:
            continue
        if not isinstance(tr, str) or not tr.strip():
            errors.append(f"{key}: empty")
            continue
        if collections.Counter(ARG.findall(en)) != collections.Counter(ARG.findall(tr)):
            errors.append(f"{key}: arguments {ARG.findall(en)} became {ARG.findall(tr)}")
        if collections.Counter(HREF.findall(en)) != collections.Counter(HREF.findall(tr)):
            errors.append(f"{key}: links {HREF.findall(en)} became {HREF.findall(tr)}")
        if collections.Counter(TAG.findall(en)) != collections.Counter(TAG.findall(tr)):
            errors.append(f"{key}: markup {TAG.findall(en)} became {TAG.findall(tr)}")
        if en.count("\n") != tr.count("\n"):
            warnings.append(f"{key}: {en.count(chr(10))} line breaks became {tr.count(chr(10))}")
        if tr == en and len(en.split()) > 3:
            warnings.append(f"{key}: still English")
    return errors, warnings


def main():
    english = json.loads((LANG / "en.json").read_text(encoding="utf-8"))
    codes = sys.argv[1:] or sorted(p.stem for p in LANG.glob("*.json") if p.stem != "en")
    failed = False
    for code in codes:
        errors, warnings = problems(code, english)
        failed |= bool(errors)
        print(f"{code}: {'FAIL' if errors else 'ok'} - {len(errors)} errors, {len(warnings)} warnings")
        for line in errors[:40]:
            print("  error   " + line)
        for line in warnings[:15]:
            print("  warning " + line)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
