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
  public static Task<object?> ListPipeNetworksAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var networks = EnumeratePipeNetworks(civilDoc, transaction, OpenMode.ForRead)
        .Select(network => ToPipeNetworkSummary(network, transaction))
        .ToList();

      return new Dictionary<string, object?>
      {
        ["networks"] = networks,
      };
    });
  }

  public static Task<object?> GetPipeNetworkAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, name, OpenMode.ForRead);
      return ToPipeNetworkDetail(network, transaction);
    });
  }

  public static Task<object?> GetPipeAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var pipeName = PluginRuntime.GetRequiredString(parameters, "pipeName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var pipe = FindPipeByName(network, transaction, pipeName, OpenMode.ForRead);
      return ToPipeData(pipe, transaction);
    });
  }

  public static Task<object?> GetStructureAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var structureName = PluginRuntime.GetRequiredString(parameters, "structureName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var structure = FindStructureByName(network, transaction, structureName, OpenMode.ForRead);
      return ToStructureData(structure, transaction);
    });
  }

  public static Task<object?> CheckPipeNetworkInterferenceAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetRequiredString(parameters, "networkName");
    var targetType = PluginRuntime.GetRequiredString(parameters, "targetType");
    var targetName = PluginRuntime.GetRequiredString(parameters, "targetName");

    // This used to return "no conflicts" without checking anything. Pipe networks now go through the real
    // 3D clearance check (overlaps only); surfaces report pipes whose top is above the surface at an end.
    if (string.Equals(targetType, "pipe_network", StringComparison.OrdinalIgnoreCase))
    {
      return NetworkAnalysisCommands.NetworkClearanceAsync(new JsonObject
      {
        ["networks"] = new JsonArray(networkName, targetName),
        ["minClearance"] = 0.0,
      });
    }

    if (!string.Equals(targetType, "surface", StringComparison.OrdinalIgnoreCase))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unsupported targetType '{targetType}'.");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, targetName, OpenMode.ForRead);
      var above = new List<Dictionary<string, object?>>();
      foreach (ObjectId id in network.GetPipeIds())
      {
        var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForRead);
        foreach (var end in new[] { pipe.StartPoint, pipe.EndPoint })
        {
          double ground;
          try { ground = surface.FindElevationAtXY(end.X, end.Y); }
          catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
          var cover = ground - (end.Z + pipe.OuterDiameterOrWidth / 2.0);
          if (cover < 0)
            above.Add(new Dictionary<string, object?> { ["pipe"] = pipe.Name, ["x"] = end.X, ["y"] = end.Y, ["cover"] = Math.Round(cover, 3) });
        }
      }

      return new Dictionary<string, object?> { ["interferences"] = above, ["totalConflicts"] = above.Count };
    });
  }

  private static List<string>? OptionalStringList(JsonObject? parameters, string name) =>
    (PluginRuntime.GetParameter(parameters, name) as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>()
      .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
  internal static List<string> ListPipeNetworkNames(object civilDoc, Transaction transaction) =>
    EnumeratePipeNetworks(civilDoc, transaction, OpenMode.ForRead).Select(n => n.Name).ToList();
  public static int CountPipeNetworks(object civilDoc)
  {
    return ((CivilDocument)civilDoc).GetPipeNetworkIds().Count;
  }

  public static string? GetFirstPipeNetworkStyleName(object civilDoc, Transaction transaction)
  {
    var styles = ((CivilDocument)civilDoc).Styles;
    var collection = Civil3DCompatibility.GetPropertyValue(styles, "PipeNetworkStyles")
      ?? Civil3DCompatibility.GetPropertyValue(styles, "NetworkStyles");
    return LookupUtils.GetFirstStyleName(collection, transaction);
  }

  private static IEnumerable<Network> EnumeratePipeNetworks(object civilDoc, Transaction transaction, OpenMode openMode)
  {
    foreach (ObjectId objectId in ((CivilDocument)civilDoc).GetPipeNetworkIds())
    {
      yield return CivilObjectUtils.GetRequiredObject<Network>(transaction, objectId, openMode);
    }
  }

  private static IEnumerable<ObjectId> GetPipeNetworkIds(object civilDoc)
  {
    var candidates = new[]
    {
      CivilObjectUtils.InvokeMethod(civilDoc, "GetPipeNetworkIds"),
      GetNamedMemberValue(civilDoc, "PipeNetworkCollection"),
      GetNamedMemberValue(civilDoc, "NetworkCollection"),
      GetNamedMemberValue(civilDoc, "PipeNetworks"),
      GetNamedMemberValue(civilDoc, "Networks"),
    };

    foreach (var candidate in candidates)
    {
      foreach (var objectId in ToObjectIdsFlexible(candidate))
      {
        if (objectId != ObjectId.Null)
        {
          yield return objectId;
        }
      }
    }
  }

  private static IEnumerable<ObjectId> ToObjectIdsFlexible(object? value)
  {
    foreach (var objectId in CivilObjectUtils.ToObjectIds(value))
    {
      yield return objectId;
    }

    if (value is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item is ObjectId objectId && objectId != ObjectId.Null)
        {
          yield return objectId;
          continue;
        }

        var itemObjectId = GetAnyObjectId(item, "ObjectId", "Id");
        if (itemObjectId != ObjectId.Null)
        {
          yield return itemObjectId;
        }
      }
    }
  }

  internal static Network FindPipeNetworkByName(object civilDoc, Transaction transaction, string name, OpenMode openMode)
  {
    foreach (var network in EnumeratePipeNetworks(civilDoc, transaction, openMode))
    {
      if (string.Equals(CivilObjectUtils.GetName(network), name, StringComparison.OrdinalIgnoreCase))
      {
        return network;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe network '{name}' was not found.");
  }

  private static Pipe FindPipeByName(Network network, Transaction transaction, string pipeName, OpenMode openMode)
  {
    foreach (ObjectId objectId in network.GetPipeIds())
    {
      var pipe = CivilObjectUtils.GetRequiredObject<Pipe>(transaction, objectId, openMode);
      if (string.Equals(pipe.Name, pipeName, StringComparison.OrdinalIgnoreCase))
      {
        return pipe;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Pipe '{pipeName}' was not found in network '{CivilObjectUtils.GetName(network)}'.");
  }

  private static Structure FindStructureByName(Network network, Transaction transaction, string structureName, OpenMode openMode)
  {
    foreach (ObjectId objectId in network.GetStructureIds())
    {
      var structure = CivilObjectUtils.GetRequiredObject<Structure>(transaction, objectId, openMode);
      if (string.Equals(structure.Name, structureName, StringComparison.OrdinalIgnoreCase))
      {
        return structure;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Structure '{structureName}' was not found in network '{CivilObjectUtils.GetName(network)}'.");
  }

  private static Dictionary<string, object?> ToPipeNetworkSummary(Network network, Transaction transaction)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = network.Name,
      ["handle"] = CivilObjectUtils.GetHandle(network),
      ["pipeCount"] = network.GetPipeIds().Count,
      ["structureCount"] = network.GetStructureIds().Count,
      ["surface"] = ResolveObjectName(transaction, ReferenceSurfaceOrNull(network)),
    };
  }

  /// <summary>C110: the network's reference surface, or ObjectId.Null when it has none (Civil throws then).</summary>
  internal static ObjectId ReferenceSurfaceOrNull(Network network)
  {
    try
    {
      return network.ReferenceSurfaceId;
    }
    catch (Autodesk.Civil.CivilException)
    {
      return ObjectId.Null;
    }
  }

  private static Dictionary<string, object?> ToPipeNetworkDetail(Network network, Transaction transaction)
  {
    // A network built by network_build failed here with CivilException "Retrieve attribute failed"
    // (2026-10-06) while get_pipe/get_structure on its parts worked; read every field and part
    // defensively and report a failing part as an error entry instead of failing the whole call.
    var pipes = network.GetPipeIds().Cast<ObjectId>()
      .Select(objectId => ReadPart(objectId, () => ToPipeData(CivilObjectUtils.GetRequiredObject<Pipe>(transaction, objectId, OpenMode.ForRead), transaction)))
      .ToList();
    var structures = network.GetStructureIds().Cast<ObjectId>()
      .Select(objectId => ReadPart(objectId, () => ToStructureData(CivilObjectUtils.GetRequiredObject<Structure>(transaction, objectId, OpenMode.ForRead), transaction)))
      .ToList();

    return new Dictionary<string, object?>
    {
      ["name"] = network.Name,
      ["handle"] = CivilObjectUtils.GetHandle(network),
      ["partsList"] = ReadField(() => ResolveObjectName(transaction, network.PartsListId)),
      ["style"] = ReadField(() => network.StyleName),
      ["referenceSurface"] = ReadField(() => ResolveObjectName(transaction, network.ReferenceSurfaceId)),
      ["referenceAlignment"] = ReadField(() => ResolveObjectName(transaction, network.ReferenceAlignmentId)),
      ["pipes"] = pipes,
      ["structures"] = structures,
    };
  }

  private static string? ReadField(Func<string?> read)
  {
    try
    {
      return read();
    }
    catch (Exception)
    {
      return null;
    }
  }

  private static Dictionary<string, object?> ReadPart(ObjectId objectId, Func<Dictionary<string, object?>> read)
  {
    try
    {
      return read();
    }
    catch (Exception ex)
    {
      return new Dictionary<string, object?> { ["handle"] = objectId.Handle.ToString(), ["error"] = ex.Message };
    }
  }

  private static double? TryRound(Func<double> read)
  {
    try { return Math.Round(read(), 3); } catch { return null; }
  }

  private static Dictionary<string, object?> ToPipeData(Pipe pipe, Transaction transaction)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = pipe.Name,
      ["handle"] = CivilObjectUtils.GetHandle(pipe),
      ["startStructure"] = ResolveObjectName(transaction, pipe.StartStructureId),
      ["endStructure"] = ResolveObjectName(transaction, pipe.EndStructureId),
      ["length"] = pipe.Length3D,
      ["diameter"] = pipe.InnerDiameterOrWidth,
      // C112: outer diameter, size name, description and family for material texts (the package bands' material rows, V3)
      ["outerDiameter"] = pipe.OuterDiameterOrWidth,
      ["partSizeName"] = ReadField(() => pipe.PartSizeName),
      ["description"] = ReadField(() => pipe.Description),
      ["partFamily"] = ReadField(() => pipe.PartFamilyName),
      // C115: the style, for choosing the legend rows (M35, V1)
      ["style"] = ReadField(() => pipe.StyleName),
      ["slope"] = pipe.Slope,
      ["material"] = pipe.Material,
      ["startPoint"] = new[] { pipe.StartPoint.X, pipe.StartPoint.Y, pipe.StartPoint.Z },
      ["endPoint"] = new[] { pipe.EndPoint.X, pipe.EndPoint.Y, pipe.EndPoint.Z },
      ["centerlineStartElevation"] = pipe.StartPoint.Z,
      ["centerlineEndElevation"] = pipe.EndPoint.Z,
      // C203: inverts, crowns and cover (Civil's CoverOfStartPoint/CoverOfEndpoint, against the reference surface)
      ["invertStart"] = Math.Round(pipe.StartPoint.Z - pipe.InnerDiameterOrWidth / 2, 4),
      ["invertEnd"] = Math.Round(pipe.EndPoint.Z - pipe.InnerDiameterOrWidth / 2, 4),
      ["crownStart"] = Math.Round(pipe.StartPoint.Z + pipe.OuterDiameterOrWidth / 2, 4),
      ["crownEnd"] = Math.Round(pipe.EndPoint.Z + pipe.OuterDiameterOrWidth / 2, 4),
      ["coverStart"] = TryRound(() => pipe.CoverOfStartPoint),
      ["coverEnd"] = TryRound(() => pipe.CoverOfEndpoint),
    };
  }

  private static Dictionary<string, object?> ToStructureData(Structure structure, Transaction transaction)
  {
    var connectedPipeIds = Enumerable.Range(0, structure.ConnectedPipesCount)
      .Select(index => Civil3DCompatibility.GetIndexedPropertyValue(structure, "ConnectedPipe", index))
      .OfType<ObjectId>();

    return new Dictionary<string, object?>
    {
      ["name"] = structure.Name,
      ["handle"] = CivilObjectUtils.GetHandle(structure),
      ["type"] = structure.PartType.ToString(),
      ["style"] = ReadField(() => structure.StyleName),   // C115
      ["partFamily"] = ReadField(() => structure.PartFamilyName),
      ["partSizeName"] = ReadField(() => structure.PartSizeName),   // C171
      ["innerDiameterOrWidthMm"] = ((Func<double?>)(() => { try { return Math.Round(structure.InnerDiameterOrWidth * 1000, 1); } catch (System.Exception) { return null; } }))(),   // C171
      ["rimElevation"] = structure.RimElevation,
      ["sumpElevation"] = structure.SumpElevation,
      ["x"] = structure.Location.X,
      ["y"] = structure.Location.Y,
      ["connectedPipes"] = connectedPipeIds.Select(objectId => ResolveObjectName(transaction, objectId) ?? objectId.Handle.ToString()).ToList(),
    };
  }

  private static IEnumerable<ObjectId> GetChildObjectIds(object owner, params string[] memberNames)
  {
    foreach (var memberName in memberNames)
    {
      object? memberValue = CivilObjectUtils.InvokeMethod(owner, memberName) ?? GetNamedMemberValue(owner, memberName);
      foreach (var objectId in ToObjectIdsFlexible(memberValue))
      {
        if (objectId != ObjectId.Null)
        {
          yield return objectId;
        }
      }
    }
  }

  private static object? GetNamedMemberValue(object? value, string memberName)
  {
    return Civil3DCompatibility.GetPropertyValue(value, memberName)
      ?? Civil3DCompatibility.GetFieldValue(value, memberName);
  }

  private static string? ResolveObjectName(Transaction transaction, ObjectId objectId)
  {
    if (objectId == ObjectId.Null)
    {
      return null;
    }

    try
    {
      var dbObject = transaction.GetObject(objectId, OpenMode.ForRead);
      return CivilObjectUtils.GetName(dbObject);
    }
    catch
    {
      return null;
    }
  }

  private static ObjectId GetAnyObjectId(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var objectId = CivilObjectUtils.GetPropertyValue<ObjectId>(value, propertyName);
      if (objectId != ObjectId.Null)
      {
        return objectId;
      }
    }

    return ObjectId.Null;
  }

  private static double? GetAnyDouble(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var propertyValue = CivilObjectUtils.GetPropertyValue<double?>(value, propertyName);
      if (propertyValue.HasValue)
      {
        return propertyValue.Value;
      }
    }

    return null;
  }

  private static string? GetAnyString(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var propertyValue = CivilObjectUtils.GetStringProperty(value, propertyName);
      if (!string.IsNullOrWhiteSpace(propertyValue))
      {
        return propertyValue;
      }
    }

    return null;
  }

  private static Point3d? GetPointProperty(object? value, params string[] propertyNames)
  {
    foreach (var propertyName in propertyNames)
    {
      var propertyValue = CivilObjectUtils.GetPropertyValue<Point3d?>(value, propertyName);
      if (propertyValue.HasValue)
      {
        return propertyValue.Value;
      }
    }

    return null;
  }

  private static double Distance(Point3d? startPoint, Point3d? endPoint)
  {
    if (!startPoint.HasValue || !endPoint.HasValue)
    {
      return 0.0;
    }

    return startPoint.Value.DistanceTo(endPoint.Value);
  }

  private static double CalculateSlope(Point3d? startPoint, Point3d? endPoint)
  {
    if (!startPoint.HasValue || !endPoint.HasValue)
    {
      return 0.0;
    }

    var horizontal = Math.Sqrt(Math.Pow(endPoint.Value.X - startPoint.Value.X, 2) + Math.Pow(endPoint.Value.Y - startPoint.Value.Y, 2));
    if (horizontal <= 1.0e-9)
    {
      return 0.0;
    }

    return (endPoint.Value.Z - startPoint.Value.Z) / horizontal;
  }

  private static Point3d? ReadPoint(JsonObject? parameters, string parameterName)
  {
    if (PluginRuntime.GetParameter(parameters, parameterName) is not JsonObject pointNode)
    {
      return null;
    }

    var x = pointNode["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{parameterName}.x is required.");
    var y = pointNode["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{parameterName}.y is required.");
    var z = pointNode["z"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{parameterName}.z is required; elevation 0 will not be assumed.");
    return new Point3d(x, y, z);
  }

  private static ObjectId FindStyleId(object civilDoc, Transaction transaction, string styleName, params string[] collectionNames)
  {
    var styles = GetNamedMemberValue(civilDoc, "Styles");
    if (styles == null)
    {
      return ObjectId.Null;
    }

    foreach (var collectionName in collectionNames)
    {
      var collection = GetNamedMemberValue(styles, collectionName);
      foreach (var objectId in CivilObjectUtils.ToObjectIds(collection))
      {
        if (objectId == ObjectId.Null)
        {
          continue;
        }

        var style = transaction.GetObject(objectId, OpenMode.ForRead);
        if (string.Equals(CivilObjectUtils.GetName(style), styleName, StringComparison.OrdinalIgnoreCase))
        {
          return objectId;
        }
      }
    }

    return ObjectId.Null;
  }
}
