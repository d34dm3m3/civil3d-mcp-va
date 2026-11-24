using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// What a drawing consists of: model space entities per layer and type (with polyline vertex counts as a
/// size proxy), block definitions and their entities, external references, raster images and the top-level
/// named object dictionary. Read-only; meant for finding what makes a drawing large.
/// </summary>
public static class DrawingStatsCommands
{
  private sealed class LayerTally
  {
    public long Entities;
    public long Vertices;
    public Dictionary<string, long> Types { get; } = new(StringComparer.OrdinalIgnoreCase);
  }

  public static Task<object?> GetDrawingLayerStatsAsync(JsonObject? parameters)
  {
    var limit = PluginRuntime.GetOptionalInt(parameters, "limit") ?? 60;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var layers = new Dictionary<string, LayerTally>(StringComparer.OrdinalIgnoreCase);
      var types = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
      long modelEntities = 0, modelVertices = 0;

      var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
      foreach (ObjectId id in modelSpace)
      {
        var type = id.ObjectClass.DxfName;
        if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity)
        {
          continue;
        }

        var vertices = Vertices(entity);
        if (!layers.TryGetValue(entity.Layer, out var tally))
        {
          tally = new LayerTally();
          layers[entity.Layer] = tally;
        }

        tally.Entities++;
        tally.Vertices += vertices;
        tally.Types[type] = tally.Types.GetValueOrDefault(type) + 1;
        types[type] = types.GetValueOrDefault(type) + 1;
        modelEntities++;
        modelVertices += vertices;
      }

      long blockDefinitions = 0, blockEntities = 0;
      var xrefs = new List<Dictionary<string, object?>>();
      var largestBlocks = new List<(string Name, long Count)>();
      foreach (ObjectId btrId in blockTable)
      {
        var btr = (BlockTableRecord)transaction.GetObject(btrId, OpenMode.ForRead);
        if (btr.IsLayout)
        {
          continue;
        }

        if (btr.IsFromExternalReference)
        {
          xrefs.Add(new Dictionary<string, object?> { ["name"] = btr.Name, ["path"] = btr.PathName, ["isOverlay"] = btr.IsFromOverlayReference });
          continue;
        }

        blockDefinitions++;
        long count = 0;
        foreach (ObjectId _ in btr)
        {
          count++;
        }

        blockEntities += count;
        largestBlocks.Add((btr.Name, count));
      }

      var images = new List<string>();
      var imageDictionaryId = RasterImageDef.GetImageDictionary(database);
      if (!imageDictionaryId.IsNull)
      {
        var imageDictionary = (DBDictionary)transaction.GetObject(imageDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in imageDictionary)
        {
          var path = transaction.GetObject(entry.Value, OpenMode.ForRead) is RasterImageDef def ? def.SourceFileName : null;
          images.Add($"{entry.Key}: {path}");
        }
      }

      var dictionaries = new List<Dictionary<string, object?>>();
      var nod = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
      foreach (DBDictionaryEntry entry in nod)
      {
        var child = transaction.GetObject(entry.Value, OpenMode.ForRead);
        dictionaries.Add(new Dictionary<string, object?>
        {
          ["name"] = entry.Key,
          ["type"] = entry.Value.ObjectClass.DxfName,
          ["entries"] = child is DBDictionary dictionary ? dictionary.Count : null,
        });
      }

      return new Dictionary<string, object?>
      {
        ["fileName"] = Path.GetFileName(database.Filename),
        ["modelSpaceEntities"] = modelEntities,
        ["modelSpaceVertices"] = modelVertices,
        ["layerCount"] = layers.Count,
        ["layers"] = layers
          .OrderByDescending(kv => kv.Value.Entities + kv.Value.Vertices / 10)
          .Take(limit)
          .Select(kv => new Dictionary<string, object?>
          {
            ["layer"] = kv.Key,
            ["entities"] = kv.Value.Entities,
            ["vertices"] = kv.Value.Vertices,
            ["types"] = kv.Value.Types.OrderByDescending(t => t.Value).Take(6).ToDictionary(t => t.Key, t => (object?)t.Value),
          })
          .ToList(),
        ["types"] = types.OrderByDescending(t => t.Value).ToDictionary(t => t.Key, t => (object?)t.Value),
        ["blockDefinitions"] = blockDefinitions,
        ["blockDefinitionEntities"] = blockEntities,
        ["largestBlocks"] = largestBlocks.OrderByDescending(b => b.Count).Take(15)
          .Select(b => new Dictionary<string, object?> { ["name"] = b.Name, ["entities"] = b.Count }).ToList(),
        ["xrefs"] = xrefs,
        ["rasterImages"] = images,
        ["namedObjectDictionary"] = dictionaries,
      };
    });
  }

  private static long Vertices(Entity entity)
  {
    try
    {
      return entity switch
      {
        Polyline polyline => polyline.NumberOfVertices,
        Polyline2d or Polyline3d => (long)Math.Round(((Curve)entity).EndParam) + 1,
        Hatch hatch => hatch.NumberOfLoops,
        _ => 0,
      };
    }
    catch (Autodesk.AutoCAD.Runtime.Exception)
    {
      return 0;
    }
  }
}
