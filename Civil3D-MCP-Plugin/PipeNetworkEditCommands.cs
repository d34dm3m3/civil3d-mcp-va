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
  public static Task<object?> CreatePipeNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var partsListName = PluginRuntime.GetRequiredString(parameters, "partsList");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var networkId = CreatePipeNetwork(civilDoc, name);
      var network = CivilObjectUtils.GetRequiredObject<Network>(transaction, networkId, OpenMode.ForWrite);

      var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
      if (!string.IsNullOrWhiteSpace(layerName) && network is Autodesk.AutoCAD.DatabaseServices.Entity entity)
      {
        entity.Layer = layerName;
      }

      var partsListId = FindPartsListId(civilDoc, transaction, partsListName);
      network.PartsListId = partsListId;

      var styleName = PluginRuntime.GetOptionalString(parameters, "style");
      if (!string.IsNullOrWhiteSpace(styleName))
      {
        var styleId = FindStyleId(civilDoc, transaction, styleName!, "PipeNetworkStyles", "NetworkStyles");
        if (styleId != ObjectId.Null)
        {
          network.StyleId = styleId;
        }
      }

      var referenceSurface = PluginRuntime.GetOptionalString(parameters, "referenceSurface");
      if (!string.IsNullOrWhiteSpace(referenceSurface))
      {
        var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, referenceSurface!, OpenMode.ForRead);
        network.ReferenceSurfaceId = surface.ObjectId;
      }

      var referenceAlignment = PluginRuntime.GetOptionalString(parameters, "referenceAlignment");
      if (!string.IsNullOrWhiteSpace(referenceAlignment))
      {
        var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, referenceAlignment!);
        network.ReferenceAlignmentId = alignment.ObjectId;
      }

      return new Dictionary<string, object?>
      {
        ["name"] = CivilObjectUtils.GetName(network) ?? name,
        ["handle"] = CivilObjectUtils.GetHandle(network),
        ["partsList"] = ResolveObjectName(transaction, partsListId),
        ["created"] = true,
      };
    });
  }

  public static Task<object?> AddStructureToNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var partName = PluginRuntime.GetRequiredString(parameters, "partName");
    var rimElevation = PluginRuntime.GetOptionalDouble(parameters, "rimElevation") ?? 0.0;
    var sumpDepth = PluginRuntime.GetOptionalDouble(parameters, "sumpDepth") ?? 0.0;
    // C170: structureDiameter (mm, inner or outer) picks the size in a family with several, as buildPipeNetworkRun does.
    // Use case (a project, 2026-10-21): a new inspection chamber (Tillsynsbrunn: ID 315, 400, 600) could not be added.
    var structureDiameter = PluginRuntime.GetOptionalDouble(parameters, "structureDiameter");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var location = new Point3d(x, y, rimElevation);
      var newName = PluginRuntime.GetOptionalString(parameters, "name")?.Trim();
      if (!string.IsNullOrEmpty(newName)) RefuseTakenName(network, transaction, newName!);
      // C203: rimFromSurface (the network's reference surface at x, y) and sumpElevation
      if (PluginRuntime.GetOptionalBool(parameters, "rimFromSurface") == true)
      {
        rimElevation = SurfaceZ(NetworkSurface(network, transaction), x, y)
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"No surface elevation at ({x:F2}, {y:F2}) for rimFromSurface. Nothing was changed.");
        location = new Point3d(x, y, rimElevation);
      }
      var createdStructureId = AddStructureToNetwork(network, transaction, location, partName, rimElevation, sumpDepth, structureDiameter);
      var sumpElevation = PluginRuntime.GetOptionalDouble(parameters, "sumpElevation");
      if (sumpElevation != null)
      {
        var ws = CivilObjectUtils.GetRequiredObject<Structure>(transaction, createdStructureId, OpenMode.ForWrite);
        ws.ControlSumpBy = Autodesk.Civil.DatabaseServices.StructureControlSumpType.ByElevation;
        ws.SumpElevation = sumpElevation.Value;
      }
      // C98h: a name of one's own, so that network_build replace (which erases the parts named prefix-…) takes the part
      // with it on a rebuild (live 2026-10-10: air valve branches added after the build kept Civil's names and would double).
      if (!string.IsNullOrEmpty(newName))
        CivilObjectUtils.GetRequiredObject<Structure>(transaction, createdStructureId, OpenMode.ForWrite).Name = newName!;
      var structure = CivilObjectUtils.GetRequiredObject<Structure>(transaction, createdStructureId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["networkName"] = CivilObjectUtils.GetName(network) ?? networkName,
        ["structure"] = ToStructureData(structure, transaction),
        ["added"] = true,
      };
    });
  }

  public static Task<object?> AddPipeToNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var partName = PluginRuntime.GetRequiredString(parameters, "partName");
    var diameter = PluginRuntime.GetOptionalDouble(parameters, "diameter");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var startStructureName = PluginRuntime.GetOptionalString(parameters, "startStructure");
      var endStructureName = PluginRuntime.GetOptionalString(parameters, "endStructure");
      var startStructureId = !string.IsNullOrWhiteSpace(startStructureName)
        ? FindStructureByName(network, transaction, startStructureName!, OpenMode.ForRead).ObjectId
        : ObjectId.Null;
      var endStructureId = !string.IsNullOrWhiteSpace(endStructureName)
        ? FindStructureByName(network, transaction, endStructureName!, OpenMode.ForRead).ObjectId
        : ObjectId.Null;

      var startPoint = ReadPoint(parameters, "startPoint");
      var endPoint = ReadPoint(parameters, "endPoint");
      // C203: with startInvert/endInvert the pipe is created at its height (a pipe at z 0 drags connected sumps down);
      // the structures' sumps are restored afterwards when the pipe lowered them
      {
        var r0 = (diameter ?? 0) / 2000;
        var si0 = PluginRuntime.GetOptionalDouble(parameters, "startInvert");
        var ei0 = PluginRuntime.GetOptionalDouble(parameters, "endInvert");
        if (startPoint != null && si0 != null) startPoint = new Point3d(startPoint.Value.X, startPoint.Value.Y, si0.Value + r0);
        if (endPoint != null && ei0 != null) endPoint = new Point3d(endPoint.Value.X, endPoint.Value.Y, ei0.Value + r0);
      }
      var sumpBefore = new[] { startStructureId, endStructureId }.Where(id => !id.IsNull).Distinct()
        .ToDictionary(id => id, id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead).SumpElevation);
      var newName = PluginRuntime.GetOptionalString(parameters, "name")?.Trim();
      if (!string.IsNullOrEmpty(newName)) RefuseTakenName(network, transaction, newName!);
      var createdPipeId = AddPipeToNetwork(network, transaction, partName, diameter, startPoint, endPoint, startStructureId, endStructureId);
      if (!string.IsNullOrEmpty(newName))
        CivilObjectUtils.GetRequiredObject<Pipe>(transaction, createdPipeId, OpenMode.ForWrite).Name = newName!;   // C98h, as for structures
      // C203: the given ends again after connecting (ConnectToStructure(force) may move them, C176), and
      // startInvert/endInvert (centreline = invert + inner diameter / 2)
      {
        var wp = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, createdPipeId, OpenMode.ForWrite);
        var si = PluginRuntime.GetOptionalDouble(parameters, "startInvert");
        var ei = PluginRuntime.GetOptionalDouble(parameters, "endInvert");
        var r = wp.InnerDiameterOrWidth / 2;
        var s0 = startPoint ?? wp.StartPoint; var e0 = endPoint ?? wp.EndPoint;
        var s1 = new Point3d(s0.X, s0.Y, si != null ? si.Value + r : s0.Z);
        var e1 = new Point3d(e0.X, e0.Y, ei != null ? ei.Value + r : e0.Z);
        if ((startPoint != null || si != null) && wp.StartPoint.DistanceTo(s1) > 1e-6) wp.StartPoint = s1;
        if ((endPoint != null || ei != null) && wp.EndPoint.DistanceTo(e1) > 1e-6) wp.EndPoint = e1;
        foreach (var (sid, before) in sumpBefore)
        {
          var st = CivilObjectUtils.GetRequiredObject<Structure>(transaction, sid, OpenMode.ForWrite);
          var inv = (sid == wp.StartStructureId ? wp.StartPoint.Z : wp.EndPoint.Z) - r;
          var want = Math.Min(before, inv);
          if (Math.Abs(st.SumpElevation - want) > 0.0005) { st.ControlSumpBy = Autodesk.Civil.DatabaseServices.StructureControlSumpType.ByElevation; st.SumpElevation = want; }
        }
      }
      var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, createdPipeId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["networkName"] = CivilObjectUtils.GetName(network) ?? networkName,
        ["pipe"] = ToPipeData(pipe, transaction),
        ["added"] = true,
      };
    });
  }

  /// <summary>C98h: renames a pipe or a structure of a network (the name must be free among the network's pipes and structures).
  /// Need: parts added after network_build kept Civil's names, so replace=true would not erase them on a rebuild.</summary>
  public static Task<object?> RenameNetworkPartAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var oldName = PluginRuntime.GetRequiredString(parameters, "name");
    var newName = PluginRuntime.GetRequiredString(parameters, "newName").Trim();
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      RefuseTakenName(network, transaction, newName);
      Part? part = network.GetPipeIds().Cast<ObjectId>().Select(id => (Part)transaction.GetObject(id, OpenMode.ForRead))
                     .FirstOrDefault(x => string.Equals(x.Name, oldName, StringComparison.OrdinalIgnoreCase))
                   ?? network.GetStructureIds().Cast<ObjectId>().Select(id => (Part)transaction.GetObject(id, OpenMode.ForRead))
                     .FirstOrDefault(x => string.Equals(x.Name, oldName, StringComparison.OrdinalIgnoreCase));
      if (part == null)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Network '{networkName}' has no pipe or structure named '{oldName}'. Nothing was changed.");
      part.UpgradeOpen();
      part.Name = newName;
      return new Dictionary<string, object?> { ["networkName"] = networkName, ["kind"] = part is Pipe ? "pipe" : "structure", ["oldName"] = oldName, ["newName"] = part.Name };
    });
  }

  private static void RefuseTakenName(Network network, Transaction transaction, string name)
  {
    var taken = network.GetPipeIds().Cast<ObjectId>().Concat(network.GetStructureIds().Cast<ObjectId>())
      .Any(id => string.Equals(CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)), name, StringComparison.OrdinalIgnoreCase));
    if (taken)
      throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Network '{network.Name}' already has a part named '{name}'. Nothing was changed.");
  }

  public static Task<object?> ResizePipeInNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeName = PluginRuntime.GetRequiredString(parameters, "pipeName");
    var newPartName = PluginRuntime.GetOptionalString(parameters, "newPartName");
    var newDiameter = PluginRuntime.GetOptionalDouble(parameters, "newDiameter");
    var holdCenterline = PluginRuntime.GetOptionalBool(parameters, "holdCenterline") ?? false;

    if (string.IsNullOrWhiteSpace(newPartName) && !newDiameter.HasValue)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Either 'newPartName' or 'newDiameter' is required.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var pipe = FindPipeByName(network, transaction, pipeName, OpenMode.ForWrite);

      if (!string.IsNullOrWhiteSpace(newPartName))
      {
        // C98a: with newDiameter (mm, inner or outer, as network_build) the size is chosen inside the named family, so a pipe
        // can move to another family whose sizes have no names (Uponor PE100 PN10 -> PN16 in a bore). Before, the family name
        // alone matched only a one-size family, and newDiameter then resized inside the old family.
        var part = FindPartForNetwork(network, transaction, newPartName!, DomainType.Pipe, newDiameter);
        var (start, end) = (pipe.StartPoint, pipe.EndPoint);
        pipe.SwapPartFamilyAndSize(part.FamilyId, part.SizeId);
        // The swap holds the inner invert (live 2026-10-10: PE100 PN10 -> PN16, same outer diameter, centre moved half the
        // inner difference, 12.7 mm for OD 400), so a pressure pipe with the same outer diameter got a step at each change of
        // class. holdCenterline puts the centreline back where it was.
        if (holdCenterline)
        {
          pipe.StartPoint = start;
          pipe.EndPoint = end;
        }
      }
      else if (newDiameter.HasValue)
      {
        pipe.ResizeByInnerDiameterOrWidth(newDiameter.Value, useClosestSize: false);
      }

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["pipeName"] = pipeName,
        ["newPartName"] = newPartName,
        ["newDiameter"] = newDiameter,
        ["resized"] = true,
        ["holdCenterline"] = holdCenterline,
        ["pipe"] = ToPipeData(pipe, transaction),
      };
    });
  }

  /// <summary>C123: sets the centreline elevation of pipe ends in a gravity network (x and y kept): pipes [{name,
  /// startZ?, endZ?}], dryRun?. All pipes are found and checked before the first change; one unknown pipe and nothing
  /// changes. Use case (a project, 2026-10-12): the water main lay at 1.0-1.1 m cover and was to be put at 1.2 m.</summary>
  public static Task<object?> SetPipeEndElevationsAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var list = PluginRuntime.GetParameter(parameters, "pipes") as JsonArray;
    if (list == null || list.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "pipes must list at least one {name, startZ?, endZ?}. Nothing was changed.");
    var wanted = list.Select((n, i) =>
    {
      if (n is not JsonObject o || o["name"]?.GetValue<string>() is not string name)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"pipes[{i}] needs a name. Nothing was changed.");
      double? sz = o["startZ"]?.GetValue<double>(), ez = o["endZ"]?.GetValue<double>();
      // C125: startX/startY and endX/endY move the end in plan too (both coordinates or neither)
      double? sx = o["startX"]?.GetValue<double>(), sy = o["startY"]?.GetValue<double>(), ex = o["endX"]?.GetValue<double>(), ey = o["endY"]?.GetValue<double>();
      if ((sx == null) != (sy == null) || (ex == null) != (ey == null))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"pipes[{i}] ({name}): give startX with startY and endX with endY. Nothing was changed.");
      if (sz == null && ez == null && sx == null && ex == null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"pipes[{i}] ({name}): give startZ, endZ, startX/Y and/or endX/Y. Nothing was changed.");
      return (Name: name, StartZ: sz, EndZ: ez, StartXY: sx == null ? ((double, double)?)null : (sx.Value, sy!.Value), EndXY: ex == null ? ((double, double)?)null : (ex.Value, ey!.Value));
    }).ToList();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var pipes = wanted.Select(w => (W: w, Pipe: FindPipeByName(network, transaction, w.Name, dryRun ? OpenMode.ForRead : OpenMode.ForWrite))).ToList();
      var result = new List<Dictionary<string, object?>>();
      foreach (var (w, pipe) in pipes)
      {
        var (s0, e0) = (pipe.StartPoint, pipe.EndPoint);
        var s1 = new Point3d(w.StartXY?.Item1 ?? s0.X, w.StartXY?.Item2 ?? s0.Y, w.StartZ ?? s0.Z);
        var e1 = new Point3d(w.EndXY?.Item1 ?? e0.X, w.EndXY?.Item2 ?? e0.Y, w.EndZ ?? e0.Z);
        if (!dryRun)
        {
          if (s1.DistanceTo(s0) > 1e-6) pipe.StartPoint = s1;
          if (e1.DistanceTo(e0) > 1e-6) pipe.EndPoint = e1;
        }
        result.Add(new Dictionary<string, object?>
        {
          ["name"] = pipe.Name,
          ["startZ"] = new[] { Math.Round(s0.Z, 4), Math.Round(dryRun ? s1.Z : pipe.StartPoint.Z, 4) },
          ["endZ"] = new[] { Math.Round(e0.Z, 4), Math.Round(dryRun ? e1.Z : pipe.EndPoint.Z, 4) },
          ["xyMoved"] = dryRun ? 0 : Math.Round(Math.Max(Math.Sqrt(Math.Pow(pipe.StartPoint.X - s0.X, 2) + Math.Pow(pipe.StartPoint.Y - s0.Y, 2)),
            Math.Sqrt(Math.Pow(pipe.EndPoint.X - e0.X, 2) + Math.Pow(pipe.EndPoint.Y - e0.Y, 2))), 4),
        });
      }
      return new Dictionary<string, object?> { ["networkName"] = network.Name, ["dryRun"] = dryRun, ["pipes"] = result, ["count"] = result.Count };
    });
  }

  /// <summary>C190: pulls each pipe end that is connected to a structure back to the structure's edge in plan, so the
  /// pipe meets the well instead of running to its centre: {networkName, radii? {structureName: m}, familyRadii?
  /// {partFamily: m}, pipes? [names], dryRun?}. Radius per structure: radii by name, else familyRadii by part family,
  /// else the structure's InnerDiameterOrWidth / 2. The new end lies on the line from the structure's centre toward the
  /// pipe's other end; its elevation follows the pipe's existing grade, so the invert at the structure centre is kept.
  /// Computed from the structure centre, so a second run gives the same result. Ends whose remaining 2D length would be
  /// under 0.05 m are left. Ends stop 5 mm inside the edge (C191: an end exactly on the boundary is disconnected). Use case (a project, 2026-11-24;
  /// requested: pipes that run into the structures should connect to the structures): pipes were added from centre to centre and ran through the real-size
  /// well symbols in plan.</summary>
  public static Task<object?> TrimPipesToStructuresAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var pipeNames = OptionalStringList(parameters, "pipes");
    Dictionary<string, double> Radii(string key)
    {
      var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
      if (PluginRuntime.GetParameter(parameters, key) is JsonObject o)
        foreach (var kv in o)
        {
          var v = kv.Value?.GetValue<double>();
          if (v == null || v < 0)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{key}.{kv.Key} must be a radius >= 0 in metres. Nothing was changed.");
          d[kv.Key] = v.Value;
        }
      return d;
    }
    var byName = Radii("radii");
    var byFamily = Radii("familyRadii");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var structures = network.GetStructureIds().Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForRead))
        .ToDictionary(st => st.ObjectId);
      var pipes = network.GetPipeIds().Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead)).ToList();
      if (pipeNames != null)
      {
        var unknown = pipeNames.Where(n => !pipes.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipes not in '{networkName}': {string.Join(", ", unknown)}. Nothing was changed.");
        pipes = pipes.Where(p => pipeNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();
      }

      (double R, string? Name) Radius(ObjectId id)
      {
        if (id.IsNull || !structures.TryGetValue(id, out var st)) return (0, null);
        if (byName.TryGetValue(st.Name ?? "", out var r)) return (r, st.Name);
        if (byFamily.TryGetValue(st.PartFamilyName ?? "", out r)) return (r, st.Name);
        return (Math.Max(0, st.InnerDiameterOrWidth / 2), st.Name);
      }

      var result = TrimPipeEnds(structures, pipes, st => Radius(st.ObjectId).R, dryRun);
      return new Dictionary<string, object?>
      {
        ["networkName"] = network.Name, ["dryRun"] = dryRun, ["pipes"] = result,
        ["trimmed"] = result.Count(r => r.TryGetValue("moved", out var m) && m is double v && v > 1e-6),
      };
    });
  }

  /// <summary>C166: points gravity networks at another surface {networkNames:[...], surface, parts? (default true), dryRun?}.
  /// Sets the network's reference surface and, with parts, every structure's and pipe's surface (Part.RefSurfaceId).
  /// Structures with automatic rim adjustment follow the new surface (rim = surface + adjustment); the answer lists each
  /// structure's rim before and after. All networks are found first; one unknown name and nothing is changed.
  /// Use case (a project, 2026-10-21): the ground model bef_mh is replaced by one built from the drone cloud and LM's laser;

  /// six networks, their parts and the profile view all point at the old surface.</summary>
  public static Task<object?> SetNetworkSurfaceAsync(JsonObject? parameters)
  {
    var names = (PluginRuntime.GetParameter(parameters, "networkNames") as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToList();
    if (names == null || names.Count == 0 || names.Any(string.IsNullOrWhiteSpace))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "networkNames must list at least one network name. Nothing was changed.");
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surface");
    var parts = PluginRuntime.GetOptionalBool(parameters, "parts") ?? true;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName((CivilDocument)civilDoc, transaction, surfaceName, OpenMode.ForRead);
      var networks = names.Select(n => FindPipeNetworkByName(civilDoc, transaction, n, dryRun ? OpenMode.ForRead : OpenMode.ForWrite)).ToList();
      var result = new List<Dictionary<string, object?>>();
      foreach (var network in networks)
      {
        var before = ResolveObjectName(transaction, ReferenceSurfaceOrNull(network));
        if (!dryRun)
          network.ReferenceSurfaceId = surface.ObjectId;
        var rims = new List<Dictionary<string, object?>>();
        int pipesSet = 0, structuresSet = 0;
        if (parts)
        {
          foreach (ObjectId id in network.GetStructureIds())
          {
            var st = (Structure)transaction.GetObject(id, dryRun ? OpenMode.ForRead : OpenMode.ForWrite);
            var rim0 = st.RimElevation;
            if (!dryRun)
            {
              st.RefSurfaceId = surface.ObjectId;
              structuresSet++;
            }
            rims.Add(new Dictionary<string, object?>
            {
              ["name"] = st.Name,
              ["automaticRim"] = st.AutomaticRimSurfaceAdjustment,
              ["rim"] = new[] { Math.Round(rim0, 3), Math.Round(st.RimElevation, 3) },
            });
          }
          foreach (ObjectId id in network.GetPipeIds())
          {
            if (dryRun)
              continue;
            var pipe = (Pipe)transaction.GetObject(id, OpenMode.ForWrite);
            pipe.RefSurfaceId = surface.ObjectId;
            pipesSet++;
          }
        }
        result.Add(new Dictionary<string, object?>
        {
          ["network"] = network.Name,
          ["surface"] = new[] { before, dryRun ? before : surface.Name },
          ["structuresSet"] = structuresSet,
          ["pipesSet"] = pipesSet,
          ["structures"] = rims,
        });
      }
      return new Dictionary<string, object?> { ["surface"] = surface.Name, ["dryRun"] = dryRun, ["parts"] = parts, ["networks"] = result };
    });
  }

  /// <summary>C172: changes the size (and optionally the family) of named structures in a gravity network {networkName,
  /// structures:[names], partName (family), structureDiameter (mm, inner or outer), dryRun?}. Rim and sump stay where
  /// they were; connections are kept. All names are found first; one unknown name and nothing is changed. Use case
  /// (a project, 2026-10-21): two new inspection chambers were added as ID 400 where the drawing uses ID 600.</summary>
  public static Task<object?> SetStructureSizeAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var partName = PluginRuntime.GetRequiredString(parameters, "partName");
    var structureDiameter = PluginRuntime.GetOptionalDouble(parameters, "structureDiameter");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var names = (PluginRuntime.GetParameter(parameters, "structures") as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToList();
    if (names == null || names.Count == 0 || names.Any(string.IsNullOrWhiteSpace))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "structures must list at least one structure name. Nothing was changed.");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var part = FindPartForNetwork(network, transaction, partName, DomainType.Structure, structureDiameter);
      var found = names.Select(n => FindStructureByName(network, transaction, n, dryRun ? OpenMode.ForRead : OpenMode.ForWrite)).ToList();
      var result = new List<Dictionary<string, object?>>();
      foreach (var st in found)
      {
        var before = ReadField(() => st.PartSizeName);
        var (rim0, sump0, auto0) = (st.RimElevation, st.SumpElevation, st.AutomaticRimSurfaceAdjustment);
        if (!dryRun)
        {
          st.SwapPartFamilyAndSize(part.FamilyId, part.SizeId);
          if (!auto0 && Math.Abs(st.RimElevation - rim0) > 1e-6) st.RimElevation = rim0;
          if (Math.Abs(st.SumpElevation - sump0) > 1e-6) { st.ControlSumpBy = StructureControlSumpType.ByElevation; st.SumpElevation = sump0; }
        }
        result.Add(new Dictionary<string, object?>
        {
          ["name"] = st.Name,
          ["partSizeName"] = new[] { before, dryRun ? before : ReadField(() => st.PartSizeName) },
          ["rimElevation"] = new[] { Math.Round(rim0, 4), Math.Round(st.RimElevation, 4) },
          ["sumpElevation"] = new[] { Math.Round(sump0, 4), Math.Round(st.SumpElevation, 4) },
        });
      }
      return new Dictionary<string, object?> { ["networkName"] = network.Name, ["dryRun"] = dryRun, ["structures"] = result, ["count"] = result.Count };
    });
  }

  /// <summary>C176: connects pipe ends to structures in the same network {networkName, connections:[{pipe, end: "start" |
  /// "end", structure}], dryRun?}. Pipe.ConnectToStructure(force: true); the pipe end's position and elevation are put back
  /// afterwards if Civil moved them, so only the topology changes. All names are found first; one unknown name and nothing is
  /// changed. Use case (a project, 2026-10-21): the user moved three cleanouts by hand and the service pipes lost their
  /// connection; the plan positions were right and only the link to the structure was missing.</summary>
  public static Task<object?> ConnectPipesToStructuresAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    if (PluginRuntime.GetParameter(parameters, "connections") is not JsonArray list || list.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "connections must list at least one {pipe, end, structure}. Nothing was changed.");
    var wanted = list.Select((n, i) =>
    {
      if (n is not JsonObject o || o["pipe"]?.GetValue<string>() is not string pipe || o["structure"]?.GetValue<string>() is not string st)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"connections[{i}] needs pipe and structure. Nothing was changed.");
      var end = (o["end"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
      if (end is not ("start" or "end"))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"connections[{i}] ({pipe}): end must be start or end. Nothing was changed.");
      return (Pipe: pipe, End: end, Structure: st);
    }).ToList();
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var items = wanted.Select(w => (W: w, Pipe: FindPipeByName(network, transaction, w.Pipe, dryRun ? OpenMode.ForRead : OpenMode.ForWrite),
        Structure: FindStructureByName(network, transaction, w.Structure, OpenMode.ForRead))).ToList();
      var result = new List<Dictionary<string, object?>>();
      foreach (var (w, pipe, st) in items)
      {
        var pos = w.End == "start" ? ConnectorPositionType.Start : ConnectorPositionType.End;
        var before = w.End == "start" ? pipe.StartPoint : pipe.EndPoint;
        var distance = Math.Sqrt(Math.Pow(before.X - st.Position.X, 2) + Math.Pow(before.Y - st.Position.Y, 2));
        if (!dryRun)
        {
          pipe.ConnectToStructure(pos, st.ObjectId, force: true);
          var after = w.End == "start" ? pipe.StartPoint : pipe.EndPoint;
          if (after.DistanceTo(before) > 1e-6)
          {
            if (w.End == "start") pipe.StartPoint = before; else pipe.EndPoint = before;
          }
        }
        result.Add(new Dictionary<string, object?>
        {
          ["pipe"] = pipe.Name,
          ["end"] = w.End,
          ["structure"] = st.Name,
          ["planDistanceToStructure"] = Math.Round(distance, 4),
          ["connected"] = !dryRun,
        });
      }
      return new Dictionary<string, object?> { ["networkName"] = network.Name, ["dryRun"] = dryRun, ["connections"] = result, ["count"] = result.Count };
    });
  }

  /// <summary>C137: sets structure rims {networkName, structures:[{name, rimElevation? | surfaceAdjustment?}], dryRun?}.
  /// rimElevation turns the automatic rim-to-surface adjustment off and sets the rim; surfaceAdjustment keeps the rim on
  /// the reference surface with that offset (m). The sump stays where it was (if Civil moves it with the rim, the sump is
  /// switched to "by elevation" and restored). All names are found first; one unknown name and nothing is changed.
  /// Use case (a project, 2026-10-16): structure N has a raised riser and a bolted lid at +42.20 while the ground is +41.5; the
  /// profiles drew the manhole to the ground and the riser was drawn as a separate polyline.</summary>
  public static Task<object?> SetStructureRimsAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var list = PluginRuntime.GetParameter(parameters, "structures") as JsonArray;
    if (list == null || list.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "structures must list at least one {name, rimElevation? | surfaceAdjustment?}. Nothing was changed.");
    var wanted = list.Select((n, i) =>
    {
      if (n is not JsonObject o || o["name"]?.GetValue<string>() is not string name)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"structures[{i}] needs a name. Nothing was changed.");
      double? rim = o["rimElevation"]?.GetValue<double>(), adj = o["surfaceAdjustment"]?.GetValue<double>();
      double? sump = o["sumpElevation"]?.GetValue<double>();   // C174
      if ((rim != null && adj != null) || (rim == null && adj == null && sump == null))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"structures[{i}] ({name}): give rimElevation or surfaceAdjustment (not both), and/or sumpElevation. Nothing was changed.");
      return (Name: name, Rim: rim, Adjustment: adj, Sump: sump);
    }).ToList();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var found = wanted.Select(w => (W: w, Structure: FindStructureByName(network, transaction, w.Name, dryRun ? OpenMode.ForRead : OpenMode.ForWrite))).ToList();
      var result = new List<Dictionary<string, object?>>();
      foreach (var (w, st) in found)
      {
        var (rim0, sump0, auto0) = (st.RimElevation, st.SumpElevation, st.AutomaticRimSurfaceAdjustment);
        if (!dryRun)
        {
          if (w.Rim != null)
          {
            st.AutomaticRimSurfaceAdjustment = false;
            st.RimElevation = w.Rim.Value;
          }
          else if (w.Adjustment != null)
          {
            st.AutomaticRimSurfaceAdjustment = true;
            st.SurfaceAdjustmentValue = w.Adjustment.Value;
          }
          // C174: sumpElevation sets the sump (by elevation); otherwise the sump stays where it was
          var sumpTarget = w.Sump ?? sump0;
          if (Math.Abs(st.SumpElevation - sumpTarget) > 1e-6)
          {
            st.ControlSumpBy = StructureControlSumpType.ByElevation;
            st.SumpElevation = sumpTarget;
          }
        }
        result.Add(new Dictionary<string, object?>
        {
          ["name"] = st.Name,
          ["rimElevation"] = new[] { Math.Round(rim0, 4), Math.Round(dryRun ? (w.Rim ?? rim0) : st.RimElevation, 4) },
          ["sumpElevation"] = new[] { Math.Round(sump0, 4), Math.Round(st.SumpElevation, 4) },
          ["automaticRimSurfaceAdjustment"] = new[] { auto0, dryRun ? (w.Rim == null) : st.AutomaticRimSurfaceAdjustment },
          ["surfaceAdjustment"] = Math.Round(st.SurfaceAdjustmentValue, 4),
        });
      }
      return new Dictionary<string, object?> { ["networkName"] = network.Name, ["dryRun"] = dryRun, ["structures"] = result, ["count"] = result.Count };
    });
  }

  /// <summary>C125: erases named pipes and structures of a gravity network (names; dryRun?). All are found first; one
  /// unknown name and nothing is erased. Pipes connected to an erased structure stay, with a free end. Use case (a project,
  /// 2026-10-12): a 1 m jog of two short pipes at the connection to the existing main was straightened (pipe and
  /// bend structure erased, the next pipe's start moved with setPipeEndElevations).</summary>
  public static Task<object?> ErasePipeNetworkPartsAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var names = (PluginRuntime.GetParameter(parameters, "names") as JsonArray)?.Select(n => n?.GetValue<string>()?.Trim() ?? "").Where(n => n.Length > 0).Distinct().ToList();
    if (names == null || names.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "names must list at least one pipe or structure. Nothing was changed.");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var found = new List<(string Name, string Kind, ObjectId Id)>();
      var pipes = network.GetPipeIds().Cast<ObjectId>().Select(id => (Pipe)transaction.GetObject(id, OpenMode.ForRead)).ToList();
      var structures = network.GetStructureIds().Cast<ObjectId>().Select(id => (Structure)transaction.GetObject(id, OpenMode.ForRead)).ToList();
      foreach (var n in names)
      {
        var pipe = pipes.FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase));
        var structure = structures.FirstOrDefault(st => string.Equals(st.Name, n, StringComparison.OrdinalIgnoreCase));
        if (pipe != null) found.Add((pipe.Name, "pipe", pipe.ObjectId));
        else if (structure != null) found.Add((structure.Name, "structure", structure.ObjectId));
        else throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Network '{network.Name}' has no pipe or structure '{n}'. Nothing was changed.");
      }
      if (!dryRun)
        foreach (var f in found.OrderBy(f => f.Kind == "structure"))   // pipes first, then structures
          transaction.GetObject(f.Id, OpenMode.ForWrite).Erase();
      return new Dictionary<string, object?>
      {
        ["networkName"] = network.Name, ["dryRun"] = dryRun,
        ["erased"] = found.Select(f => new Dictionary<string, object?> { ["name"] = f.Name, ["kind"] = f.Kind }).ToList(),
      };
    });
  }

  private static ObjectId CreatePipeNetwork(object civilDoc, string name)
  {
    var requestedName = name;
    return Network.Create((CivilDocument)civilDoc, ref requestedName);
  }

  private static ObjectId AddStructureToNetwork(Network network, Transaction transaction, Point3d location, string partName, double rimElevation, double sumpDepth, double? structureDiameter = null)
  {
    var part = FindPartForNetwork(network, transaction, partName, DomainType.Structure, structureDiameter);
    var createdId = ObjectId.Null;
    network.AddStructure(part.FamilyId, part.SizeId, location, 0.0, ref createdId, applyRules: false);
    var structure = CivilObjectUtils.GetRequiredObject<Structure>(transaction, createdId, OpenMode.ForWrite);
    structure.RimElevation = rimElevation;
    structure.RimToSumpHeight = sumpDepth;
    return createdId;
  }

  private static ObjectId AddPipeToNetwork(Network network, Transaction transaction, string partName, double? diameter, Point3d? startPoint, Point3d? endPoint, ObjectId startStructureId, ObjectId endStructureId)
  {
    var start = startPoint ?? GetStructureLocation(transaction, startStructureId, "start");
    var end = endPoint ?? GetStructureLocation(transaction, endStructureId, "end");
    var part = FindPartForNetwork(network, transaction, partName, DomainType.Pipe, diameter);
    var createdId = ObjectId.Null;
    network.AddLinePipe(part.FamilyId, part.SizeId, new LineSegment3d(start, end), ref createdId, applyRules: false);
    var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, createdId, OpenMode.ForWrite);
    if (startStructureId != ObjectId.Null)
      pipe.ConnectToStructure(ConnectorPositionType.Start, startStructureId, force: true);
    if (endStructureId != ObjectId.Null)
      pipe.ConnectToStructure(ConnectorPositionType.End, endStructureId, force: true);
    return createdId;
  }

  private static ObjectId FindPartsListId(object civilDoc, Transaction transaction, string partsListName)
  {
    foreach (ObjectId id in ((CivilDocument)civilDoc).Styles.PartsListSet)
    {
      if (string.Equals(CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)), partsListName, StringComparison.OrdinalIgnoreCase))
      {
        return id;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Parts list '{partsListName}' was not found.");
  }

  private readonly record struct NetworkPartIds(ObjectId FamilyId, ObjectId SizeId);

  /// <summary>Finds a part by size name, by the name of a single-size family, or by family name plus
  /// diameter in catalog units (mm), matched against the outer or the inner diameter within 0.5 mm.</summary>
  private static NetworkPartIds FindPartForNetwork(Network network, Transaction transaction, string partName, DomainType domain, double? diameterMm = null)
  {
    if (network.PartsListId.IsNull)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{network.Name}' does not have a parts list assigned.");
    }

    var partsList = CivilObjectUtils.GetRequiredObject<PartsList>(transaction, network.PartsListId, OpenMode.ForRead);
    var familySizes = new List<string>();
    foreach (ObjectId familyId in partsList.GetPartFamilyIdsByDomain(domain))
    {
      if (familyId.IsNull) continue;
      var family = CivilObjectUtils.GetRequiredObject<PartFamily>(transaction, familyId, OpenMode.ForRead);
      var familyMatches = string.Equals(family.Name, partName, StringComparison.OrdinalIgnoreCase);
      for (var index = 0; index < family.PartSizeCount; index++)
      {
        var sizeId = family[index];
        if (sizeId.IsNull) continue;
        var size = CivilObjectUtils.GetRequiredObject<PartSize>(transaction, sizeId, OpenMode.ForRead);
        string? sizeName;
        double? inner = null, outer = null;
        using (var record = size.SizeDataRecord)
        {
          sizeName = SizeName(record);
          if (domain == DomainType.Pipe)
          {
            (inner, outer, _) = PipeDiameters(record);
          }
          else
          {
            inner = AsDouble(FieldValue(record, PartContextType.StructInnerDiameter));
            outer = AsDouble(FieldValue(record, PartContextType.StructDiameter));
          }
        }

        if (sizeName != null && string.Equals(sizeName, partName, StringComparison.OrdinalIgnoreCase))
          return new NetworkPartIds(familyId, sizeId);
        if (familyMatches && diameterMm.HasValue
          && ((outer.HasValue && Math.Abs(outer.Value - diameterMm.Value) <= 0.5) || (inner.HasValue && Math.Abs(inner.Value - diameterMm.Value) <= 0.5)))
          return new NetworkPartIds(familyId, sizeId);
        if (familyMatches && !diameterMm.HasValue && family.PartSizeCount == 1)
          return new NetworkPartIds(familyId, sizeId);
        if (familyMatches)
          familySizes.Add(outer.HasValue ? $"OD {outer:0.#} (ID {inner:0.#})" : $"ID {inner:0.#}");
      }
    }

    var hint = familySizes.Count > 0
      ? $" Family '{partName}' has: {string.Join(", ", familySizes)}; give {(domain == DomainType.Pipe ? "diameter" : "structureDiameter")} (mm) to choose one."
      : "";
    var withDiameter = diameterMm.HasValue ? $" with diameter {diameterMm} mm" : "";
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Exact {domain} size '{partName}'{withDiameter} was not found in the parts list for network '{network.Name}'.{hint}");
  }

  private static Point3d GetStructureLocation(Transaction transaction, ObjectId structureId, string endpointName)
  {
    if (structureId.IsNull)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Provide {endpointName}Point or {endpointName}Structure; an origin point will not be assumed.");
    return CivilObjectUtils.GetRequiredObject<Structure>(transaction, structureId, OpenMode.ForRead).Location;
  }

  public static Task<object?> DeletePipeNetworkAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForWrite);
      var pipes = network.GetPipeIds().Cast<ObjectId>().ToList();
      var structures = network.GetStructureIds().Cast<ObjectId>().ToList();
      foreach (var id in pipes) transaction.GetObject(id, OpenMode.ForWrite).Erase();
      foreach (var id in structures) transaction.GetObject(id, OpenMode.ForWrite).Erase();
      network.Erase();
      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["deleted"] = true,
        ["pipesErased"] = pipes.Count,
        ["structuresErased"] = structures.Count,
      };
    });
  }
}
