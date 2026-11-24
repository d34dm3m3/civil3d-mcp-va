using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// C98b: layers, linetypes and text styles a style is to refer to, checked against the drawing before anything is
/// written. The drawing's own spelling is returned; an unknown name is refused with the names that exist (in a long
/// table only those containing the given text).
/// </summary>
internal static class DrawingNames
{
  private const int FullListLimit = 40;
  private const int MatchListLimit = 25;

  internal static string Layer(Database database, Transaction transaction, string name, string what) =>
    Find((SymbolTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead), transaction, name, what, "layer");

  /// <summary>A loaded linetype, ByLayer and ByBlock included (both are records of the linetype table).</summary>
  internal static string Linetype(Database database, Transaction transaction, string name, string what) =>
    Find((SymbolTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead), transaction, name, what, "loaded linetype");

  internal static string TextStyle(Database database, Transaction transaction, string name, string what) =>
    Find((SymbolTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead), transaction, name, what, "text style");

  private static string Find(SymbolTable table, Transaction transaction, string name, string what, string kind)
  {
    var wanted = name.Trim();
    var names = new List<string>();
    foreach (ObjectId id in table)
    {
      if (id.IsNull || id.IsErased || transaction.GetObject(id, OpenMode.ForRead) is not SymbolTableRecord record) continue;
      if (string.IsNullOrEmpty(record.Name)) continue;   // shape file entries of the text style table have no name
      if (string.Equals(record.Name, wanted, StringComparison.OrdinalIgnoreCase)) return record.Name;
      names.Add(record.Name);
    }

    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what}: the drawing has no {kind} '{wanted}'. {Listing(names, wanted)} Nothing was changed.");
  }

  /// <summary>"Names: a, b, c." for a short list; for a long one the names containing wanted (or the count).</summary>
  internal static string Listing(IReadOnlyCollection<string> names, string wanted)
  {
    var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    if (sorted.Count <= FullListLimit) return $"Names: {string.Join(", ", sorted)}.";
    var near = sorted.Where(n => wanted.Length > 0 && n.Contains(wanted, StringComparison.OrdinalIgnoreCase)).Take(MatchListLimit).ToList();
    return near.Count > 0
      ? $"{sorted.Count} names; containing '{wanted}': {string.Join(", ", near)}."
      : $"{sorted.Count} names, none containing '{wanted}'.";
  }
}
