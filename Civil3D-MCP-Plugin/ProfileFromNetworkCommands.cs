using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// C103: a layout profile along an alignment from a pipe network's pipe ends, at the pipes' top (crown, centre + OD/2),
/// invert (centre - ID/2), outside bottom (centre - OD/2) or centre. Use case (lesson 17, two pilot projects): the
/// package bands show the crown or pipe bottom from helper profiles, which both projects built in Python from JSON
/// files of the network's vertices and thinned to keep the band labels apart. Stations are the nearest point on the
/// alignment (C101); pipe ends farther from it than maxOffset (an inlet that leaves the alignment) or beyond its ends are
/// left out. Ends at the same station (within 5 cm) merge: the highest for top, the lowest for invert and bottom.
/// minSpacing thins the vertices as the pilots did: while two lie closer, the inner vertex whose removal changes the line
/// least goes, except the ends and vertices at fixedStations. endSnap moves pipe ends near the alignment's ends onto them. An existing profile of the name is replaced only with
/// replace: true, and the bands and profile views that used it get the new one. The profile is not drawn in the
/// profile views unless drawInProfileViews asks (C102).
/// </summary>
public static class ProfileFromNetworkCommands
{
  private sealed record Vertex(double Station, double Elevation, string Pipe, bool Fixed);

  public static Task<object?> ProfileFromNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName").Trim();
    var reference = (PluginRuntime.GetOptionalString(parameters, "reference") ?? "top").Trim().ToLowerInvariant();
    if (reference is not ("top" or "invert" or "bottom" or "centre" or "center"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "reference must be top (crown), invert, bottom (outside bottom) or centre. Nothing was changed.");
    var minSpacing = PluginRuntime.GetOptionalDouble(parameters, "minSpacing") ?? 0.0;
    var maxOffset = PluginRuntime.GetOptionalDouble(parameters, "maxOffset");
    var endSnap = PluginRuntime.GetOptionalDouble(parameters, "endSnap") ?? 0.0;
    var fromStation = PluginRuntime.GetOptionalDouble(parameters, "fromStation");
    var toStation = PluginRuntime.GetOptionalDouble(parameters, "toStation");
    var replace = PluginRuntime.GetOptionalBool(parameters, "replace") ?? false;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var styleName = PluginRuntime.GetOptionalString(parameters, "style");
    var labelSetName = PluginRuntime.GetOptionalString(parameters, "labelSet");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    var fixedStations = (PluginRuntime.GetParameter(parameters, "fixedStations") as JsonArray ?? new JsonArray()).Select(n => n!.GetValue<double>()).ToList();
    var pipeNames = (PluginRuntime.GetParameter(parameters, "pipeNames") as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(n => n.Length > 0)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var drawIn = ProfileCommands.ParseDrawIn(parameters);
    if (minSpacing < 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "minSpacing must be >= 0. Nothing was changed.");
    if (maxOffset is <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "maxOffset must be > 0. Nothing was changed.");
    if (endSnap < 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "endSnap must be >= 0. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      Network? network = null;
      var networks = new List<string>();
      foreach (ObjectId id in civilDoc.GetPipeNetworkIds())
      {
        var n = (Network)transaction.GetObject(id, OpenMode.ForRead);
        networks.Add(n.Name);
        if (string.Equals(n.Name, networkName, StringComparison.OrdinalIgnoreCase)) network = n;
      }
      if (network == null)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{networkName}' was not found. Available: {string.Join(", ", networks)}. Nothing was changed.");

      // Pipe ends with station, offset and the reference elevation.
      var raw = new List<Vertex>();
      var skipped = new List<Dictionary<string, object?>>();
      var seenPipes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      // endSnap: a pipe end within this distance of the alignment's start or end point gets that station (the pilot's
      // 1.5 m; the run starts or ends at the alignment's end although the pipe end lies a little beyond or beside it).
      double sx = 0, sy = 0, ex = 0, ey = 0;
      alignment.PointLocation(alignment.StartingStation, 0.0, ref sx, ref sy);
      alignment.PointLocation(alignment.EndingStation, 0.0, ref ex, ref ey);
      foreach (ObjectId id in network.GetPipeIds())
      {
        var pipe = (Pipe)transaction.GetObject(id, OpenMode.ForRead);
        seenPipes.Add(pipe.Name);
        if (pipeNames != null && !pipeNames.Contains(pipe.Name)) continue;
        var od = pipe.OuterDiameterOrWidth;
        var inner = pipe.InnerDiameterOrWidth;
        foreach (var end in new[] { pipe.StartPoint, pipe.EndPoint })
        {
          var near = AlignmentCommands.NearestStation(alignment, end.X, end.Y);
          if (endSnap > 0 && Math.Sqrt((end.X - sx) * (end.X - sx) + (end.Y - sy) * (end.Y - sy)) <= endSnap)
            near = near with { Station = alignment.StartingStation, OutOfRange = false, Distance = Math.Min(near.Distance, endSnap) };
          else if (endSnap > 0 && Math.Sqrt((end.X - ex) * (end.X - ex) + (end.Y - ey) * (end.Y - ey)) <= endSnap)
            near = near with { Station = alignment.EndingStation, OutOfRange = false, Distance = Math.Min(near.Distance, endSnap) };
          string? why = near.OutOfRange ? "beyond the alignment's end"
            : maxOffset != null && near.Distance > maxOffset.Value ? $"{near.Distance:0.###} from the alignment (maxOffset {maxOffset.Value:0.###})"
            : fromStation != null && near.Station < fromStation.Value - 1e-6 ? "before fromStation"
            : toStation != null && near.Station > toStation.Value + 1e-6 ? "after toStation" : null;
          if (why != null)
          {
            skipped.Add(new Dictionary<string, object?> { ["pipe"] = pipe.Name, ["station"] = Math.Round(near.Station, 3), ["reason"] = why });
            continue;
          }
          var z = reference switch
          {
            "top" => end.Z + od / 2.0,
            "invert" => end.Z - inner / 2.0,
            "bottom" => end.Z - od / 2.0,
            _ => end.Z,
          };
          raw.Add(new Vertex(near.Station, z, pipe.Name, fixedStations.Any(f => Math.Abs(f - near.Station) <= 0.1)));
        }
      }
      if (pipeNames != null)
      {
        var unknown = pipeNames.Where(n => !seenPipes.Contains(n)).ToList();
        if (unknown.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipes {string.Join(", ", unknown)} are not in '{network.Name}'. Nothing was changed.");
      }

      // Ends at the same station merge.
      var merged = new List<Vertex>();
      foreach (var v in raw.OrderBy(v => v.Station))
      {
        if (merged.Count > 0 && v.Station - merged[^1].Station < 0.05)
        {
          var last = merged[^1];
          var z = reference == "top" ? Math.Max(last.Elevation, v.Elevation)
            : reference is "invert" or "bottom" ? Math.Min(last.Elevation, v.Elevation) : (last.Elevation + v.Elevation) / 2.0;
          merged[^1] = last with { Elevation = z, Fixed = last.Fixed || v.Fixed };
          continue;
        }
        merged.Add(v);
      }
      if (merged.Count < 2)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Only {merged.Count} pipe end(s) of '{network.Name}' lie along '{alignment.Name}' ({skipped.Count} left out). Nothing was changed.");

      // Thinning: the inner vertex whose removal changes the line least, while two lie closer than minSpacing.
      var points = merged.ToList();
      var removed = new List<Dictionary<string, object?>>();
      while (minSpacing > 0)
      {
        var best = -1;
        var bestChange = double.MaxValue;
        for (var j = 1; j < points.Count - 1; j++)
        {
          if (points[j].Fixed) continue;
          if (points[j + 1].Station - points[j].Station >= minSpacing && points[j].Station - points[j - 1].Station >= minSpacing) continue;
          var a = points[j - 1];
          var c = points[j + 1];
          var t = (points[j].Station - a.Station) / (c.Station - a.Station);
          var change = Math.Abs(a.Elevation + t * (c.Elevation - a.Elevation) - points[j].Elevation);
          if (change < bestChange) { bestChange = change; best = j; }
        }
        if (best < 0) break;
        removed.Add(new Dictionary<string, object?> { ["station"] = Math.Round(points[best].Station, 3), ["elevationChange"] = Math.Round(bestChange, 4) });
        points.RemoveAt(best);
      }
      var tooClose = minSpacing > 0 ? points.Zip(points.Skip(1), (a, b) => b.Station - a.Station).Count(d => d < minSpacing) : 0;

      var result = new Dictionary<string, object?>
      {
        ["networkName"] = network.Name,
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profileName,
        ["reference"] = reference == "center" ? "centre" : reference,
        ["pipeEnds"] = raw.Count,
        ["vertices"] = points.Select(p => new Dictionary<string, object?> { ["station"] = Math.Round(p.Station, 3), ["elevation"] = Math.Round(p.Elevation, 3), ["fixed"] = p.Fixed }).ToList(),
        ["thinnedOut"] = removed,
        ["leftOut"] = skipped,
        ["dryRun"] = dryRun,
      };
      if (tooClose > 0) result["warning"] = $"{tooClose} pair(s) of fixed or end vertices lie closer than minSpacing.";
      if (dryRun) return result;

      // An existing profile is replaced only on request; the bands and profile views that used it get the new one.
      var oldId = ObjectId.Null;
      foreach (ObjectId pid in alignment.GetProfileIds())
        if (string.Equals(((Profile)transaction.GetObject(pid, OpenMode.ForRead)).Name, profileName, StringComparison.OrdinalIgnoreCase)) oldId = pid;
      var users = new List<(ObjectId View, string Location, int Index, bool P1, bool P2)>();
      var drawnIn = new List<string>();
      if (!oldId.IsNull)
      {
        if (!replace)
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Profile '{profileName}' exists on '{alignment.Name}'; give replace: true to replace it. Nothing was changed.");
        foreach (ObjectId vid in alignment.GetProfileViewIds())
        {
          var view = (ProfileView)transaction.GetObject(vid, OpenMode.ForRead);
          foreach (var (location, items) in new[] { ("bottom", view.Bands.GetBottomBandItems()), ("top", view.Bands.GetTopBandItems()) })
          {
            var i = 0;
            foreach (ProfileViewBandItem item in items)
            {
              bool p1 = false, p2 = false;
              try { p1 = item.Profile1Id == oldId; } catch (InvalidOperationException) { }
              try { p2 = item.Profile2Id == oldId; } catch (InvalidOperationException) { }
              if (p1 || p2) users.Add((vid, location, i, p1, p2));
              i++;
            }
          }
          foreach (var o in view.GraphOverrides)
            if (string.Equals(o.ProfileName, profileName, StringComparison.OrdinalIgnoreCase) && o.Draw) drawnIn.Add(view.Name);
        }
        ((Profile)transaction.GetObject(oldId, OpenMode.ForWrite)).Erase();
      }

      var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
      var styleId = LookupUtils.GetProfileStyleId(civilDoc, transaction, styleName);
      var labelSetId = LookupUtils.GetProfileLabelSetId(civilDoc, transaction, labelSetName);
      var newId = Profile.CreateByLayout(profileName, alignment.ObjectId, layerId, styleId, labelSetId);
      var profile = (Profile)transaction.GetObject(newId, OpenMode.ForWrite);
      foreach (var p in points) profile.PVIs.AddPVI(p.Station, p.Elevation);

      foreach (var group in users.GroupBy(u => (u.View, u.Location)))
      {
        var view = (ProfileView)transaction.GetObject(group.Key.View, OpenMode.ForWrite);
        var items = group.Key.Location == "bottom" ? view.Bands.GetBottomBandItems() : view.Bands.GetTopBandItems();
        var list = items.Cast<ProfileViewBandItem>().ToList();
        foreach (var u in group)
        {
          if (u.P1) list[u.Index].Profile1Id = newId;
          if (u.P2) list[u.Index].Profile2Id = newId;
        }
        if (group.Key.Location == "bottom") view.Bands.SetBottomBandItems(items);
        else view.Bands.SetTopBandItems(items);
      }
      var draw = drawnIn.Count == 0 ? drawIn
        : (drawIn.All, drawIn.Names.Concat(drawnIn).ToHashSet(StringComparer.OrdinalIgnoreCase));
      result["replaced"] = !oldId.IsNull;
      result["bandsRepointed"] = users.Count;
      result["profileViews"] = ProfileCommands.DrawInProfileViews(transaction, alignment, profile.Name, draw);
      result["pvis"] = profile.PVIs.Count;
      return result;
    });
  }
}
