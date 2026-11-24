using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// C98c: label expressions (Settings tab: a label type's Expressions). Use case (pilot project): the
/// pipe network bands ("Spillv-Mtrl &amp; Dim", "Vatten-Mtrl &amp; Dim") cannot show the outer diameter; the Uponor PE100
/// families carry only the inner diameter and the wall thickness, so an expression has to add them up.
/// An expression collection belongs to a label style collection (LabelStyleCollection.Expressions, the only place the
/// API exposes ExpressionCollection), addressed as in C90 by its path under CivilDocument.Styles.LabelStyles. Since
/// Civil 3D 2021.2 the expressions of PipeLabelStyles.PlanProfileLabelStyles (and of the pressure pipe labels) appear as
/// properties in the labels of pipe network data bands, so the band styles use that collection; the band style
/// collections themselves have no expressions. An expression: Name (fixed once created), Description, ExpressionString
/// (label properties in braces, e.g. {Wall Thickness}, values in drawing units: metres here, angles in radians),
/// FormatResultAs (ExpressionFormatedType: Double, Dimension, Distance, Elevation, …) and ReferenceString (the field code
/// a text component uses). Writes go to the collection node, which is not opened in the call's transaction and may not
/// follow it, so everything is checked first, one expression changes per call, a value Civil 3D refuses or does not keep
/// is undone by hand (a new expression removed again, the earlier values put back), and the methods are refused inside a
/// single-transaction batch (BatchCommands), where an aborted batch could not undo them.
/// </summary>
public static class LabelExpressionCommands
{
  private static readonly Regex FieldCode = new(@"<\[\s*([^\[\]()|<>{}]+?)\s*\(", RegexOptions.CultureInvariant);
  private static readonly Regex Braced = new(@"\{([^{}]*)\}", RegexOptions.CultureInvariant);
  private const string ForbiddenNameChars = "{}[]<>()|\"";

  /// <summary>Label style collections whose expressions the pipe network band labels use (Civil 3D 2021.2 and later).</summary>
  private static readonly string[] BandCollections = { "PipeLabelStyles.PlanProfileLabelStyles", "StructureLabelStyles.LabelStyles" };

  private static T? Safe<T>(Func<T> read)
  {
    try
    {
      return read();
    }
    catch (Exception ex) when (ex is not JsonRpcDispatchException)
    {
      return default;   // Civil's wrappers throw several types (CivilException derives from Exception itself)
    }
  }

  /// <summary>C98c: the expressions of a label style collection (name, description, expression, result format, the
  /// field code that refers to it, the label styles, band styles and other expressions that use it), the result formats,
  /// and the property names the collection's label texts use (a guide to the names an expression can put in braces).</summary>
  public static Task<object?> ListLabelExpressionsAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "collection");
    var contains = PluginRuntime.GetOptionalString(parameters, "contains")?.Trim();
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var (collection, resolved) = LabelStyleTextCommands.ResolveCollection(civilDoc, path);
      var expressions = Expressions(collection, resolved);
      var own = CollectionTexts(collection, transaction);
      var texts = Texts(own, civilDoc, resolved, transaction);
      var list = new List<Dictionary<string, object?>>();
      foreach (var expression in All(expressions))
      {
        var described = Describe(expression);
        var name = described["name"] as string ?? "";
        if (!string.IsNullOrEmpty(contains) && !name.Contains(contains, StringComparison.OrdinalIgnoreCase)) continue;
        var users = UsedBy(name, texts, expressions);
        described["usedBy"] = users.Count > 0 ? users : null;
        list.Add(described);
      }

      return new Dictionary<string, object?>
      {
        ["collection"] = resolved,
        ["count"] = list.Count,
        ["expressions"] = list,
        ["formatResultAsTypes"] = Enum.GetNames<ExpressionFormatedType>(),
        ["fieldNamesInUse"] = FieldNames(own),
        ["note"] = "In an expression, label properties go in braces ({Wall Thickness}) with the names the label composer lists (the "
          + "names in the field codes <[Name(...)]> of fieldNamesInUse), in drawing units (metres, angles in radians). reference "
          + "is the field code for a text component. Expressions of PipeLabelStyles.PlanProfileLabelStyles also appear in the "
          + "labels of pipe network band styles (Civil 3D 2021.2 and later).",
      };
    });
  }

  /// <summary>
  /// C98c: creates an expression (name new: expression required; description empty and formatResultAs Double unless
  /// given) or changes one (expression, description and/or formatResultAs). The name follows Civil's field code rules
  /// (none of { } [ ] &lt; &gt; ( ) | "), braces and parentheses must pair, and a name in braces that the collection's
  /// labels do not use is reported as a warning. dryRun reports without writing.
  /// </summary>
  public static Task<object?> SetLabelExpressionAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "collection");
    var name = CheckName(PluginRuntime.GetRequiredString(parameters, "name"));
    var text = PluginRuntime.GetOptionalString(parameters, "expression");
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var formatText = PluginRuntime.GetOptionalString(parameters, "formatResultAs")?.Trim();
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    if (text == null && description == null && formatText == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give expression, description and/or formatResultAs. Nothing was changed.");
    var braced = text == null ? new List<string>() : CheckExpression(text);
    ExpressionFormatedType? format = null;
    if (formatText != null)
    {
      if (!Enum.TryParse<ExpressionFormatedType>(formatText, true, out var parsed) || !Enum.IsDefined(parsed))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"formatResultAs '{formatText}' is unknown. Use {string.Join(", ", Enum.GetNames<ExpressionFormatedType>())}. Nothing was changed.");
      format = parsed;
    }

    object? Body(Autodesk.AutoCAD.ApplicationServices.Document doc, CivilDocument civilDoc, Database database, Transaction transaction)
    {
      var (collection, resolved) = LabelStyleTextCommands.ResolveCollection(civilDoc, path);
      var expressions = Expressions(collection, resolved);
      var existing = Find(expressions, name);
      var names = All(expressions).Select(e => Safe(() => e.Name) ?? "").ToList();
      if (existing == null && text == null)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"{resolved} has no expression '{name}'; give expression to create it. Expressions: {(names.Count == 0 ? "none" : string.Join(", ", names))}. Nothing was changed.");
      var exact = existing == null ? name : Safe(() => existing.Name) ?? name;

      var fieldNames = FieldNames(CollectionTexts(collection, transaction));
      if (existing == null && fieldNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"'{name}' is also the name of a label property in {resolved}; choose another expression name. Nothing was changed.");
      var known = new HashSet<string>(fieldNames.Concat(names), StringComparer.OrdinalIgnoreCase);
      var unseen = braced.Where(b => !known.Contains(b)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
      var warnings = unseen.Count == 0 ? null : new List<string>
      {
        $"Not used in any label text of {resolved}: {string.Join(", ", unseen.Select(u => "{" + u + "}"))}. Check the names against the label "
          + "composer's property list; a name Civil 3D does not know makes the label show ???.",
      };

      var before = existing == null ? null : Describe(existing);
      var plannedFormat = format ?? (existing == null ? ExpressionFormatedType.Double : null);
      var plannedDescription = description ?? (existing == null ? "" : null);
      if (dryRun)
      {
        return new Dictionary<string, object?>
        {
          ["collection"] = resolved,
          ["name"] = exact,
          ["dryRun"] = true,
          ["created"] = existing == null,
          ["before"] = before,
          ["after"] = new Dictionary<string, object?>
          {
            ["name"] = exact,
            ["description"] = (object?)plannedDescription ?? before?["description"],
            ["expression"] = (object?)text ?? before?["expression"],
            ["formatResultAs"] = (object?)plannedFormat?.ToString() ?? before?["formatResultAs"],
          },
          ["warnings"] = warnings,
          ["note"] = "Dry run: nothing was changed.",
        };
      }

      // The earlier values of an existing expression, to put back should Civil 3D refuse or not keep the change.
      var old = existing == null
        ? default
        : (Text: Safe(() => existing.ExpressionString), Description: Safe(() => existing.Description), Format: Safe<ExpressionFormatedType?>(() => existing.FormatResultAs));
      string Undo() => existing == null
        ? Remove(expressions, exact) ? "The new expression was removed again; nothing was changed." : "The new expression could not be removed again; delete it with label_expression_delete."
        : Restore(existing, old.Text, old.Description, old.Format) ? "The earlier values were put back; nothing was changed." : "The earlier values could not all be put back; check it with label_expressions_list.";

      if (existing == null)
      {
        Expression? added;
        try
        {
          added = expressions.Add(exact, plannedDescription!, text!);
        }
        catch (Exception ex) when (ex is not JsonRpcDispatchException)
        {
          // Add may have stored the expression before it failed; it did not exist before, so whatever has the name goes.
          var leftOver = Remove(expressions, exact);
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Civil 3D refused the expression '{exact}': {ex.Message} {(leftOver ? "What it had stored was removed again; nothing was changed." : "Nothing was changed.")}");
        }

        if (added == null)
          throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Civil 3D returned no expression for '{exact}'. {Undo()}");
        try
        {
          added.FormatResultAs = plannedFormat!.Value;
        }
        catch (Exception ex) when (ex is not JsonRpcDispatchException)
        {
          throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Civil 3D refused formatResultAs {plannedFormat} for '{exact}': {ex.Message} {Undo()}");
        }
      }
      else
      {
        try
        {
          // C98g (live 2026-10-10): writing ExpressionString, even unchanged, left the band label texts that already used
          // the expression drawing nothing (copied band styles blank after a rerun); only what differs is written.
          if (text != null && Squeeze(Safe(() => existing.ExpressionString)) != Squeeze(text)) existing.ExpressionString = text;
          if (description != null && (Safe(() => existing.Description) ?? "") != description) existing.Description = description;
          if (format != null && Safe<ExpressionFormatedType?>(() => existing.FormatResultAs) != format.Value) existing.FormatResultAs = format.Value;
        }
        catch (Exception ex) when (ex is not JsonRpcDispatchException)
        {
          throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Civil 3D refused the change of '{exact}': {ex.Message} {Undo()}");
        }
      }

      // Read back from the collection: what Civil 3D did not keep is undone.
      var stored = FindExact(expressions, exact);
      if (stored == null)
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"'{exact}' is not in {resolved} after the change; check it with label_expressions_list.");
      var lost = new List<string>();
      if (text != null && Squeeze(Safe(() => stored.ExpressionString)) != Squeeze(text)) lost.Add($"expression (read back '{Safe(() => stored.ExpressionString)}')");
      if (plannedDescription != null && (Safe(() => stored.Description) ?? "") != plannedDescription) lost.Add($"description (read back '{Safe(() => stored.Description)}')");
      if (plannedFormat != null && Safe<ExpressionFormatedType?>(() => stored.FormatResultAs) != plannedFormat) lost.Add($"formatResultAs (read back {Safe(() => stored.FormatResultAs.ToString())})");
      if (lost.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Civil 3D did not keep {string.Join(", ", lost)} for '{exact}'. {Undo()}");

      return new Dictionary<string, object?>
      {
        ["collection"] = resolved, ["name"] = exact, ["created"] = existing == null, ["before"] = before, ["after"] = Describe(stored), ["warnings"] = warnings,
      };
    }

    return dryRun ? CivilExecution.ReadAsync<object?>(Body) : CivilExecution.WriteAsync<object?>(Body);
  }

  /// <summary>C98c: deletes an expression. One still used by a label text of the collection, a pipe network band label
  /// (for the pipe and structure collections) or another expression is refused unless force (those would then show
  /// ???). dryRun reports without deleting.</summary>
  public static Task<object?> DeleteLabelExpressionAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetRequiredString(parameters, "collection");
    var name = PluginRuntime.GetRequiredString(parameters, "name").Trim();
    var force = PluginRuntime.GetOptionalBool(parameters, "force") ?? false;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;

    object? Body(Autodesk.AutoCAD.ApplicationServices.Document doc, CivilDocument civilDoc, Database database, Transaction transaction)
    {
      var (collection, resolved) = LabelStyleTextCommands.ResolveCollection(civilDoc, path);
      var expressions = Expressions(collection, resolved);
      var existing = Find(expressions, name);
      if (existing == null)
      {
        var names = All(expressions).Select(e => Safe(() => e.Name) ?? "").ToList();
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"{resolved} has no expression '{name}'. Expressions: {(names.Count == 0 ? "none" : string.Join(", ", names))}. Nothing was changed.");
      }

      var described = Describe(existing);
      var exact = described["name"] as string ?? name;
      var users = UsedBy(exact, Texts(CollectionTexts(collection, transaction), civilDoc, resolved, transaction), expressions);
      if (users.Count > 0 && !force)
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
          $"'{exact}' is used by {string.Join(", ", users)}; those would show ???. force: true deletes it anyway. Nothing was changed.");
      if (dryRun)
        return new Dictionary<string, object?> { ["collection"] = resolved, ["dryRun"] = true, ["deleted"] = described, ["usedBy"] = users.Count > 0 ? users : null, ["note"] = "Dry run: nothing was changed." };

      if (!Remove(expressions, exact))
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"Civil 3D did not delete '{exact}' from {resolved}. Nothing was changed.");
      if (FindExact(expressions, exact) != null)
        throw new JsonRpcDispatchException("CIVIL3D.OPERATION_FAILED", $"'{exact}' is still in {resolved} after the deletion; check it with label_expressions_list.");
      return new Dictionary<string, object?> { ["collection"] = resolved, ["deleted"] = described, ["usedBy"] = users.Count > 0 ? users : null };
    }

    return dryRun ? CivilExecution.ReadAsync<object?>(Body) : CivilExecution.WriteAsync<object?>(Body);
  }

  // ---------------------------------------------------------------- helpers

  private static ExpressionCollection Expressions(LabelStyleCollection collection, string path)
  {
    try
    {
      return collection.Expressions ?? throw new InvalidOperationException("no expression collection");
    }
    catch (Exception ex) when (ex is not JsonRpcDispatchException)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path} has no expressions ({ex.Message}). Nothing was changed.");
    }
  }

  private static List<Expression> All(ExpressionCollection expressions)
  {
    var list = new List<Expression>();
    foreach (var expression in expressions)
      if (expression != null) list.Add(expression);
    return list;
  }

  /// <summary>An expression by name: exact first, then without case.</summary>
  private static Expression? Find(ExpressionCollection expressions, string name) =>
    FindExact(expressions, name) ?? All(expressions).FirstOrDefault(e => string.Equals(Safe(() => e.Name), name, StringComparison.OrdinalIgnoreCase));

  private static Expression? FindExact(ExpressionCollection expressions, string name) =>
    All(expressions).FirstOrDefault(e => string.Equals(Safe(() => e.Name), name, StringComparison.Ordinal));

  /// <summary>Removes the expression with exactly this name, by its index in the collection itself; false when it is not
  /// there or Civil 3D refuses.</summary>
  private static bool Remove(ExpressionCollection expressions, string name)
  {
    try
    {
      var count = expressions.Count;
      for (var i = 0; i < count; i++)
      {
        var index = i;
        if (!string.Equals(Safe(() => expressions[index]?.Name), name, StringComparison.Ordinal)) continue;
        expressions.RemoveAt(index);
        return true;
      }
      return false;
    }
    catch (Exception ex) when (ex is not JsonRpcDispatchException)
    {
      return false;
    }
  }

  private static bool Restore(Expression expression, string? text, string? description, ExpressionFormatedType? format)
  {
    try
    {
      if (text != null && expression.ExpressionString != text) expression.ExpressionString = text;
      if (description != null && expression.Description != description) expression.Description = description;
      if (format != null && expression.FormatResultAs != format.Value) expression.FormatResultAs = format.Value;
      return true;
    }
    catch (Exception ex) when (ex is not JsonRpcDispatchException)
    {
      return false;
    }
  }

  private static Dictionary<string, object?> Describe(Expression expression) => new()
  {
    ["name"] = Safe(() => expression.Name),
    ["description"] = Safe(() => expression.Description),
    ["expression"] = Safe(() => expression.ExpressionString),
    ["formatResultAs"] = Safe(() => expression.FormatResultAs.ToString()),
    ["reference"] = Safe(() => expression.ReferenceString),
  };

  /// <summary>The text contents of every label style of the collection (child styles included), by style name.</summary>
  private static List<(string Owner, string Contents)> CollectionTexts(LabelStyleCollection collection, Transaction transaction)
  {
    var texts = new List<(string, string)>();
    foreach (var id in LabelStyleTextCommands.AllStyleIds(collection))
    {
      if (transaction.GetObject(id, OpenMode.ForRead) is not LabelStyle style) continue;
      var parts = Safe(() => LabelStyleTextCommands.TextParts(style, transaction)) ?? new List<LabelStyleTextCommands.TextPart>();
      foreach (var part in parts)
        if (!string.IsNullOrEmpty(part.Contents)) texts.Add((style.Name, part.Contents));
    }
    return texts;
  }

  /// <summary>The collection's own label texts, plus the pipe network band labels for the pipe and structure collections.</summary>
  private static List<(string Owner, string Contents)> Texts(List<(string Owner, string Contents)> own, CivilDocument civilDoc, string path, Transaction transaction) =>
    BandCollections.Contains(path, StringComparer.OrdinalIgnoreCase) ? own.Concat(BandTexts(civilDoc, transaction)).ToList() : own;

  /// <summary>The label texts of the pipe network band styles (pipe and structure groups), where the expressions of the
  /// pipe and structure label styles are used since Civil 3D 2021.2.</summary>
  private static List<(string Owner, string Contents)> BandTexts(CivilDocument civilDoc, Transaction transaction)
  {
    var texts = new List<(string, string)>();
    foreach (ObjectId id in civilDoc.Styles.BandStyles.ProfileViewPipeNetworkBandStyles)
    {
      if (id.IsNull || id.IsErased || transaction.GetObject(id, OpenMode.ForRead) is not PipeNetworkBandStyle band) continue;
      foreach (var (group, labelId) in new[] { ("pipe", Safe(() => band.PipeLabelStyleId)), ("structure", Safe(() => band.StructureLabelStyleId)) })
      {
        if (labelId.IsNull || labelId.IsErased || transaction.GetObject(labelId, OpenMode.ForRead) is not LabelStyle label) continue;
        var parts = Safe(() => LabelStyleTextCommands.TextParts(label, transaction)) ?? new List<LabelStyleTextCommands.TextPart>();
        foreach (var part in parts)
          if (!string.IsNullOrEmpty(part.Contents)) texts.Add(($"band style '{band.Name}' ({group})", part.Contents));
      }
    }
    return texts;
  }

  /// <summary>The owners whose texts refer to the name as a field code (&lt;[Name(…)]&gt;), and the other expressions of
  /// the collection that have it in braces.</summary>
  private static List<string> UsedBy(string name, List<(string Owner, string Contents)> texts, ExpressionCollection expressions)
  {
    var field = new Regex(@"<\[\s*" + Regex.Escape(name) + @"\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    var braced = new Regex(@"\{\s*" + Regex.Escape(name) + @"\s*\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    var users = texts.Where(t => field.IsMatch(t.Contents)).Select(t => t.Owner).Distinct(StringComparer.Ordinal).ToList();
    foreach (var other in All(expressions))
    {
      var otherName = Safe(() => other.Name);
      if (otherName == null || string.Equals(otherName, name, StringComparison.Ordinal)) continue;
      if (braced.IsMatch(Safe(() => other.ExpressionString) ?? "")) users.Add($"expression '{otherName}'");
    }
    return users;
  }

  /// <summary>The property names in the field codes of the texts, sorted.</summary>
  private static List<string> FieldNames(IEnumerable<(string Owner, string Contents)> texts) => texts
    .SelectMany(t => FieldCode.Matches(t.Contents).Select(m => m.Groups[1].Value.Trim()))
    .Where(n => n.Length > 0)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
    .ToList();

  private static string CheckName(string raw)
  {
    var name = raw.Trim();
    if (name.Length == 0 || name.Length > 255 || name.IndexOfAny(ForbiddenNameChars.ToCharArray()) >= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"'{raw}' is not a valid expression name (1-255 characters, none of {{ }} [ ] < > ( ) | \"). Nothing was changed.");
    return name;
  }

  /// <summary>Checks the pairing of braces (not nested) and parentheses and returns the names in braces.</summary>
  private static List<string> CheckExpression(string expression)
  {
    if (string.IsNullOrWhiteSpace(expression))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "expression is empty. Nothing was changed.");
    if (expression.Contains("<[", StringComparison.Ordinal))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "expression contains a field code <[...]>; in an expression a property goes in braces, e.g. {Wall Thickness}. Nothing was changed.");
    var depth = 0;
    var inBrace = false;
    foreach (var c in expression)
    {
      switch (c)
      {
        case '{' when inBrace:
        case '}' when !inBrace:
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The braces in '{expression}' do not pair (one property per pair, not nested). Nothing was changed.");
        case '{': inBrace = true; break;
        case '}': inBrace = false; break;
        case '(' when !inBrace: depth++; break;
        case ')' when !inBrace:
          if (--depth < 0)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The parentheses in '{expression}' do not pair. Nothing was changed.");
          break;
      }
    }
    if (inBrace || depth != 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The {(inBrace ? "braces" : "parentheses")} in '{expression}' do not pair. Nothing was changed.");
    var names = Braced.Matches(expression).Select(m => m.Groups[1].Value.Trim()).ToList();
    if (names.Any(n => n.Length == 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{expression}' has empty braces. Nothing was changed.");
    return names;
  }

  private static string Squeeze(string? text) => Regex.Replace(text ?? "", @"\s+", "");
}
