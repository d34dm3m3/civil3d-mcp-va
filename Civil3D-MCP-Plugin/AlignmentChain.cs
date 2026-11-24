using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Reads the entities of an alignment as plain values (no Civil wrappers are kept, so the values stay valid
/// across a rolled-back nested transaction) and checks that they form one linked chain. Civil 3D links the
/// entities only through EntityBefore/EntityAfter; the collection index (Entities[i], RemoveAt, the order of
/// `get`) follows the entity ids, which are never reused, not the order along the alignment. The chain is walked
/// from Entities.FirstEntity to Entities.LastEntity, so the before-link of the first entity and the after-link
/// of the last one are not interpreted. All stations are raw (without station equations).
/// </summary>
internal static class AlignmentChain
{
  internal const double LinkTolerance = 1e-6;

  internal sealed record EntityInfo(
    int Index,
    int Id,
    string Type,
    string Constraint,
    Point2d Start,
    Point2d End,
    double StartStation,
    double EndStation,
    double Length,
    int? Before,
    int? After,
    double? Radius,
    Point2d? Center,
    bool? Clockwise);

  internal sealed record ChainCheck(bool Continuous, List<int> OrderIds, List<string> Problems, int FirstId, int LastId);

  internal static string TypeName(AlignmentEntityType type) => type switch
  {
    AlignmentEntityType.Line => "line",
    AlignmentEntityType.Arc => "arc",
    AlignmentEntityType.Spiral => "spiral",
    _ => type.ToString(),
  };

  /// <summary>
  /// The id of the entity before/after, or null when there is none: Civil 3D throws EntityNotFoundException
  /// (NoNextAlignmentEntity / NoPreviousAlignmentEntity) instead of returning a sentinel (live 2026-10-07, C48b).
  /// The only place EntityBefore/EntityAfter are read (tests/alignment_replace_curve.test.ts checks this).
  /// </summary>
  internal static int? Neighbour(AlignmentEntity entity, bool after)
  {
    try { return after ? entity.EntityAfter : entity.EntityBefore; } catch (Exception) { return null; }
  }

  private static int? EndId(AlignmentEntityCollection entities, bool last)
  {
    try { return last ? entities.LastEntity : entities.FirstEntity; } catch (Exception) { return null; }
  }

  internal static List<EntityInfo> Read(AlignmentEntityCollection entities)
  {
    var list = new List<EntityInfo>(entities.Count);
    for (var index = 0; index < entities.Count; index++)
    {
      var entity = entities[index];
      Point2d start, end;
      double startStation, endStation, length;
      if (entity is AlignmentCurve curve)
      {
        (start, end, startStation, endStation, length) = (curve.StartPoint, curve.EndPoint, curve.StartStation, curve.EndStation, curve.Length);
      }
      else
      {
        var first = entity[0];
        var last = entity[entity.SubEntityCount - 1];
        (start, end, startStation, endStation) = (first.StartPoint, last.EndPoint, first.StartStation, last.EndStation);
        length = 0;
        for (var s = 0; s < entity.SubEntityCount; s++) length += entity[s].Length;
      }

      var arc = entity as AlignmentArc;
      list.Add(new EntityInfo(
        index,
        entity.EntityId,
        TypeName(entity.EntityType),
        entity.Constraint1.ToString(),
        start,
        end,
        startStation,
        endStation,
        length,
        Neighbour(entity, after: false),
        Neighbour(entity, after: true),
        arc?.Radius,
        arc?.CenterPoint,
        arc?.Clockwise));
    }

    return list;
  }

  internal static ChainCheck Check(List<EntityInfo> infos, AlignmentEntityCollection entities)
  {
    var problems = new List<string>();
    var order = new List<int>();
    var byId = infos.ToDictionary(i => i.Id);
    if (infos.Count == 0)
    {
      return new ChainCheck(false, order, new List<string> { "the alignment has no entities" }, -1, -1);
    }

    var firstId = EndId(entities, last: false) ?? -1;
    var lastId = EndId(entities, last: true) ?? -1;
    if (!byId.ContainsKey(firstId)) problems.Add($"FirstEntity {firstId} is not an entity of the alignment");
    if (!byId.ContainsKey(lastId)) problems.Add($"LastEntity {lastId} is not an entity of the alignment");
    if (problems.Count > 0) return new ChainCheck(false, order, problems, firstId, lastId);

    var visited = new HashSet<int>();
    var current = firstId;
    while (true)
    {
      if (!visited.Add(current))
      {
        problems.Add($"entity id {current} is reached twice (the links form a loop)");
        break;
      }

      order.Add(current);
      if (current == lastId) break;
      var info = byId[current];
      if (info.After is not int afterId || !byId.TryGetValue(afterId, out var next))
      {
        problems.Add($"the chain ends after entity id {current} (index {info.Index}, ends at station {info.EndStation:F4}): " +
          (info.After == null ? "it has no entity after" : $"its entity after ({info.After}) is not an entity of the alignment"));
        break;
      }

      if (next.Before != current)
        problems.Add($"entity id {current} (index {info.Index}) points to {next.Id} (index {next.Index}) as the entity after, but that one has {next.Before?.ToString() ?? "none"} as the entity before");
      var gap = info.End.GetDistanceTo(next.Start);
      if (gap > LinkTolerance)
        problems.Add($"gap of {gap:G6} between the end of entity id {current} (index {info.Index}) and the start of entity id {next.Id} (index {next.Index})");
      current = next.Id;
    }

    var unreached = infos.Where(i => !visited.Contains(i.Id)).ToList();
    if (unreached.Count > 0)
      problems.Add("not reached from the first entity: " + string.Join(", ", unreached.Select(i => $"index {i.Index} (id {i.Id}, {i.Type}, stations {i.StartStation:F4}-{i.EndStation:F4})")));

    return new ChainCheck(problems.Count == 0, order, problems, firstId, lastId);
  }

  internal static Dictionary<string, object?> Xy(Point2d point) => new() { ["x"] = point.X, ["y"] = point.Y };

  /// <summary>Plan direction of travel at the start or end of an entity: exact for lines and arcs, otherwise
  /// from the alignment 1 cm (at most half the entity) inside the entity.</summary>
  internal static Vector2d Direction(EntityInfo info, bool atEnd, Alignment alignment)
  {
    if (info.Type == "line") return (info.End - info.Start).GetNormal();
    var point = atEnd ? info.End : info.Start;
    if (info.Type == "arc" && info.Center is Point2d center && info.Clockwise is bool clockwise)
    {
      var radial = point - center;
      return (clockwise ? new Vector2d(radial.Y, -radial.X) : new Vector2d(-radial.Y, radial.X)).GetNormal();
    }

    var step = Math.Min(0.01, info.Length / 2);
    double x = 0, y = 0;
    alignment.PointLocation(atEnd ? info.EndStation - step : info.StartStation + step, 0, ref x, ref y);
    var inner = new Point2d(x, y);
    return (atEnd ? point - inner : inner - point).GetNormal();
  }

  /// <summary>Signed deflection in degrees from one direction of travel to the next; positive turns left.</summary>
  internal static double DeflectionDegrees(Vector2d incoming, Vector2d outgoing)
  {
    var cross = incoming.X * outgoing.Y - incoming.Y * outgoing.X;
    return Math.Atan2(cross, incoming.DotProduct(outgoing)) * 180.0 / Math.PI;
  }

  /// <summary>
  /// Raw stations at which the alignment shows the given nominal station (station equations applied). Before the
  /// first equation nominal = raw; after an equation at raw R with station ahead A, nominal = A + (raw - R) when
  /// increasing and A - (raw - R) when decreasing, up to the next equation or the end of the alignment. More than
  /// one value means the nominal station occurs twice.
  /// </summary>
  internal static List<double> NominalToRaw(Alignment alignment, double nominal)
  {
    var equations = new List<(double RawBack, double Ahead, bool Increasing)>();
    foreach (StationEquation equation in alignment.StationEquations)
      equations.Add((equation.RawStationBack, equation.StationAhead, equation.EquationType == StationEquationType.Increasing));
    equations.Sort((a, b) => a.RawBack.CompareTo(b.RawBack));

    var regions = new List<(double From, double To, double NominalAtFrom, bool Increasing)>();
    var from = alignment.StartingStation;
    var nominalAtFrom = alignment.StartingStation;
    var increasing = true;
    foreach (var equation in equations)
    {
      regions.Add((from, equation.RawBack, nominalAtFrom, increasing));
      (from, nominalAtFrom, increasing) = (equation.RawBack, equation.Ahead, equation.Increasing);
    }

    regions.Add((from, alignment.EndingStation, nominalAtFrom, increasing));

    var raws = new List<double>();
    foreach (var region in regions)
    {
      var raw = region.Increasing ? region.From + (nominal - region.NominalAtFrom) : region.From + (region.NominalAtFrom - nominal);
      if (raw < region.From - 1e-9 || raw > region.To + 1e-9) continue;
      raw = Math.Min(Math.Max(raw, region.From), region.To);
      if (!raws.Any(r => Math.Abs(r - raw) < 1e-9)) raws.Add(raw);
    }

    return raws;
  }

  internal static List<Dictionary<string, object?>> DescribeEquations(Alignment alignment)
  {
    var list = new List<Dictionary<string, object?>>();
    foreach (StationEquation equation in alignment.StationEquations)
    {
      list.Add(new Dictionary<string, object?>
      {
        ["rawStationBack"] = equation.RawStationBack,
        ["stationBack"] = equation.StationBack,
        ["stationAhead"] = equation.StationAhead,
        ["equationType"] = equation.EquationType == StationEquationType.Increasing ? "increasing" : "decreasing",
      });
    }

    return list;
  }
}
