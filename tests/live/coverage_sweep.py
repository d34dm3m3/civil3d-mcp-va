"""Coverage sweep: runs the plugin commands against a copy of the live test's test drawing and classifies every command.

    python -I tests/live/coverage_sweep.py --folder DIR [--read-only] [--report FILE.md]

Arguments (or environment variables):
    --folder DIR      the live test's test folder (CIVIL3D_MCP_TEST_FOLDER), same as for va_smoke.py
    --read-only       run only the read commands
    --report FILE.md  also write a markdown summary

Prerequisite: va_smoke.py has been run with the same folder (DIR/livetest_va.dwg exists). The sweep copies it to
sweep_fixture.dwg, opens the copy and discards it at the end (it is never saved). Every call carries expectDrawing, so
nothing can write to another drawing. Parameters are taken from the handlers' source code (GetRequired*/GetOptional*)
and filled from a table with the test drawing's object names; a required field without a known value gives "not_tried"
without a call.

Classes: ok, ok_silent (a creating command replied ok but the drawing did not change), input (INVALID_INPUT or
OBJECT_NOT_FOUND after filling in: the sweep could not supply correct input, the command is not tested), error (API_ERROR,
INTERNAL_ERROR, UNSUPPORTED etc.), timeout, blocked (touches files outside the test folder, global settings,
document handling or waits for the user), not_tried (required field without a value).
Result: tests/live/tackning.json (git-ignored) and an optional markdown report.
"""
import argparse
import json
import os
import re
import shutil
import socket
import sys
import time
import uuid
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PLUGIN = ROOT / "Civil3D-MCP-Plugin"
# Set in main() from --folder / CIVIL3D_MCP_TEST_FOLDER.
FOLDER = SOURCE = FIXTURE = OUT_DIR = None
X0, Y0 = 145000.0, 6234000.0

BLOCKED = {
    # documents, saving, reloading, undo
    "reloadPlugin", "newDrawing", "documentOpen", "documentClose", "documentActivate", "saveDrawing", "saveTest",
    "undoDrawing", "redoDrawing", "dxfToDwg", "drawingTransform", "batch",
    # interactive or external programs
    "startCommand", "openStormSanitaryAnalysis", "importStm", "exportStm",
    # global settings and files outside the test folder
    "setSystemVariable", "setDataShortcutProject", "createDataShortcut", "createDataShortcutReference", "publishDataShortcuts",
    "promoteDataShortcut", "syncDataShortcuts", "referenceDataShortcut", "createSurveyDatabase", "adjustSurveyNetwork",
    "importSurveyLandXml", "createSheetSet", "addSheet", "publishSheetPdf", "exportSheetSet", "setSheetTitleBlock",
    "createSheetView", "setSheetViewScale",
}
BLOCKED_DOMAINS = {"Workflow"}

NAME_PER_DOMAIN = {
    "Surface": "LT_Ground", "SurfaceEdit": "LT_Ground", "Alignment": "LT_Alignment", "AlignmentEdit": "LT_Alignment",
    "PipeNetwork": "LT_TS", "NetworkPresentation": "LT_TS", "NetworkAnalysis": "LT_TS", "PipeHydraulics": "LT_TS",
    "Layout": "LT_A3", "ExternalReference": "LT_XREF", "PressureNetwork": "LT_Pressure", "Profile": "LT_FG",
    "ProfileEdit": "LT_FG", "PointGroup": "LT_Group", "Corridor": "LT_Corridor", "CorridorEditing": "LT_Corridor",
    "Grading": "LT_Grading", "Catchment": "LT_Catchment", "Section": "LT_Sections", "Viewport": "LT_A3",
    "Block": "LT_Block", "BlockDefinition": "LT_Block", "TextStyle": "Standard", "AssemblyCreation": "LT_Assembly",
    "Intersection": "LT_Intersection", "ParcelEditing": "LT_Parcel", "Cogo": "LT_Point",
}


NAME_PER_WORD = [("pressure", "LT_Pressure"), ("pipenetwork", "LT_TS"), ("network", "LT_TS"), ("pipe", "LT_TS"), ("corridor", "LT_Corridor"),
                 ("alignment", "LT_Alignment"), ("profileview", "LT_PV"), ("profile", "LT_FG"), ("surface", "LT_Ground"),
                 ("parcel", "LT_Parcel"), ("grading", "LT_Grading"), ("catchment", "LT_Catchment"), ("layout", "LT_A3"),
                 ("xref", "LT_XREF"), ("assembl", "LT_Assembly"), ("pointgroup", "LT_Group")]

# Commands that need a specific form of a parameter
PRESSURE_PIPE = "pipe-200 mm-push on-ductile iron-25 bar-AWWA C151"
PRESSURE_BEND = "elbow-200 mm-11.25 degree-push on-ductile iron-25 bar-AWWA C110/C111/C153"
PRESSURE = {"networkName": "LT_Pressure"}
SPECIAL = {
    "addPressurePipe": {**PRESSURE, "partName": PRESSURE_PIPE, "startPoint": {"x": X0 + 110, "y": Y0 - 6, "z": 38.2}, "endPoint": {"x": X0 + 125, "y": Y0 - 8, "z": 38.0}},
    "addPressureFitting": {**PRESSURE, "partName": PRESSURE_BEND, "position": {"x": X0 + 110, "y": Y0 - 6, "z": 38.2}},
    "addPressureAppurtenance": {**PRESSURE, "partName": "valve", "position": {"x": X0 + 30, "y": Y0 - 3, "z": 38.2}},
    "getPressurePipeProperties": {**PRESSURE},
    "resizePressurePipe": {**PRESSURE, "newPartName": PRESSURE_PIPE},
    "getPressureFittingProperties": {**PRESSURE},
    "setPressureNetworkCover": {**PRESSURE, "minCoverDepth": 1.5},
    "validatePressureNetwork": {**PRESSURE},
    "exportPressureNetwork": {**PRESSURE},
    "assignPressurePartsList": {**PRESSURE, "partsList": "Standard"},
    "connectPressureNetworks": {"targetNetwork": "LT_Pressure", "sourceNetwork": "LT_Pressure2"},
    "createPressureNetwork": {"name": "LT_Pressure3", "partsList": "Standard"},
    "profileViewStationToXY": {"points": [{"station": 10.0, "elevation": 40.0}]},
    "networkClearance": {"networks": ["LT_TS"]},
    "networkQuantities": {"networks": ["LT_TS"]},
}


def name_for(method, domain):
    m = method.lower()
    for word, name in NAME_PER_WORD:
        if word in m:
            return name
    return NAME_PER_DOMAIN.get(domain, "LT_Ground")


def known_values(fix):
    """Known values per parameter name. fix: data from the test drawing (handles, part names)."""
    return {
        "surfaceName": "LT_Ground", "baseSurface": "LT_Ground", "comparisonSurface": "LT_Ground2", "referenceSurface": "LT_Ground",
        "alignmentName": "LT_Alignment", "referenceAlignment": "LT_Alignment", "profileName": "LT_FG", "profileViewName": "LT_PV",
        "networkName": "LT_TS", "network": "LT_TS", "partsList": "Spillvatten tryck", "familyName": "Uponor PE100 tryckrörssystem PN10",
        "domain": "pipe", "layout": "LT_A3", "layoutName": "LT_A3", "layer": "0", "layers": ["0"], "x": X0 + 40.0, "y": Y0 + 3.0,
        "z": 40.0, "station": 30.0, "stations": [10.0, 20.0, 30.0], "offset": 0.0, "elevation": 40.0, "startStation": 0.0,
        "endStation": 100.0, "stationStart": 0.0, "stationEnd": 100.0, "interval": 10.0, "handle": fix.get("handle"),
        "handles": [fix["handle"]] if fix.get("handle") else None, "text": "LT", "textHeight": 2.5, "radius": 2.0,
        "width": 20.0, "height": 15.0, "centerX": 200.0, "centerY": 150.0, "pipeName": fix.get("pipe"), "partName": fix.get("pipe"),
        "structureName": fix.get("structure"), "names": [fix["pipe"]] if fix.get("pipe") else None,
        "points": [{"x": X0 + 10, "y": Y0 + 1, "z": 40.0}, {"x": X0 + 50, "y": Y0 + 4, "z": 40.5}, {"x": X0 + 90, "y": Y0 + 2, "z": 40.2}],
        "startX": X0 + 10, "startY": Y0 + 1, "endX": X0 + 90, "endY": Y0 + 2, "startZ": 39.0, "endZ": 38.5,
        "corridorName": "LT_Corridor", "assemblyName": "LT_Assembly", "groupName": "LT_Group", "siteName": "LT_Site",
        "sampleLineGroupName": "LT_Sections", "catchmentName": "LT_Catchment", "objectType": "Alignment", "collection": "alignments",
        "blockName": "Norrpil", "bandType": "PipeNetwork", "bandSet": "VA tryck", "designSpeed": 50.0, "runoffCoefficient": 0.5,
        "rainfallIntensity": 100.0, "tolerance": 0.01, "limit": 20, "scale": 1.0, "scaleDenominator": 500, "rotationDegrees": 0.0,
        "objectName": "LT_Alignment", "description": "coverage sweep", "contains": "LT", "pressureNetworkName": "LT_Pressure",
        "featureLineName": "LT_Alignment", "pointGroupName": "LT_Group", "parcelName": "LT_Parcel", "number": 1,
        "pointNumber": 1, "pointNumbers": [1], "radiusMeters": 5.0, "length": 10.0, "depth": 1.5, "diameter": 0.4,
        "slope": 0.01, "grade": 0.01, "startPoint": {"x": X0 + 10, "y": Y0 + 1}, "endPoint": {"x": X0 + 90, "y": Y0 + 2},
        "insertX": X0, "insertY": Y0 - 400.0, "viewCenterX": X0 + 60, "viewCenterY": Y0, "linetype": "HIDDEN", "color": 3,
        "path": "acadiso.lin", "media": None, "pageSetup": None, "query": "LT", "pattern": "LT*", "prefix": "LT",
        "outputPath": None, "output": None, "label": "LT", "value": "1", "style": "Standard", "newName": "LT_New",
        "intensityUnits": "mm/h", "drainageArea": 1.0, "areaUnits": "ha", "regionIndex": 0, "minorInterval": 1.0,
        "majorInterval": 5.0, "insertionX": X0, "insertionY": Y0 - 400.0, "outletX": X0 + 100, "outletY": Y0, "entityIndex": 0,
        "side": "left", "find": "LT", "copyFrom": "A3", "sourceSurfaceName": "LT_Ground", "textX": 100.0, "textY": 100.0,
    }


def output_file(method):
    m = method.lower()
    ext = ".csv" if "payitem" in m else ".xml" if "landxml" in m or "xml" in m else ".csv" if "csv" in m else ".dxf" if "dxf" in m else ".pdf" if "pdf" in m or "plot" in m else ".png" if "png" in m else ".txt"
    return str(OUT_DIR / f"{method}{ext}")


def parameters_from_source():
    """method -> (domain, kind, [(name, required, type)]) from CommandRegistry.cs and the handlers' method bodies."""
    reg = (PLUGIN / "CommandRegistry.cs").read_text(encoding="utf-8")
    files = {p.stem: p.read_text(encoding="utf-8-sig") for p in PLUGIN.glob("*.cs")}
    out = {}
    for m in re.finditer(r'R\("([^"]+)", "([^"]+)", (R_|W_), \(p, ct\) => (.+?)\);', reg):
        name, domain, kind, expression = m.groups()
        calls = re.findall(r"([A-Za-z0-9_]+Commands)\.([A-Za-z0-9_]+)\(", expression)
        fields = []
        for cls, method in calls[-1:]:
            s = files.get(cls, "")
            mm = re.search(r"(public|internal) static [^\n(]*\b" + re.escape(method) + r"\s*\(", s)
            if not mm:
                continue
            rest = s[mm.start():]
            nxt = re.search(r"\n  (public|internal|private) static ", rest[10:])
            body = rest[: nxt.start() + 10] if nxt else rest
            for g in re.finditer(r'Get(Required|Optional)([A-Za-z]*)\(parameters, "([a-zA-Z]+)"', body):
                fields.append((g.group(3), g.group(1) == "Required", g.group(2)))
            for g in re.finditer(r'GetParameter\(parameters, "([a-zA-Z]+)"\)|parameters\?\["([a-zA-Z]+)"\]', body):
                fields.append((g.group(1) or g.group(2), False, "Node"))
        seen, unique = set(), []
        for f in fields:
            if f[0] not in seen:
                seen.add(f[0])
                unique.append(f)
        out[name] = (domain, "read" if kind == "R_" else "write", unique)
    return out


class Rpc:
    def __init__(self, drawing):
        self.drawing = drawing

    def __call__(self, method, params=None, timeout=150, expect=True):
        p = dict(params or {})
        if expect:
            p["expectDrawing"] = self.drawing
        s = socket.create_connection(("127.0.0.1", 8080), timeout=timeout)
        try:
            s.sendall(json.dumps({"jsonrpc": "2.0", "id": str(uuid.uuid4()), "method": method, "params": p}).encode("utf-8"))
            buf = b""
            while True:
                chunk = s.recv(1 << 20)
                if not chunk:
                    break
                buf += chunk
                try:
                    return json.loads(buf.decode("utf-8"))
                except ValueError:
                    continue
        except socket.timeout:
            return {"error": {"message": f"timeout {timeout} s", "data": {"code": "SWEEP.TIMEOUT"}}}
        finally:
            s.close()
        return {"error": {"message": "empty reply", "data": {"code": "SWEEP.EMPTY"}}}


def fingerprint(rpc):
    info = rpc("getDrawingInfo").get("result", {})
    ent = rpc("listEntities", {"limit": 1}).get("result", {})
    lay = rpc("listLayouts").get("result", {})
    pressure = rpc("listPressureNetworks").get("result", {}).get("networks", [])
    groups = rpc("listPointGroups").get("result", {})
    return (json.dumps(info.get("objectCounts", {}), sort_keys=True), ent.get("matched"), len(lay.get("layouts", [])),
            json.dumps([(n["name"], n.get("pipeCount"), n.get("fittingCount")) for n in pressure]), json.dumps(groups, sort_keys=True)[:2000])


def classify(reply):
    if "result" in reply:
        return "ok", ""
    e = reply.get("error", {})
    code = e.get("data", {}).get("code", "") or str(e.get("code"))
    text = e.get("message", "")
    if code in ("CIVIL3D.INVALID_INPUT", "CIVIL3D.OBJECT_NOT_FOUND", "CIVIL3D.CONFLICT", "CIVIL3D.WRONG_DRAWING"):
        return "input", f"{code}: {text}"
    if code in ("SWEEP.TIMEOUT", "CIVIL3D.TIMEOUT"):
        return "timeout", text
    return "error", f"{code}: {text}"


def order(method, kind):
    if kind == "read":
        return 1
    if re.match(r"(delete|erase|remove|detach|purge|unload|clear)", method):
        return 3
    if re.match(r"(create|add|insert|build|attach|import|copy|load|new|make|generate|paste)", method):
        return 0
    return 2


def main():
    global FOLDER, SOURCE, FIXTURE, OUT_DIR
    ap = argparse.ArgumentParser(description="Coverage sweep of the plugin commands against a copy of the live test drawing.")
    ap.add_argument("--folder", type=Path, default=os.environ.get("CIVIL3D_MCP_TEST_FOLDER"),
                    help="the live test's test folder, same as for va_smoke.py (env CIVIL3D_MCP_TEST_FOLDER)")
    ap.add_argument("--read-only", action="store_true", help="run only the read commands")
    ap.add_argument("--report", type=Path, help="also write a markdown summary to this file")
    a = ap.parse_args()
    if not a.folder:
        ap.error("no test folder: pass --folder DIR or set CIVIL3D_MCP_TEST_FOLDER")
    FOLDER = Path(a.folder)
    SOURCE = FOLDER / "livetest_va.dwg"
    FIXTURE = FOLDER / "sweep_fixture.dwg"
    OUT_DIR = FOLDER / "sweep_out"
    if not SOURCE.exists():
        sys.exit(f"{SOURCE} missing: run va_smoke.py first")
    OUT_DIR.mkdir(exist_ok=True)
    shutil.copyfile(SOURCE, FIXTURE)
    rpc = Rpc(str(FIXTURE))
    r = rpc("documentOpen", {"path": str(FIXTURE)}, expect=False)
    if "error" in r:
        sys.exit(f"documentOpen: {r['error']}")
    time.sleep(2)

    # a second surface 0.5 m above the first, for the volume commands
    rpc("createSurface", {"name": "LT_Ground2"})
    rpc("addSurfacePoints", {"name": "LT_Ground2", "points": [{"x": X0 + i * 10.0, "y": Y0 + j * 10.0, "z": 40.5 + 0.02 * i * 10 + 0.01 * j * 10}
                                                              for i in range(-1, 14) for j in range(-2, 3)]})
    rpc("createPressureNetwork", {"name": "LT_Pressure2", "partsList": "Standard"})
    pinfo = rpc("getPressureNetworkInfo", {"name": "LT_Pressure"}).get("result", {})
    ent = rpc("listEntities", {"limit": 50}).get("result", {}).get("entities", [])
    net = rpc("getPipeNetwork", {"name": "LT_TS"}).get("result", {})
    fix = {"handle": next((e["handle"] for e in ent if "olyline" in e.get("type", "")), ent[0]["handle"] if ent else None),
           "pipe": (net.get("pipes") or [{}])[0].get("name"), "structure": (net.get("structures") or [{}])[0].get("name")}
    SPECIAL["getPressurePipeProperties"]["pipeName"] = SPECIAL["resizePressurePipe"]["pipeName"] = (pinfo.get("pipes") or [{}])[0].get("name")
    SPECIAL["getPressureFittingProperties"]["fittingName"] = (pinfo.get("fittings") or [{}])[0].get("name")
    known = known_values(fix)
    catalog = parameters_from_source()
    results = {}
    t0 = time.time()
    commands = sorted(catalog.items(), key=lambda kv: (order(kv[0], kv[1][1]), kv[0]))
    for method, (domain, kind, fields) in commands:
        if a.read_only and kind != "read":
            continue
        row = {"domain": domain, "kind": kind}
        if method in BLOCKED or domain in BLOCKED_DOMAINS and kind == "write":
            results[method] = {**row, "class": "blocked"}
            continue
        p, missing = {}, []
        for name, required, typ in fields:
            v = output_file(method) if name in ("output", "outputPath") else known.get(name)
            if name == "name":
                v = name_for(method, domain)
            if name in SPECIAL.get(method, {}):
                v = SPECIAL[method][name]
            if v is None:
                if required:
                    missing.append(name)
                continue
            if required or name in ("name", "alignmentName", "surfaceName", "networkName", "profileViewName", "profileName", "layout",
                                    "output", "outputPath", "overwrite"):
                p[name] = v
        if any(n == "overwrite" for n, _, _ in fields):
            p["overwrite"] = True
        for name, v in SPECIAL.get(method, {}).items():
            p.setdefault(name, v)
        if missing:
            results[method] = {**row, "class": "not_tried", "info": "missing value: " + ", ".join(missing)}
            continue
        creating = kind == "write" and order(method, kind) == 0
        before = fingerprint(rpc) if creating else None
        t = time.time()
        reply = rpc(method, p)
        k, info = classify(reply)
        if k == "ok" and creating and fingerprint(rpc) == before:
            k, info = "ok_silent", "replied ok but object counts, entities and layouts are unchanged"
        results[method] = {**row, "class": k, "info": info[:300], "s": round(time.time() - t, 2), "params": list(p)}
        print(f"{k:9} {method:42} {info[:110]}", flush=True)
        if k == "timeout":
            time.sleep(5)

    rpc("documentClose", {"name": FIXTURE.name, "discard": True}, expect=False)
    (ROOT / "tests" / "live" / "tackning.json").write_text(json.dumps(results, ensure_ascii=False, indent=1), encoding="utf-8")
    totals = Counter(v["class"] for v in results.values())
    print(f"\n{len(results)} commands in {time.time() - t0:.0f} s: {dict(totals)}")
    if a.report:
        write_report(a.report, results)
    return 0


def write_report(path: Path, res):
    per = defaultdict(Counter)
    for v in res.values():
        per[v["domain"]][v["class"]] += 1
    classes = ["ok", "ok_silent", "error", "timeout", "input", "not_tried", "blocked"]
    rows = ["| Domain | " + " | ".join(classes) + " |", "|---|" + "---|" * len(classes)]
    for d in sorted(per, key=lambda d: -sum(per[d].values())):
        rows.append(f"| {d} | " + " | ".join(str(per[d][k] or "") for k in classes) + " |")
    failures = [f"| {m} | {v['domain']} | {v['kind']} | {v['class']} | {v.get('info', '')[:160].replace('|', '/')} |"
                for m, v in sorted(res.items()) if v["class"] in ("error", "timeout", "ok_silent")]
    path.write_text("\n".join(rows) + "\n\n| Command | Domain | Kind | Class | Reply |\n|---|---|---|---|---|\n" + "\n".join(failures) + "\n",
                    encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
