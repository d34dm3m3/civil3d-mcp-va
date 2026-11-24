using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// Profile label style components: read a style's components, copy a style, and add or update
/// a text component (contents, visibility, attachment, offsets). A new text component can take
/// its anchor, attachment, size and offsets from an existing one ("like"), so that for example a
/// length text can sit under the grade text of a tangent label.
/// Component names are read through StyleGeneral.Name (StyleBase.Name has only a setter).
/// </summary>
public static class ProfileLabelStyleCommands
{
  public static Task<object?> GetProfileLabelStyleAsync(JsonObject? parameters)
  {
    var kind = PluginRuntime.GetRequiredString(parameters, "labelKind");
    var styleName = PluginRuntime.GetRequiredString(parameters, "style");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var style = CivilObjectUtils.GetRequiredObject<LabelStyle>(
        transaction, FindStyleId(civilDoc, transaction, kind, styleName), OpenMode.ForRead);
      return Describe(kind, style, transaction);
    });
  }

  public static Task<object?> CopyProfileLabelStyleAsync(JsonObject? parameters)
  {
    var kind = PluginRuntime.GetRequiredString(parameters, "labelKind");
    var styleName = PluginRuntime.GetRequiredString(parameters, "style");
    var newName = PluginRuntime.GetRequiredString(parameters, "newStyle");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var sourceId = FindStyleId(civilDoc, transaction, kind, styleName);
      if (TryFindStyleId(civilDoc, transaction, kind, newName, out _))
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.CONFLICT", $"A {kind} label style named '{newName}' already exists. Nothing was copied.");
      }

      var source = CivilObjectUtils.GetRequiredObject<LabelStyle>(transaction, sourceId, OpenMode.ForWrite);
      var copyId = source.CopyAsSibling(newName);
      var copy = CivilObjectUtils.GetRequiredObject<LabelStyle>(transaction, copyId, OpenMode.ForRead);
      return Describe(kind, copy, transaction);
    });
  }

  public static Task<object?> SetProfileLabelStyleTextAsync(JsonObject? parameters)
  {
    var kind = PluginRuntime.GetRequiredString(parameters, "labelKind");
    var styleName = PluginRuntime.GetRequiredString(parameters, "style");
    if (PluginRuntime.GetParameter(parameters, "text") is not JsonObject text)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "text must be an object with at least a component name.");
    }

    var update = ParseText(text);

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var style = CivilObjectUtils.GetRequiredObject<LabelStyle>(
        transaction, FindStyleId(civilDoc, transaction, kind, styleName), OpenMode.ForWrite);

      var target = FindTextComponent(style, transaction, update.Component, OpenMode.ForWrite);
      var added = false;
      var notCopied = new List<string>();
      if (target == null)
      {
        if (update.Contents == null)
        {
          throw new JsonRpcDispatchException(
            "CIVIL3D.INVALID_INPUT",
            $"Label style '{styleName}' has no text component '{update.Component}'. Give contents to add it. Nothing was changed.");
        }

        LabelStyleTextComponent? like = null;
        if (update.Like != null)
        {
          like = FindTextComponent(style, transaction, update.Like, OpenMode.ForRead) ?? throw new JsonRpcDispatchException(
            "CIVIL3D.INVALID_INPUT",
            $"Label style '{styleName}' has no text component '{update.Like}' to copy the layout from. Nothing was changed.");
        }

        var id = style.AddComponent(update.Component, LabelStyleComponentType.Text);
        target = CivilObjectUtils.GetRequiredObject<LabelStyleTextComponent>(transaction, id, OpenMode.ForWrite);
        if (like != null)
        {
          CopyLayout(like, target, notCopied);
        }

        added = true;
      }

      if (update.Contents != null) target.Text.Contents.Value = update.Contents;
      if (update.Visible != null) target.General.Visible.Value = update.Visible.Value;
      if (update.Attachment != null) target.Text.Attachment.Value = update.Attachment.Value;
      if (update.XOffset != null) target.Text.XOffset.Value = update.XOffset.Value;
      if (update.YOffset != null) target.Text.YOffset.Value = update.YOffset.Value;

      var result = Describe(kind, style, transaction);
      result["component"] = update.Component;
      result["added"] = added;
      result["layoutNotCopied"] = notCopied;
      return result;
    });
  }

  private sealed record TextUpdate(
    string Component, string? Contents, bool? Visible, string? Like,
    LabelTextAttachmentType? Attachment, double? XOffset, double? YOffset);

  private static TextUpdate ParseText(JsonObject o)
  {
    var component = o["component"]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(component))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "text.component must be a non-empty string.");
    }

    LabelTextAttachmentType? attachment = null;
    var attachmentName = o["attachment"]?.GetValue<string>();
    if (attachmentName != null)
    {
      if (!Enum.TryParse<LabelTextAttachmentType>(attachmentName, ignoreCase: false, out var parsed))
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          $"text.attachment '{attachmentName}' is not one of {string.Join(", ", Enum.GetNames<LabelTextAttachmentType>())}.");
      }

      attachment = parsed;
    }

    return new TextUpdate(
      component,
      o["contents"]?.GetValue<string>(),
      o["visible"]?.GetValue<bool>(),
      o["like"]?.GetValue<string>(),
      attachment,
      o["xOffset"]?.GetValue<double>(),
      o["yOffset"]?.GetValue<double>());
  }

  private static void CopyLayout(LabelStyleTextComponent from, LabelStyleTextComponent to, List<string> notCopied)
  {
    void Copy(string name, Action copy)
    {
      try
      {
        copy();
      }
      catch (Exception ex) when (ex is InvalidOperationException or Autodesk.AutoCAD.Runtime.Exception or ArgumentException)
      {
        notCopied.Add($"{name}: {ex.Message}");
      }
    }

    Copy("anchorComponent", () => to.General.AnchorComponent.Value = from.General.AnchorComponent.Value);
    Copy("anchorLocation", () => to.General.AnchorLocation.Value = from.General.AnchorLocation.Value);
    Copy("usedIn", () => to.General.UsedIn.Value = from.General.UsedIn.Value);
    Copy("attachment", () => to.Text.Attachment.Value = from.Text.Attachment.Value);
    Copy("height", () => to.Text.Height.Value = from.Text.Height.Value);
    Copy("angle", () => to.Text.Angle.Value = from.Text.Angle.Value);
    Copy("maxWidth", () => to.Text.MaxWidth.Value = from.Text.MaxWidth.Value);
    Copy("xOffset", () => to.Text.XOffset.Value = from.Text.XOffset.Value);
    Copy("yOffset", () => to.Text.YOffset.Value = from.Text.YOffset.Value);
    Copy("color", () => to.Text.Color.Value = from.Text.Color.Value);
    Copy("lineweight", () => to.Text.Lineweight.Value = from.Text.Lineweight.Value);
  }

  private static Dictionary<string, object?> Describe(string kind, LabelStyle style, Transaction transaction)
  {
    var components = new List<Dictionary<string, object?>>();
    foreach (var id in style.GetComponentsDrawOrder())
    {
      var component = transaction.GetObject(id, OpenMode.ForRead);
      components.Add(component switch
      {
        LabelStyleTextComponent t => new Dictionary<string, object?>
        {
          ["type"] = "Text",
          ["name"] = Safe(() => t.General.Name),
          ["visible"] = Safe<object?>(() => t.General.Visible.Value),
          ["contents"] = Safe(() => t.Text.Contents.Value),
          ["anchorComponent"] = Safe(() => t.General.AnchorComponent.Value),
          ["anchorLocation"] = Safe(() => t.General.AnchorLocation.Value.ToString()),
          ["attachment"] = Safe(() => t.Text.Attachment.Value.ToString()),
          ["xOffset"] = Safe<object?>(() => t.Text.XOffset.Value),
          ["yOffset"] = Safe<object?>(() => t.Text.YOffset.Value),
          ["height"] = Safe<object?>(() => t.Text.Height.Value),
          ["angle"] = Safe<object?>(() => t.Text.Angle.Value),
        },
        LabelStyleLineComponent l => Basic("Line", Safe(() => l.General.Name), Safe<object?>(() => l.General.Visible.Value)),
        LabelStyleBlockComponent b => Basic("Block", Safe(() => b.General.Name), Safe<object?>(() => b.General.Visible.Value)),
        LabelStyleTickComponent k => Basic("Tick", Safe(() => k.General.Name), Safe<object?>(() => k.General.Visible.Value)),
        _ => Basic(component.GetType().Name, null, null),
      });
    }

    return new Dictionary<string, object?>
    {
      ["labelKind"] = kind,
      ["style"] = CivilObjectUtils.GetName(style),
      ["components"] = components,
    };
  }

  private static Dictionary<string, object?> Basic(string type, string? name, object? visible) => new()
  {
    ["type"] = type,
    ["name"] = name,
    ["visible"] = visible,
  };

  private static LabelStyleTextComponent? FindTextComponent(LabelStyle style, Transaction transaction, string name, OpenMode mode)
  {
    foreach (ObjectId id in style.GetComponents(LabelStyleComponentType.Text))
    {
      var text = CivilObjectUtils.GetRequiredObject<LabelStyleTextComponent>(transaction, id, OpenMode.ForRead);
      if (string.Equals(Safe(() => text.General.Name), name, StringComparison.OrdinalIgnoreCase))
      {
        return mode == OpenMode.ForRead
          ? text
          : CivilObjectUtils.GetRequiredObject<LabelStyleTextComponent>(transaction, id, mode);
      }
    }

    return null;
  }

  private static ObjectId FindStyleId(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, string kind, string name)
  {
    if (TryFindStyleId(civilDoc, transaction, kind, name, out var id))
    {
      return id;
    }

    var available = new List<string>();
    foreach (ObjectId styleId in ProfileViewBandLabelCommands.LabelStyles(civilDoc, kind))
    {
      available.Add(CivilObjectUtils.GetName(transaction.GetObject(styleId, OpenMode.ForRead)) ?? "");
    }

    throw new JsonRpcDispatchException(
      "CIVIL3D.INVALID_INPUT", $"Profile {kind} label style '{name}' was not found. Available: {string.Join(", ", available)}.");
  }

  private static bool TryFindStyleId(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, string kind, string name, out ObjectId found)
  {
    foreach (ObjectId id in ProfileViewBandLabelCommands.LabelStyles(civilDoc, kind))
    {
      if (string.Equals(CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)), name, StringComparison.OrdinalIgnoreCase))
      {
        found = id;
        return true;
      }
    }

    found = ObjectId.Null;
    return false;
  }

  private static T? Safe<T>(Func<T> read)
  {
    try
    {
      return read();
    }
    catch (InvalidOperationException)
    {
      return default;
    }
  }
}
