using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

/// <summary>C226 (F5, 2026-11-24): network parts locked in the drawing against recomputation (a design decision of
/// 2026-11-24: network parts are locked in the drawing). The lock is XData "C3DMCP_LOCK" on the
/// pipe or structure (note, time); syncPipeNetwork, gradeGravityNetwork, moveStructures, splitBends,
/// addServiceConnection and trimPipesToStructures leave locked parts as they are and report them under locked.
/// Use case: parts the user had edited by hand were protected with name lists in each script and were lost when
/// another script rebuilt the network.</summary>
public static partial class PipeNetworkCommands
{
  private const string LockApp = "C3DMCP_LOCK";

  internal sealed record PartLock(string Note, string At);

  /// <summary>The lock on a network part, null when it is not locked.</summary>
  internal static PartLock? ReadLock(DBObject part)
  {
    using var buffer = part.GetXDataForApplication(LockApp);
    if (buffer == null) return null;
    var v = buffer.AsArray();
    if (v.Length < 2) return null;
    return new PartLock(v[1].Value as string ?? "", v.Length > 2 ? v[2].Value as string ?? "" : "");
  }

  internal static bool IsLocked(DBObject part) => ReadLock(part) != null;

  private static void WriteLock(Database database, Transaction transaction, DBObject part, string note)
  {
    XDataApps.Ensure(database, transaction, LockApp);
    if (!part.IsWriteEnabled) part.UpgradeOpen();
    using var buffer = new ResultBuffer(
      new TypedValue((int)DxfCode.ExtendedDataRegAppName, LockApp),
      new TypedValue((int)DxfCode.ExtendedDataAsciiString, note.Length > 250 ? note[..250] : note),
      new TypedValue((int)DxfCode.ExtendedDataAsciiString, DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
    part.XData = buffer;   // replaces only this application's XData (links and script ids stay)
  }

  private static void ClearLock(DBObject part)
  {
    if (ReadLock(part) == null) return;
    if (!part.IsWriteEnabled) part.UpgradeOpen();
    using var buffer = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, LockApp));   // the app name alone removes it
    part.XData = buffer;
  }

  /// <summary>One entry of the locked list in the answers: name, kind, note and what the command left undone.</summary>
  private static Dictionary<string, object?> LockedEntry(Part part, string skipped, IEnumerable<string>? wouldChange = null)
  {
    var l = ReadLock(part);
    var d = new Dictionary<string, object?>
    {
      ["name"] = part.Name, ["kind"] = part is Pipe ? "pipe" : "structure", ["note"] = l?.Note, ["skipped"] = skipped,
    };
    if (wouldChange != null) d["wouldChange"] = wouldChange.ToList();
    return d;
  }

  /// <summary>C226: locks or unlocks network parts: {network, parts: [names], locked=true, note?, dryRun?}. All names are
  /// checked first (one unknown name and nothing is changed). Answer: locked, unlocked, unchanged.</summary>
  public static Task<object?> SetPartLockAsync(JsonObject? parameters)
  {
    var networkName = PluginRuntime.GetOptionalString(parameters, "network") ?? PluginRuntime.GetRequiredString(parameters, "networkName");
    var names = OptionalStringList(parameters, "parts");
    if (names == null || names.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "parts must list at least one pipe or structure name. Nothing was changed.");
    var locked = PluginRuntime.GetOptionalBool(parameters, "locked") ?? true;
    var note = PluginRuntime.GetOptionalString(parameters, "note") ?? "";
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
      var all = network.GetPipeIds().Cast<ObjectId>().Concat(network.GetStructureIds().Cast<ObjectId>())
        .Select(id => (Part)transaction.GetObject(id, OpenMode.ForRead)).ToList();
      var missing = names.Where(n => !all.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
      if (missing.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Not in '{network.Name}': {string.Join(", ", missing)}. Nothing was changed.");
      var changed = new List<string>();
      var unchanged = new List<string>();
      foreach (var n in names)
      {
        var part = all.First(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase));
        var cur = ReadLock(part);
        var same = locked ? cur != null && cur.Note == note : cur == null;
        if (same) { unchanged.Add(part.Name); continue; }
        changed.Add(part.Name);
        if (dryRun) continue;
        if (locked) WriteLock(database, transaction, part, note); else ClearLock(part);
      }
      return new Dictionary<string, object?>
      {
        ["network"] = network.Name, ["dryRun"] = dryRun, [locked ? "locked" : "unlocked"] = changed, ["unchanged"] = unchanged, ["note"] = locked ? note : null,
      };
    });
  }

  /// <summary>C226: the locked parts of the given networks (default all): network, part, kind, note, lockedAt.</summary>
  public static Task<object?> GetPartLocksAsync(JsonObject? parameters)
  {
    var networks = OptionalStringList(parameters, "networks");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var names = networks is { Count: > 0 } ? networks : ListPipeNetworkNames(civilDoc, transaction);
      var locks = new List<Dictionary<string, object?>>();
      foreach (var nn in names)
      {
        var network = FindPipeNetworkByName(civilDoc, transaction, nn, OpenMode.ForRead);
        foreach (var id in network.GetPipeIds().Cast<ObjectId>().Concat(network.GetStructureIds().Cast<ObjectId>()))
        {
          var part = (Part)transaction.GetObject(id, OpenMode.ForRead);
          var l = ReadLock(part);
          if (l == null) continue;
          locks.Add(new Dictionary<string, object?> { ["network"] = network.Name, ["part"] = part.Name, ["kind"] = part is Pipe ? "pipe" : "structure",
            ["note"] = l.Note, ["lockedAt"] = l.At });
        }
      }
      return new Dictionary<string, object?> { ["locks"] = locks, ["count"] = locks.Count };
    });
  }
}
