using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace Civil3DMcpPlugin;

/// <summary>
/// C105: an id in XData (application name: the constant App below) on objects that scripts create, so that a rerun finds
/// its own objects and can keep what the user moved by hand. Use case (lesson 19, 2026-10-11): three projects solved it three ways (positions in
/// the code, handle files that are never erased, positions read back from the drawing), and handles change when an
/// object is recreated. XData: 1001 App, 1000 the id (at most 255 characters), then the optional data (for example
/// the position the script last wrote, as JSON) in 1000 strings of at most 250 characters. listEntities reports id and
/// idData and filters on idPrefix (C105); entityIdsSet writes, entityIdsClear removes.
/// </summary>
public static class ScriptIdCommands
{
  internal const string App = "C3DMCP_ID";
  // C205: application names of earlier versions, still read; a write or clear removes them from the object
  internal static readonly string[] LegacyApps = {  };
  private const int Chunk = 250;

  internal static (string? Id, string? Data) Read(DBObject entity)
  {
    foreach (var app in LegacyApps.Prepend(App))
    {
      var r = ReadApp(entity, app);
      if (r.Id != null) return r;
    }
    return (null, null);
  }

  /// <summary>C205: removes the script id under every application name (current and earlier) the object carries; an
  /// application that is not registered in the drawing cannot be written, so only names the object has are cleared.</summary>
  private static bool RemoveIds(DBObject entity, bool keepCurrent)
  {
    var removed = false;
    foreach (var app in keepCurrent ? LegacyApps : LegacyApps.Prepend(App).ToArray())
    {
      using var has = entity.GetXDataForApplication(app);
      if (has == null) continue;
      if (!entity.IsWriteEnabled) entity.UpgradeOpen();
      using var buffer = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, app));   // the app name alone removes its XData
      entity.XData = buffer;
      removed = true;
    }
    return removed;
  }

  private static (string? Id, string? Data) ReadApp(DBObject entity, string app)
  {
    using var buffer = entity.GetXDataForApplication(app);
    if (buffer == null) return (null, null);
    var values = buffer.AsArray();
    if (values.Length < 2 || values[1].TypeCode != (short)DxfCode.ExtendedDataAsciiString) return (null, null);
    var data = values.Length > 2
      ? string.Concat(values.Skip(2).Where(v => v.TypeCode == (short)DxfCode.ExtendedDataAsciiString).Select(v => (string)v.Value))
      : null;
    return ((string)values[1].Value, string.IsNullOrEmpty(data) ? null : data);
  }

  private static void EnsureApp(Database database, Transaction transaction) => XDataApps.Ensure(database, transaction, App);

  public static Task<object?> SetAsync(JsonObject? parameters)
  {
    if (PluginRuntime.GetParameter(parameters, "items") is not JsonArray list || list.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "items must list {handle, id, data?}. Nothing was changed.");
    var items = list.Select((n, i) =>
    {
      if (n is not JsonObject o) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"items[{i}] must be {{handle, id, data?}}. Nothing was changed.");
      var handle = o["handle"]?.GetValue<string>()?.Trim() ?? "";
      var id = o["id"]?.GetValue<string>() ?? "";
      if (handle.Length == 0 || id.Length == 0 || id.Length > 255)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"items[{i}]: handle and id are needed, the id at most 255 characters. Nothing was changed.");
      var data = o["data"] switch { null => null, JsonValue v when v.TryGetValue<string>(out var s) => s, var other => other.ToJsonString() };
      return (Handle: handle, Id: id, Data: data);
    }).ToList();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var entities = items.Select(it => (it, Entity: Find(database, transaction, it.Handle))).ToList();
      EnsureApp(database, transaction);
      var written = new List<Dictionary<string, object?>>();
      foreach (var (it, entity) in entities)
      {
        entity.UpgradeOpen();
        var values = new List<TypedValue> { new((int)DxfCode.ExtendedDataRegAppName, App), new((int)DxfCode.ExtendedDataAsciiString, it.Id) };
        if (it.Data != null)
          for (var i = 0; i < it.Data.Length; i += Chunk)
            values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, it.Data.Substring(i, Math.Min(Chunk, it.Data.Length - i))));
        using var buffer = new ResultBuffer(values.ToArray());
        entity.XData = buffer;
        RemoveIds(entity, keepCurrent: true);   // C205: an id under an earlier application name goes
        written.Add(new Dictionary<string, object?> { ["handle"] = entity.Handle.ToString(), ["id"] = it.Id, ["dataLength"] = it.Data?.Length ?? 0 });
      }
      return new Dictionary<string, object?> { ["written"] = written };
    });
  }

  public static Task<object?> ClearAsync(JsonObject? parameters)
  {
    var handles = (PluginRuntime.GetParameter(parameters, "handles") as JsonArray ?? new JsonArray())
      .Select(n => n?.GetValue<string>()?.Trim() ?? "").Where(h => h.Length > 0).ToList();
    if (handles.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "handles must list at least one handle. Nothing was changed.");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var entities = handles.Select(h => Find(database, transaction, h)).ToList();
      var cleared = new List<string>();
      foreach (var entity in entities)
      {
        if (!RemoveIds(entity, keepCurrent: false)) continue;
        cleared.Add(entity.Handle.ToString());
      }
      return new Dictionary<string, object?> { ["cleared"] = cleared, ["withoutId"] = handles.Count - cleared.Count };
    });
  }

  /// <summary>C114: move drawing objects by handle (dx, dy, dz in drawing units), as MOVE does: TransformBy with a
  /// displacement, so a multileader's arrow and text move together. Use case: the tests of the script ids (C105, V2) simulate
  /// the user moving a flag by hand; tools can move their own objects without recreating them. All handles are checked first.</summary>
  public static Task<object?> MoveAsync(JsonObject? parameters)
  {
    var handles = (PluginRuntime.GetParameter(parameters, "handles") as JsonArray ?? new JsonArray())
      .Select(n => n?.GetValue<string>()?.Trim() ?? "").Where(h => h.Length > 0).Distinct().ToList();
    var dx = PluginRuntime.GetOptionalDouble(parameters, "dx") ?? 0.0;
    var dy = PluginRuntime.GetOptionalDouble(parameters, "dy") ?? 0.0;
    var dz = PluginRuntime.GetOptionalDouble(parameters, "dz") ?? 0.0;
    if (handles.Count == 0 || (dx == 0 && dy == 0 && dz == 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give handles and a non-zero dx, dy or dz. Nothing was moved.");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var entities = handles.Select(h => Find(database, transaction, h)).ToList();
      var move = Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(new Autodesk.AutoCAD.Geometry.Vector3d(dx, dy, dz));
      foreach (var entity in entities)
      {
        entity.UpgradeOpen();
        entity.TransformBy(move);
      }
      return new Dictionary<string, object?> { ["moved"] = entities.Select(e => e.Handle.ToString()).ToList(), ["displacement"] = new[] { dx, dy, dz } };
    });
  }

  private static AcEntity Find(Database database, Transaction transaction, string handle)
  {
    ObjectId id;
    try
    {
      id = database.GetObjectId(false, new Handle(Convert.ToInt64(handle, 16)), 0);
    }
    catch (Exception ex) when (ex is FormatException or OverflowException or Autodesk.AutoCAD.Runtime.Exception)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No object with handle {handle}. Nothing was changed.");
    }
    if (id.IsNull || id.IsErased || transaction.GetObject(id, OpenMode.ForRead) is not AcEntity entity)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Handle {handle} is not a drawing object. Nothing was changed.");
    return entity;
  }
}
