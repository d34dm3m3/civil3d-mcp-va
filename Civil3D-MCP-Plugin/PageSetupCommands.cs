using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Page setups (C69): the plotter's paper sizes, the drawing's named page setups and the plot settings of every
/// layout; create or update a named page setup (device, paper, pen table, 1:1 or reduced scale such as 1:2 for an
/// A1 sheet on A3, rotation, lineweights) and apply it to layouts. Paper by canonical or locale name, or by size
/// in mm (either orientation). Use case: plot templates in all of the organisation's paper sizes,
/// black-and-white and colour (2026-10-09). Plot type is always Layout (the layout's paper at the given scale from the paper origin).
/// </summary>
public static class PageSetupCommands
{
  private const string DefaultDevice = "DWG To PDF.pc3";
  private static readonly Regex Size = new(@"\(\s*([\d.]+)\s*x\s*([\d.]+)\s*(MM|Inches|Pixels)\s*\)", RegexOptions.IgnoreCase);
  private const double SizeTolerance = 0.6;

  private sealed record Paper(string Canonical, string Locale, double? WidthMm, double? HeightMm);

  public static Task<object?> ListPlotMediaAsync(JsonObject? parameters)
  {
    var device = PluginRuntime.GetOptionalString(parameters, "device") ?? DefaultDevice;
    var contains = PluginRuntime.GetOptionalString(parameters, "contains");
    var width = PluginRuntime.GetOptionalDouble(parameters, "widthMm");
    var height = PluginRuntime.GetOptionalDouble(parameters, "heightMm");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      using var settings = new PlotSettings(false);
      var papers = Papers(settings, device);
      var matches = papers.Where(p => contains == null
          || p.Locale.Contains(contains, StringComparison.OrdinalIgnoreCase) || p.Canonical.Contains(contains, StringComparison.OrdinalIgnoreCase))
        .Where(p => width == null || SameSize(p, width.Value, height!.Value))
        .Select(p => new Dictionary<string, object?> { ["name"] = p.Canonical, ["locale"] = p.Locale, ["widthMm"] = p.WidthMm, ["heightMm"] = p.HeightMm })
        .ToList();
      return new Dictionary<string, object?> { ["device"] = device, ["total"] = papers.Count, ["media"] = matches };
    });
  }

  public static Task<object?> ListPageSetupsAsync(JsonObject? parameters) =>
    CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var setups = new List<Dictionary<string, object?>>();
      var dictionary = (DBDictionary)transaction.GetObject(database.PlotSettingsDictionaryId, OpenMode.ForRead);
      foreach (DBDictionaryEntry entry in dictionary)
        setups.Add(Describe((PlotSettings)transaction.GetObject(entry.Value, OpenMode.ForRead)));
      var layouts = LayoutCommands.Layouts(database, transaction).Where(l => !l.ModelType).OrderBy(l => l.TabOrder)
        .Select(l => { var d = Describe(l); d["layout"] = l.LayoutName; return d; }).ToList();
      // C93b review: PLOTTRANSPARENCYOVERRIDE 0 never plots transparency and 2 always does, whatever the page setup
      // says; only 1 follows plotTransparency.
      object? transparencyOverride = null;
      try
      {
        transparencyOverride = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("PLOTTRANSPARENCYOVERRIDE");
      }
      catch (System.Exception)
      {
        // not available in this build
      }
      return new Dictionary<string, object?> { ["pageSetups"] = setups, ["layouts"] = layouts, ["plotTransparencyOverride"] = transparencyOverride };
    });

  public static Task<object?> SetPageSetupAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var device = PluginRuntime.GetOptionalString(parameters, "device");
    var media = PluginRuntime.GetOptionalString(parameters, "media");
    var width = PluginRuntime.GetOptionalDouble(parameters, "widthMm");
    var height = PluginRuntime.GetOptionalDouble(parameters, "heightMm");
    var styleSheet = PluginRuntime.GetOptionalString(parameters, "plotStyleTable");
    var denominator = PluginRuntime.GetOptionalDouble(parameters, "scaleDenominator");
    var rotation = PluginRuntime.GetOptionalDouble(parameters, "rotationDegrees");
    var plotStyles = PluginRuntime.GetOptionalBool(parameters, "plotStyles");
    var lineweights = PluginRuntime.GetOptionalBool(parameters, "lineweights");
    var scaleLineweights = PluginRuntime.GetOptionalBool(parameters, "scaleLineweights");
    var plotTransparency = PluginRuntime.GetOptionalBool(parameters, "plotTransparency");
    Point2d? origin = null;
    if (PluginRuntime.GetParameter(parameters, "plotOriginMm") is JsonArray xy)
    {
      if (xy.Count != 2) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "plotOriginMm must be [x, y]. Nothing was changed.");
      origin = new Point2d(xy[0]!.GetValue<double>(), xy[1]!.GetValue<double>());
    }
    if ((width == null) != (height == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give widthMm and heightMm together. Nothing was changed.");
    if (media != null && width != null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give media or widthMm/heightMm, not both. Nothing was changed.");
    if (denominator is <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "scaleDenominator must be positive. Nothing was changed.");
    PlotRotation? plotRotation = rotation switch
    {
      null => null,
      0 => PlotRotation.Degrees000,
      90 => PlotRotation.Degrees090,
      180 => PlotRotation.Degrees180,
      270 => PlotRotation.Degrees270,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "rotationDegrees must be 0, 90, 180 or 270. Nothing was changed."),
    };

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var dictionary = (DBDictionary)transaction.GetObject(database.PlotSettingsDictionaryId, OpenMode.ForRead);
      var exists = dictionary.Contains(name);
      if (!exists && media == null && width == null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"A new page setup '{name}' needs media or widthMm/heightMm. Nothing was changed.");

      // Work on a scratch copy and validate it completely before the dictionary is touched.
      using var scratch = new PlotSettings(false);
      if (exists) scratch.CopyFrom(transaction.GetObject(dictionary.GetAt(name), OpenMode.ForRead));
      var validator = PlotSettingsValidator.Current;
      var targetDevice = device ?? (exists && !string.IsNullOrWhiteSpace(scratch.PlotConfigurationName) ? scratch.PlotConfigurationName : DefaultDevice);
      // C74: the paper list is read through a probe of its own. Papers() sets the device with no media, which puts
      // the device's default paper on the object it is given; read through the scratch copy it replaced the paper of
      // every page setup updated without media (live 2026-10-09: 16 setups given a plot offset all became ISO A4).
      using var probe = new PlotSettings(false);
      var papers = Papers(probe, targetDevice);
      string canonical;
      if (media != null)
      {
        canonical = papers.FirstOrDefault(p => string.Equals(p.Canonical, media, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Locale, media, StringComparison.OrdinalIgnoreCase))?.Canonical
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown media '{media}' for {targetDevice}. Use plot_media_list. Nothing was changed.");
      }
      else if (width != null)
      {
        var sized = papers.Where(p => SameSize(p, width.Value, height!.Value)).ToList();
        canonical = (sized.FirstOrDefault(p => p.Locale.Contains("full bleed", StringComparison.OrdinalIgnoreCase)) ?? sized.FirstOrDefault())?.Canonical
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"{targetDevice} has no paper of {width} x {height} mm. Closest: {string.Join("; ", Closest(papers, width.Value, height!.Value))}. Nothing was changed.");
      }
      else
      {
        canonical = papers.Any(p => string.Equals(p.Canonical, scratch.CanonicalMediaName, StringComparison.OrdinalIgnoreCase))
          ? scratch.CanonicalMediaName
          : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Page setup '{name}' has paper '{scratch.CanonicalMediaName}', which {targetDevice} does not offer; give media. Nothing was changed.");
      }

      validator.SetPlotConfigurationName(scratch, targetDevice, canonical);
      validator.SetPlotPaperUnits(scratch, PlotPaperUnit.Millimeters);
      validator.SetPlotType(scratch, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
      if (plotRotation != null) validator.SetPlotRotation(scratch, plotRotation.Value);
      validator.SetPlotOrigin(scratch, origin ?? (exists ? scratch.PlotOrigin : Point2d.Origin));
      if (denominator != null)
      {
        if (Math.Abs(denominator.Value - 1.0) < 1e-9)
        {
          validator.SetUseStandardScale(scratch, true);
          validator.SetStdScaleType(scratch, StdScaleType.StdScale1To1);
        }
        else
        {
          validator.SetUseStandardScale(scratch, false);
          validator.SetCustomPrintScale(scratch, new CustomScale(1.0, denominator.Value));
        }
      }
      if (styleSheet != null)
      {
        var installed = validator.GetPlotStyleSheetList().Cast<string>().ToList();
        var match = installed.FirstOrDefault(s => string.Equals(s, styleSheet, StringComparison.OrdinalIgnoreCase))
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Plot style table '{styleSheet}' is not installed. Available: {string.Join(", ", installed)}. Nothing was changed.");
        validator.SetCurrentStyleSheet(scratch, match);
      }
      if (plotStyles != null) scratch.PlotPlotStyles = plotStyles.Value;
      else if (styleSheet != null) scratch.PlotPlotStyles = true;
      if (lineweights != null) scratch.PrintLineweights = lineweights.Value;
      if (scaleLineweights != null) scratch.ScaleLineweights = scaleLineweights.Value;
      // C93b: layer transparency (the grey base map at 50 %) reaches the PDF only when the page setup plots it.
      if (plotTransparency != null) scratch.PlotTransparency = plotTransparency.Value;

      PlotSettings target;
      if (exists)
      {
        target = (PlotSettings)transaction.GetObject(dictionary.GetAt(name), OpenMode.ForWrite);
      }
      else
      {
        target = new PlotSettings(false) { PlotSettingsName = name };
        target.AddToPlotSettingsDictionary(database);
        transaction.AddNewlyCreatedDBObject(target, true);
      }
      target.CopyFrom(scratch);
      target.PlotSettingsName = name;

      return new Dictionary<string, object?> { ["created"] = !exists, ["pageSetup"] = Describe(target) };
    });
  }

  public static Task<object?> ApplyPageSetupAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var layoutNames = (PluginRuntime.GetParameter(parameters, "layoutNames") as JsonArray ?? new JsonArray())
      .Select(n => n?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList();
    if (layoutNames.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give layoutNames. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var dictionary = (DBDictionary)transaction.GetObject(database.PlotSettingsDictionaryId, OpenMode.ForRead);
      if (!dictionary.Contains(name))
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Page setup '{name}' was not found. Nothing was changed.");
      var setup = (PlotSettings)transaction.GetObject(dictionary.GetAt(name), OpenMode.ForRead);
      var layouts = layoutNames.Select(n => LayoutCommands.FindLayout(database, transaction, n)).ToList();   // throws before any change
      var applied = new List<Dictionary<string, object?>>();
      foreach (var layout in layouts)
      {
        if (layout.ModelType)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Page setups here are for paper layouts, not Model. Nothing was changed.");
        layout.UpgradeOpen();
        layout.CopyFrom(setup);
        var d = Describe(layout);
        d["layout"] = layout.LayoutName;
        applied.Add(d);
      }

      return new Dictionary<string, object?> { ["pageSetup"] = name, ["layouts"] = applied };
    });
  }

  private static List<Paper> Papers(PlotSettings settings, string device)
  {
    var validator = PlotSettingsValidator.Current;
    var devices = validator.GetPlotDeviceList().Cast<string>().ToList();
    var match = devices.FirstOrDefault(d => string.Equals(d, device, StringComparison.OrdinalIgnoreCase))
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Plot device '{device}' was not found. Nothing was changed.");
    validator.SetPlotConfigurationName(settings, match, null);
    validator.RefreshLists(settings);
    return validator.GetCanonicalMediaNameList(settings).Cast<string>().Select(canonical =>
    {
      var locale = validator.GetLocaleMediaName(settings, canonical);
      var m = Size.Match(locale);
      if (!m.Success) m = Size.Match(canonical.Replace('_', ' '));
      double? w = null, h = null;
      if (m.Success)
      {
        var factor = m.Groups[3].Value.Equals("Inches", StringComparison.OrdinalIgnoreCase) ? 25.4
          : m.Groups[3].Value.Equals("MM", StringComparison.OrdinalIgnoreCase) ? 1.0 : double.NaN;
        if (!double.IsNaN(factor))
        {
          w = Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * factor, 2);
          h = Math.Round(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * factor, 2);
        }
      }
      return new Paper(canonical, locale, w, h);
    }).ToList();
  }

  private static bool SameSize(Paper p, double width, double height) =>
    p.WidthMm != null && p.HeightMm != null
    && ((Math.Abs(p.WidthMm.Value - width) <= SizeTolerance && Math.Abs(p.HeightMm.Value - height) <= SizeTolerance)
        || (Math.Abs(p.WidthMm.Value - height) <= SizeTolerance && Math.Abs(p.HeightMm.Value - width) <= SizeTolerance));

  private static IEnumerable<string> Closest(List<Paper> papers, double width, double height) =>
    papers.Where(p => p.WidthMm != null && p.HeightMm != null)
      .OrderBy(p => Math.Min(Math.Abs(p.WidthMm!.Value - width) + Math.Abs(p.HeightMm!.Value - height),
        Math.Abs(p.WidthMm.Value - height) + Math.Abs(p.HeightMm.Value - width)))
      .Take(3).Select(p => p.Locale);

  private static Dictionary<string, object?> Describe(PlotSettings s)
  {
    string scale;
    if (s.UseStandardScale) scale = s.StdScaleType.ToString();
    else
    {
      var c = s.CustomPrintScale;
      scale = c.Numerator == 0 ? "?" : $"1:{(c.Denominator / c.Numerator).ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    // C69c: no PlotSettingsValidator call here. The validator writes to the object it is given (it loads the device's
    // paper list lazily); handed a layout or page setup opened for read it made AutoCAD abort with
    // "INTERNAL ERROR: !dbobji.cpp@8703: eNotOpenForWrite" (live 2026-10-09, listPageSetups as the first plot call of
    // a session). The readable name follows from the canonical one: ISO_A1_(594.00_x_841.00_MM) → ISO A1 (594.00 x 841.00 MM).
    var locale = string.IsNullOrWhiteSpace(s.CanonicalMediaName) ? null : s.CanonicalMediaName.Replace('_', ' ');

    return new Dictionary<string, object?>
    {
      ["name"] = s.PlotSettingsName,
      ["device"] = s.PlotConfigurationName,
      ["media"] = s.CanonicalMediaName,
      ["mediaLocale"] = locale,
      ["paperSizeMm"] = new[] { Math.Round(s.PlotPaperSize.X, 2), Math.Round(s.PlotPaperSize.Y, 2) },
      // Unprintable margins of the paper (left, bottom, right, top in mm, unrotated) and the plot offset.
      ["marginsMm"] = new[] { Math.Round(s.PlotPaperMargins.MinPoint.X, 2), Math.Round(s.PlotPaperMargins.MinPoint.Y, 2),
        Math.Round(s.PlotPaperMargins.MaxPoint.X, 2), Math.Round(s.PlotPaperMargins.MaxPoint.Y, 2) },
      ["plotOriginMm"] = new[] { Math.Round(s.PlotOrigin.X, 2), Math.Round(s.PlotOrigin.Y, 2) },
      ["plotStyleTable"] = string.IsNullOrWhiteSpace(s.CurrentStyleSheet) ? null : s.CurrentStyleSheet,
      ["scale"] = scale,
      ["rotation"] = s.PlotRotation.ToString(),
      ["plotType"] = s.PlotType.ToString(),
      ["plotStyles"] = s.PlotPlotStyles,
      ["lineweights"] = s.PrintLineweights,
      ["scaleLineweights"] = s.ScaleLineweights,
      ["plotTransparency"] = s.PlotTransparency,
    };
  }
}
