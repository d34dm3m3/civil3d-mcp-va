"""Live test of the water/wastewater workflow against the running plugin (JSON-RPC 127.0.0.1:8080, bypassing Node).

Creates a new drawing from a Civil 3D template in a test folder inside the export roots and runs the chain
surface -> alignment -> profile -> profile view -> pressure pipe network -> network in profile view -> band set ->
layout + viewport -> plot -> save -> close -> reopen (read-only) -> close.
Every step is read back and checked. Never touches project drawings: the test drawing has its own name
and every write carries expectDrawing (C150).

Run after every plugin build (plugin_reload):
    python -I tests/live/va_smoke.py --folder DIR --template DWT [--keep]

Arguments (or environment variables):
    --folder DIR     test folder inside the plugin's export roots (CIVIL3D_MCP_TEST_FOLDER); created if missing
    --template DWT   Civil 3D template for the new drawing (CIVIL3D_MCP_TEST_TEMPLATE)
    --keep           leave the test drawing open (read-only) after the run

The template must contain the parts list, pipe family, band set and structure named in the constants below,
the pressure parts list "Standard" and an A3 layout. The xref step needs livetest_a.dwg in the test folder
(any drawing with at least one layer other than 0).

Report: tests/live/senaste.json (git-ignored). Exit 0 = green.
"""
import argparse
import json
import math
import os
import socket
import sys
import time
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent

# Test geometry in SWEREF 99 13 30, local sloping ground surface 120 x 40 m.
X0, Y0 = 145000.0, 6234000.0
ALIGNMENT = "LT_Alignment"
SURFACE = "LT_Ground"
PROFILE = "LT_Ground_profile"
PV = "LT_PV"
NETWORK = "LT_TS"
PARTS_LIST = "Spillvatten tryck"                  # parts list in the template
FAMILY = "Uponor PE100 tryckrörssystem PN10"      # pipe family in the parts catalog
INNER = 352.6          # pressure sewer DN400 PN10, inner diameter
BAND_SET = "VA tryck"                             # band set in the template
COVER = 1.5


def ground(x, y):
    return 40.0 + 0.02 * (x - X0) + 0.01 * (y - Y0)


class Failure(Exception):
    pass


class Run:
    def __init__(self, dwg: Path):
        self.dwg = dwg
        self.steps = []
        self.t0 = time.time()

    def rpc(self, method, params=None, timeout=600, write=True):
        p = dict(params or {})
        if write:
            p.setdefault("expectDrawing", str(self.dwg))
        s = socket.create_connection(("127.0.0.1", 8080), timeout=timeout)
        s.sendall(json.dumps({"jsonrpc": "2.0", "id": str(uuid.uuid4()), "method": method, "params": p}).encode("utf-8"))
        buf, reply = b"", None
        try:
            while reply is None:
                chunk = s.recv(1 << 20)
                if not chunk:
                    break
                buf += chunk
                try:
                    reply = json.loads(buf.decode("utf-8"))
                except ValueError:
                    continue
        finally:
            s.close()
        if reply is None:
            raise Failure(f"{method}: empty or incomplete reply")
        if "error" in reply:
            raise Failure(f"{method}: {reply['error'].get('data', {}).get('code', '')} {reply['error'].get('message')}")
        return reply["result"]

    def step(self, name, fn):
        t = time.time()
        try:
            info = fn() or ""
            self.steps.append({"step": name, "ok": True, "s": round(time.time() - t, 2), "info": info})
            print(f"OK  {name:28} {time.time() - t:6.1f} s  {info}")
            return True
        except Exception as e:  # noqa: BLE001 (one failure stops the chain but is reported)
            self.steps.append({"step": name, "ok": False, "s": round(time.time() - t, 2), "error": str(e)[:800]})
            print(f"ERR {name:28} {time.time() - t:6.1f} s  {str(e)[:400]}")
            return False


def check(condition, text):
    if not condition:
        raise Failure(text)


def open_documents(p: Run):
    return [d["name"] for d in p.rpc("documentsList", write=False)["documents"]]


def close(p: Run, name: str, discard=False):
    p.rpc("documentClose", {"name": name, "discard": discard}, write=False)
    for _ in range(40):
        time.sleep(0.5)
        if not any(name.lower() in d.lower() for d in open_documents(p)):
            return
    raise Failure(f"{name} was not closed")


def run(folder: Path, template: Path, keep: bool) -> int:
    dwg = folder / "livetest_va.dwg"
    pdf = folder / "livetest_va.pdf"
    p = Run(dwg)
    vertices = []

    def prepare():
        for d in open_documents(p):
            if dwg.name.lower() in d.lower():
                close(p, dwg.name, discard=True)
        p.rpc("newDrawing", {"templatePath": str(template)}, write=False)
        name = p.rpc("getDrawingInfo", write=False)["fileName"]
        check(name.lower().startswith("drawing"), f"new drawing is named {name}")
        p.rpc("saveDrawing", {"saveAs": str(dwg), "overwrite": True, "expectDrawing": name}, write=False)
        info = p.rpc("getDrawingInfo")
        check((info.get("filePath") or "").lower() == str(dwg).lower(), f"saved as {info.get('filePath')}")
        return f"{template.name} -> {dwg.name}"

    def surface():
        p.rpc("createSurface", {"name": SURFACE})
        pts = [{"x": X0 + i * 10.0, "y": Y0 + j * 10.0, "z": round(ground(X0 + i * 10.0, Y0 + j * 10.0), 3)}
               for i in range(-1, 14) for j in range(-2, 3)]
        p.rpc("addSurfacePoints", {"name": SURFACE, "points": pts})
        s = p.rpc("getSurface", {"name": SURFACE}, write=False)
        txt = json.dumps(s)
        check(str(len(pts)) in txt or '"pointCount"' in txt, f"point count missing in {txt[:200]}")
        # C157 (C13): a point outside the surface gives null, not an error for the whole call
        r = p.rpc("getSurfaceElevationsAlong", {"name": SURFACE, "points": [{"x": X0 + 5, "y": Y0}, {"x": X0 - 500, "y": Y0}]}, write=False)
        z = [x["elevation"] for x in r["samples"]]
        check(z[0] is not None and z[1] is None and r.get("outside") == 1, f"outside the surface: {r}")
        return f"{len(pts)} points, point outside = null"

    def alignment():
        pts = [{"x": X0, "y": Y0}, {"x": X0 + 60.0, "y": Y0 + 5.0}, {"x": X0 + 120.0, "y": Y0}]
        p.rpc("createAlignment", {"name": ALIGNMENT, "points": pts, "addCurvesBetweenTangents": False})
        a = p.rpc("getAlignment", {"name": ALIGNMENT}, write=False)
        length = a.get("length") or a.get("endStation", 0) - a.get("startStation", 0)
        expected = math.dist((0, 0), (60, 5)) + math.dist((60, 5), (120, 0))
        check(abs(length - expected) < 0.01, f"length {length}, expected {expected:.3f}")
        parts = [e["length"] for e in a.get("entities", [])]   # C157 (C8): entity lengths
        check(parts and all(d > 0 for d in parts) and abs(sum(parts) - expected) < 0.01, f"entity lengths {parts}")
        # C158: many stations in one call, own offset per station (negative = left, here north)
        st = [{"station": i * 0.5, "offset": (-1.0) ** i} for i in range(int(length / 0.5))]
        r = p.rpc("alignmentSampleStations", {"name": ALIGNMENT, "stations": st}, write=False)["samples"]
        check(len(r) == len(st) and r[1]["offset"] == -1.0 and abs(r[1]["y"] - (Y0 + 0.5 * 5 / math.hypot(60, 5) + 1.0 * 60 / math.hypot(60, 5))) < 0.01,
              f"station list: {len(r)} of {len(st)}, {r[1] if len(r) > 1 else r}")
        return f"{length:.3f} m, {len(parts)} entities, {len(r)} stations in one call"

    def profile():
        p.rpc("createProfileFromSurface", {"alignmentName": ALIGNMENT, "surfaceName": SURFACE, "profileName": PROFILE})
        r = p.rpc("getProfileElevation", {"alignmentName": ALIGNMENT, "profileName": PROFILE, "station": 60.0}, write=False)
        a = p.rpc("alignmentStationToPoint", {"name": ALIGNMENT, "station": 60.0, "offset": 0.0}, write=False)
        expected = ground(a["x"], a["y"])
        check(abs(r["elevation"] - expected) < 0.05, f"elevation at 0+060 {r['elevation']}, expected {expected:.3f}")
        return f"z(0+060) = {r['elevation']:.3f} (expected {expected:.3f})"

    def layout_profile():
        p.rpc("createLayoutProfile", {"alignmentName": ALIGNMENT, "profileName": "LT_FG"})
        length = p.rpc("getAlignment", {"name": ALIGNMENT}, write=False)["length"]
        for st, z in [(0.0, 38.0), (60.0, 38.6), (length, 37.9)]:
            p.rpc("profileAddPvi", {"alignmentName": ALIGNMENT, "profileName": "LT_FG", "station": st, "elevation": z})
        r = p.rpc("getProfile", {"alignmentName": ALIGNMENT, "profileName": "LT_FG"}, write=False)
        check(len(r.get("pvis", [])) == 3 == r.get("pviCount"), f"PVI: {r.get('pviCount')} / {r.get('pvis')}")   # C157 (C14)
        return f"{len(r['pvis'])} PVIs listed"

    def parts_list_dry_run():
        # The family is added from the catalog into its own parts list: it only gets the catalog's first size (C79), so a
        # dry run with all has sizes to report and must not add them (C156).
        p.rpc("addPartsListFamily", {"partsList": "LT_PartsList", "familyName": FAMILY, "domain": "pipe", "createPartsList": True})
        args = {"partsList": "LT_PartsList", "familyName": FAMILY, "domain": "pipe", "all": True, "dryRun": True}
        a = p.rpc("addPartsListSizes", args)
        b = p.rpc("addPartsListSizes", args)
        check(a.get("dryRun") is True and len(a.get("wouldAdd", [])) > 0, f"nothing to dry-run: {json.dumps(a)[:200]}")
        check(a.get("wouldAdd") == b.get("wouldAdd") and len(a["sizes"]) == len(b["sizes"]),
              f"the dry run changed the parts list: {len(a['sizes'])} -> {len(b['sizes'])} sizes")
        return f"{len(a['wouldAdd'])} sizes would be added, parts list unchanged ({len(a['sizes'])})"

    def profile_view():
        p.rpc("profileViewCreate", {"alignmentName": ALIGNMENT, "profileViewName": PV, "insertX": X0, "insertY": Y0 - 200.0})
        pv = p.rpc("listProfileViews", write=False)
        names = [v.get("name") for v in pv.get("profileViews", pv if isinstance(pv, list) else [])]
        check(PV in names, f"{PV} missing among {names}")
        return f"{len(names)} profile view(s)"

    def network():
        nonlocal vertices
        geo = [(X0 + 1.0, Y0 + 0.08), (X0 + 60.0, Y0 + 5.0), (X0 + 119.0, Y0 + 0.08)]
        vertices = [{"x": x, "y": y, "z": round(ground(x, y) - COVER - 0.4, 3)} for x, y in geo]
        p.rpc("buildPipeNetworkRun", {"networkName": NETWORK, "partsList": PARTS_LIST, "pipeFamily": FAMILY, "pipeDiameter": INNER,
                                      "referenceSurface": SURFACE, "referenceAlignment": ALIGNMENT, "defaultStructure": "Brytpunkt",
                                      "namePrefix": "LT", "replace": True, "vertices": vertices, "alignStructuresToPipes": True})
        n = p.rpc("getPipeNetwork", {"name": NETWORK}, write=False)
        pipes = n.get("pipes", [])
        check(len(pipes) == len(vertices) - 1, f"{len(pipes)} pipes, expected {len(vertices) - 1}")
        return f"{len(pipes)} pipes, {len(n.get('structures', []))} structures"

    def pressure_network():
        # C164: Civil 3D's pressure network (parts list Standard in the template: ductile iron AWWA, no PE yet)
        pipe_part = "pipe-200 mm-push on-ductile iron-25 bar-AWWA C151"
        bend_part = "elbow-200 mm-11.25 degree-push on-ductile iron-25 bar-AWWA C110/C111/C153"
        p.rpc("createPressureNetwork", {"name": "LT_Pressure", "partsList": "Standard", "referenceSurface": SURFACE, "referenceAlignment": ALIGNMENT})
        a, b, c = (X0 + 5, Y0 - 5, 38.0), (X0 + 60, Y0 - 1, 38.5), (X0 + 110, Y0 - 6, 38.2)
        for s_, e_ in [(a, b), (b, c)]:
            p.rpc("addPressurePipe", {"networkName": "LT_Pressure", "partName": pipe_part, "startPoint": dict(zip("xyz", s_)), "endPoint": dict(zip("xyz", e_))})
        p.rpc("addPressureFitting", {"networkName": "LT_Pressure", "partName": bend_part, "position": dict(zip("xyz", b))})
        n = next(x for x in p.rpc("listPressureNetworks", write=False)["networks"] if x["name"] == "LT_Pressure")
        check(n["pipeCount"] == 2 and n["fittingCount"] == 1, f"counted {n['pipeCount']} pipes, {n['fittingCount']} bends")
        info = p.rpc("getPressureNetworkInfo", {"name": "LT_Pressure"}, write=False)
        length = sum(x["length"] or 0 for x in info["pipes"])
        expected = math.dist(a, b) + math.dist(b, c)
        check(abs(length - expected) < 0.5, f"pipe length {length:.2f}, expected about {expected:.2f}")
        q = p.rpc("qtyPressureNetworkLengths", {"name": "LT_Pressure"}, write=False)
        check("error" not in q, f"quantities: {q}")
        return f"2 pipes ({length:.1f} m), 1 bend, quantities read"

    def network_in_pv():
        p.rpc("addNetworkToProfileView", {"networkName": NETWORK, "profileViewName": PV})
        d = p.rpc("getProfileViewParts", {"profileViewName": PV, "network": NETWORK}, write=False)
        txt = json.dumps(d)
        check("LT" in txt, f"no network parts in the profile view: {txt[:200]}")
        return "parts drawn"

    def bands():
        band_sets = json.dumps(p.rpc("listBandSets", write=False))
        check(BAND_SET in band_sets, f"band set {BAND_SET} missing")
        r = p.rpc("profileViewApplyBandSet", {"profileViewName": PV, "bandSet": BAND_SET, "profile1": PROFILE, "network": NETWORK})
        b = p.rpc("getProfileViewBands", {"profileViewName": PV}, write=False)
        count = sum(len(v) for v in b.values() if isinstance(v, list))
        check(count > 0, f"no bands: {json.dumps(b)[:200]}")
        return f"{count} bands, {len(r.get('warnings', []))} warnings"

    def bands_follow_network():
        # C160: pipe network bands are recomputed after an API change to the network (C125: they stayed until the band settings were written)
        def positions():
            g = p.rpc("listProfileViewBandLabels", {"profileViewName": PV}, write=False)["groups"]
            return [[round(l["x"], 2) for l in x["labels"]] for x in g if x["kind"] == "pipeNetwork"]
        before = positions()
        pipe = p.rpc("getPipeNetwork", {"name": NETWORK}, write=False)["pipes"][0]["name"]
        r = p.rpc("setPipeEndElevations", {"networkName": NETWORK, "pipes": [{"name": pipe, "endX": X0 + 65.0, "endY": Y0 + 4.6}]})
        after = positions()
        check(r.get("bandsRefreshed") and after != before, f"the bands did not follow the network: {r.get('bandsRefreshed')}")
        return f"{len(r['bandsRefreshed'])} profile view recomputed, labels moved"

    def band_weeding():
        # C204: weeding of band labels; a large minimum spacing hides all but one per row, a rerun changes nothing
        t = p.rpc("weedProfileViewBandLabels", {"profileViewName": PV, "minSpacing": 1000, "dryRun": True}, write=False)
        check(t["labels"] > 0 and t["hidden"] == t["labels"] - t["rows"], f"dry run: {t}")
        r1 = p.rpc("weedProfileViewBandLabels", {"profileViewName": PV, "minSpacing": 3.0})
        r2 = p.rpc("weedProfileViewBandLabels", {"profileViewName": PV, "minSpacing": 3.0})
        check(r2["hidden"] == 0, f"rerun hid {r2['hidden']}")
        p.rpc("setProfileViewBandLabels", {"profileViewName": PV, "resetAll": True})
        return f"{t['labels']} labels in {t['rows']} rows, {r1['hidden']} hidden at 3 m, rerun 0"

    def linetype_replace():
        # C159: replacing an already loaded linetype (crashed Civil 3D in the C88b implementation)
        p.rpc("linetypesLoad", {"path": "acadiso.lin", "names": ["HIDDEN", "GAS_LINE"]})
        before = {x["name"]: x for x in p.rpc("listLinetypes", write=False).get("linetypes", [])}.get("HIDDEN")
        r = p.rpc("linetypesLoad", {"path": "acadiso.lin", "names": ["HIDDEN", "GAS_LINE"], "replace": True})
        after = {x["name"]: x for x in p.rpc("listLinetypes", write=False).get("linetypes", [])}.get("HIDDEN")
        check(r.get("replaced") == ["GAS_LINE", "HIDDEN"] and before and after, f"replacement: {r}, before {before}, after {after}")
        check(before.get("handle") in (None, after.get("handle")), f"the handle changed: {before.get('handle')} -> {after.get('handle')}")
        names = [x["name"] for x in p.rpc("listLinetypes", write=False).get("linetypes", [])]
        check(not any(n.startswith("C88-") for n in names), "temporary name left")
        return "HIDDEN and GAS_LINE (with text) replaced, no temporary names left"

    def xref_overrides():
        # C161: the host drawing's linetype on an xref layer survives xrefReload (it was lost on every reload)
        source = folder / "livetest_a.dwg"
        check(source.exists(), f"{source.name} missing in the test folder (any drawing with at least one layer other than 0)")
        p.rpc("xrefAttach", {"path": str(source), "name": "LT_XREF", "x": X0, "y": Y0, "overlay": True})
        layers = [x["name"] for x in p.rpc("listLayers", write=False).get("layers", []) if x["name"].upper().startswith("LT_XREF|")]
        check(layers, "the xref gave no layers")
        p.rpc("setLayers", {"layers": [layers[0]], "linetype": "HIDDEN"})
        r = p.rpc("xrefReload", {"name": "LT_XREF"})
        lt = next(x for x in p.rpc("listLayers", write=False)["layers"] if x["name"] == layers[0]).get("linetype")
        check(str(lt).upper() == "HIDDEN", f"{layers[0]} has {lt} after reload (restored: {r.get('layerOverridesRestored')})")
        return f"{layers[0]}: HIDDEN kept, restored by the plugin: {len(r.get('layerOverridesRestored', []))}"

    def layout():
        lay = p.rpc("listLayouts", write=False)
        names = [x["name"] for x in lay.get("layouts", [])]
        source = next((n for n in names if n.startswith("A3")), None)
        check(source, f"no A3 layout in the template: {names}")
        p.rpc("createLayout", {"name": "LT_A3", "copyFrom": source})
        v = p.rpc("createViewport", {"layout": "LT_A3", "centerX": 180.0, "centerY": 150.0, "width": 300.0, "height": 200.0,
                                     "viewCenterX": X0 + 60.0, "viewCenterY": Y0, "scaleDenominator": 1000})
        handle = v.get("handle")
        check(handle, f"viewport without handle: {v}")
        p.rpc("setViewport", {"layout": "LT_A3", "handle": handle, "viewCenterX": X0 + 60.0, "viewCenterY": Y0,
                              "twistDegrees": 0.0, "scaleDenominator": 500, "locked": True})
        read_back = next(x for x in p.rpc("listLayouts", {"layout": "LT_A3"}, write=False)["layouts"][0]["viewports"] if x["handle"] == handle)
        check(abs(read_back.get("scaleDenominator", 500) - 500) < 0.5, f"scale {read_back.get('scaleDenominator')}")
        return f"LT_A3 from {source}, viewport {handle}"

    def plot():
        r = p.rpc("plotLayout", {"layout": "LT_A3", "output": str(pdf), "overwrite": True})
        check(r.get("bytes", 0) > 1000, f"PDF {r.get('bytes')} bytes")
        check(r.get("regen") == "all", f"regen {r.get('regen')}")
        return f"{r['bytes']} bytes, {r.get('media')}"

    def save_and_reopen():
        p.rpc("saveDrawing", {})
        close(p, dwg.name)
        p.rpc("documentOpen", {"path": str(dwg), "readOnly": True}, write=False)
        info = p.rpc("getDrawingInfo")
        c = info.get("objectCounts", {})
        check(c.get("surfaces", 0) >= 1 and c.get("alignments", 0) >= 1 and c.get("pipeNetworks", 0) >= 1, f"after reopening: {c}")
        n = p.rpc("getPipeNetwork", {"name": NETWORK}, write=False)
        check(len(n.get("pipes", [])) == len(vertices) - 1, "the pipes were not kept by the save")
        if not keep:
            close(p, dwg.name)
        return f"objects {c}"

    STEPS = [("prepare drawing", prepare), ("surface", surface), ("alignment", alignment), ("profile from surface", profile),
             ("layout profile", layout_profile), ("parts list dry run", parts_list_dry_run),
             ("profile view", profile_view), ("pressure pipe network", network), ("pressure network (Civil)", pressure_network), ("network in profile view", network_in_pv),
             ("band set", bands), ("bands follow network", bands_follow_network), ("band label weeding", band_weeding), ("linetype replaced", linetype_replace), ("xref layers after reload", xref_overrides), ("layout + viewport", layout), ("plot PDF", plot),
             ("save, close, reopen", save_and_reopen)]
    for name, fn in STEPS:
        if not p.step(name, fn):
            break

    ok = all(s["ok"] for s in p.steps) and len(p.steps) == len(STEPS)
    if not ok and not keep:
        try:
            if any(dwg.name.lower() in d.lower() for d in open_documents(p)):
                close(p, dwg.name, discard=True)
        except Exception:  # noqa: BLE001
            pass
    report = {"time": time.strftime("%Y-%m-%d %H:%M:%S"), "ok": ok, "seconds": round(time.time() - p.t0, 1), "steps": p.steps}
    (HERE / "senaste.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n{'GREEN' if ok else 'RED'}: {sum(s['ok'] for s in p.steps)}/{len(STEPS)} steps, {report['seconds']} s")
    return 0 if ok else 1


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="Live smoke test of the water/wastewater workflow against the running plugin.")
    ap.add_argument("--folder", type=Path, default=os.environ.get("CIVIL3D_MCP_TEST_FOLDER"),
                    help="test folder inside the plugin's export roots (env CIVIL3D_MCP_TEST_FOLDER)")
    ap.add_argument("--template", type=Path, default=os.environ.get("CIVIL3D_MCP_TEST_TEMPLATE"),
                    help="Civil 3D template (.dwt) for the new drawing (env CIVIL3D_MCP_TEST_TEMPLATE)")
    ap.add_argument("--keep", action="store_true", help="leave the test drawing open (read-only) after the run")
    a = ap.parse_args()
    if not a.folder:
        ap.error("no test folder: pass --folder DIR or set CIVIL3D_MCP_TEST_FOLDER")
    if not a.template:
        ap.error("no template: pass --template DWT or set CIVIL3D_MCP_TEST_TEMPLATE")
    a.folder = Path(a.folder)
    a.template = Path(a.template)
    a.folder.mkdir(parents=True, exist_ok=True)
    sys.exit(run(a.folder, a.template, a.keep))
