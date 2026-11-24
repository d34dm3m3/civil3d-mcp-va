using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// C99h: attach a raster image (PNG, JPG, TIF) in model space with a full affine placement, as IMAGEATTACH does, so that a
/// georeferenced scan lines up with the drawing. Use case (pilot project, 2026-10-11): the other utility owners'
/// maps came as scanned PDFs; they are georeferenced against the base map outside Civil (world file) and shown under the
/// traced lines for checking. Placement: origin (x, y) = the image's lower-left corner, and the vectors of the image's
/// bottom edge (ux, uy) and left edge (vx, vy) in drawing units, so rotation and skew from an affine world file are kept;
/// or width + rotationDegrees for an unskewed image (height from the pixel aspect). The path must lie under the import or
/// export roots; it is stored relative to the drawing when on the same drive (relativePath, default true).
/// C184 (2026-11-11): transparent (default false) turns on the image's transparency, so a PNG with an alpha channel shows the
/// drawing under its transparent pixels. Use case: in a project, a utility owner's plan sheets (vector PDF,
/// white paper) georeferenced over the base map; an opaque image hid the base map and the survey.
/// </summary>
public static class RasterImageCommands
{
  public static Task<object?> AttachRasterAsync(JsonObject? parameters)
  {
    var path = FileBoundary.ResolveLocalReadPath(PluginRuntime.GetRequiredString(parameters, "path"), ".png", ".jpg", ".jpeg", ".tif", ".tiff");
    var name = (PluginRuntime.GetOptionalString(parameters, "name") ?? Path.GetFileNameWithoutExtension(path)).Trim();
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var ux = PluginRuntime.GetOptionalDouble(parameters, "ux");
    var uy = PluginRuntime.GetOptionalDouble(parameters, "uy");
    var vx = PluginRuntime.GetOptionalDouble(parameters, "vx");
    var vy = PluginRuntime.GetOptionalDouble(parameters, "vy");
    var width = PluginRuntime.GetOptionalDouble(parameters, "width");
    var rotation = (PluginRuntime.GetOptionalDouble(parameters, "rotationDegrees") ?? 0.0) * Math.PI / 180.0;
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    var createLayer = PluginRuntime.GetOptionalBool(parameters, "createLayer") ?? false;
    var relativePath = PluginRuntime.GetOptionalBool(parameters, "relativePath") ?? true;
    var fade = PluginRuntime.GetOptionalInt(parameters, "fade") ?? 0;
    var transparent = PluginRuntime.GetOptionalBool(parameters, "transparent") ?? false;   // C184
    var vectors = new[] { ux, uy, vx, vy };
    if (vectors.Any(v => v != null) && vectors.Any(v => v == null))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give all of ux, uy, vx, vy, or none of them (then width). Nothing was attached.");
    if (vectors.All(v => v == null) && (width is null or <= 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give the placement vectors ux, uy, vx, vy or a positive width. Nothing was attached.");
    if (fade is < 0 or > 100)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "fade must be 0-100. Nothing was attached.");
    if (string.IsNullOrWhiteSpace(name))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "name must not be empty. Nothing was attached.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var dictId = RasterImageDef.GetImageDictionary(database);
      if (dictId.IsNull)
        dictId = RasterImageDef.CreateImageDictionary(database);
      var dict = (DBDictionary)transaction.GetObject(dictId, OpenMode.ForWrite);
      if (dict.Contains(name))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"An image definition '{name}' exists already; give another name. Nothing was attached.");

      var host = DocumentCommands.PathOf(doc);
      var folder = host == null ? null : Path.GetDirectoryName(host);
      var stored = path;
      if (relativePath && folder != null && string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))
        stored = ExternalReferenceCommands.Relative(folder, path);

      var def = new RasterImageDef { SourceFileName = stored };
      var defId = dict.SetAt(name, def);
      transaction.AddNewlyCreatedDBObject(def, true);
      if (!string.Equals(stored, path, StringComparison.OrdinalIgnoreCase))
        def.ActiveFileName = path;                 // load from the absolute path, keep the relative one in the drawing
      def.Load();
      var size = def.Size;                          // pixels
      if (size.X <= 0 || size.Y <= 0)
        throw new JsonRpcDispatchException("CIVIL3D.TRANSACTION_FAILED", $"The image '{path}' could not be read (size {size.X} x {size.Y}). Nothing was attached.");

      Vector3d u, v;
      if (ux != null)
      {
        u = new Vector3d(ux.Value, uy!.Value, 0);
        v = new Vector3d(vx!.Value, vy!.Value, 0);
      }
      else
      {
        var w = width!.Value;
        var h = w * size.Y / size.X;
        u = new Vector3d(w * Math.Cos(rotation), w * Math.Sin(rotation), 0);
        v = new Vector3d(-h * Math.Sin(rotation), h * Math.Cos(rotation), 0);
      }
      if (u.Length <= 0 || v.Length <= 0 || u.CrossProduct(v).Z <= 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The placement vectors must be non-zero and v must lie counterclockwise of u (the image would be mirrored). Nothing was attached.");

      var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
      var image = new RasterImage();
      image.SetDatabaseDefaults(database);
      image.ImageDefId = defId;
      image.Orientation = new CoordinateSystem3d(new Point3d(x, y, 0), u, v);
      image.ShowImage = true;
      image.Fade = (byte)fade;
      image.ImageTransparency = transparent;   // C184: a PNG's alpha channel shows what lies under the image
      if (!string.IsNullOrWhiteSpace(layerName))
        image.LayerId = LookupUtils.GetOrCreateLayerId(database, transaction, layerName, createLayer);
      var id = space.AppendEntity(image);
      transaction.AddNewlyCreatedDBObject(image, true);
      RasterImage.EnableReactors(true);
      image.AssociateRasterDef(def);

      return new Dictionary<string, object?>
      {
        ["handle"] = image.Handle.ToString(),
        ["name"] = name,
        ["path"] = path,
        ["storedPath"] = stored,
        ["pixels"] = new[] { size.X, size.Y },
        ["origin"] = new[] { x, y },
        ["u"] = new[] { u.X, u.Y },
        ["v"] = new[] { v.X, v.Y },
        ["layer"] = image.Layer,
        ["fade"] = fade,
        ["transparent"] = image.ImageTransparency,
      };
    });
  }

  /// <summary>C109: detach raster images by definition name, as IMAGE Detach does: every image that shows the definition (in
  /// any space or block) is erased, then the definition. Use case (pilot project, 2026-10-11): the georeferenced
  /// scans of the other owners' maps were scrapped; erasing the images alone left the definitions with their file paths.
  /// Unknown names fail before anything changes.</summary>
  public static Task<object?> DetachRasterAsync(JsonObject? parameters)
  {
    var names = (PluginRuntime.GetParameter(parameters, "names") as JsonArray ?? new JsonArray())
      .Select(n => n?.GetValue<string>()?.Trim() ?? "").Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (names.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "names must list at least one image definition name. Nothing was detached.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var dictId = RasterImageDef.GetImageDictionary(database);
      var dict = dictId.IsNull ? null : (DBDictionary)transaction.GetObject(dictId, OpenMode.ForRead);
      var available = new List<string>();
      if (dict != null) foreach (DBDictionaryEntry entry in dict) available.Add(entry.Key);
      var missing = names.Where(n => !available.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
      if (missing.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"No image definition {string.Join(", ", missing)}. Available: {string.Join(", ", available)}. Nothing was detached.");

      var defIds = names.ToDictionary(n => dict!.GetAt(n), n => n);
      var erasedImages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
      foreach (ObjectId btrId in table)
      {
        var btr = (BlockTableRecord)transaction.GetObject(btrId, OpenMode.ForRead);
        if (btr.IsFromExternalReference || btr.IsDependent) continue;
        foreach (ObjectId id in btr)
        {
          if (id.ObjectClass.DxfName != "IMAGE") continue;
          if (transaction.GetObject(id, OpenMode.ForRead) is not RasterImage image || !defIds.TryGetValue(image.ImageDefId, out var name)) continue;
          image.UpgradeOpen();
          image.Erase();
          erasedImages[name] = erasedImages.GetValueOrDefault(name) + 1;
        }
      }

      dict!.UpgradeOpen();
      var detached = new List<Dictionary<string, object?>>();
      foreach (var (defId, name) in defIds)
      {
        var def = (RasterImageDef)transaction.GetObject(defId, OpenMode.ForWrite);
        var source = def.SourceFileName;
        if (def.IsLoaded) def.Unload(true);
        dict.Remove(defId);
        def.Erase();
        detached.Add(new Dictionary<string, object?> { ["name"] = name, ["path"] = source, ["imagesErased"] = erasedImages.GetValueOrDefault(name) });
      }
      return new Dictionary<string, object?> { ["detached"] = detached };
    });
  }
}
