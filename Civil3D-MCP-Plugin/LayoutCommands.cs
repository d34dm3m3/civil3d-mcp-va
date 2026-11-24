using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Layouts (sheets) after plan production: list layouts with their viewports and block attributes (title
/// block), set title block attributes, put notes (MText) on a layout, and copy or delete layouts (viewports:
/// ViewportCommands). View frames and sheets themselves have no managed API in Civil 3D 2026 and are made
/// with the Create View Frames / Create Sheets wizards.
/// </summary>
public static class LayoutCommands
{
  internal static IEnumerable<Layout> Layouts(Database database, Transaction transaction)
  {
    var dictionary = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
    foreach (DBDictionaryEntry entry in dictionary)
      yield return (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
  }

  internal static Layout FindLayout(Database database, Transaction transaction, string name) =>
    Layouts(database, transaction).FirstOrDefault(l => string.Equals(l.LayoutName, name, StringComparison.OrdinalIgnoreCase))
    ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
      $"Layout '{name}' was not found. Layouts: {string.Join(", ", Layouts(database, transaction).Select(l => l.LayoutName))}.");

  /// <summary>C124: renames layouts: renames [{name, newName}], all checked first (exists, new name free and valid, no
  /// duplicates), then Layout.LayoutName is set. Use case: the sheets were called A1, A1 (2) … and
  /// were to carry their drawing numbers.</summary>
  /// setLayoutOrder {order: [name, ...]}: the listed layouts are put last in the given order (a rebuilt sheet gets a
  /// new layout at the end of the tab row); the other layouts keep their order before them. Model is not touched. An
  /// unknown name or a duplicate changes nothing.
  public static Task<object?> SetLayoutOrderAsync(JsonObject? parameters)
  {
    var list = PluginRuntime.GetParameter(parameters, "order") as JsonArray;
    if (list == null || list.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "order must list at least one layout name. Nothing was changed.");
    var names = list.Select((n, i) => n?.GetValue<string>()?.Trim() is string a && a.Length > 0 ? a
      : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"order[{i}] must be a layout name. Nothing was changed.")).ToList();
    if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "order lists a layout twice. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var listed = names.Select(n => FindLayout(database, transaction, n)).ToList();
      var listedIds = listed.Select(l => l.ObjectId).ToHashSet();
      var others = Layouts(database, transaction).Where(l => !l.ModelType && !listedIds.Contains(l.ObjectId)).OrderBy(l => l.TabOrder).ToList();
      var tab = 1;
      foreach (var layout in others.Concat(listed))
      {
        layout.UpgradeOpen();
        layout.TabOrder = tab++;
      }
      return new Dictionary<string, object?>
      {
        ["order"] = Layouts(database, transaction).Where(l => !l.ModelType).OrderBy(l => l.TabOrder).Select(l => l.LayoutName).ToList()
      };
    });
  }

  public static Task<object?> RenameLayoutsAsync(JsonObject? parameters)
  {
    var list = PluginRuntime.GetParameter(parameters, "renames") as JsonArray;
    if (list == null || list.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "renames must list at least one {name, newName}. Nothing was changed.");
    var pairs = list.Select((n, i) => n is JsonObject o && o["name"]?.GetValue<string>() is string a && o["newName"]?.GetValue<string>() is string b
      ? (Name: a.Trim(), NewName: b.Trim())
      : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"renames[{i}] must be {{name, newName}}. Nothing was changed.")).ToList();
    if (pairs.Select(p => p.NewName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pairs.Count)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Two layouts cannot get the same new name. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var all = Layouts(database, transaction).ToList();
      var renaming = pairs.Select(p => (p, Layout: FindLayout(database, transaction, p.Name))).ToList();
      var leaving = renaming.Select(r => r.Layout.ObjectId).ToHashSet();
      foreach (var (p, _) in renaming)
      {
        if (p.NewName.Length == 0 || p.NewName.Length > 255 || p.NewName.IndexOfAny("<>/\\\":;?*|,=`".ToCharArray()) >= 0)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{p.NewName}' is not a valid layout name. Nothing was changed.");
        if (all.Any(l => !leaving.Contains(l.ObjectId) && string.Equals(l.LayoutName, p.NewName, StringComparison.OrdinalIgnoreCase)))
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"A layout named '{p.NewName}' already exists. Nothing was changed.");
      }
      // two steps, so that names can be swapped
      foreach (var (p, layout) in renaming) { layout.UpgradeOpen(); layout.LayoutName = "~tmp~" + layout.ObjectId.Handle; }
      var renamed = new List<Dictionary<string, object?>>();
      foreach (var (p, layout) in renaming)
      {
        layout.LayoutName = p.NewName;
        renamed.Add(new Dictionary<string, object?> { ["from"] = p.Name, ["to"] = layout.LayoutName });
      }
      return new Dictionary<string, object?> { ["renamed"] = renamed, ["count"] = renamed.Count };
    });
  }

  /// <summary>C124b: replaces literal text in texts and multiline texts given by handle (model space or a layout):
  /// handles, find, replaceWith. MText is edited in its raw contents (format codes kept). A handle whose text does not
  /// contain find is reported, not an error. Use case: a misspelt word in the legend.
  /// C144: also multileaders with text content; their MText is edited and set back, so the text keeps its place, frame
  /// and leaders. Use case (standard drawings): a flag the user had moved by hand needed new wording;
  /// erasing and redrawing it would have lost that placement.</summary>
  public static Task<object?> ReplaceTextAsync(JsonObject? parameters)
  {
    var handles = (PluginRuntime.GetParameter(parameters, "handles") as JsonArray)?.Select(n => n?.GetValue<string>()?.Trim() ?? "").Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (handles == null || handles.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "handles must list at least one text handle. Nothing was changed.");
    var find = PluginRuntime.GetRequiredString(parameters, "find");
    var with = PluginRuntime.GetOptionalString(parameters, "replaceWith") ?? "";
    if (find.Length == 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "find must not be empty. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var texts = handles.Select(h =>
      {
        if (!h.All(Uri.IsHexDigit) || !database.TryGetObjectId(CivilObjectUtils.ParseHandle(h), out var id) || id.IsErased)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No object with handle {h}. Nothing was changed.");
        var o = transaction.GetObject(id, OpenMode.ForRead);
        if (o is MLeader leader && leader.ContentType != ContentType.MTextContent)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{h} is a multileader without text content. Nothing was changed.");
        return o is DBText or MText or MLeader ? (Entity)o : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{h} is not a text, multiline text or multileader. Nothing was changed.");
      }).ToList();
      var result = new List<Dictionary<string, object?>>();
      foreach (var e in texts)
      {
        var before = e switch { MText m => m.Contents, MLeader l => l.MText.Contents, _ => ((DBText)e).TextString };
        var hits = before.Split(find).Length - 1;
        if (hits > 0)
        {
          e.UpgradeOpen();
          if (e is MText mt) mt.Contents = before.Replace(find, with);
          else if (e is MLeader ml)
          {
            var location = ml.TextLocation;               // setting MText re-places the text from the landing:
            var content = ml.MText;                       // a copy: edit it, set it back and restore the location (C144)
            content.Contents = before.Replace(find, with);
            ml.MText = content;
            if (!ml.TextLocation.IsEqualTo(location)) ml.TextLocation = location;
          }
          else ((DBText)e).TextString = before.Replace(find, with);
        }
        result.Add(new Dictionary<string, object?> { ["handle"] = e.Handle.ToString(), ["replaced"] = hits });
      }
      return new Dictionary<string, object?> { ["texts"] = result, ["replacements"] = result.Sum(r => (int)r["replaced"]!) };
    });
  }

  private static string BlockName(BlockReference reference, Transaction transaction) => reference.IsDynamicBlock
    ? ((BlockTableRecord)transaction.GetObject(reference.DynamicBlockTableRecord, OpenMode.ForRead)).Name
    : reference.Name;

  public static Task<object?> ListLayoutsAsync(JsonObject? parameters)
  {
    var only = PluginRuntime.GetOptionalString(parameters, "layout");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var result = new List<Dictionary<string, object?>>();
      foreach (var layout in Layouts(database, transaction).OrderBy(l => l.TabOrder))
      {
        if (layout.ModelType) continue;
        if (!string.IsNullOrWhiteSpace(only) && !string.Equals(layout.LayoutName, only, StringComparison.OrdinalIgnoreCase)) continue;
        result.Add(DescribeLayout(layout, transaction));
      }

      return new Dictionary<string, object?> { ["layouts"] = result };
    });
  }

  private static Dictionary<string, object?> DescribeLayout(Layout layout, Transaction transaction)
  {
    var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
    var viewports = new List<Dictionary<string, object?>>();
    var blocks = new List<Dictionary<string, object?>>();
    var first = true;
    foreach (ObjectId id in space)
    {
      var entity = transaction.GetObject(id, OpenMode.ForRead);
      if (entity is Viewport vp)
      {
        if (first) { first = false; continue; }          // the paper space viewport itself
        viewports.Add(ViewportCommands.Describe(vp, transaction));
      }
      else if (entity is BlockReference reference)
      {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectId attributeId in reference.AttributeCollection)
        {
          if (attributeId.IsErased) continue;   // erased by an attribute sync in this drawing
          if (transaction.GetObject(attributeId, OpenMode.ForRead) is AttributeReference attribute)
            attributes[attribute.Tag] = attribute.TextString;
        }

        blocks.Add(new Dictionary<string, object?>
        {
          ["handle"] = reference.Handle.ToString(),
          ["block"] = BlockName(reference, transaction),
          ["position"] = new[] { Math.Round(reference.Position.X, 2), Math.Round(reference.Position.Y, 2) },
          ["attributes"] = attributes,
        });
      }
    }

    return new Dictionary<string, object?>
    {
      ["name"] = layout.LayoutName,
      ["tabOrder"] = layout.TabOrder,
      ["paperSize"] = new[] { Math.Round(layout.PlotPaperSize.X, 1), Math.Round(layout.PlotPaperSize.Y, 1) },
      ["plotDevice"] = layout.PlotConfigurationName,
      ["mediaName"] = layout.CanonicalMediaName,
      ["plotStyleTable"] = layout.CurrentStyleSheet,
      ["plotRotation"] = layout.PlotRotation.ToString(),
      ["viewports"] = viewports,
      ["blocks"] = blocks,
    };
  }

  /// <summary>Copies a paper layout (LayoutManager.CloneLayout: page setup, title block, viewports) to a new
  /// name, placed last. Fails when the name is taken.</summary>
  public static Task<object?> CreateLayoutAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name").Trim();
    var copyFrom = PluginRuntime.GetRequiredString(parameters, "copyFrom");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // Checks through an open/close transaction, so nothing is held open when LayoutManager writes.
      string sourceName;
      using (var check = database.TransactionManager.StartOpenCloseTransaction())
      {
        if (Layouts(database, check).Any(l => string.Equals(l.LayoutName, name, StringComparison.OrdinalIgnoreCase)))
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Layout '{name}' already exists. Nothing was changed.");
        var source = FindLayout(database, check, copyFrom);
        if (source.ModelType)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "copyFrom must be a paper layout, not Model. Nothing was changed.");
        sourceName = source.LayoutName;
        check.Commit();
      }

      try
      {
        SymbolUtilityServices.ValidateSymbolName(name, false);
      }
      catch (Autodesk.AutoCAD.Runtime.Exception)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{name}' is not a valid layout name. Nothing was changed.");
      }

      var manager = LayoutManager.Current;
      manager.CloneLayout(sourceName, name, manager.LayoutCount);
      var created = FindLayout(database, transaction, name);
      var result = DescribeLayout(created, transaction);
      result["copiedFrom"] = sourceName;
      return result;
    });
  }

  /// <summary>Deletes a paper layout and everything on it. Model and the last paper layout are refused.</summary>
  public static Task<object?> DeleteLayoutAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      // Checks through an open/close transaction, so nothing is held open when LayoutManager erases the layout.
      string deletedName;
      int entityCount;
      using (var check = database.TransactionManager.StartOpenCloseTransaction())
      {
        var layout = FindLayout(database, check, name);
        if (layout.ModelType)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Model space cannot be deleted. Nothing was changed.");
        if (Layouts(database, check).Count(l => !l.ModelType) <= 1)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{layout.LayoutName}' is the only paper layout; a drawing keeps at least one. Nothing was changed.");
        var space = (BlockTableRecord)check.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        entityCount = space.Cast<ObjectId>().Count();
        deletedName = layout.LayoutName;
        check.Commit();
      }

      LayoutManager.Current.DeleteLayout(deletedName);
      return new Dictionary<string, object?>
      {
        ["deleted"] = deletedName,
        ["entitiesDeleted"] = entityCount,
        ["remaining"] = Layouts(database, transaction).Where(l => !l.ModelType).OrderBy(l => l.TabOrder).Select(l => l.LayoutName).ToList(),
      };
    });
  }

  /// <summary>C186: makes a layout (or Model) the current tab: setCurrentLayout {name} -> {previous, current}. Use case
  /// (run REGENALL on every sheet): REGENALL (startCommand, C118d) only
  /// regenerates the current tab, and a script could not switch tabs, so the sheets' viewports stayed stale.</summary>
  public static Task<object?> SetCurrentLayoutAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      string target;
      using (var check = database.TransactionManager.StartOpenCloseTransaction())
      {
        target = FindLayout(database, check, name).LayoutName;
        check.Commit();
      }
      var previous = LayoutManager.Current.CurrentLayout;
      LayoutManager.Current.CurrentLayout = target;
      return new Dictionary<string, object?> { ["previous"] = previous, ["current"] = LayoutManager.Current.CurrentLayout };
    });
  }

  /// <summary>Sets attribute values on the block references named blockName in a layout (all layouts when
  /// layout is omitted). Unknown tags are reported, not created.</summary>
  public static Task<object?> SetBlockAttributesAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetOptionalString(parameters, "layout");
    var blockName = PluginRuntime.GetRequiredString(parameters, "blockName");
    var values = (PluginRuntime.GetParameter(parameters, "values") as JsonObject ?? new JsonObject()).Where(kv => kv.Value != null)
      .ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
    // positions: tag -> [x, y] in the layout's units (the insertion point of a BaseLeft attribute, else its
    // alignment point); heights: tag -> text height. For title blocks whose attributes sit outside their fields.
    var positions = new Dictionary<string, Point3d>(StringComparer.OrdinalIgnoreCase);
    if (PluginRuntime.GetParameter(parameters, "positions") is JsonObject positionsNode)
    {
      foreach (var (tag, node) in positionsNode)
      {
        if (node is not JsonArray xy || xy.Count < 2)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"positions.{tag} must be [x, y]. Nothing was changed.");
        positions[tag] = new Point3d(xy[0]!.GetValue<double>(), xy[1]!.GetValue<double>(), 0.0);
      }
    }

    var heights = (PluginRuntime.GetParameter(parameters, "heights") as JsonObject ?? new JsonObject()).Where(kv => kv.Value != null)
      .ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<double>(), StringComparer.OrdinalIgnoreCase);
    if (heights.Values.Any(h => !(h > 0)))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "heights must be > 0. Nothing was changed.");
    if (values.Count == 0 && positions.Count == 0 && heights.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give values, positions and/or heights for at least one attribute tag.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var layouts = string.IsNullOrWhiteSpace(layoutName)
        ? Layouts(database, transaction).Where(l => !l.ModelType).ToList()
        : new List<Layout> { FindLayout(database, transaction, layoutName!) };
      var updated = new List<Dictionary<string, object?>>();
      var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var layout in layouts)
      {
        var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        foreach (ObjectId id in space)
        {
          if (transaction.GetObject(id, OpenMode.ForRead) is not BlockReference reference) continue;
          if (!string.Equals(BlockName(reference, transaction), blockName, StringComparison.OrdinalIgnoreCase)) continue;
          var changed = new List<string>();
          foreach (ObjectId attributeId in reference.AttributeCollection)
          {
            if (attributeId.IsErased) continue;   // erased by an attribute sync in this drawing
            if (transaction.GetObject(attributeId, OpenMode.ForRead) is not AttributeReference attribute) continue;
            seenTags.Add(attribute.Tag);
            var hasValue = values.TryGetValue(attribute.Tag, out var value);
            var hasPosition = positions.TryGetValue(attribute.Tag, out var position);
            var hasHeight = heights.TryGetValue(attribute.Tag, out var height);
            if (!hasValue && !hasPosition && !hasHeight) continue;
            attribute.UpgradeOpen();
            if (hasValue) MultilineAttributes.SetValue(attribute, value!);
            if (hasHeight) attribute.Height = height;
            if (hasPosition)
            {
              var at = new Point3d(position.X, position.Y, attribute.Position.Z);
              if (attribute.IsMTextAttribute)
              {
                // C116: multiline attributes are justified; Position, AlignmentPoint and the MText location move together
                attribute.Position = at;
                if (attribute.Justify != AttachmentPoint.BaseLeft) attribute.AlignmentPoint = at;
                var mtext = attribute.MTextAttribute;
                mtext.Location = at;
                attribute.MTextAttribute = mtext;
              }
              else if (attribute.Justify == AttachmentPoint.BaseLeft) attribute.Position = at;
              else attribute.AlignmentPoint = at;
            }

            Realign(attribute, database);
            changed.Add(attribute.Tag);
          }

          updated.Add(new Dictionary<string, object?> { ["layout"] = layout.LayoutName, ["handle"] = reference.Handle.ToString(), ["tags"] = changed });
        }
      }

      if (updated.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No block '{blockName}' in {(layoutName ?? "any layout")}. Nothing was changed.");
      return new Dictionary<string, object?>
      {
        ["blockName"] = blockName,
        ["updated"] = updated,
        ["unknownTags"] = values.Keys.Concat(positions.Keys).Concat(heights.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
          .Where(k => !seenTags.Contains(k)).ToList(),
      };
    });
  }

  /// <summary>After TextString changes, a justified (not BaseLeft) attribute keeps its old Position until the
  /// alignment is recomputed, so the text is drawn away from its field; AdjustAlignment needs the attribute's
  /// database as the working database. Multiline attributes are rebuilt from their MText instead.</summary>
  private static void Realign(AttributeReference attribute, Database database)
  {
    if (attribute.IsMTextAttribute)
    {
      attribute.UpdateMTextAttribute();
      return;
    }

    if (attribute.Justify == AttachmentPoint.BaseLeft) return;
    var working = HostApplicationServices.WorkingDatabase;
    try
    {
      HostApplicationServices.WorkingDatabase = database;
      attribute.AdjustAlignment(database);
    }
    finally
    {
      HostApplicationServices.WorkingDatabase = working;
    }
  }

  /// <summary>Lists the attributes of the block references named blockName in a layout (all layouts when layout
  /// is omitted): tag, prompt (from the block definition), value, position and alignment point (in the layout's
  /// units), justification, height, rotation, invisible and multiline. Constant attributes live only in the
  /// block definition and are listed under constantAttributes.</summary>
  public static Task<object?> GetBlockAttributesAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetOptionalString(parameters, "layout");
    var blockName = PluginRuntime.GetRequiredString(parameters, "blockName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var layouts = string.IsNullOrWhiteSpace(layoutName)
        ? Layouts(database, transaction).Where(l => !l.ModelType).ToList()
        : new List<Layout> { FindLayout(database, transaction, layoutName!) };
      var references = new List<Dictionary<string, object?>>();
      foreach (var layout in layouts)
      {
        var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        foreach (ObjectId id in space)
        {
          if (transaction.GetObject(id, OpenMode.ForRead) is not BlockReference reference) continue;
          if (!string.Equals(BlockName(reference, transaction), blockName, StringComparison.OrdinalIgnoreCase)) continue;
          var prompts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
          var constants = new List<Dictionary<string, object?>>();
          var definition = (BlockTableRecord)transaction.GetObject(
            reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord, OpenMode.ForRead);
          foreach (ObjectId defId in definition)
          {
            if (transaction.GetObject(defId, OpenMode.ForRead) is not AttributeDefinition attDef) continue;
            prompts[attDef.Tag] = attDef.Prompt;
            if (attDef.Constant)
              constants.Add(new Dictionary<string, object?> { ["tag"] = attDef.Tag, ["value"] = attDef.TextString });
          }

          var attributes = new List<Dictionary<string, object?>>();
          foreach (ObjectId attributeId in reference.AttributeCollection)
          {
            if (attributeId.IsErased) continue;   // erased by an attribute sync in this drawing
            if (transaction.GetObject(attributeId, OpenMode.ForRead) is not AttributeReference a) continue;
            attributes.Add(new Dictionary<string, object?>
            {
              ["tag"] = a.Tag,
              ["prompt"] = prompts.TryGetValue(a.Tag, out var prompt) ? prompt : null,
              ["value"] = MultilineAttributes.Value(a),
              ["position"] = new[] { Math.Round(a.Position.X, 3), Math.Round(a.Position.Y, 3) },
              ["alignmentPoint"] = new[] { Math.Round(a.AlignmentPoint.X, 3), Math.Round(a.AlignmentPoint.Y, 3) },
              ["justify"] = a.Justify.ToString(),
              ["height"] = Math.Round(a.Height, 3),
              ["rotationDegrees"] = Math.Round(a.Rotation * 180.0 / Math.PI, 3),
              ["invisible"] = a.Invisible,
              ["multiline"] = a.IsMTextAttribute,
              ["layer"] = a.Layer,
            });
          }

          references.Add(new Dictionary<string, object?>
          {
            ["layout"] = layout.LayoutName,
            ["handle"] = reference.Handle.ToString(),
            ["insertion"] = new[] { Math.Round(reference.Position.X, 3), Math.Round(reference.Position.Y, 3) },
            ["rotationDegrees"] = Math.Round(reference.Rotation * 180.0 / Math.PI, 3),
            ["scale"] = new[] { Math.Round(reference.ScaleFactors.X, 4), Math.Round(reference.ScaleFactors.Y, 4) },
            ["attributes"] = attributes,
            ["constantAttributes"] = constants,
          });
        }
      }

      if (references.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No block '{blockName}' in {(layoutName ?? "any layout")}.");
      return new Dictionary<string, object?> { ["blockName"] = blockName, ["references"] = references };
    });
  }

  /// <summary>Adds MText to a layout (paper space units, normally mm) or to model space (layout "Model").</summary>
  public static Task<object?> AddLayoutTextAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetRequiredString(parameters, "layout");
    var text = PluginRuntime.GetRequiredString(parameters, "text");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var height = PluginRuntime.GetOptionalDouble(parameters, "height") ?? 2.5;
    var width = PluginRuntime.GetOptionalDouble(parameters, "width") ?? 0.0;
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    // C77: rotation, text style and fields (AutoCAD field codes such as %<\AcVar Filename>% in the text become a
    // live field, evaluated at plot). Use case: the company template's plot stamp (file, layout, plot date) in the margin.
    var rotation = PluginRuntime.GetOptionalDouble(parameters, "rotationDegrees") ?? 0.0;
    var createLayer = PluginRuntime.GetOptionalBool(parameters, "createLayer") ?? false;
    var textStyle = PluginRuntime.GetOptionalString(parameters, "textStyle");
    var hasFields = text.Contains("%<\\", StringComparison.Ordinal);
    // C142: attachment and exact line spacing. Need: a column of notes written as one MText with "\P" between lines
    // gets even rows; with TopLeft and per-line texts, lines with accented capitals sat lower (found in a drawing review).
    var attachmentName = PluginRuntime.GetOptionalString(parameters, "attachment");
    var attachment = AttachmentPoint.TopLeft;
    if (!string.IsNullOrWhiteSpace(attachmentName) && !Enum.TryParse(attachmentName, true, out attachment))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"attachment '{attachmentName}' is not one of TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight. Nothing was changed.");
    var lineSpacing = PluginRuntime.GetOptionalDouble(parameters, "lineSpacing");
    if (lineSpacing is <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "lineSpacing must be positive (distance between lines in drawing units). Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      ObjectId styleId = ObjectId.Null;
      if (!string.IsNullOrWhiteSpace(textStyle))
      {
        var styles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
        if (!styles.Has(textStyle))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Text style '{textStyle}' does not exist. Nothing was changed.");
        styleId = styles[textStyle];
      }
      var spaceId = string.Equals(layoutName, "Model", StringComparison.OrdinalIgnoreCase)
        ? SymbolUtilityServices.GetBlockModelSpaceId(database)
        : FindLayout(database, transaction, layoutName).BlockTableRecordId;
      if (!string.IsNullOrWhiteSpace(layer))
      {
        // C181: createLayer creates the layer on request (as addMLeader, createPolyline); otherwise it must exist
        if (createLayer)
          LookupUtils.GetOrCreateLayerId(database, transaction, layer!, true);
        var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (!table.Has(layer))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layer '{layer}' does not exist. Nothing was changed.");
      }

      var space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForWrite);
      var mtext = new MText
      {
        Location = new Point3d(x, y, 0),
        Contents = text.Replace("\r\n", "\\P").Replace("\n", "\\P"),
        TextHeight = height,
        Width = width,
        Attachment = attachment,
      };
      mtext.SetDatabaseDefaults(database);
      mtext.TextHeight = height;
      if (lineSpacing is double ls)
      {
        mtext.LineSpacingStyle = LineSpacingStyle.Exactly;
        mtext.LineSpaceDistance = ls;
      }
      mtext.Rotation = rotation * Math.PI / 180.0;
      if (!styleId.IsNull) mtext.TextStyleId = styleId;
      if (!string.IsNullOrWhiteSpace(layer)) mtext.Layer = layer;
      AcadCommands.ApplyTextMask(mtext, parameters);          // C99g: backgroundMask, maskScale, backgroundColor
      var id = space.AppendEntity(mtext);
      transaction.AddNewlyCreatedDBObject(mtext, true);
      string? shown = null;
      if (hasFields)
      {
        // The field is made from the text as given: MText reads "\A…" in its contents as a format code, and a field
        // only evaluates once it belongs to the text (live 2026-10-09: "####" in the plot otherwise).
        var field = new Field(text);
        mtext.SetField(field);
        transaction.AddNewlyCreatedDBObject(field, true);
        field.Evaluate();
        shown = mtext.Text;
      }
      return new Dictionary<string, object?>
      {
        ["layout"] = layoutName, ["handle"] = mtext.Handle.ToString(), ["layer"] = mtext.Layer,
        ["field"] = hasFields, ["shown"] = shown,
      };
    });
  }
}
