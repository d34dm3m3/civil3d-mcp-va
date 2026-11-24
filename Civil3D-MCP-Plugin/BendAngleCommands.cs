using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Horizontal and vertical angles in structures (C92, Hv/Vv). Use case: the company's plan drawings (plan/profile sheets) mark the bend in
/// each manhole where the line turns: Hv, the horizontal deviation from straight through, and Vv, the change of
/// grade, as text by the structure. Civil has no such label property, so the angles are computed here from the two pipes
/// of each structure and written to the structure's Description; a structure label style whose text is
/// &lt;[Description(CP)]&gt; (copied with label_style_copy, C90) shows it. The angle math is BendAngleMath (no AutoCAD
/// types, checked by tools/bendanglemath_check). The pipes of a structure are found from the pipes' StartStructureId
/// and EndStructureId (typed; Structure.ConnectedPipe is an indexed property C# cannot call). Every structure name and
/// every structure to be written (not on a locked layer) is checked before the first description changes.
/// </summary>
public static class BendAngleCommands
{
  private const double CurveTolerance = 0.001;

  private sealed record PipeEnd(Pipe Pipe, bool AtStart);

  /// <summary>
  /// C92: Hv and Vv for each structure of networkName (or only structureNames) with exactly two connected pipes. With
  /// write: true the text "Hv 30°" and/or "Vv 5°" (decimals, default 0; only an angle of at least minHvDeg / minVvDeg,
  /// default 1) becomes the structure's Description; keepExisting keeps the rest of the description (earlier Hv/Vv text
  /// replaced, the new text after " | "). dryRun with write lists the new descriptions without writing them.
  /// </summary>
  public static Task<object?> StructureBendAnglesAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var names = (PluginRuntime.GetParameter(parameters, "structureNames") as JsonArray)?.Select(n => n?.GetValue<string>()?.Trim() ?? "").ToList();
    var write = PluginRuntime.GetOptionalBool(parameters, "write") ?? false;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var decimals = PluginRuntime.GetOptionalInt(parameters, "decimals") ?? 0;
    var minHv = PluginRuntime.GetOptionalDouble(parameters, "minHvDeg") ?? 1.0;
    var minVv = PluginRuntime.GetOptionalDouble(parameters, "minVvDeg") ?? 1.0;
    var keepExisting = PluginRuntime.GetOptionalBool(parameters, "keepExisting") ?? false;
    if (names != null && (names.Count == 0 || names.Any(n => n.Length == 0)))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "structureNames must hold structure names (leave it out for every structure). Nothing was changed.");
    if (decimals is < 0 or > 3)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "decimals must be 0-3. Nothing was changed.");
    if (!(minHv >= 0 && minHv <= 180) || !(minVv >= 0 && minVv <= 180))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "minHvDeg and minVvDeg must be 0-180 degrees. Nothing was changed.");
    var wanted = names?.ToHashSet(StringComparer.OrdinalIgnoreCase);

    object? Body(Autodesk.AutoCAD.ApplicationServices.Document doc, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Database database, Transaction transaction)
    {
      var network = PipeNetworkCommands.FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var ends = new Dictionary<ObjectId, List<PipeEnd>>();
      void Add(ObjectId structureId, PipeEnd end)
      {
        if (structureId.IsNull) return;
        if (!ends.TryGetValue(structureId, out var list)) ends[structureId] = list = new List<PipeEnd>();
        list.Add(end);
      }
      foreach (ObjectId pipeId in network.GetPipeIds())
      {
        if (transaction.GetObject(pipeId, OpenMode.ForRead) is not Pipe pipe) continue;
        Add(pipe.StartStructureId, new PipeEnd(pipe, true));
        Add(pipe.EndStructureId, new PipeEnd(pipe, false));
      }

      var structures = new List<Structure>();
      foreach (ObjectId structureId in network.GetStructureIds())
        if (transaction.GetObject(structureId, OpenMode.ForRead) is Structure structure && (wanted == null || wanted.Contains(structure.Name)))
          structures.Add(structure);
      if (wanted != null)
      {
        var missing = wanted.Where(n => !structures.Any(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))).OrderBy(n => n).ToList();
        if (missing.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            $"Not in network '{CivilObjectUtils.GetName(network)}': {string.Join(", ", missing)}. Nothing was changed.");
      }

      var report = new List<Dictionary<string, object?>>();
      var plan = new List<(Structure Structure, string Description)>();
      foreach (var structure in structures.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
      {
        var entry = new Dictionary<string, object?> { ["name"] = structure.Name };
        report.Add(entry);
        var connected = ends.GetValueOrDefault(structure.ObjectId) ?? new List<PipeEnd>();
        if (connected.Count != 2 || connected[0].Pipe.ObjectId == connected[1].Pipe.ObjectId)
        {
          entry["skipped"] = connected.Count == 2 ? "one pipe has both ends at this structure" : $"{connected.Count} connected pipe(s); Hv/Vv need exactly two";
          PlanDescription(structure, "", entry);
          continue;
        }

        // The pipe flowing in first (only for the reported inclinations; Hv and Vv do not depend on the order).
        if (FlowsOut(connected[0]) && !FlowsOut(connected[1])) connected.Reverse();
        var warnings = new List<string>();
        var a = Away(connected[0], warnings);
        var b = Away(connected[1], warnings);
        var bend = BendAngleMath.Compute(a, b);
        entry["pipes"] = connected.Select(c => c.Pipe.Name).ToList();
        if (bend == null)
        {
          entry["skipped"] = "a pipe has no plan length (vertical)";
          PlanDescription(structure, "", entry);
          continue;
        }

        var text = BendAngleMath.Text(bend.Value.HorizontalDeg, bend.Value.VerticalDeg, decimals, minHv, minVv);
        entry["hvDeg"] = Math.Round(bend.Value.HorizontalDeg, 2);
        entry["vvDeg"] = Math.Round(bend.Value.VerticalDeg, 2);
        entry["inclinationInDeg"] = Math.Round(bend.Value.InclinationInDeg, 3);
        entry["inclinationOutDeg"] = Math.Round(bend.Value.InclinationOutDeg, 3);
        entry["text"] = text;
        if (warnings.Count > 0) entry["warnings"] = warnings;
        PlanDescription(structure, text, entry);
      }

      // With write, a skipped structure only loses a stale Hv/Vv text (text ""); see BendAngleMath.NewDescription.
      void PlanDescription(Structure structure, string text, Dictionary<string, object?> entry)
      {
        if (!write) return;
        var before = structure.Description ?? "";
        var after = BendAngleMath.NewDescription(before, text, keepExisting);
        if (text.Length == 0 && before == after) return;
        entry["descriptionBefore"] = before;
        entry["descriptionAfter"] = after;
        if (!string.Equals(before, after, StringComparison.Ordinal)) plan.Add((structure, after));
      }

      if (write && !dryRun && plan.Count > 0)
      {
        if (network.IsReferenceObject)
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
            $"Network '{CivilObjectUtils.GetName(network)}' is a data shortcut reference; write the descriptions in its source drawing. Nothing was changed.");
        var locked = plan.Where(p => transaction.GetObject(p.Structure.LayerId, OpenMode.ForRead) is LayerTableRecord { IsLocked: true })
          .Select(p => $"{p.Structure.Name} ({p.Structure.Layer})").ToList();
        if (locked.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Structures on locked layers: {string.Join(", ", locked)}. Nothing was changed.");
        foreach (var (structure, description) in plan)
        {
          structure.UpgradeOpen();
          structure.Description = description;
        }
      }

      return new Dictionary<string, object?>
      {
        ["network"] = CivilObjectUtils.GetName(network),
        ["write"] = write,
        ["dryRun"] = dryRun,
        ["structures"] = report,
        ["descriptionsChanged"] = write ? plan.Count : null,
        ["note"] = !write ? "Computed only; write: true puts the text in each structure's Description."
          : dryRun ? "Dry run: nothing was changed." : null,
      };
    }

    return write && !dryRun ? CivilExecution.WriteAsync<object?>(Body) : CivilExecution.ReadAsync<object?>(Body);
  }

  /// <summary>True when the pipe's flow leaves the structure at this end.</summary>
  private static bool FlowsOut(PipeEnd end)
  {
    FlowDirectionType flow;
    try
    {
      flow = end.Pipe.FlowDirection;
    }
    catch (Exception ex) when (ex is InvalidOperationException or Autodesk.AutoCAD.Runtime.Exception)
    {
      return false;
    }
    return end.AtStart ? flow == FlowDirectionType.StartToEnd : flow == FlowDirectionType.EndToStart;
  }

  /// <summary>From the pipe's end at the structure to its far end; a curved pipe by the arc's tangent at that end.</summary>
  private static BendAngleMath.Vec Away(PipeEnd end, List<string> warnings)
  {
    var pipe = end.Pipe;
    var here = end.AtStart ? pipe.StartPoint : pipe.EndPoint;
    var there = end.AtStart ? pipe.EndPoint : pipe.StartPoint;
    var chord = new BendAngleMath.Vec(there.X - here.X, there.Y - here.Y, there.Z - here.Z);
    PipeSubEntityType kind;
    try
    {
      kind = pipe.SubEntityType;
    }
    catch (Exception ex) when (ex is InvalidOperationException or Autodesk.AutoCAD.Runtime.Exception)
    {
      kind = PipeSubEntityType.Straight;
    }

    if (kind == PipeSubEntityType.Curved)
    {
      try
      {
        var center = pipe.Curve2d.Center;
        if (BendAngleMath.ArcAway(here.X, here.Y, center.X, center.Y, there.X, there.Y, pipe.Length2D, there.Z - here.Z) is { } arc) return arc;
      }
      catch (Exception ex) when (ex is InvalidOperationException or Autodesk.AutoCAD.Runtime.Exception or NullReferenceException)
      {
        // falls back to the chord below
      }
      warnings.Add($"{pipe.Name}: curved pipe whose arc could not be read; its chord was used.");
      return chord;
    }

    var planChord = Math.Sqrt(chord.X * chord.X + chord.Y * chord.Y);
    if (kind != PipeSubEntityType.Straight || pipe.Length2D > planChord + CurveTolerance)
      warnings.Add($"{pipe.Name}: {kind} pipe; its chord was used for the direction.");
    return chord;
  }
}
