using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plan rotation of structures (Structure.Rotation, radians, counterclockwise from +X in WCS): an absolute angle,
/// or the direction of the connected pipes so that symbols drawn for a pipe along +X (valve tick, valve chamber,
/// plug) follow the pipe. One pipe: its axis start to end. Two pipes: the mean of the two axes (the second turned
/// half a turn first if the two point in opposite senses). offsetDegrees is added afterwards. Connected pipes are
/// found through Pipe.StartStructureId/EndStructureId over the network (typed API).
/// </summary>
public static class StructureRotationCommands
{
  internal sealed record Change(string Name, string? Family, double OldDegrees, double NewDegrees, List<string> Pipes, string Source);

  private static double Normalize(double degrees)
  {
    var d = degrees % 360.0;
    if (d < 0) d += 360.0;
    return Math.Abs(d - 360.0) < 1e-9 ? 0.0 : d;
  }

  /// <summary>Pipes of the network per structure id, with each pipe's plan axis start to end.</summary>
  private static Dictionary<ObjectId, List<(string Name, Vector2d Axis)>> PipesByStructure(Network network, Transaction transaction)
  {
    var map = new Dictionary<ObjectId, List<(string, Vector2d)>>();
    foreach (ObjectId id in network.GetPipeIds())
    {
      if (transaction.GetObject(id, OpenMode.ForRead) is not Pipe pipe) continue;
      var axis = new Vector2d(pipe.EndPoint.X - pipe.StartPoint.X, pipe.EndPoint.Y - pipe.StartPoint.Y);
      foreach (var structureId in new[] { pipe.StartStructureId, pipe.EndStructureId })
      {
        if (structureId.IsNull) continue;
        if (!map.TryGetValue(structureId, out var list)) map[structureId] = list = new List<(string, Vector2d)>();
        list.Add((pipe.Name, axis));
      }
    }

    return map;
  }

  /// <summary>Plan direction (degrees) of the pipes at a structure, or a reason why there is none.</summary>
  private static (double? Degrees, string Source) PipeDirection(List<(string Name, Vector2d Axis)> pipes)
  {
    var axes = pipes.Where(p => p.Axis.Length > 1e-9).Select(p => p.Axis.GetNormal()).ToList();
    if (axes.Count == 1) return (axes[0].Angle * 180.0 / Math.PI, "pipe");
    if (axes.Count == 2)
    {
      var second = axes[0].DotProduct(axes[1]) < 0 ? axes[1].Negate() : axes[1];
      var mean = axes[0] + second;
      return mean.Length < 1e-9 ? (null, "pipes cancel out") : (mean.Angle * 180.0 / Math.PI, "mean of 2 pipes");
    }

    return (null, axes.Count == 0 ? "no connected pipe with a plan length" : $"{axes.Count} pipes connected; give rotationDegrees");
  }

  /// <summary>C207: sets the rotation without moving pipes. Civil snaps the ends of pipes connected to a rotated
  /// structure back to its centre and keeps their elevation (live: the service
  /// pipes of rotated gullies, trimmed to the edge came back at the centre 2-11 cm off grade, so every syncPipeNetwork/addServiceConnection
  /// rerun saw changed geometry). The ends are restored after the rotation; an unchanged angle is not set at all.</summary>
  internal static void SetRotationKeepingPipes(Structure structure, double radians, Transaction transaction)
  {
    var diff = Math.Abs(Math.IEEERemainder(structure.Rotation - radians, 2 * Math.PI));
    if (diff < 1e-9) return;
    var ends = new List<(Pipe Pipe, Point3d Start, Point3d End)>();
    var net = transaction.GetObject(structure.NetworkId, OpenMode.ForRead) as Network;
    if (net != null)
      foreach (ObjectId id in net.GetPipeIds())
      {
        var pipe = (Pipe)transaction.GetObject(id, OpenMode.ForRead);
        if (pipe.StartStructureId == structure.ObjectId || pipe.EndStructureId == structure.ObjectId)
          ends.Add((pipe, pipe.StartPoint, pipe.EndPoint));
      }
    structure.Rotation = radians;
    foreach (var (pipe, s0, e0) in ends)
    {
      if (pipe.StartPoint.DistanceTo(s0) <= 1e-6 && pipe.EndPoint.DistanceTo(e0) <= 1e-6) continue;
      if (!pipe.IsWriteEnabled) pipe.UpgradeOpen();
      if (pipe.StartPoint.DistanceTo(s0) > 1e-6) pipe.StartPoint = s0;
      if (pipe.EndPoint.DistanceTo(e0) > 1e-6) pipe.EndPoint = e0;
    }
  }

  /// <summary>Rotates the given structures (open for write) to their pipes; used by structure_rotation_set and
  /// network_build alignStructuresToPipes.</summary>
  internal static (List<Change> Changed, List<Dictionary<string, object?>> Skipped) AlignToPipes(
    Network network, IEnumerable<Structure> structures, Transaction transaction, double offsetDegrees)
  {
    var pipes = PipesByStructure(network, transaction);
    var changed = new List<Change>();
    var skipped = new List<Dictionary<string, object?>>();
    foreach (var structure in structures)
    {
      var connected = pipes.GetValueOrDefault(structure.ObjectId) ?? new List<(string, Vector2d)>();
      var (direction, source) = PipeDirection(connected);
      if (direction == null)
      {
        skipped.Add(new Dictionary<string, object?> { ["name"] = structure.Name, ["reason"] = source, ["connectedPipes"] = connected.Select(p => p.Name).ToList() });
        continue;
      }

      var old = Normalize(structure.Rotation * 180.0 / Math.PI);
      var value = Normalize(direction.Value + offsetDegrees);
      SetRotationKeepingPipes(structure, value * Math.PI / 180.0, transaction);
      changed.Add(new Change(structure.Name, structure.PartFamilyName, old, Normalize(structure.Rotation * 180.0 / Math.PI), connected.Select(p => p.Name).ToList(), source));
    }

    return (changed, skipped);
  }

  internal static Dictionary<string, object?> Describe(Change c) => new()
  {
    ["name"] = c.Name,
    ["family"] = c.Family,
    ["oldRotationDegrees"] = Math.Round(c.OldDegrees, 4),
    ["newRotationDegrees"] = Math.Round(c.NewDegrees, 4),
    ["connectedPipes"] = c.Pipes,
    ["source"] = c.Source,
  };

  public static Task<object?> SetStructureRotationAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var names = (PluginRuntime.GetParameter(parameters, "structureNames") as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>()
      .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var namePrefix = PluginRuntime.GetOptionalString(parameters, "namePrefix");
    var partFamily = PluginRuntime.GetOptionalString(parameters, "partFamily");
    var rotationDegrees = PluginRuntime.GetOptionalDouble(parameters, "rotationDegrees");
    var alignToPipe = PluginRuntime.GetOptionalBool(parameters, "alignToPipe") ?? false;
    var offsetDegrees = PluginRuntime.GetOptionalDouble(parameters, "offsetDegrees") ?? 0.0;
    if ((rotationDegrees != null) == alignToPipe)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give either rotationDegrees or alignToPipe=true. Nothing was changed.");
    if (names is { Count: 0 })
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "structureNames must list at least one structure when given. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = PipeNetworkCommands.FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var selected = new List<Structure>();
      foreach (ObjectId id in network.GetStructureIds())
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is not Structure structure) continue;
        if (names != null && !names.Contains(structure.Name, StringComparer.OrdinalIgnoreCase)) continue;
        if (!string.IsNullOrWhiteSpace(namePrefix) && !structure.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)) continue;
        if (!string.IsNullOrWhiteSpace(partFamily) && !string.Equals(structure.PartFamilyName, partFamily, StringComparison.OrdinalIgnoreCase)) continue;
        selected.Add(structure);
      }

      if (names != null)
      {
        var missing = names.Where(n => !selected.Any(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Not in network '{networkName}' (or filtered out): {string.Join(", ", missing)}. Nothing was changed.");
      }

      if (selected.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No structure in network '{networkName}' matches the filter. Nothing was changed.");

      foreach (var structure in selected) structure.UpgradeOpen();
      List<Change> changed;
      var skipped = new List<Dictionary<string, object?>>();
      if (alignToPipe)
      {
        (changed, skipped) = AlignToPipes(network, selected, transaction, offsetDegrees);
      }
      else
      {
        var pipes = PipesByStructure(network, transaction);
        changed = new List<Change>();
        foreach (var structure in selected)
        {
          var old = Normalize(structure.Rotation * 180.0 / Math.PI);
          SetRotationKeepingPipes(structure, Normalize(rotationDegrees!.Value + offsetDegrees) * Math.PI / 180.0, transaction);
          changed.Add(new Change(structure.Name, structure.PartFamilyName, old, Normalize(structure.Rotation * 180.0 / Math.PI),
            (pipes.GetValueOrDefault(structure.ObjectId) ?? new List<(string, Vector2d)>()).Select(p => p.Name).ToList(), "absolute"));
        }
      }

      return new Dictionary<string, object?>
      {
        ["networkName"] = networkName,
        ["changed"] = changed.Select(Describe).ToList(),
        ["skipped"] = skipped,
      };
    });
  }
}
