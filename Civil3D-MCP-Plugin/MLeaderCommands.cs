using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Multileader with MText in model space: arrowhead at (x, y), text at (textX, textY). Uses the drawing's
/// current multileader style (Standard when none is current) and overrides per object: the leader is made
/// non-annotative with overall scale 1, so textHeight and arrowSize are model units; landing gap and dogleg
/// length follow the style scaled by textHeight / style text height. rotationDegrees turns the text (WCS,
/// counterclockwise from +X) so it reads horizontally in a twisted viewport (rotationDegrees = -twistDegrees);
/// the landing stays on the side of the text facing the arrowhead, in the text's frame.
/// </summary>
public static class MLeaderCommands
{
  /// <summary>
  /// C83: create or update a named multileader style with MText content: text style and height, arrow size, landing
  /// gap, dogleg, frame, background fill colour and annotative (sizes are paper units, mm, when annotative), and make
  /// it current. Use case: the company template's pipe flag style (grey text box with leader, as on a plan/profile sheet).
  /// </summary>
  public static Task<object?> SetMLeaderStyleAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var textStyle = PluginRuntime.GetOptionalString(parameters, "textStyle");
    var textHeight = PluginRuntime.GetOptionalDouble(parameters, "textHeight");
    var arrowSize = PluginRuntime.GetOptionalDouble(parameters, "arrowSize");
    var landingGap = PluginRuntime.GetOptionalDouble(parameters, "landingGap");
    var doglegLength = PluginRuntime.GetOptionalDouble(parameters, "doglegLength");
    var annotative = PluginRuntime.GetOptionalBool(parameters, "annotative");
    var frame = PluginRuntime.GetOptionalBool(parameters, "frame");
    var backgroundColor = PluginRuntime.GetOptionalInt(parameters, "backgroundColor");
    var makeCurrent = PluginRuntime.GetOptionalBool(parameters, "makeCurrent") ?? false;
    // C99k: the style's arrowhead, "none" or an AutoCAD arrow block name (as C98j per leader); the company's flags have no arrow,
    // so the template's pipe flag and bend angle flag styles carry it (M37) and hand-drawn flags come out right
    var arrowhead = PluginRuntime.GetOptionalString(parameters, "arrowhead")?.Trim();
    foreach (var (key, v) in new[] { ("textHeight", textHeight), ("arrowSize", arrowSize), ("landingGap", landingGap), ("doglegLength", doglegLength) })
      if (v is < 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{key} must be >= 0. Nothing was changed.");
    if (backgroundColor is < 0 or > 255)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "backgroundColor must be an ACI colour 1-255, or 0 for a mask in the background colour. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      ObjectId textStyleId = ObjectId.Null;
      if (textStyle != null)
      {
        var styles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
        if (!styles.Has(textStyle)) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Text style '{textStyle}' does not exist. Nothing was changed.");
        textStyleId = styles[textStyle];
      }

      var dictionary = (DBDictionary)transaction.GetObject(database.MLeaderStyleDictionaryId, OpenMode.ForRead);
      MLeaderStyle style;
      var created = !dictionary.Contains(name);
      if (created)
      {
        style = new MLeaderStyle();
        style.PostMLeaderStyleToDb(database, name);
        transaction.AddNewlyCreatedDBObject(style, true);
      }
      else
      {
        style = (MLeaderStyle)transaction.GetObject(dictionary.GetAt(name), OpenMode.ForWrite);
      }

      style.ContentType = ContentType.MTextContent;
      if (!textStyleId.IsNull) style.TextStyleId = textStyleId;
      if (textHeight != null) style.TextHeight = textHeight.Value;
      if (arrowSize != null) style.ArrowSize = arrowSize.Value;
      if (!string.IsNullOrEmpty(arrowhead))
        style.ArrowSymbolId = ArrowBlock(database, transaction, arrowhead!.Equals("none", StringComparison.OrdinalIgnoreCase) ? "_NONE" : arrowhead!);
      if (landingGap != null) style.LandingGap = landingGap.Value;
      if (doglegLength != null) { style.DoglegLength = doglegLength.Value; style.EnableDogleg = doglegLength.Value > 0; }
      style.EnableLanding = true;
      if (frame != null) style.EnableFrameText = frame.Value;
      if (annotative != null) style.Annotative = annotative.Value ? AnnotativeStates.True : AnnotativeStates.False;
      if (backgroundColor != null)
      {
        var mtext = new MText();
        mtext.SetDatabaseDefaults(database);
        // 0 = a mask in the drawing's background colour (white on paper also through monochrome.ctb, where a grey
        // fill would plot black); 1-255 = a filled box in that ACI colour.
        mtext.BackgroundFill = true;
        mtext.UseBackgroundColor = backgroundColor.Value == 0;
        if (backgroundColor.Value > 0) mtext.BackgroundFillColor = Color.FromColorIndex(ColorMethod.ByAci, (short)backgroundColor.Value);
        mtext.BackgroundScaleFactor = 1.25;
        style.DefaultMText = mtext;
      }
      if (makeCurrent) database.MLeaderstyle = style.ObjectId;

      return new Dictionary<string, object?>
      {
        ["name"] = name,
        ["created"] = created,
        ["current"] = database.MLeaderstyle == style.ObjectId,
        ["textStyle"] = ((TextStyleTableRecord)transaction.GetObject(style.TextStyleId, OpenMode.ForRead)).Name,
        ["textHeight"] = style.TextHeight,
        ["arrowSize"] = style.ArrowSize,
        ["arrowhead"] = style.ArrowSymbolId.IsNull ? "closed filled" : ((BlockTableRecord)transaction.GetObject(style.ArrowSymbolId, OpenMode.ForRead)).Name,
        ["landingGap"] = style.LandingGap,
        ["doglegLength"] = style.DoglegLength,
        ["frame"] = style.EnableFrameText,
        ["annotative"] = style.Annotative.ToString(),
        ["background"] = style.DefaultMText?.BackgroundFill != true ? null : style.DefaultMText.UseBackgroundColor ? "mask" : style.DefaultMText.BackgroundFillColor.ColorIndex.ToString(),
      };
    });
  }

  public static Task<object?> AddMLeaderAsync(JsonObject? parameters)
  {
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var textX = PluginRuntime.GetRequiredDouble(parameters, "textX");
    var textY = PluginRuntime.GetRequiredDouble(parameters, "textY");
    var text = PluginRuntime.GetRequiredString(parameters, "text");
    var textHeight = PluginRuntime.GetRequiredDouble(parameters, "textHeight");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var createLayer = PluginRuntime.GetOptionalBool(parameters, "createLayer") ?? false;
    var frame = PluginRuntime.GetOptionalBool(parameters, "frame") ?? false;
    var backgroundColor = PluginRuntime.GetOptionalInt(parameters, "backgroundColor");
    var arrowSize = PluginRuntime.GetOptionalDouble(parameters, "arrowSize");
    var landing = PluginRuntime.GetOptionalBool(parameters, "landing") ?? true;
    var attachment = PluginRuntime.GetOptionalString(parameters, "attachment");
    // C98j ("leader only, no arrow"): arrowhead "none" or an AutoCAD arrow block name (_DOT, _OBLIQUE …).
    var arrowhead = PluginRuntime.GetOptionalString(parameters, "arrowhead")?.Trim();
    if (textHeight <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "textHeight must be > 0 (model units). Nothing was created.");
    if (arrowSize is <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "arrowSize must be > 0 (model units). Nothing was created.");
    if (backgroundColor is < 0 or > 255)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "backgroundColor must be an ACI colour 1-255, or 0 for a text mask in the background colour (C99g). Nothing was created.");
    if (attachment != null && attachment != "left" && attachment != "right")
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "attachment must be 'left' or 'right'. Nothing was created.");
    var rotationDegrees = PluginRuntime.GetOptionalDouble(parameters, "rotationDegrees") ?? 0.0;
    var rotation = rotationDegrees * Math.PI / 180.0;
    // C178: more leader lines on the same landing (one flag pointing at several pipes), [{x, y}, ...].
    var extraPoints = (PluginRuntime.GetParameter(parameters, "extraPoints") as System.Text.Json.Nodes.JsonArray)?
      .Select(n => n as System.Text.Json.Nodes.JsonObject).Where(o => o != null)
      .Select(o => new Point3d(o!["x"]!.GetValue<double>(), o!["y"]!.GetValue<double>(), 0)).ToList() ?? new List<Point3d>();
    // Text direction in WCS (counterclockwise from +X); "left"/"right" are sides in the text's own frame.
    var textDirection = new Vector3d(Math.Cos(rotation), Math.Sin(rotation), 0);
    // "left": the leader meets the left side of the text (the text lies after the arrowhead along textDirection).
    var left = attachment == null ? (textX - x) * textDirection.X + (textY - y) * textDirection.Y >= 0 : attachment == "left";

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var layerId = string.IsNullOrWhiteSpace(layer) ? ObjectId.Null : LookupUtils.GetOrCreateLayerId(database, transaction, layer!, createLayer);
      var styleId = database.MLeaderstyle;
      if (styleId.IsNull)
      {
        var styles = (DBDictionary)transaction.GetObject(database.MLeaderStyleDictionaryId, OpenMode.ForRead);
        styleId = styles.Contains("Standard") ? styles.GetAt("Standard")
          : throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "The drawing has no current multileader style and no 'Standard' style. Nothing was created.");
      }

      var style = (MLeaderStyle)transaction.GetObject(styleId, OpenMode.ForRead);
      var factor = style.TextHeight > 0 ? textHeight / style.TextHeight : 1.0;
      var textPoint = new Point3d(textX, textY, 0);
      var arrowPoint = new Point3d(x, y, 0);

      var modelSpace = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
      var leader = new MLeader();
      leader.SetDatabaseDefaults(database);
      leader.MLeaderStyle = styleId;
      if (Math.Abs(rotation) > 1e-12)
      {
        // Rotated text: the leader's plane X axis follows the text, so left/right and the landing are in the
        // text's frame (as when an MLeader is drawn in a rotated UCS).
        leader.SetPlane(new Plane(arrowPoint, textDirection, Vector3d.ZAxis.CrossProduct(textDirection)));
      }
      leader.ContentType = ContentType.MTextContent;
      void ApplySizes()
      {
        leader.EnableAnnotationScale = false;
        leader.Scale = 1.0;
        leader.TextHeight = textHeight;
        leader.ArrowSize = arrowSize ?? style.ArrowSize * factor;
        leader.LandingGap = style.LandingGap * factor;
        leader.DoglegLength = style.DoglegLength * factor;
      }

      ApplySizes();
      leader.EnableLanding = landing;
      leader.EnableDogleg = landing;
      leader.EnableFrameText = frame;
      leader.TextAttachmentDirection = TextAttachmentDirection.AttachmentHorizontal;
      leader.TextAlignmentType = left ? TextAlignmentType.LeftAlignment : TextAlignmentType.RightAlignment;
      leader.TextAngleType = TextAngleType.InsertAngle;

      using (var mtext = new MText())
      {
        mtext.SetDatabaseDefaults(database);
        mtext.TextStyleId = style.TextStyleId;
        mtext.Contents = text.Replace("\r\n", "\\P").Replace("\n", "\\P");
        mtext.TextHeight = textHeight;
        mtext.Attachment = left ? AttachmentPoint.MiddleLeft : AttachmentPoint.MiddleRight;
        mtext.Location = textPoint;
        mtext.Rotation = rotation;
        if (backgroundColor != null)
        {
          mtext.BackgroundFill = true;
          mtext.UseBackgroundColor = backgroundColor.Value == 0;      // C99g: 0 = mask in the background colour
          if (backgroundColor.Value > 0)
            mtext.BackgroundFillColor = Color.FromColorIndex(ColorMethod.ByAci, (short)backgroundColor.Value);
          mtext.BackgroundScaleFactor = 1.25;
        }

        leader.MText = mtext;
      }

      if (!string.IsNullOrEmpty(arrowhead))
        leader.ArrowSymbolId = ArrowBlock(database, transaction, arrowhead!.Equals("none", StringComparison.OrdinalIgnoreCase) ? "_NONE" : arrowhead!);

      leader.TextLocation = textPoint;
      var lineIndex = leader.AddLeaderLine(arrowPoint);
      leader.SetDogleg(leader.GetLeaderIndex(lineIndex), left ? textDirection : -textDirection);
      var leaderIndex = leader.GetLeaderIndex(lineIndex);
      foreach (var extra in extraPoints)
      {
        var extraLine = leader.AddLeaderLine(leaderIndex);
        leader.AddFirstVertex(extraLine, extra);
      }
      if (!layerId.IsNull) leader.LayerId = layerId;

      modelSpace.AppendEntity(leader);
      transaction.AddNewlyCreatedDBObject(leader, true);
      // An annotative style makes the new leader annotative; turned off once it is in the drawing (sizes are model
      // units here), then the sizes are applied again since the scale may have been reset.
      if (leader.Annotative == AnnotativeStates.True)
      {
        leader.Annotative = AnnotativeStates.False;
        ApplySizes();
      }

      var result = new Dictionary<string, object?>
      {
        ["handle"] = leader.Handle.ToString(),
        ["layer"] = leader.Layer,
        ["style"] = style.Name,
        ["attachment"] = left ? "left" : "right",
        ["rotationDegrees"] = rotationDegrees,
        ["annotative"] = leader.Annotative.ToString(),
        ["arrowSize"] = Math.Round(leader.ArrowSize, 4),
        ["arrowhead"] = arrowhead,
        ["doglegLength"] = landing ? Math.Round(leader.DoglegLength, 4) : null,
      };
      EntityQueryCommands.DescribeMLeader(leader, result);
      try
      {
        var e = leader.GeometricExtents;
        result["extents"] = new[] { e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.X, e.MaxPoint.Y }.Select(v => Math.Round(v, 3)).ToArray();
      }
      catch (Autodesk.AutoCAD.Runtime.Exception)
      {
        result["extents"] = null;
      }

      return result;
    });
  }

  /// <summary>C98j: the block of an AutoCAD arrowhead; a predefined one (_NONE, _DOT …) that the drawing lacks is created the
  /// way AutoCAD does it, by setting DIMBLK to the name and back.</summary>
  private static ObjectId ArrowBlock(Database database, Transaction transaction, string name)
  {
    var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
    if (!table.Has(name))
    {
      var old = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("DIMBLK") as string;
      try
      {
        Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("DIMBLK", name);
      }
      catch (System.Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{name}' is not an AutoCAD arrowhead: {ex.Message} Nothing was created.");
      }
      finally
      {
        Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("DIMBLK", string.IsNullOrEmpty(old) ? "." : old);
      }
      table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      if (!table.Has(name))
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"The arrow block '{name}' was not created. Nothing was created.");
    }
    return table[name];
  }
}
