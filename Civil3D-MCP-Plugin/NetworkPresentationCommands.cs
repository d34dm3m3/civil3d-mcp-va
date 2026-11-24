using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using CivilEntity = Autodesk.Civil.DatabaseServices.Entity;

namespace Civil3DMcpPlugin;

/// <summary>
/// How a pipe network is presented on the drawing: default part styles in a parts list, and labels on
/// pipes, structures and runs of pipes (spanning labels) in plan and in a profile view. Parts are picked by
/// network, name prefix ("{prefix}-…", as network_build names them) and part family.
/// </summary>
public static class NetworkPresentationCommands
{
  /// <summary>Sets the style that every size of a family in a parts list gives new parts.</summary>
  public static Task<object?> SetPartsListFamilyStyleAsync(JsonObject? parameters)
  {
    var partsListName = PluginRuntime.GetRequiredString(parameters, "partsList");
    var familyName = PluginRuntime.GetRequiredString(parameters, "familyName");
    var domainText = PluginRuntime.GetRequiredString(parameters, "domain").Trim().ToLowerInvariant();
    var styleName = PluginRuntime.GetRequiredString(parameters, "style");
    // C81: only the sizes whose current style is fromStyle (a family can mix styles, e.g. Inlopp/Utlopp).
    var fromStyle = PluginRuntime.GetOptionalString(parameters, "fromStyle");
    var domain = domainText switch
    {
      "pipe" => DomainType.Pipe,
      "structure" => DomainType.Structure,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"domain must be 'pipe' or 'structure', not '{domainText}'."),
    };

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var cdoc = (CivilDocument)civilDoc;
      var styleId = domain == DomainType.Pipe
        ? LookupUtils.GetStyleIdStrict(cdoc.Styles.PipeStyles, transaction, styleName, "Pipe style")
        : LookupUtils.GetStyleIdStrict(cdoc.Styles.StructureStyles, transaction, styleName, "Structure style");
      PartsList? partsList = null;
      foreach (ObjectId id in cdoc.Styles.PartsListSet)
      {
        var candidate = CivilObjectUtils.GetRequiredObject<PartsList>(transaction, id, OpenMode.ForRead);
        if (string.Equals(candidate.Name, partsListName, StringComparison.OrdinalIgnoreCase)) partsList = candidate;
      }

      if (partsList == null)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Parts list '{partsListName}' was not found.");
      var family = partsList.GetPartFamilyIdsByDomain(domain).Cast<ObjectId>()
        .Where(id => !id.IsNull)
        .Select(id => CivilObjectUtils.GetRequiredObject<PartFamily>(transaction, id, OpenMode.ForRead))
        .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase))
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Family '{familyName}' is not in parts list '{partsListName}'.");

      var sizes = 0;
      for (var i = 0; i < family.PartSizeCount; i++)
      {
        if (family[i].IsNull) continue;
        var size = CivilObjectUtils.GetRequiredObject<PartSize>(transaction, family[i], OpenMode.ForWrite);
        if (fromStyle != null)
        {
          var current = size.PartStyleId.IsNull ? null : (transaction.GetObject(size.PartStyleId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Styles.StyleBase)?.Name;
          if (!string.Equals(current, fromStyle, StringComparison.OrdinalIgnoreCase)) continue;
        }
        size.PartStyleId = styleId;
        sizes++;
      }

      return new Dictionary<string, object?>
      {
        ["partsList"] = partsListName,
        ["family"] = family.Name,
        ["style"] = styleName,
        ["fromStyle"] = fromStyle,
        ["sizesChanged"] = sizes,
        ["note"] = "Existing parts keep their style; use drawing style_set to change them.",
      };
    });
  }

  private sealed record LabelRequest(
    string NetworkName, string Target, string Part, string? LabelStyle, string? Prefix, string? Family,
    string? ProfileViewName, double Ratio, bool Replace, string? Layer);

  private static LabelRequest ReadLabelRequest(JsonObject? parameters, bool forAdd)
  {
    var target = PluginRuntime.GetRequiredString(parameters, "target").Trim().ToLowerInvariant();
    var part = PluginRuntime.GetRequiredString(parameters, "partKind").Trim().ToLowerInvariant();
    if (target is not ("plan" or "profile"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"target must be 'plan' or 'profile', not '{target}'.");
    if (part is not ("pipe" or "structure" or "spanning"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"partKind must be 'pipe', 'structure' or 'spanning', not '{part}'.");
    var profileView = PluginRuntime.GetOptionalString(parameters, "profileViewName");
    if (target == "profile" && string.IsNullOrWhiteSpace(profileView))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "profileViewName is required for target 'profile'.");
    var style = PluginRuntime.GetOptionalString(parameters, "labelStyle");
    if (forAdd && string.IsNullOrWhiteSpace(style))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "labelStyle is required.");
    var ratio = PluginRuntime.GetOptionalDouble(parameters, "ratio") ?? 0.5;
    if (ratio is < 0 or > 1)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "ratio must be between 0 and 1.");
    return new LabelRequest(
      PluginRuntime.GetRequiredString(parameters, "networkName"), target, part, style,
      PluginRuntime.GetOptionalString(parameters, "namePrefix"), PluginRuntime.GetOptionalString(parameters, "partFamily"),
      profileView, ratio, PluginRuntime.GetOptionalBool(parameters, "replace") ?? false,
      forAdd ? PluginRuntime.GetOptionalString(parameters, "layer") : null);
  }

  /// <summary>Adds labels to the selected parts of a network in plan or in a profile view. partKind
  /// "spanning" puts one label across each run (pipes grouped by name prefix) anchored at its middle pipe.
  /// With replace, labels of the same kind on the same parts (and view) are erased first. With layer, each new
  /// label is put on that (existing) layer; otherwise Civil 3D uses the current layer (often 0).</summary>
  public static Task<object?> AddNetworkLabelsAsync(JsonObject? parameters)
  {
    var r = ReadLabelRequest(parameters, forAdd: true);
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var cdoc = (CivilDocument)civilDoc;
      if (!string.IsNullOrWhiteSpace(r.Layer)
          && !((LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead)).Has(r.Layer))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Layer '{r.Layer}' does not exist. Nothing was changed.");
      var network = PipeNetworkCommands.FindPipeNetworkByName(cdoc, transaction, r.NetworkName, OpenMode.ForRead);
      var viewId = r.Target == "profile" ? ProfileEditCommands.FindProfileViewByName(cdoc, transaction, r.ProfileViewName!).ObjectId : ObjectId.Null;
      var parts = SelectParts(network, transaction, r);
      var styleId = r.Part == "structure"
        ? LookupUtils.GetStyleIdStrict(cdoc.Styles.LabelStyles.StructureLabelStyles.LabelStyles, transaction, r.LabelStyle, "Structure label style")
        : LookupUtils.GetStyleIdStrict(cdoc.Styles.LabelStyles.PipeLabelStyles.PlanProfileLabelStyles, transaction, r.LabelStyle, "Pipe label style");

      var erased = r.Replace ? EraseLabels(database, transaction, r, parts.SelectMany(p => new[] { p.Id, p.ViewPart }).Where(i => !i.IsNull).ToHashSet(), viewId) : 0;
      var created = 0;
      var errors = new List<string>();
      var handles = new List<string>();
      void Try(string what, Func<ObjectId> create)
      {
        try
        {
          var id = create();
          if (!string.IsNullOrWhiteSpace(r.Layer) && !id.IsNull)
            ((Autodesk.AutoCAD.DatabaseServices.Entity)transaction.GetObject(id, OpenMode.ForWrite)).Layer = r.Layer;
          if (!id.IsNull) handles.Add(id.Handle.ToString());
          created++;
        }
        catch (Exception ex)
        {
          if (errors.Count < 20) errors.Add($"{what}: {ex.Message}");
        }
      }

      if (r.Part == "spanning")
      {
        foreach (var run in parts.GroupBy(p => RunKey(p.Name)))
        {
          var ids = run.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => r.Target == "profile" ? p.ViewPart : p.Id).Where(i => !i.IsNull).ToList();
          if (ids.Count == 0)
          {
            if (errors.Count < 20) errors.Add($"{run.Key}: not shown in profile view '{r.ProfileViewName}'");
            continue;
          }

          var collection = new ObjectIdCollection(ids.ToArray());
          var anchor = ids[ids.Count / 2];
          Try(run.Key, () => r.Target == "plan"
            ? SpanningPipeLabel.Create(collection, anchor, r.Ratio, styleId)
            : SpanningPipeProfileLabel.Create(collection, anchor, viewId, r.Ratio, styleId));
        }
      }
      else
      {
        foreach (var p in parts)
        {
          Try(p.Name, () =>
          {
            switch (r.Part, r.Target)
            {
              case ("pipe", "plan"):
                return PipeLabel.Create(p.Id, r.Ratio, styleId);
              case ("pipe", "profile"):
                return PipeProfileLabel.Create(ViewPartOf(p), viewId, r.Ratio, styleId);
              case ("structure", "plan"):
                var location = CivilObjectUtils.GetRequiredObject<Structure>(transaction, p.Id, OpenMode.ForRead).Location;
                return StructureLabel.Create(p.Id, styleId, location);
              default:
                return StructureProfileLabel.Create(viewId, ViewPartOf(p), styleId);
            }
          });
        }
      }

      return new Dictionary<string, object?>
      {
        ["networkName"] = network.Name,
        ["target"] = r.Target,
        ["partKind"] = r.Part,
        ["labelStyle"] = r.LabelStyle,
        ["partsSelected"] = parts.Count,
        ["labelsCreated"] = created,
        ["labelsErased"] = erased,
        ["layer"] = r.Layer,
        ["handles"] = handles,
        ["errors"] = errors,
      };
    });
  }

  /// <summary>Erases labels of one kind (plan or profile view) on the selected parts of a network.</summary>
  public static Task<object?> DeleteNetworkLabelsAsync(JsonObject? parameters)
  {
    var r = ReadLabelRequest(parameters, forAdd: false);
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var cdoc = (CivilDocument)civilDoc;
      var network = PipeNetworkCommands.FindPipeNetworkByName(cdoc, transaction, r.NetworkName, OpenMode.ForRead);
      var viewId = r.Target == "profile" ? ProfileEditCommands.FindProfileViewByName(cdoc, transaction, r.ProfileViewName!).ObjectId : ObjectId.Null;
      var parts = SelectParts(network, transaction, r);
      return new Dictionary<string, object?>
      {
        ["networkName"] = network.Name,
        ["target"] = r.Target,
        ["partKind"] = r.Part,
        ["labelsErased"] = EraseLabels(database, transaction, r, parts.SelectMany(p => new[] { p.Id, p.ViewPart }).Where(i => !i.IsNull).ToHashSet(), viewId),
      };
    });
  }

  // ViewPart: the part as shown in a profile view (Part.ProfileViewPartId); profile labels need that id,
  // not the pipe or structure (live 2026-10-06: "An ObjectId of type ProfileViewPart is expected").
  private sealed record SelectedPart(ObjectId Id, string Name, ObjectId ViewPart);

  private static List<SelectedPart> SelectParts(Network network, Transaction transaction, LabelRequest r)
  {
    var ids = r.Part == "structure" ? network.GetStructureIds() : network.GetPipeIds();
    var result = new List<SelectedPart>();
    foreach (ObjectId id in ids)
    {
      var part = CivilObjectUtils.GetRequiredObject<Part>(transaction, id, OpenMode.ForRead);
      var name = part.Name ?? "";
      if (!string.IsNullOrWhiteSpace(r.Prefix) && !name.StartsWith(r.Prefix + "-", StringComparison.OrdinalIgnoreCase)) continue;
      if (!string.IsNullOrWhiteSpace(r.Family) && !string.Equals(part.PartFamilyName, r.Family, StringComparison.OrdinalIgnoreCase)) continue;
      result.Add(new SelectedPart(id, name, part.ProfileViewPartId));
    }

    if (result.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
        $"No {(r.Part == "structure" ? "structures" : "pipes")} in network '{network.Name}' match prefix '{r.Prefix}' and family '{r.Family}'.");
    return result;
  }

  private static ObjectId ViewPartOf(SelectedPart p) => p.ViewPart.IsNull
    ? throw new InvalidOperationException("not shown in a profile view (add the network with network_profile_view_add)")
    : p.ViewPart;

  private static string RunKey(string partName)
  {
    var dash = partName.LastIndexOf('-');
    return dash > 0 ? partName[..dash] : partName;
  }

  private static bool IsKind(LabelBase label, LabelRequest r) => (r.Part, r.Target) switch
  {
    ("pipe", "plan") => label is PipeLabel,
    ("pipe", "profile") => label is PipeProfileLabel,
    ("structure", "plan") => label is StructureLabel,
    ("structure", "profile") => label is StructureProfileLabel,
    ("spanning", "plan") => label is SpanningPipeLabel,
    _ => label is SpanningPipeProfileLabel,
  };

  private static int EraseLabels(Database database, Transaction transaction, LabelRequest r, HashSet<ObjectId> parts, ObjectId viewId)
  {
    var modelSpace = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
    var erased = 0;
    foreach (ObjectId id in modelSpace)
    {
      // The RXClass test let a Network through (InvalidCastException live 2026-10-06); test the managed type.
      if (transaction.GetObject(id, OpenMode.ForRead) is not LabelBase label) continue;
      if (!IsKind(label, r) || !parts.Contains(label.FeatureId)) continue;
      if (r.Target == "profile" && label.ViewId != viewId) continue;
      label.UpgradeOpen();
      label.Erase();
      erased++;
    }

    return erased;
  }
}
