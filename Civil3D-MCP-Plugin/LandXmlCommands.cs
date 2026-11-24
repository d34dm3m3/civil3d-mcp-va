using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// LandXML 1.2 export of TIN surfaces (C59, 2026-10-08): the visible triangles of each surface as Pnts/Faces, for
/// machine control and model exchange. A surface may be clipped to a polygon (a triangle is kept when its centroid
/// lies inside), so a large ground model can be cut to the work area. Points are "northing easting elevation" as
/// LandXML requires. The file goes through FileBoundary (export roots, .xml/.landxml) and is written atomically.
/// Civil 3D has its own LandXML export only as an interactive dialog.
/// </summary>
public static class LandXmlCommands
{
  private sealed record SurfaceRequest(string Name, string? NewName, string? Description, List<(double X, double Y)>? Clip);

  public static Task<object?> ExportLandXmlAsync(JsonObject? parameters)
  {
    var outputPath = PluginRuntime.GetRequiredString(parameters, "outputPath");
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    var decimals = PluginRuntime.GetOptionalInt(parameters, "decimals") ?? 3;
    if (decimals is < 0 or > 6)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "decimals must be 0-6. Nothing was written.");
    var requests = ParseSurfaces(parameters);
    var crs = PluginRuntime.GetParameter(parameters, "coordinateSystem") as JsonObject;
    var projectName = PluginRuntime.GetOptionalString(parameters, "projectName");
    // checked before Civil is touched, so a refused path changes nothing
    _ = FileBoundary.ResolveExportPath(outputPath, overwrite, ".xml", ".landxml");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surfaces = new List<LandXmlWriter.SurfaceData>();
      var summary = new List<Dictionary<string, object?>>();
      foreach (var request in requests)
      {
        var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, request.Name, OpenMode.ForRead);
        var tin = surface as TinSurface ?? throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT", $"Surface '{surface.Name}' is a {surface.GetType().Name}; LandXML export needs a TIN surface. Nothing was written.");
        var data = new LandXmlWriter.SurfaceData(request.NewName ?? tin.Name, request.Description ?? tin.Description);
        var scanned = 0;
        var (minX, minY, maxX, maxY) = request.Clip == null ? (0d, 0d, 0d, 0d)
          : (request.Clip.Min(p => p.X), request.Clip.Min(p => p.Y), request.Clip.Max(p => p.X), request.Clip.Max(p => p.Y));
        foreach (TinSurfaceTriangle triangle in tin.GetTriangles(false))
        {
          scanned++;
          var a = triangle.Vertex1.Location;
          var b = triangle.Vertex2.Location;
          var c = triangle.Vertex3.Location;
          if (request.Clip != null)
          {
            var cx = (a.X + b.X + c.X) / 3.0;
            var cy = (a.Y + b.Y + c.Y) / 3.0;
            if (cx < minX || cx > maxX || cy < minY || cy > maxY || !LandXmlWriter.Inside(request.Clip, cx, cy))
              continue;
          }
          data.AddTriangle((a.X, a.Y, a.Z), (b.X, b.Y, b.Z), (c.X, c.Y, c.Z));
        }

        if (data.FaceCount == 0)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Surface '{tin.Name}' has no visible triangles{(request.Clip != null ? " inside the clip polygon" : "")}. Nothing was written.");
        surfaces.Add(data);
        summary.Add(new Dictionary<string, object?>
        {
          ["surface"] = tin.Name,
          ["exportedAs"] = data.Name,
          ["clipped"] = request.Clip != null,
          ["trianglesScanned"] = scanned,
          ["points"] = data.PointCount,
          ["faces"] = data.FaceCount,
          ["area2d"] = Math.Round(data.Area2D, 1),
          ["area3d"] = Math.Round(data.Area3D, 1),
          ["elevationMin"] = Math.Round(data.ElevationMin, 3),
          ["elevationMax"] = Math.Round(data.ElevationMax, 3),
        });
      }

      var xml = LandXmlWriter.Write(surfaces, decimals, projectName,
        crs?["name"]?.GetValue<string>(), crs?["epsgCode"]?.GetValue<string>(), crs?["verticalDatum"]?.GetValue<string>(),
        Path.GetFileName(doc.Name));
      var path = FileBoundary.WriteAllTextAtomic(outputPath, xml, new UTF8Encoding(false), overwrite, ".xml", ".landxml");
      return new Dictionary<string, object?>
      {
        ["outputPath"] = path,
        ["bytes"] = new FileInfo(path).Length,
        ["format"] = "LandXML 1.2",
        ["surfaces"] = summary,
      };
    });
  }

  private static List<SurfaceRequest> ParseSurfaces(JsonObject? parameters)
  {
    if (PluginRuntime.GetParameter(parameters, "surfaces") is not JsonArray array || array.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "surfaces must list at least one surface (a name or {name, exportName?, description?, clip?}). Nothing was written.");
    var requests = new List<SurfaceRequest>();
    foreach (var node in array)
    {
      if (node is JsonValue value && value.TryGetValue<string>(out var name))
      {
        requests.Add(new SurfaceRequest(name, null, null, null));
        continue;
      }

      if (node is not JsonObject item || item["name"]?.GetValue<string>() is not { Length: > 0 } itemName)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each surface is a name or an object with name. Nothing was written.");
      List<(double X, double Y)>? clip = null;
      if (item["clip"] is JsonArray ring)
      {
        clip = ring.Select(p => p is JsonObject o
            ? (o["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Clip point is missing x."),
               o["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Clip point is missing y."))
            : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "clip must be a list of {x, y} points."))
          .ToList();
        if (clip.Count < 3)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "clip needs at least three points. Nothing was written.");
      }

      requests.Add(new SurfaceRequest(itemName, item["exportName"]?.GetValue<string>(), item["description"]?.GetValue<string>(), clip));
    }

    var duplicate = requests.GroupBy(r => r.NewName ?? r.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
    if (duplicate != null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Two surfaces would be exported as '{duplicate.Key}'; give exportName. Nothing was written.");
    return requests;
  }
}

/// <summary>LandXML 1.2 text for TIN surfaces; no Civil 3D types, so the format can be checked on its own.</summary>
public static class LandXmlWriter
{
  public const string Namespace = "http://www.landxml.org/schema/LandXML-1.2";

  public sealed class SurfaceData
  {
    private readonly Dictionary<(long, long, long), int> ids = new();
    public string Name { get; }
    public string? Description { get; }
    public List<(double X, double Y, double Z)> Points { get; } = new();
    public List<(int A, int B, int C)> Faces { get; } = new();
    public double Area2D { get; private set; }
    public double Area3D { get; private set; }
    public double ElevationMin { get; private set; } = double.MaxValue;
    public double ElevationMax { get; private set; } = double.MinValue;
    public int PointCount => Points.Count;
    public int FaceCount => Faces.Count;

    public SurfaceData(string name, string? description)
    {
      Name = name;
      Description = string.IsNullOrWhiteSpace(description) ? null : description;
    }

    /// <summary>Adds a triangle; shared vertices (same position to 0.1 mm) get the same point id (1-based).</summary>
    public void AddTriangle((double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c)
    {
      Faces.Add((Id(a), Id(b), Id(c)));
      Area2D += Math.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) / 2.0;
      var ux = b.X - a.X; var uy = b.Y - a.Y; var uz = b.Z - a.Z;
      var vx = c.X - a.X; var vy = c.Y - a.Y; var vz = c.Z - a.Z;
      var nx = uy * vz - uz * vy; var ny = uz * vx - ux * vz; var nz = ux * vy - uy * vx;
      Area3D += Math.Sqrt(nx * nx + ny * ny + nz * nz) / 2.0;
    }

    private int Id((double X, double Y, double Z) p)
    {
      var key = ((long)Math.Round(p.X * 10000), (long)Math.Round(p.Y * 10000), (long)Math.Round(p.Z * 10000));
      if (ids.TryGetValue(key, out var id))
        return id;
      Points.Add(p);
      ElevationMin = Math.Min(ElevationMin, p.Z);
      ElevationMax = Math.Max(ElevationMax, p.Z);
      id = Points.Count;
      ids[key] = id;
      return id;
    }
  }

  /// <summary>Point in polygon (even-odd rule).</summary>
  public static bool Inside(IReadOnlyList<(double X, double Y)> ring, double x, double y)
  {
    var inside = false;
    for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
    {
      var (xi, yi) = ring[i];
      var (xj, yj) = ring[j];
      if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
        inside = !inside;
    }

    return inside;
  }

  public static string Write(IReadOnlyList<SurfaceData> surfaces, int decimals, string? projectName, string? crsName,
    string? epsgCode, string? verticalDatum, string? sourceFile)
  {
    var format = "F" + decimals.ToString(CultureInfo.InvariantCulture);
    string N(double v) => v.ToString(format, CultureInfo.InvariantCulture);
    var now = DateTime.Now;
    var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
    var sb = new StringBuilder();
    using (var stringWriter = new Utf8StringWriter(sb))
    using (var w = XmlWriter.Create(stringWriter, settings))
    {
      w.WriteStartDocument();
      w.WriteStartElement("LandXML", Namespace);
      w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
      w.WriteAttributeString("xsi", "schemaLocation", "http://www.w3.org/2001/XMLSchema-instance",
        Namespace + " http://www.landxml.org/schema/LandXML-1.2/LandXML-1.2.xsd");
      w.WriteAttributeString("version", "1.2");
      w.WriteAttributeString("date", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
      w.WriteAttributeString("time", now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

      w.WriteStartElement("Units", Namespace);
      w.WriteStartElement("Metric", Namespace);
      w.WriteAttributeString("areaUnit", "squareMeter");
      w.WriteAttributeString("linearUnit", "meter");
      w.WriteAttributeString("volumeUnit", "cubicMeter");
      w.WriteAttributeString("temperatureUnit", "celsius");
      w.WriteAttributeString("pressureUnit", "milliBars");
      w.WriteAttributeString("angularUnit", "decimal degrees");
      w.WriteAttributeString("directionUnit", "decimal degrees");
      w.WriteEndElement();
      w.WriteEndElement();

      if (crsName != null || epsgCode != null || verticalDatum != null)
      {
        w.WriteStartElement("CoordinateSystem", Namespace);
        if (crsName != null) w.WriteAttributeString("name", crsName);
        if (epsgCode != null) w.WriteAttributeString("epsgCode", epsgCode);
        if (crsName != null) w.WriteAttributeString("horizontalCoordinateSystemName", crsName);
        if (verticalDatum != null) w.WriteAttributeString("verticalCoordinateSystemName", verticalDatum);
        w.WriteEndElement();
      }

      if (projectName != null)
      {
        w.WriteStartElement("Project", Namespace);
        w.WriteAttributeString("name", projectName);
        w.WriteEndElement();
      }

      w.WriteStartElement("Application", Namespace);
      w.WriteAttributeString("name", "Civil3D MCP plugin");
      w.WriteAttributeString("desc", "exportLandXml (TIN surfaces)" + (sourceFile != null ? ", " + sourceFile : ""));
      w.WriteAttributeString("manufacturer", "Autodesk Civil 3D via MCP plugin");
      w.WriteAttributeString("timeStamp", now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
      w.WriteEndElement();

      w.WriteStartElement("Surfaces", Namespace);
      foreach (var s in surfaces)
      {
        w.WriteStartElement("Surface", Namespace);
        w.WriteAttributeString("name", s.Name);
        if (s.Description != null) w.WriteAttributeString("desc", s.Description);
        w.WriteStartElement("Definition", Namespace);
        w.WriteAttributeString("surfType", "TIN");
        w.WriteAttributeString("area2DSurf", N(s.Area2D));
        w.WriteAttributeString("area3DSurf", N(s.Area3D));
        w.WriteAttributeString("elevMax", N(s.ElevationMax));
        w.WriteAttributeString("elevMin", N(s.ElevationMin));
        w.WriteStartElement("Pnts", Namespace);
        for (var i = 0; i < s.Points.Count; i++)
        {
          var p = s.Points[i];
          w.WriteStartElement("P", Namespace);
          w.WriteAttributeString("id", (i + 1).ToString(CultureInfo.InvariantCulture));
          w.WriteString($"{N(p.Y)} {N(p.X)} {N(p.Z)}");          // northing easting elevation
          w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteStartElement("Faces", Namespace);
        foreach (var f in s.Faces)
          w.WriteElementString("F", Namespace, $"{f.A} {f.B} {f.C}");
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
      }

      w.WriteEndElement();
      w.WriteEndElement();
      w.WriteEndDocument();
    }

    return sb.ToString();
  }

  private sealed class Utf8StringWriter : StringWriter
  {
    public Utf8StringWriter(StringBuilder sb) : base(sb, CultureInfo.InvariantCulture) { }
    public override Encoding Encoding => new UTF8Encoding(false);
  }
}
