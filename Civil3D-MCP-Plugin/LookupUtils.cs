using System.Collections;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

public static class LookupUtils
{
  /// <summary>
  /// Strict layer lookup: returns the named layer, creates it when allowed,
  /// otherwise throws. Unlike GetLayerId it never falls back to CLAYER.
  /// </summary>
  public static ObjectId GetOrCreateLayerId(Database database, Transaction transaction, string layerName, bool createIfMissing)
  {
    var layerTable = CivilObjectUtils.GetRequiredObject<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
    if (layerTable.Has(layerName))
      return layerTable[layerName];

    if (!createIfMissing)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.OBJECT_NOT_FOUND",
        $"Layer '{layerName}' does not exist. Pass createLayer=true to create it. No entity was created.");
    }

    SymbolUtilityServices.ValidateSymbolName(layerName, false);
    layerTable.UpgradeOpen();
    using var record = new LayerTableRecord { Name = layerName };
    var layerId = layerTable.Add(record);
    transaction.AddNewlyCreatedDBObject(record, true);
    return layerId;
  }

  public static ObjectId GetLayerId(Database database, Transaction transaction, string? layerName)
  {
    if (string.IsNullOrWhiteSpace(layerName))
    {
      return database.Clayer;
    }

    var layerTable = CivilObjectUtils.GetRequiredObject<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
    if (layerTable.Has(layerName))
    {
      return layerTable[layerName];
    }

    return database.Clayer;
  }

  public static ObjectId GetSiteId(CivilDocument civilDoc, Transaction transaction, string? siteName)
  {
    if (string.IsNullOrWhiteSpace(siteName))
    {
      return ObjectId.Null;
    }

    foreach (ObjectId objectId in civilDoc.GetSiteIds())
    {
      var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, objectId, OpenMode.ForRead);
      if (string.Equals(site.Name, siteName, StringComparison.OrdinalIgnoreCase))
      {
        return objectId;
      }
    }

    return ObjectId.Null;
  }

  public static ObjectId GetAlignmentStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.AlignmentStyles, transaction, styleName);
  }

  public static ObjectId GetProfileStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.ProfileStyles, transaction, styleName);
  }

  public static ObjectId GetSurfaceStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.SurfaceStyles, transaction, styleName);
  }

  public static ObjectId GetAlignmentLabelSetId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles, transaction, styleName);
  }

  public static ObjectId GetProfileLabelSetId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.LabelSetStyles.ProfileLabelSetStyles, transaction, styleName);
  }

  public static ObjectId GetProfileViewStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleIdStrict(civilDoc.Styles.ProfileViewStyles, transaction, styleName, "Profile view style");
  }

  // Band set styles live on StylesRoot, not on LabelSetStylesRoot (the old lookup always returned Null).
  public static ObjectId GetProfileViewBandSetId(CivilDocument civilDoc, Transaction transaction, string? bandSetName)
  {
    return GetStyleIdStrict(civilDoc.Styles.ProfileViewBandSetStyles, transaction, bandSetName, "Profile view band set");
  }

  public static ObjectId GetParcelStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.ParcelStyles, transaction, styleName);
  }

  public static ObjectId GetParcelAreaLabelStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.LabelStyles.ParcelLabelStyles.AreaLabelStyles, transaction, styleName);
  }

  public static ObjectId GetSectionViewStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.SectionViewStyles, transaction, styleName);
  }

  public static ObjectId GetSectionViewBandSetId(CivilDocument civilDoc, Transaction transaction, string? bandSetName)
  {
    return string.IsNullOrWhiteSpace(bandSetName)
      ? ObjectId.Null
      : GetStyleId(civilDoc.Styles.SectionViewBandSetStyles, transaction, bandSetName);
  }

  public static ObjectId GetGroupPlotStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return string.IsNullOrWhiteSpace(styleName)
      ? ObjectId.Null
      : GetStyleId(civilDoc.Styles.GroupPlotStyles, transaction, styleName);
  }

  public static string? GetFirstStyleName(object? collection, Transaction transaction)
  {
    foreach (var objectId in EnumerateObjectIds(collection))
    {
      if (objectId == ObjectId.Null)
      {
        continue;
      }

      var style = transaction.GetObject(objectId, OpenMode.ForRead);
      return CivilObjectUtils.GetName(style);
    }

    return null;
  }

  private static ObjectId GetStyleId(object collection, Transaction transaction, string? styleName)
  {
    var fallback = ObjectId.Null;

    foreach (var objectId in EnumerateObjectIds(collection))
    {
      if (objectId == ObjectId.Null)
      {
        continue;
      }

      if (fallback == ObjectId.Null)
      {
        fallback = objectId;
      }

      if (string.IsNullOrWhiteSpace(styleName))
      {
        continue;
      }

      var style = transaction.GetObject(objectId, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(style), styleName, StringComparison.OrdinalIgnoreCase))
      {
        return objectId;
      }
    }

    return fallback;
  }

  /// <summary>
  /// Named style, or the first style when no name is given. Unlike GetStyleId an unknown
  /// name throws (listing the available names) instead of silently using the first style.
  /// </summary>
  internal static ObjectId GetStyleIdStrict(object collection, Transaction transaction, string? styleName, string kind)
  {
    var names = new List<string>();
    var first = ObjectId.Null;

    foreach (var objectId in EnumerateObjectIds(collection))
    {
      if (objectId == ObjectId.Null)
      {
        continue;
      }

      if (string.IsNullOrWhiteSpace(styleName))
      {
        return objectId;
      }

      if (first == ObjectId.Null)
      {
        first = objectId;
      }

      var name = CivilObjectUtils.GetName(transaction.GetObject(objectId, OpenMode.ForRead)) ?? string.Empty;
      if (string.Equals(name, styleName, StringComparison.OrdinalIgnoreCase))
      {
        return objectId;
      }

      names.Add(name);
    }

    if (first == ObjectId.Null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"The drawing has no {kind.ToLowerInvariant()}s.");
    }

    throw new JsonRpcDispatchException(
      "CIVIL3D.INVALID_INPUT",
      $"{kind} '{styleName}' was not found. Available: {string.Join(", ", names)}.");
  }

  private static IEnumerable<ObjectId> EnumerateObjectIds(object? collection)
  {
    if (collection is ObjectIdCollection objectIds)
    {
      foreach (ObjectId objectId in objectIds)
      {
        yield return objectId;
      }

      yield break;
    }

    if (collection is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item is ObjectId objectId)
        {
          yield return objectId;
        }
      }
    }
  }
}
