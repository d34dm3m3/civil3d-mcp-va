using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Block references by handle (C75): insertion point, scale, rotation and dynamic block properties (visibility
/// states, lookups, distances, flips), read and set. Use case: the company template's A2–A4 and A3L frames were inserted
/// squeezed (scale 1.02 x 0.91–0.96, off the sheet origin) to fit a printer's 17 mm margins, so a printed A3 frame
/// was 6 % too low; the scale bar is a dynamic block with one state per scale.
/// Moves and rotations go through TransformBy so attribute references follow the block.
/// </summary>
public static class BlockReferenceCommands
{
  public static Task<object?> GetBlockReferenceAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      Describe(Find(database, transaction, handle), transaction));
  }

  public static Task<object?> SetBlockReferenceAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "handle");
    var x = PluginRuntime.GetOptionalDouble(parameters, "x");
    var y = PluginRuntime.GetOptionalDouble(parameters, "y");
    var scaleX = PluginRuntime.GetOptionalDouble(parameters, "scaleX");
    var scaleY = PluginRuntime.GetOptionalDouble(parameters, "scaleY");
    var rotation = PluginRuntime.GetOptionalDouble(parameters, "rotationDegrees");
    var dynamic = PluginRuntime.GetParameter(parameters, "dynamicProperties") as JsonObject;
    if ((x == null) != (y == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give x and y together. Nothing was changed.");
    if (scaleX is 0 || scaleY is 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "scaleX and scaleY must not be 0. Nothing was changed.");
    if (x == null && scaleX == null && scaleY == null && rotation == null && (dynamic == null || dynamic.Count == 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give x/y, scaleX, scaleY, rotationDegrees or dynamicProperties. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var reference = Find(database, transaction, handle);
      var before = Describe(reference, transaction);
      var hasAttributes = reference.AttributeCollection.Count > 0;
      var current = reference.ScaleFactors;
      var newX = scaleX ?? current.X;
      var newY = scaleY ?? current.Y;
      var scaling = Math.Abs(newX - current.X) > 1e-12 || Math.Abs(newY - current.Y) > 1e-12;
      // Attribute text cannot be scaled non-uniformly: a reference with attributes is only scaled by one factor.
      if (scaling && hasAttributes && Math.Abs(newX / current.X - newY / current.Y) > 1e-9)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          "This block has attributes; scale it by the same factor in x and y. Nothing was changed.");

      // Dynamic properties are checked completely before anything is set.
      var properties = new List<(string Name, object Value)>();
      if (dynamic != null && dynamic.Count > 0)
      {
        if (!reference.IsDynamicBlock)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{handle} is not a dynamic block. Nothing was changed.");
        var collection = reference.DynamicBlockReferencePropertyCollection.Cast<DynamicBlockReferenceProperty>().ToList();
        foreach (var (name, node) in dynamic)
        {
          var property = collection.FirstOrDefault(p => string.Equals(p.PropertyName, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"Dynamic property '{name}' not found. Available: {string.Join(", ", collection.Where(p => p.Show).Select(p => p.PropertyName))}. Nothing was changed.");
          if (property.ReadOnly)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Dynamic property '{property.PropertyName}' is read-only. Nothing was changed.");
          var value = Convert(property, node, name);
          var allowed = property.GetAllowedValues();
          if (allowed.Length > 0 && !allowed.Any(a => Equals(a, value) || string.Equals(a?.ToString(), value.ToString(), StringComparison.OrdinalIgnoreCase)))
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"'{node}' is not allowed for '{property.PropertyName}'. Allowed: {string.Join(", ", allowed.Select(a => a?.ToString()))}. Nothing was changed.");
          if (allowed.Length > 0 && value is string text)
            value = allowed.First(a => string.Equals(a?.ToString(), text, StringComparison.OrdinalIgnoreCase))!;
          properties.Add((property.PropertyName, value));
        }
      }

      reference.UpgradeOpen();
      if (scaling)
      {
        if (hasAttributes) reference.TransformBy(Matrix3d.Scaling(newX / current.X, reference.Position));
        else reference.ScaleFactors = new Scale3d(newX, newY, current.Z);
      }
      if (rotation != null)
      {
        var delta = rotation.Value * Math.PI / 180.0 - reference.Rotation;
        if (Math.Abs(delta) > 1e-12) reference.TransformBy(Matrix3d.Rotation(delta, reference.Normal, reference.Position));
      }
      if (x != null)
        reference.TransformBy(Matrix3d.Displacement(new Point3d(x.Value, y!.Value, reference.Position.Z) - reference.Position));
      // The properties are fetched again from the reference now open for write.
      if (properties.Count > 0)
      {
        var writable = reference.DynamicBlockReferencePropertyCollection.Cast<DynamicBlockReferenceProperty>().ToList();
        foreach (var (name, value) in properties) writable.First(p => p.PropertyName == name).Value = value;
      }

      return new Dictionary<string, object?> { ["before"] = before, ["after"] = Describe(reference, transaction) };
    });
  }

  private static object Convert(DynamicBlockReferenceProperty property, JsonNode? node, string name)
  {
    if (node is not JsonValue value)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Dynamic property '{name}' needs a single value. Nothing was changed.");
    var element = value.GetValue<JsonElement>();
    try
    {
      return property.Value switch
      {
        string => element.ValueKind == JsonValueKind.String ? element.GetString()! : element.ToString(),
        double => element.ValueKind == JsonValueKind.String ? double.Parse(element.GetString()!, CultureInfo.InvariantCulture) : element.GetDouble(),
        short => element.ValueKind == JsonValueKind.True ? (short)1 : element.ValueKind == JsonValueKind.False ? (short)0 : (short)element.GetInt32(),
        int => element.GetInt32(),
        _ => element.ValueKind == JsonValueKind.String ? element.GetString()! : element.ToString(),
      };
    }
    catch (Exception ex) when (ex is FormatException or InvalidOperationException)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"'{element}' is not a valid {property.Value?.GetType().Name} for '{property.PropertyName}'. Nothing was changed.");
    }
  }

  private static BlockReference Find(Database database, Transaction transaction, string handle)
  {
    if (!long.TryParse(handle, NumberStyles.HexNumber, null, out var value)
      || !database.TryGetObjectId(new Handle(value), out var id) || id.IsErased
      || transaction.GetObject(id, OpenMode.ForRead) is not BlockReference reference || reference is Table)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No block reference with handle {handle}. Nothing was changed.");
    return reference;
  }

  private static Dictionary<string, object?> Describe(BlockReference reference, Transaction transaction)
  {
    var record = (BlockTableRecord)transaction.GetObject(reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord, OpenMode.ForRead);
    var owner = (BlockTableRecord)transaction.GetObject(reference.OwnerId, OpenMode.ForRead);
    var space = owner.IsLayout ? ((Layout)transaction.GetObject(owner.LayoutId, OpenMode.ForRead)).LayoutName : owner.Name;
    var dynamic = reference.IsDynamicBlock
      ? reference.DynamicBlockReferencePropertyCollection.Cast<DynamicBlockReferenceProperty>()
        .Where(p => p.Show && !string.Equals(p.PropertyName, "Origin", StringComparison.OrdinalIgnoreCase))
        .Select(p => new Dictionary<string, object?>
        {
          ["name"] = p.PropertyName,
          ["value"] = p.Value is double d ? Math.Round(d, 6) : p.Value?.ToString(),
          ["readOnly"] = p.ReadOnly,
          ["allowed"] = p.GetAllowedValues().Select(a => a is double v ? (object)Math.Round(v, 6) : a?.ToString()).ToList(),
          ["description"] = string.IsNullOrWhiteSpace(p.Description) ? null : p.Description,
        }).ToList()
      : null;
    return new Dictionary<string, object?>
    {
      ["handle"] = reference.Handle.ToString(),
      ["block"] = record.Name,
      ["space"] = space,
      ["layer"] = reference.Layer,
      ["position"] = new[] { Math.Round(reference.Position.X, 4), Math.Round(reference.Position.Y, 4) },
      ["scale"] = new[] { Math.Round(reference.ScaleFactors.X, 6), Math.Round(reference.ScaleFactors.Y, 6) },
      ["rotationDegrees"] = Math.Round(reference.Rotation * 180.0 / Math.PI, 6),
      ["attributes"] = reference.AttributeCollection.Count,
      ["dynamic"] = reference.IsDynamicBlock,
      ["dynamicProperties"] = dynamic,
    };
  }
}
