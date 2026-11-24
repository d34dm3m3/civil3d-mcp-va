using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

public static partial class PipeNetworkCommands
{
  private sealed record RunVertex(double X, double Y, double Z, string? Structure, double? StructureDiameter);

  /// <summary>Builds one pipe run in a gravity network in a single transaction: a structure at every vertex
  /// (defaultStructure unless the vertex names another family; structureDiameter in mm, inner or outer,
  /// picks the size in a family with several) and a pipe of the given family and diameter
  /// (mm, outer or inner) between consecutive vertices. Vertex z is the pipe centre. Names are
  /// "{prefix}-B001.." for structures and "{prefix}-R001.." for pipes; with replace, parts with that prefix
  /// are erased first, otherwise an existing prefix is a conflict. Rim elevations come from the reference
  /// surface (z + 1.5 m where the surface has no elevation); sumps sit at the pipe bottom.</summary>
  public static Task<object?> BuildPipeNetworkRunAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var partsListName = PluginRuntime.GetRequiredString(parameters, "partsList");
    var referenceSurface = PluginRuntime.GetOptionalString(parameters, "referenceSurface");
    var referenceAlignment = PluginRuntime.GetOptionalString(parameters, "referenceAlignment");
    var pipeFamily = PluginRuntime.GetRequiredString(parameters, "pipeFamily");
    var pipeDiameter = PluginRuntime.GetRequiredDouble(parameters, "pipeDiameter");
    var prefix = PluginRuntime.GetRequiredString(parameters, "namePrefix");
    var defaultStructure = PluginRuntime.GetOptionalString(parameters, "defaultStructure") ?? "Brytpunkt";
    var replace = PluginRuntime.GetOptionalBool(parameters, "replace") ?? false;
    var pipeStyleName = PluginRuntime.GetOptionalString(parameters, "pipeStyle");
    var alignStructures = PluginRuntime.GetOptionalBool(parameters, "alignStructuresToPipes") ?? false;
    // C63 (2026-10-08): replace=true erases the run, and with it its place in every profile view. keepProfileViews
    // puts the new parts back into the same views (draw flag and style override as before) in the same transaction,
    // instead of one network_profile_view_add per view afterwards (about 100 s of profile view updates each).
    var keepProfileViews = PluginRuntime.GetOptionalBool(parameters, "keepProfileViews") ?? false;
    var structureStyleNames = (PluginRuntime.GetParameter(parameters, "structureStyles") as JsonObject)?
      .Where(kv => kv.Value != null)
      .ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
      ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (PluginRuntime.GetParameter(parameters, "vertices") is not JsonArray vertexNodes || vertexNodes.Count < 2)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "vertices must contain at least two points.");
    }

    var vertices = vertexNodes.Select(n => n is JsonObject o
      ? new RunVertex(
          o["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "vertex x is required."),
          o["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "vertex y is required."),
          o["z"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "vertex z is required."),
          o["structure"]?.GetValue<string>(),
          o["structureDiameter"]?.GetValue<double>())
      : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each vertex must be an object.")).ToList();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var cdoc = (CivilDocument)civilDoc;
      Network network;
      var created = false;
      if (EnumeratePipeNetworks(civilDoc, transaction, OpenMode.ForRead).Any(n => string.Equals(CivilObjectUtils.GetName(n), networkName, StringComparison.OrdinalIgnoreCase)))
      {
        network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      }
      else
      {
        network = CivilObjectUtils.GetRequiredObject<Network>(transaction, CreatePipeNetwork(civilDoc, networkName), OpenMode.ForWrite);
        network.PartsListId = FindPartsListId(civilDoc, transaction, partsListName);
        created = true;
      }

      if (!string.IsNullOrWhiteSpace(referenceSurface))
        network.ReferenceSurfaceId = CivilObjectUtils.FindSurfaceByName(cdoc, transaction, referenceSurface!, OpenMode.ForRead).ObjectId;
      if (!string.IsNullOrWhiteSpace(referenceAlignment))
        network.ReferenceAlignmentId = CivilObjectUtils.FindAlignmentByName(cdoc, transaction, referenceAlignment!).ObjectId;

      bool HasPrefix(ObjectId id) => (CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)) ?? "").StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase);
      var oldPipes = network.GetPipeIds().Cast<ObjectId>().Where(HasPrefix).ToList();
      var oldStructures = network.GetStructureIds().Cast<ObjectId>().Where(HasPrefix).ToList();
      if ((oldPipes.Count > 0 || oldStructures.Count > 0) && !replace)
      {
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
          $"Network '{networkName}' already has {oldPipes.Count} pipes and {oldStructures.Count} structures named '{prefix}-…'. Pass replace=true to rebuild them. Nothing was changed.");
      }

      var membership = keepProfileViews ? ProfileViewMembership(database, transaction, prefix) : new List<ViewMembership>();
      foreach (var id in oldPipes) transaction.GetObject(id, OpenMode.ForWrite).Erase();
      foreach (var id in oldStructures) transaction.GetObject(id, OpenMode.ForWrite).Erase();

      var pipePart = FindPartForNetwork(network, transaction, pipeFamily, DomainType.Pipe, pipeDiameter);
      var structureParts = new Dictionary<string, NetworkPartIds>(StringComparer.OrdinalIgnoreCase);
      NetworkPartIds StructurePart(string family, double? diameter)
      {
        var key = $"{family}|{diameter}";
        if (!structureParts.TryGetValue(key, out var part))
        {
          part = FindPartForNetwork(network, transaction, family, DomainType.Structure, diameter);
          structureParts[key] = part;
        }

        return part;
      }

      var pipeStyleId = string.IsNullOrWhiteSpace(pipeStyleName) ? ObjectId.Null
        : LookupUtils.GetStyleIdStrict(cdoc.Styles.PipeStyles, transaction, pipeStyleName, "Pipe style");
      var structureStyleIds = structureStyleNames.ToDictionary(
        kv => kv.Key, kv => LookupUtils.GetStyleIdStrict(cdoc.Styles.StructureStyles, transaction, kv.Value, "Structure style"),
        StringComparer.OrdinalIgnoreCase);

      // C110: a network without a reference surface throws "Retrieve attribute failed" from ReferenceSurfaceId (live
      // 2026-10-11, T6 in a new drawing from the template without a surface); it is read as none.
      var referenceSurfaceId = ReferenceSurfaceOrNull(network);
      CivilSurface? surface = referenceSurfaceId.IsNull ? null
        : CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, referenceSurfaceId, OpenMode.ForRead);

      var structureIds = new List<ObjectId>();
      for (var i = 0; i < vertices.Count; i++)
      {
        var v = vertices[i];
        var part = StructurePart(string.IsNullOrWhiteSpace(v.Structure) ? defaultStructure : v.Structure!, v.StructureDiameter);
        var sid = ObjectId.Null;
        network.AddStructure(part.FamilyId, part.SizeId, new Point3d(v.X, v.Y, v.Z), 0.0, ref sid, applyRules: false);
        var structure = CivilObjectUtils.GetRequiredObject<Structure>(transaction, sid, OpenMode.ForWrite);
        structure.Name = $"{prefix}-B{i + 1:000}";
        if (structureStyleIds.TryGetValue(structure.PartFamilyName ?? "", out var structureStyleId)) structure.StyleId = structureStyleId;
        double rim;
        try
        {
          rim = surface?.FindElevationAtXY(v.X, v.Y) ?? v.Z + 1.5;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
          rim = v.Z + 1.5;
        }

        structure.RimElevation = rim;
        structureIds.Add(sid);
      }

      var pipeIds = new List<ObjectId>();
      double length2d = 0, maxEndDeviation = 0, outer = 0;
      var minSlope = double.MaxValue;
      var maxSlope = double.MinValue;
      for (var i = 0; i + 1 < vertices.Count; i++)
      {
        var a = vertices[i];
        var b = vertices[i + 1];
        var start = new Point3d(a.X, a.Y, a.Z);
        var end = new Point3d(b.X, b.Y, b.Z);
        var pid = ObjectId.Null;
        network.AddLinePipe(pipePart.FamilyId, pipePart.SizeId, new LineSegment3d(start, end), ref pid, applyRules: false);
        var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, pid, OpenMode.ForWrite);
        pipe.Name = $"{prefix}-R{i + 1:000}";
        pipeIds.Add(pid);
        if (!pipeStyleId.IsNull) pipe.StyleId = pipeStyleId;
        pipe.ConnectToStructure(ConnectorPositionType.Start, structureIds[i], force: true);
        pipe.ConnectToStructure(ConnectorPositionType.End, structureIds[i + 1], force: true);
        if (pipe.StartPoint.DistanceTo(start) > 1e-4) pipe.StartPoint = start;
        if (pipe.EndPoint.DistanceTo(end) > 1e-4) pipe.EndPoint = end;
        maxEndDeviation = Math.Max(maxEndDeviation, Math.Max(pipe.StartPoint.DistanceTo(start), pipe.EndPoint.DistanceTo(end)));
        outer = pipe.OuterDiameterOrWidth;
        var horizontal = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
        length2d += horizontal;
        if (horizontal > 1e-6)
        {
          var slope = (b.Z - a.Z) / horizontal;
          minSlope = Math.Min(minSlope, slope);
          maxSlope = Math.Max(maxSlope, slope);
        }
      }

      for (var i = 0; i < structureIds.Count; i++)
      {
        var structure = CivilObjectUtils.GetRequiredObject<Structure>(transaction, structureIds[i], OpenMode.ForWrite);
        var sump = vertices[i].Z - outer / 2.0;
        structure.RimToSumpHeight = Math.Max(0.0, structure.RimElevation - sump);
      }

      // Structures are added with rotation 0; symbols drawn for a pipe along +X (valve tick, plug) follow the
      // run when asked (same rule as structure_rotation_set alignToPipe).
      List<Dictionary<string, object?>>? aligned = null, notAligned = null;
      if (alignStructures)
      {
        var built = structureIds.Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForWrite)).ToList();
        var (changed, skipped) = StructureRotationCommands.AlignToPipes(network, built, transaction, 0.0);
        aligned = changed.Select(StructureRotationCommands.Describe).ToList();
        notAligned = skipped;
      }

      var restoredViews = new List<Dictionary<string, object?>>();
      bool New(string? name) => (name ?? "").StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase);
      string? StyleName(ObjectId id) => id.IsNull ? null : (transaction.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Styles.StyleBase)?.Name;
      foreach (var m in membership)
      {
        if (m.Pipes > 0)
          foreach (var id in pipeIds) CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForWrite).AddToProfileView(m.ViewId);
        if (m.Structures > 0)
          foreach (var id in structureIds) CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForWrite).AddToProfileView(m.ViewId);
        var view = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, m.ViewId, OpenMode.ForWrite);
        foreach (PipeOverride o in view.PipeOverrides)
        {
          if (!New(o.PipeName)) continue;
          o.Draw = m.PipesDrawn;
          if (!m.PipeStyle.IsNull) { o.OverrideStyleId = m.PipeStyle; o.UseOverrideStyle = true; }
        }

        foreach (StructureOverride o in view.StructureOverrides)
        {
          if (!New(o.StructName)) continue;
          o.Draw = m.StructuresDrawn;
          if (!m.StructureStyle.IsNull) { o.OverrideStyleId = m.StructureStyle; o.UseOverrideStyle = true; }
        }

        restoredViews.Add(new Dictionary<string, object?>
        {
          ["profileViewName"] = m.ViewName,
          ["pipes"] = m.Pipes > 0 ? pipeIds.Count : 0,
          ["structures"] = m.Structures > 0 ? structureIds.Count : 0,
          ["pipeOverrideStyle"] = StyleName(m.PipeStyle),
          ["structureOverrideStyle"] = StyleName(m.StructureStyle),
        });
      }

      return new Dictionary<string, object?>
      {
        ["profileViewsRestored"] = keepProfileViews ? restoredViews : null,
        ["structureRotations"] = aligned,
        ["structuresNotAligned"] = notAligned,
        ["networkName"] = CivilObjectUtils.GetName(network),
        ["networkCreated"] = created,
        ["prefix"] = prefix,
        ["replacedPipes"] = oldPipes.Count,
        ["replacedStructures"] = oldStructures.Count,
        ["pipes"] = vertices.Count - 1,
        ["structures"] = structureIds.Count,
        ["structureFamilies"] = structureParts.Keys.Select(key => key.TrimEnd('|')).Distinct().ToList(),
        ["pipeOuterDiameter"] = outer,
        ["length2d"] = length2d,
        ["minSlope"] = minSlope == double.MaxValue ? null : minSlope,
        ["maxSlope"] = maxSlope == double.MinValue ? null : maxSlope,
        ["maxEndDeviation"] = maxEndDeviation,
      };
    });
  }
}
