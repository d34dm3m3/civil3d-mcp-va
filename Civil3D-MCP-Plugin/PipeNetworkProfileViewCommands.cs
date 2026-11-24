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
  /// <summary>Adds a network's parts (optionally only prefix-…) to a profile view. C63 (2026-10-08): networkNames
  /// and profileViewNames add several networks to several views in ONE transaction; Civil 3D updates the profile
  /// views after every commit (live about 100 s each), so one call per network and view took an hour.</summary>
  public static Task<object?> AddNetworkToProfileViewAsync(JsonObject? parameters)
  {
    var networks = OptionalStringList(parameters, "networkNames");
    var views = OptionalStringList(parameters, "profileViewNames");
    var single = networks == null && views == null;
    networks ??= new List<string> { PluginRuntime.GetRequiredString(parameters, "networkName") };
    views ??= new List<string> { PluginRuntime.GetRequiredString(parameters, "profileViewName") };
    if (networks.Count == 0 || views.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "networkNames and profileViewNames must not be empty. Nothing was changed.");
    var namePrefix = PluginRuntime.GetOptionalString(parameters, "namePrefix");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      bool Selected(Part part) => string.IsNullOrWhiteSpace(namePrefix)
        || (part.Name ?? "").StartsWith(namePrefix + "-", StringComparison.OrdinalIgnoreCase);
      var viewObjects = views.Select(v => ProfileEditCommands.FindProfileViewByName(civilDoc, transaction, v)).ToList();
      var added = new List<Dictionary<string, object?>>();
      foreach (var networkName in networks)
      {
        var network = FindPipeNetworkByName(civilDoc, transaction, networkName, OpenMode.ForRead);
        var pipes = network.GetPipeIds().Cast<ObjectId>()
          .Select(id => CivilObjectUtils.GetRequiredObject<Pipe>(transaction, id, OpenMode.ForWrite)).Where(Selected).ToList();
        var structures = network.GetStructureIds().Cast<ObjectId>()
          .Select(id => CivilObjectUtils.GetRequiredObject<Structure>(transaction, id, OpenMode.ForWrite)).Where(Selected).ToList();
        foreach (var view in viewObjects)
        {
          foreach (var pipe in pipes) pipe.AddToProfileView(view.ObjectId);
          foreach (var structure in structures) structure.AddToProfileView(view.ObjectId);
          added.Add(new Dictionary<string, object?>
          {
            ["networkName"] = network.Name,
            ["profileViewName"] = view.Name,
            ["pipesAdded"] = pipes.Count,
            ["structuresAdded"] = structures.Count,
          });
        }
      }

      if (single) return added[0];
      return new Dictionary<string, object?>
      {
        ["added"] = added,
        ["pipesAdded"] = added.Sum(a => (int)a["pipesAdded"]!),
        ["structuresAdded"] = added.Sum(a => (int)a["structuresAdded"]!),
      };
    });
  }

  /// <summary>Profile views in model space, found by class without opening every entity.</summary>
  internal static List<ProfileView> AllProfileViews(Database database, Transaction transaction)
  {
    var profileViewClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(ProfileView));
    var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
    var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
    var views = new List<ProfileView>();
    foreach (ObjectId id in modelSpace)
      if (id.ObjectClass.IsDerivedFrom(profileViewClass) && transaction.GetObject(id, OpenMode.ForRead) is ProfileView view)
        views.Add(view);
    return views;
  }

  /// <summary>A profile view that shows parts of a run, with the draw flag and the first style override per kind,
  /// so that a rebuilt run can be put back the same way (network_build keepProfileViews).</summary>
  internal sealed record ViewMembership(ObjectId ViewId, string ViewName, int Pipes, bool PipesDrawn, ObjectId PipeStyle,
    int Structures, bool StructuresDrawn, ObjectId StructureStyle);
  internal static List<ViewMembership> ProfileViewMembership(Database database, Transaction transaction, string prefix)
  {
    bool Match(string? name) => (name ?? "").StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase);
    ObjectId Style(IEnumerable<GraphOverride> overrides) =>
      overrides.FirstOrDefault(o => o.UseOverrideStyle)?.OverrideStyleId ?? ObjectId.Null;
    var result = new List<ViewMembership>();
    foreach (var view in AllProfileViews(database, transaction))
    {
      var pipes = view.PipeOverrides.Cast<PipeOverride>().Where(o => Match(o.PipeName)).ToList();
      var structures = view.StructureOverrides.Cast<StructureOverride>().Where(o => Match(o.StructName)).ToList();
      if (pipes.Count == 0 && structures.Count == 0) continue;
      result.Add(new ViewMembership(view.ObjectId, view.Name,
        pipes.Count, pipes.Count == 0 || pipes.Any(o => o.Draw), Style(pipes),
        structures.Count, structures.Count == 0 || structures.Any(o => o.Draw), Style(structures)));
    }

    return result;
  }
}
