using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>Registration of XData application names (architecture pass A3): the script ids (ScriptIdCommands, "C3DMCP")
/// and the network links (C199, "C3DMCP_LINK") each registered their name with a copy of the same code.</summary>
internal static class XDataApps
{
  /// <summary>Adds the application name to the drawing's RegAppTable when it is missing (XData needs it).</summary>
  internal static void Ensure(Database database, Transaction transaction, string app)
  {
    var table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
    if (table.Has(app)) return;
    table.UpgradeOpen();
    var record = new RegAppTableRecord { Name = app };
    table.Add(record);
    transaction.AddNewlyCreatedDBObject(record, true);
  }
}
