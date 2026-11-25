using System.Text.Json.Nodes;
using Civil3DMcpPlugin;

// Standalone check of FlagPlacer (C241 placeFlags) against the original Python flag placement. fixture.json was
// written by a Python script running that placement on a synthetic scene (area, pipes, structures, soft lines,
// another owner's line and poles, existing texts; coordinates in a local frame) and holds per flag Python's text
// position, score and whether a free spot was found. The C# port
// places the same flags in the same order; a flag matches when the text point is within 1 mm, or when the scores
// tie (another candidate with the same score, within 1e-6).
// Run: dotnet build -o out && dotnet out/flagplacement_check.dll [fixture.json]
var path = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "fixture.json");
var fx = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
var failures = 0;
void Check(string name, bool ok, string detail = "")
{
  if (!ok) failures++;
  Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name} {detail}");
}
List<(double X, double Y)> Pts(JsonNode? n) => n!.AsArray().Select(p => (p![0]!.GetValue<double>(), p[1]!.GetValue<double>())).ToList();

double h = fx["h"]!.GetValue<double>();
var placer = new FlagPlacer(fx["rotation"]!.GetValue<double>(), Pts(fx["area"]), fx["margin"]!.GetValue<double>());
foreach (var pipe in fx["pipes"]!.AsArray())
{
  var pts = Pts(pipe);
  placer.AddSoftLine(pts, FlagPlacer.NetworkWeight);
  placer.ReserveBuffered(pts, 0.35);
  placer.AddHard(pts, 0.15);
}
foreach (var s in fx["structures"]!.AsArray()) placer.AddHard(new[] { (s![0]!.GetValue<double>(), s[1]!.GetValue<double>()) }, s[2]!.GetValue<double>() + 0.1);
foreach (var l in fx["soft"]!.AsArray()) placer.AddSoftLine(Pts(l));
foreach (var l in fx["otherLines"]!.AsArray()) placer.AddHard(Pts(l), 0.15);
foreach (var c in fx["otherCircles"]!.AsArray()) placer.AddHard(new[] { (c![0]!.GetValue<double>(), c[1]!.GetValue<double>()) }, c[2]!.GetValue<double>() + 0.15);
foreach (var t in fx["texts"]!.AsArray()) placer.AddHard(Pts(t), 0.1, closed: true);

var matched = 0;
foreach (var f in fx["flags"]!.AsArray())
{
  var id = f!["id"]!.GetValue<string>();
  var a = Pts(new JsonArray(f["anchor"]!.DeepClone()))[0];
  var dirs = (f["directions"] as JsonArray)?.Select(x => x!.GetValue<double>()).ToList();
  var p = placer.Place(a.X, a.Y, f["text"]!.GetValue<string>(), h, dirs);
  var e = f["expected"]!;
  double ex = e["textX"]!.GetValue<double>(), ey = e["textY"]!.GetValue<double>(), es = e["score"]!.GetValue<double>();
  var dist = Math.Sqrt((p.TextX - ex) * (p.TextX - ex) + (p.TextY - ey) * (p.TextY - ey));
  var same = dist < 0.001 && p.Free == e["free"]!.GetValue<bool>() && Math.Abs(p.Score - es) < 1e-6;
  var tie = !same && Math.Abs(p.Score - es) < 1e-6;
  if (same || tie) matched++;
  Check(id, same || tie, $"free {p.Free} score {p.Score:F4} (Python {es:F4}) text ({p.TextX:F3}, {p.TextY:F3}) d {dist * 1000:F2} mm{(tie ? " (tie)" : "")}");
}
Check($"all {fx["flags"]!.AsArray().Count} flags as Python", matched == fx["flags"]!.AsArray().Count);

// text size and box (as the Python text size and box functions)
var (w, hh) = FlagPlacer.TextSize("STB1 TB 600\\PRL +45,30", 1.0);
Check("text size", Math.Abs(w - 11 * 0.68) < 1e-12 && Math.Abs(hh - 2.667) < 1e-12, $"{w} {hh}");
var (w2, _) = FlagPlacer.TextSize("S PP 200 8,0 {\\Fisocpeur|b0|i0|c0|p34;‰}", 1.0);
Check("formatting codes not measured", Math.Abs(w2 - 14 * 0.68) < 1e-12, $"{w2 / 0.68}");
var b = FlagPlacer.FlagBox(new FlagPlacer.Pt(0, 0), 5, 2.667, "ML", 1.0);
Check("ML box", Math.Abs(b.MinX + 0.5) < 1e-12 && Math.Abs(b.MaxX - 5.5) < 1e-12 && Math.Abs(b.MaxY - 1.0) < 1e-12 && Math.Abs(b.MinY - (0.5 - 2.667 - 0.5)) < 1e-12);

// a flag boxed in by existing texts has no free spot: the soft placement is used and reported
{
  var pl = new FlagPlacer(0, new[] { (0.0, 0.0), (100.0, 0.0), (100.0, 100.0), (0.0, 100.0) }, 1.0);
  for (var i = -18; i <= 18; i++)
    for (var j = -18; j <= 18; j++)
      if (i != 0 || j != 0) pl.AddHard(new[] { (50.0 + 2.5 * i, 50.0 + 2.5 * j) }, 0.9);
  var p = pl.Place(50, 50, "BOXED IN", 1.0);
  Check("no free spot", !p.Free && p.Candidates > 0, $"score {p.Score:F2}");
}

Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
