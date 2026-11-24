using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.Runtime;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

/// <summary>
/// Assembly operations implemented with the documented Civil 3D 2026 API.
/// Dynamic stock-subassembly parameters are the only compatibility access.
/// </summary>
public static class AssemblyCreationCommands
{
  public static Task<object?> ListAssembliesAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var usage = GetCorridorUsage(civilDoc, transaction);
      var assemblies = civilDoc.AssemblyCollection
        .Select(id => CivilObjectUtils.GetRequiredObject<Assembly>(transaction, id, OpenMode.ForRead))
        .Select(assembly =>
        {
          var summary = ToAssemblySummary(assembly);
          summary["usedByCorridors"] = usage.TryGetValue(assembly.ObjectId, out var corridors)
            ? corridors
            : new List<string>();
          return summary;
        })
        .ToList();

      return new Dictionary<string, object?> { ["assemblies"] = assemblies };
    });
  }

  public static Task<object?> GetAssemblyAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var verbose = PluginRuntime.GetOptionalBool(parameters, "verbose") ?? false;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assembly = FindAssemblyByName(civilDoc, transaction, name, OpenMode.ForRead);
      var usedByCorridors = GetCorridorUsage(civilDoc, transaction).TryGetValue(assembly.ObjectId, out var corridors)
        ? corridors
        : new List<string>();

      return new Dictionary<string, object?>
      {
        ["name"] = assembly.Name,
        ["handle"] = CivilObjectUtils.GetHandle(assembly),
        ["subassemblyCount"] = GetSubassemblyIds(assembly).Count,
        ["style"] = GetStyleName(assembly, transaction),
        ["type"] = assembly.Type.ToString(),
        ["subassemblies"] = GetSubassemblies(assembly, transaction, verbose),
        ["usedByCorridors"] = usedByCorridors,
      };
    });
  }

  public static Task<object?> CreateAssemblyAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var insertX = PluginRuntime.GetRequiredDouble(parameters, "insertX");
    var insertY = PluginRuntime.GetRequiredDouble(parameters, "insertY");
    var description = PluginRuntime.GetOptionalString(parameters, "description") ?? string.Empty;
    var assemblyTypeText = PluginRuntime.GetRequiredString(parameters, "assemblyType");
    if (!Enum.TryParse<AssemblyType>(assemblyTypeText, ignoreCase: true, out var assemblyType))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Invalid assemblyType '{assemblyTypeText}'. Use {string.Join(", ", Enum.GetNames<AssemblyType>())}.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assemblyId = civilDoc.AssemblyCollection.Add(name, assemblyType, new Point3d(insertX, insertY, 0));
      var assembly = CivilObjectUtils.GetRequiredObject<Assembly>(transaction, assemblyId, OpenMode.ForWrite);
      assembly.Description = description;

      return new Dictionary<string, object?>
      {
        ["name"] = assembly.Name,
        ["handle"] = CivilObjectUtils.GetHandle(assembly),
        ["insertX"] = insertX,
        ["insertY"] = insertY,
        ["assemblyType"] = assembly.Type.ToString(),
        ["created"] = true,
      };
    });
  }

  public static Task<object?> CreateSubassemblyAsync(JsonObject? parameters)
  {
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var subassemblyType = PluginRuntime.GetRequiredString(parameters, "subassemblyType");
    var side = PluginRuntime.GetRequiredString(parameters, "side");
    if (!new[] { "Left", "Right", "Both", "None" }.Contains(side, StringComparer.OrdinalIgnoreCase))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "side must be Left, Right, Both, or None.");

    var subParams = ReadParameters(parameters?["parameters"] as JsonObject);
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assembly = FindAssemblyByName(civilDoc, transaction, assemblyName, OpenMode.ForWrite);
      // "None" = symmetric subassembly without a Side parameter (e.g. TrenchPipe1).
      string?[] requestedSides = side.ToLowerInvariant() switch
      {
        "both" => new string?[] { "Left", "Right" },
        "none" => new string?[] { null },
        _ => new string?[] { side },
      };
      var created = new List<Dictionary<string, object?>>();

      foreach (var requestedSide in requestedSides)
      {
        var subassemblyName = $"{subassemblyType}-{requestedSide ?? "None"}-{Guid.NewGuid():N}";
        var subassemblyId = civilDoc.SubassemblyCollection.ImportStockSubassembly(
          subassemblyName,
          subassemblyType,
          assembly.Location);
        assembly.AddSubassembly(subassemblyId);

        var subassembly = CivilObjectUtils.GetRequiredObject<Subassembly>(transaction, subassemblyId, OpenMode.ForWrite);
        if (requestedSide == null)
        {
          if (subassembly.HasSide)
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.INVALID_INPUT",
              $"Stock subassembly '{subassemblyType}' has a Side parameter; use side Left, Right, or Both. The transaction was not committed.");
          }
        }
        else
        {
          if (!subassembly.HasSide)
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.INVALID_INPUT",
              $"Stock subassembly '{subassemblyType}' is symmetric and has no Side parameter; use side None. The transaction was not committed.");
          }
          subassembly.Side = Enum.Parse<SubassemblySideType>(requestedSide, ignoreCase: true);
        }

        var updated = ApplySubassemblyParameters(subassembly, subParams);
        if (updated.Count != subParams.Count)
        {
          var missing = subParams.Keys.Except(updated, StringComparer.OrdinalIgnoreCase);
          throw new JsonRpcDispatchException(
            "CIVIL3D.INVALID_INPUT",
            $"Subassembly parameters were not writable on '{subassemblyType}': {string.Join(", ", missing)}. The transaction was not committed.");
        }

        created.Add(new Dictionary<string, object?>
        {
          ["name"] = CivilObjectUtils.GetName(subassembly) ?? subassemblyName,
          ["handle"] = CivilObjectUtils.GetHandle(subassembly),
          ["side"] = requestedSide ?? "None",
          ["stockParameters"] = ReadStockParameters(subassembly),
        });
      }

      return new Dictionary<string, object?>
      {
        ["assemblyName"] = assemblyName,
        ["subassemblyType"] = subassemblyType,
        ["subassemblies"] = created,
        ["added"] = true,
      };
    });
  }

  public static Task<object?> EditAssemblyAsync(JsonObject? parameters)
  {
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var subassemblyName = PluginRuntime.GetOptionalString(parameters, "subassemblyName");
    var deleteSubassembly = PluginRuntime.GetOptionalBool(parameters, "delete") ?? false;
    var editParameters = ReadParameters(parameters?["parameters"] as JsonObject);

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var openMode = deleteSubassembly || editParameters.Count > 0 ? OpenMode.ForWrite : OpenMode.ForRead;
      var assembly = FindAssemblyByName(civilDoc, transaction, assemblyName, openMode);
      var subassemblyIds = GetSubassemblyIds(assembly);

      if (string.IsNullOrWhiteSpace(subassemblyName))
      {
        var subassemblies = subassemblyIds
          .Select(id => CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead))
          .Select(ToSubassemblySummary)
          .ToList();
        return new Dictionary<string, object?>
        {
          ["assemblyName"] = assemblyName,
          ["subassemblyCount"] = subassemblies.Count,
          ["subassemblies"] = subassemblies,
        };
      }

      var targetId = subassemblyIds.FirstOrDefault(id =>
      {
        var candidate = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead);
        return string.Equals(CivilObjectUtils.GetName(candidate), subassemblyName, StringComparison.OrdinalIgnoreCase);
      });
      if (targetId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Subassembly '{subassemblyName}' not found in assembly '{assemblyName}'.");

      var target = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, targetId, OpenMode.ForWrite);
      if (deleteSubassembly)
      {
        target.Erase();
        return new Dictionary<string, object?>
        {
          ["assemblyName"] = assemblyName,
          ["subassemblyName"] = subassemblyName,
          ["deleted"] = true,
        };
      }

      var updated = ApplySubassemblyParameters(target, editParameters);
      if (editParameters.Count > 0 && updated.Count != editParameters.Count)
      {
        var missing = editParameters.Keys.Except(updated, StringComparer.OrdinalIgnoreCase);
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          $"Subassembly parameters were not writable: {string.Join(", ", missing)}. The transaction was not committed.");
      }

      return new Dictionary<string, object?>
      {
        ["assemblyName"] = assemblyName,
        ["subassemblyName"] = subassemblyName,
        ["updatedParameters"] = updated,
        ["updated"] = updated.Count > 0,
      };
    });
  }

  private static Dictionary<string, object?> ReadParameters(JsonObject? values)
  {
    var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    if (values == null) return result;
    foreach (var pair in values)
      result[pair.Key] = ToPrimitive(pair.Value);
    return result;
  }

  // JsonValue.GetValue<object>() yields a JsonElement, which IConvertible-based setters reject.
  private static object? ToPrimitive(JsonNode? node)
  {
    if (node is not JsonValue value) return node?.ToJsonString();
    if (value.TryGetValue<bool>(out var b)) return b;
    if (value.TryGetValue<double>(out var d)) return d;
    if (value.TryGetValue<string>(out var s)) return s;
    return value.ToJsonString();
  }

  internal static Assembly FindAssemblyByName(
    CivilDocument civilDocument,
    Transaction transaction,
    string name,
    OpenMode openMode)
  {
    foreach (ObjectId id in civilDocument.AssemblyCollection)
    {
      var assembly = CivilObjectUtils.GetRequiredObject<Assembly>(transaction, id, openMode);
      if (string.Equals(assembly.Name, name, StringComparison.OrdinalIgnoreCase))
        return assembly;
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Assembly '{name}' was not found in the drawing.");
  }

  private static List<ObjectId> GetSubassemblyIds(Assembly assembly)
  {
    return assembly.Groups
      .SelectMany(group => group.GetSubassemblyIds().Cast<ObjectId>())
      .Where(id => !id.IsNull)
      .Distinct()
      .ToList();
  }

  private static List<string> ApplySubassemblyParameters(AcDbObject subassembly, IReadOnlyDictionary<string, object?> parameters)
  {
    var updated = new List<string>();
    foreach (var pair in parameters)
    {
      // Stock subassembly inputs (Width, Slope, ...) live in the Params* collections,
      // not as CLR properties; try those first, then fall back to object properties.
      if (subassembly is Subassembly sa && TrySetStockParameter(sa, pair.Key, pair.Value))
        updated.Add(pair.Key);
      else if (Civil3DCompatibility.TrySetProperty(subassembly, pair.Key, pair.Value))
        updated.Add(pair.Key);
    }
    return updated;
  }

  private static bool TrySetStockParameter(Subassembly sa, string key, object? value)
  {
    foreach (ParamDouble p in sa.ParamsDouble)
    {
      if (!string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
      p.Value = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
      return true;
    }
    foreach (ParamLong p in sa.ParamsLong)
    {
      if (!string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
      p.Value = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
      return true;
    }
    foreach (ParamBool p in sa.ParamsBool)
    {
      if (!string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
      p.Value = Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
      return true;
    }
    foreach (ParamString p in sa.ParamsString)
    {
      if (!string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
      p.Value = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
      return true;
    }
    return false;
  }

  private static Dictionary<string, object?> ReadStockParameters(Subassembly sa)
  {
    var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    foreach (ParamDouble p in sa.ParamsDouble) values[p.Key] = p.Value;
    foreach (ParamLong p in sa.ParamsLong) values[p.Key] = p.Value;
    foreach (ParamBool p in sa.ParamsBool) values[p.Key] = p.Value;
    foreach (ParamString p in sa.ParamsString) values[p.Key] = p.Value;
    return values;
  }

  private static string? GetStyleName(Assembly assembly, Transaction transaction)
  {
    if (assembly.StyleId.IsNull) return null;
    return CivilObjectUtils.GetName(transaction.GetObject(assembly.StyleId, OpenMode.ForRead));
  }

  private static List<Dictionary<string, object?>> GetSubassemblies(Assembly assembly, Transaction transaction, bool verbose)
  {
    return GetSubassemblyIds(assembly)
      .Select(id => CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead))
      .Select(subassembly => ToSubassemblySummary(subassembly, verbose))
      .ToList();
  }

  private static Dictionary<string, object?> ToSubassemblySummary(AcDbObject subassembly) =>
    ToSubassemblySummary(subassembly, verbose: false);

  private static Dictionary<string, object?> ToSubassemblySummary(AcDbObject subassembly, bool verbose)
  {
    var summary = new Dictionary<string, object?>
    {
      ["name"] = CivilObjectUtils.GetName(subassembly) ?? subassembly.Handle.ToString(),
      ["handle"] = CivilObjectUtils.GetHandle(subassembly),
      ["type"] = subassembly.GetType().Name,
      ["className"] = subassembly.GetType().Name,
      ["side"] = subassembly is Subassembly typed
        ? (typed.HasSide ? typed.Side.ToString().ToLowerInvariant() : "none")
        : "none",
      ["stockParameters"] = subassembly is Subassembly sa ? ReadStockParameters(sa) : null,
    };
    // The reflected .NET property dump (~80 entries) is opt-in.
    if (verbose)
      summary["parameters"] = Civil3DCompatibility.GetReadableScalarProperties(subassembly);
    return summary;
  }

  private static Dictionary<ObjectId, List<string>> GetCorridorUsage(CivilDocument civilDoc, Transaction transaction)
  {
    var usage = new Dictionary<ObjectId, List<string>>();
    foreach (ObjectId corridorId in civilDoc.CorridorCollection)
    {
      var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForRead);
      foreach (Baseline baseline in corridor.Baselines)
      {
        foreach (BaselineRegion region in baseline.BaselineRegions)
        {
          if (!usage.TryGetValue(region.AssemblyId, out var names))
            usage[region.AssemblyId] = names = new List<string>();
          if (!names.Contains(corridor.Name))
            names.Add(corridor.Name);
        }
      }
    }
    return usage;
  }

  private static Dictionary<string, object?> ToAssemblySummary(Assembly assembly)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = assembly.Name,
      ["handle"] = CivilObjectUtils.GetHandle(assembly),
      ["subassemblyCount"] = GetSubassemblyIds(assembly).Count,
      ["type"] = assembly.Type.ToString(),
    };
  }
}
