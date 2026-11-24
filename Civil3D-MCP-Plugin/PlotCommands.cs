using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.Civil.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plots through AutoCAD's plot engine, run as a real command (plotting from the command context is not
/// supported) with BACKGROUNDPLOT 0; the output file is checked before success is reported.
/// view_png: a model-space window or the extents of a profile view as a PNG ("PublishToWeb PNG.pc3"), scaled
/// to fit; the user's view is not touched. layout_plot: a paper layout to PDF ("DWG To PDF.pc3", layout limits
/// at 1:1 on the layout's paper) or PNG (layout limits scaled to fit); the layout is made current while it is
/// plotted (viewports are only drawn for the current layout) and the previous one is restored.
/// </summary>
public static class PlotCommands
{
  private const string PngDevice = "PublishToWeb PNG.pc3";
  private const string PdfDevice = "DWG To PDF.pc3";
  private static readonly Regex Pixels = new(@"\(([\d.]+)\s*x\s*([\d.]+)\s*Pixels\)", RegexOptions.IgnoreCase);

  private sealed record Media(string Name, string Locale, double Width, double Height);

  /// <summary>Output path inside the export roots (FileBoundary: CIVIL3D_EXPORT_ROOTS, canonical, no reparse points)
  /// with the given extension; the folder must exist; an existing file only with overwrite. C65 (2026-10-09): was any
  /// local path, so a localhost client could overwrite any .pdf/.png the user owns (security review 2026-10-08).</summary>
  private static string ResolveOutput(JsonObject? parameters, params string[] extensions)
  {
    var output = PluginRuntime.GetRequiredString(parameters, "output");
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    var full = FileBoundary.ResolveExportPath(output, overwrite, extensions);
    if (!Directory.Exists(Path.GetDirectoryName(full)))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The folder of '{full}' does not exist. Nothing was plotted.");
    if (File.Exists(full) && !overwrite)
      throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"'{full}' exists; pass overwrite=true to replace it. Nothing was plotted.");
    return full;
  }

  private static List<Media> PngSizes(PlotSettingsValidator validator, PlotSettings settings) =>
    validator.GetCanonicalMediaNameList(settings).Cast<string>()
      .Select(name => (Name: name, Locale: validator.GetLocaleMediaName(settings, name)))
      .Select(m => (m.Name, m.Locale, Match: Pixels.Match(m.Locale)))
      .Where(m => m.Match.Success)
      .Select(m => new Media(m.Name, m.Locale,
        double.Parse(m.Match.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Match.Groups[2].Value, CultureInfo.InvariantCulture)))
      .ToList();

  /// <summary>Default PNG size: 4000-8000 px wide with the aspect closest to the area plotted (the largest
  /// size made lines too thin to see, C36 live); otherwise the size whose width is closest to 6000 px.</summary>
  private static Media ChoosePngMedia(List<Media> sizes, double aspect)
  {
    var inRange = sizes.Where(s => s.Width >= 4000 && s.Width <= 8000 && s.Height > 0).ToList();
    return inRange.Count > 0
      ? inRange.OrderBy(s => Math.Abs(Math.Log(s.Width / s.Height / aspect))).ThenByDescending(s => s.Width).First()
      : sizes.OrderBy(s => Math.Abs(s.Width - 6000)).First();
  }

  private static Media? FindMedia(List<Media> sizes, string media) =>
    sizes.FirstOrDefault(s => string.Equals(s.Locale, media, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Name, media, StringComparison.OrdinalIgnoreCase));

  /// <summary>Sets the plot style table when one is given (it must be installed) and makes the plot use it.</summary>
  private static void ApplyStyleSheet(PlotSettingsValidator validator, PlotSettings settings, string? styleSheet)
  {
    if (string.IsNullOrWhiteSpace(styleSheet)) return;
    var installed = validator.GetPlotStyleSheetList().Cast<string>().ToList();
    var match = installed.FirstOrDefault(s => string.Equals(s, styleSheet, StringComparison.OrdinalIgnoreCase))
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Plot style table '{styleSheet}' is not installed. Available: {string.Join(", ", installed)}. Nothing was plotted.");
    validator.SetCurrentStyleSheet(settings, match);
    settings.PlotPlotStyles = true;
  }

  private static void RunEngine(Document doc, PlotInfo info, string full)
  {
    new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled }.Validate(info);
    if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
      throw new JsonRpcDispatchException("CIVIL3D.BUSY", "AutoCAD is already plotting. Nothing was plotted.");

    var background = AcApp.GetSystemVariable("BACKGROUNDPLOT");
    AcApp.SetSystemVariable("BACKGROUNDPLOT", 0);
    try
    {
      if (File.Exists(full)) File.Delete(full);
      using var engine = PlotFactory.CreatePublishEngine();
      engine.BeginPlot(null, null);
      engine.BeginDocument(info, doc.Name, null, 1, true, full);
      using (var page = new PlotPageInfo())
      {
        engine.BeginPage(page, info, true, null);
        engine.BeginGenerateGraphics(null);
        engine.EndGenerateGraphics(null);
        engine.EndPage(null);
      }

      engine.EndDocument(null);
      engine.EndPlot(null);
    }
    finally
    {
      AcApp.SetSystemVariable("BACKGROUNDPLOT", background);
    }

    if (!File.Exists(full) || new FileInfo(full).Length == 0)
      throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"The plot finished but '{full}' was not written.");
  }

  public static Task<object?> PlotViewPngAsync(JsonObject? parameters)
  {
    var full = ResolveOutput(parameters, ".png");
    var profileViewName = PluginRuntime.GetOptionalString(parameters, "profileViewName");
    var x = PluginRuntime.GetOptionalDouble(parameters, "x");
    var y = PluginRuntime.GetOptionalDouble(parameters, "y");
    var width = PluginRuntime.GetOptionalDouble(parameters, "width");
    var height = PluginRuntime.GetOptionalDouble(parameters, "height");
    var margin = PluginRuntime.GetOptionalDouble(parameters, "margin") ?? 2.0;
    var media = PluginRuntime.GetOptionalString(parameters, "media");
    var styleSheet = PluginRuntime.GetOptionalString(parameters, "plotStyleTable");
    var window = x != null && y != null && width is > 0 && height is > 0;
    if (window == !string.IsNullOrWhiteSpace(profileViewName))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give either profileViewName or a window (x, y, width, height > 0). Nothing was plotted.");

    return CivilExecution.WriteInCommandAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      Extents2d area;
      if (window)
      {
        area = new Extents2d(x!.Value, y!.Value, x.Value + width!.Value, y.Value + height!.Value);
      }
      else
      {
        var view = ProfileEditCommands.FindProfileViewByName((CivilDocument)civilDoc, transaction, profileViewName!);
        var e = view.GeometricExtents;
        area = new Extents2d(e.MinPoint.X - margin, e.MinPoint.Y - margin, e.MaxPoint.X + margin, e.MaxPoint.Y + margin);
      }

      var modelSpace = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
      var layout = (Layout)transaction.GetObject(modelSpace.LayoutId, OpenMode.ForRead);
      using var settings = new PlotSettings(layout.ModelType);
      settings.CopyFrom(layout);
      var validator = PlotSettingsValidator.Current;
      validator.SetPlotConfigurationName(settings, PngDevice, null);
      validator.RefreshLists(settings);

      var sizes = PngSizes(validator, settings);
      if (sizes.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.UNSUPPORTED", $"{PngDevice} lists no raster sizes. Nothing was plotted.");
      var areaWidth = area.MaxPoint.X - area.MinPoint.X;
      var areaHeight = area.MaxPoint.Y - area.MinPoint.Y;
      var chosen = media == null ? ChoosePngMedia(sizes, areaWidth / Math.Max(areaHeight, 1e-9))
        : FindMedia(sizes, media) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown media '{media}'. Available: {string.Join(", ", sizes.Select(s => s.Locale))}. Nothing was plotted.");

      // The area is plotted as a temporary named view (plan, no twist, target at the area's centre), not as a
      // plot window: SetPlotWindowArea takes DCS coordinates of the current model view, so a WCS window gave
      // all-white PNGs live (2026-10-07) whenever that view had a target or twist. The view is added in a
      // nested transaction that is aborted after plotting, so the drawing keeps no trace of it. Model space is
      // made current while plotting (as for layouts) and the previous layout is restored afterwards.
      var target = new Point3d((area.MinPoint.X + area.MaxPoint.X) / 2, (area.MinPoint.Y + area.MaxPoint.Y) / 2, 0);
      var viewName = "C3DMCP_PLOT_" + Guid.NewGuid().ToString("N")[..8];
      var manager = LayoutManager.Current;
      var previous = manager.CurrentLayout;
      var switched = !string.Equals(previous, layout.LayoutName, StringComparison.OrdinalIgnoreCase);
      if (switched) manager.CurrentLayout = layout.LayoutName;
      try
      {
        using var scratch = database.TransactionManager.StartTransaction();
        var views = (ViewTable)scratch.GetObject(database.ViewTableId, OpenMode.ForWrite);
        var view = new ViewTableRecord
        {
          Name = viewName,
          IsPaperspaceView = false,
          ViewDirection = Vector3d.ZAxis,
          Target = target,
          CenterPoint = Point2d.Origin,
          ViewTwist = 0,
          Width = areaWidth,
          Height = areaHeight,
        };
        views.Add(view);
        scratch.AddNewlyCreatedDBObject(view, true);

        validator.SetPlotConfigurationName(settings, PngDevice, chosen.Name);
        validator.SetPlotViewName(settings, viewName);
        validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.View);
        validator.SetUseStandardScale(settings, true);
        validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
        validator.SetPlotCentered(settings, true);
        validator.SetPlotRotation(settings, PlotRotation.Degrees000);
        ApplyStyleSheet(validator, settings, styleSheet);
        RunEngine(doc, new PlotInfo { Layout = modelSpace.LayoutId, OverrideSettings = settings }, full);
        // scratch is disposed without Commit: the temporary view is rolled back.
      }
      finally
      {
        if (switched) manager.CurrentLayout = previous;
      }

      var blank = PngIsUniform(full);
      var result = new Dictionary<string, object?>
      {
        ["output"] = full,
        ["bytes"] = new FileInfo(full).Length,
        ["media"] = chosen.Locale,
        ["window"] = new[] { area.MinPoint.X, area.MinPoint.Y, area.MaxPoint.X, area.MaxPoint.Y }.Select(v => Math.Round(v, 3)).ToArray(),
        ["plotView"] = new Dictionary<string, object?>
        {
          ["target"] = new[] { Math.Round(target.X, 3), Math.Round(target.Y, 3) },
          ["width"] = Math.Round(areaWidth, 3),
          ["height"] = Math.Round(areaHeight, 3),
          ["twistDegrees"] = 0,
        },
        ["plotStyleTable"] = settings.CurrentStyleSheet,
        ["blank"] = blank,
      };
      if (blank == true) result["warning"] = "The PNG has a single colour: nothing visible was plotted in this area.";
      return result;
    }, "plotViewPng", TimeSpan.FromSeconds(100));
  }

  /// <summary>Plots a paper layout to .pdf (1:1, layout limits, the layout's paper or the closest DWG To PDF
  /// size) or .png (layout limits scaled to fit a raster size).</summary>
  public static Task<object?> PlotLayoutAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetRequiredString(parameters, "layout");
    var full = ResolveOutput(parameters, ".pdf", ".png");
    var pdf = full.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    var media = PluginRuntime.GetOptionalString(parameters, "media");
    var styleSheet = PluginRuntime.GetOptionalString(parameters, "plotStyleTable");
    var pageSetupName = PluginRuntime.GetOptionalString(parameters, "pageSetup");
    // C151: regenerate every viewport before plotting (default; was the scripts' REGENALL routine, C57's Regen of
    // the current viewport did not draw view dependent graphics in all of them), only the current one, or none.
    var regen = (PluginRuntime.GetOptionalString(parameters, "regen") ?? "all").Trim().ToLowerInvariant();
    if (regen is not ("all" or "current" or "none"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"regen must be all, current or none, not '{regen}'. Nothing was plotted.");

    return CivilExecution.WriteInCommandAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var layout = LayoutCommands.FindLayout(database, transaction, layoutName);
      if (layout.ModelType)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "layout_plot plots a paper layout; use view_png for model space. Nothing was plotted.");

      using var settings = new PlotSettings(false);
      settings.CopyFrom(layout);
      var validator = PlotSettingsValidator.Current;
      var device = pdf ? PdfDevice : PngDevice;
      validator.SetPlotConfigurationName(settings, device, null);
      validator.RefreshLists(settings);

      // A page setup copied from the layout may be centred (window/extents setups); centring is invalid for
      // PlotType.Layout, so it is cleared first where AutoCAD allows it.
      try
      {
        validator.SetPlotCentered(settings, false);
      }
      catch (Autodesk.AutoCAD.Runtime.Exception)
      {
        // already a Layout plot: nothing to clear
      }

      string chosenName, chosenLocale, plotArea = "layout";
      string? scaleText = null;
      if (pageSetupName != null)
      {
        // C72: plot exactly as a named page setup says (paper, pen table, scale such as 1:2, rotation).
        if (!pdf) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "pageSetup plots to PDF only. Nothing was plotted.");
        var setups = (DBDictionary)transaction.GetObject(database.PlotSettingsDictionaryId, OpenMode.ForRead);
        if (!setups.Contains(pageSetupName))
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Page setup '{pageSetupName}' was not found. Nothing was plotted.");
        var named = (PlotSettings)transaction.GetObject(setups.GetAt(pageSetupName), OpenMode.ForRead);
        if (!string.Equals(named.PlotConfigurationName, PdfDevice, StringComparison.OrdinalIgnoreCase))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Page setup '{pageSetupName}' uses '{named.PlotConfigurationName}', not {PdfDevice}. Nothing was plotted.");
        settings.CopyFrom(named);
        validator.SetPlotConfigurationName(settings, PdfDevice, named.CanonicalMediaName);
        chosenName = named.CanonicalMediaName;
        chosenLocale = validator.GetLocaleMediaName(settings, chosenName);
        scaleText = named.UseStandardScale ? named.StdScaleType.ToString()
          : $"1:{(named.CustomPrintScale.Denominator / named.CustomPrintScale.Numerator).ToString("0.###", CultureInfo.InvariantCulture)}";
        // C151: the page setup's own pen table unless plotStyleTable is given (was always the page setup's: a grey
        // plot of a colour page setup came out in colour, 2026-10-12).
      }
      else if (pdf)
      {
        var names = validator.GetCanonicalMediaNameList(settings).Cast<string>().ToList();
        if (media != null)
        {
          chosenName = names.FirstOrDefault(n => string.Equals(n, media, StringComparison.OrdinalIgnoreCase)
              || string.Equals(validator.GetLocaleMediaName(settings, n), media, StringComparison.OrdinalIgnoreCase))
            ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown media '{media}' for {PdfDevice}. Nothing was plotted.");
        }
        else if (names.Contains(layout.CanonicalMediaName, StringComparer.OrdinalIgnoreCase))
        {
          chosenName = names.First(n => string.Equals(n, layout.CanonicalMediaName, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
          // The layout uses another device: the DWG To PDF size closest to its paper (PlotPaperSize is in mm).
          validator.SetClosestMediaName(settings, layout.PlotPaperSize.X, layout.PlotPaperSize.Y, PlotPaperUnit.Millimeters, false);
          chosenName = settings.CanonicalMediaName;
        }

        chosenLocale = validator.GetLocaleMediaName(settings, chosenName);
        validator.SetPlotConfigurationName(settings, PdfDevice, chosenName);
        validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
        validator.SetUseStandardScale(settings, true);
        validator.SetStdScaleType(settings, StdScaleType.StdScale1To1);
      }
      else
      {
        var sizes = PngSizes(validator, settings);
        if (sizes.Count == 0)
          throw new JsonRpcDispatchException("CIVIL3D.UNSUPPORTED", $"{PngDevice} lists no raster sizes. Nothing was plotted.");
        var limits = layout.Limits;
        var aspect = (limits.MaxPoint.X - limits.MinPoint.X) / Math.Max(limits.MaxPoint.Y - limits.MinPoint.Y, 1e-9);
        var chosen = media == null ? ChoosePngMedia(sizes, aspect)
          : FindMedia(sizes, media) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown media '{media}'. Available: {string.Join(", ", sizes.Select(s => s.Locale))}. Nothing was plotted.");
        chosenName = chosen.Name;
        chosenLocale = chosen.Locale;
        // Device and media first, then area, scale and rotation. Centring is refused for PlotType.Layout
        // (eInvalidInput live 2026-10-07), so the layout limits are scaled to fit without it; should AutoCAD
        // refuse fit-to-paper for Layout too, the extents are plotted scaled to fit and centred instead.
        validator.SetPlotConfigurationName(settings, PngDevice, chosenName);
        validator.SetPlotRotation(settings, PlotRotation.Degrees000);
        try
        {
          validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
          validator.SetUseStandardScale(settings, true);
          validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
          plotArea = "layout";
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
          validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Extents);
          validator.SetUseStandardScale(settings, true);
          validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
          validator.SetPlotCentered(settings, true);
          plotArea = "extents";
        }
      }

      ApplyStyleSheet(validator, settings, styleSheet);

      // Viewports are only drawn for the current layout: make it current while plotting, then switch back.
      var manager = LayoutManager.Current;
      var previous = manager.CurrentLayout;
      var switched = !string.Equals(previous, layout.LayoutName, StringComparison.OrdinalIgnoreCase);
      string regenDone = "none";
      if (switched) manager.CurrentLayout = layout.LayoutName;
      try
      {
        // C57: regenerate before plotting. Live 2026-10-07 a profile view's surface profile (view dependent
        // graphics) was missing on most sheets plotted right after their viewports were made; a regen draws it.
        regenDone = Regenerate(doc, regen);
        RunEngine(doc, new PlotInfo { Layout = layout.ObjectId, OverrideSettings = settings }, full);
      }
      finally
      {
        if (switched) manager.CurrentLayout = previous;
      }

      var blank = pdf ? null : PngIsUniform(full);
      var result = new Dictionary<string, object?>
      {
        ["layout"] = layout.LayoutName,
        ["output"] = full,
        ["bytes"] = new FileInfo(full).Length,
        ["device"] = device,
        ["media"] = chosenLocale,
        ["plotStyleTable"] = settings.CurrentStyleSheet,
        ["scale"] = scaleText ?? (pdf ? "1:1" : "fit"),
        ["pageSetup"] = pageSetupName,
        ["regen"] = regenDone,
        ["plotArea"] = plotArea,
        ["plotRotation"] = settings.PlotRotation.ToString(),
        ["blank"] = blank,
      };
      if (blank == true) result["warning"] = "The PNG has a single colour: nothing visible was plotted.";
      return result;
    }, "plotLayout", TimeSpan.FromSeconds(100));
  }

  /// <summary>C151: REGENALL (all viewports) through the document's COM object, synchronously inside the plot command
  /// (SendStringToExecute would run after the plot); falls back to Editor.Regen (current viewport) and says so.</summary>
  private static string Regenerate(Document doc, string regen)
  {
    if (regen == "none") return "none";
    if (regen == "all")
    {
      try
      {
        var acadDocument = doc.GetAcadDocument();
        if (acadDocument != null)
        {
          Civil3DCompatibility.InvokeComMethod(acadDocument, "Regen", 1);   // AcRegenType.acAllViewports
          return "all";
        }
      }
      catch (Exception ex)
      {
        PluginLog.Warn("Plot", $"REGENALL through COM failed ({PluginRuntime.DescribeException(ex)}); regenerating the current viewport.");
      }
    }
    doc.Editor.Regen();
    return regen == "all" ? "current (REGENALL failed, see the plugin log)" : "current";
  }

  /// <summary>True when every pixel of the PNG has the same value (nothing visible was plotted), false when
  /// not, null when the file cannot be checked (interlaced or unreadable). Rows are inflated and unfiltered one
  /// at a time, stopping at the first pixel that differs.</summary>
  internal static bool? PngIsUniform(string path)
  {
    try
    {
      using var file = File.OpenRead(path);
      var signature = new byte[8];
      file.ReadExactly(signature);
      if (signature[0] != 0x89 || signature[1] != (byte)'P') return null;

      int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
      var idat = new MemoryStream();
      var header = new byte[8];
      while (file.Position < file.Length)
      {
        file.ReadExactly(header);
        var length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
        var data = new byte[length];
        file.ReadExactly(data);
        file.Seek(4, SeekOrigin.Current);                    // CRC
        if (type == "IHDR")
        {
          width = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
          height = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
          bitDepth = data[8];
          colorType = data[9];
          interlace = data[12];
        }
        else if (type == "IDAT") idat.Write(data);
        else if (type == "IEND") break;
      }

      if (width <= 0 || height <= 0 || interlace != 0) return null;
      var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
      if (channels == 0) return null;
      var bitsPerPixel = channels * bitDepth;
      var bpp = Math.Max(1, bitsPerPixel / 8);
      var rowBytes = (width * bitsPerPixel + 7) / 8;

      idat.Position = 0;
      using var inflate = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
      var previous = new byte[rowBytes];
      var row = new byte[rowBytes];
      var filter = new byte[1];
      byte[]? first = null;
      for (var r = 0; r < height; r++)
      {
        inflate.ReadExactly(filter);
        inflate.ReadExactly(row);
        for (var i = 0; i < rowBytes; i++)
        {
          int a = i >= bpp ? row[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
          row[i] = (byte)(row[i] + filter[0] switch
          {
            1 => a,
            2 => b,
            3 => (a + b) / 2,
            4 => Paeth(a, b, c),
            _ => 0,
          });
        }

        if (bitsPerPixel >= 8)
        {
          first ??= row[..bpp];
          for (var i = 0; i < rowBytes; i += bpp)
          {
            for (var k = 0; k < bpp; k++)
              if (row[i + k] != first[k]) return false;
          }
        }
        else
        {
          first ??= (byte[])row.Clone();
          for (var i = 0; i < rowBytes - 1; i++)
            if (row[i] != first[0]) return false;
          if (row[rowBytes - 1] != first[rowBytes - 1]) return false;
        }

        (previous, row) = (row, previous);
      }

      return true;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or IndexOutOfRangeException or ArgumentException)
    {
      return null;
    }
  }

  private static int Paeth(int a, int b, int c)
  {
    var p = a + b - c;
    int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
    return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
  }
}
