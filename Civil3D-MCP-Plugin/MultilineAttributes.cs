using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// C116: multiline (MText) attributes. Use case: the company template's drilling sheet (M38, decision 5) has one
/// attribute per note in the drilling notes block, and a note runs over several lines; the drilling tool (V8) fills
/// placeholders, strikes notes and renumbers. A single-line attribute cannot hold a note, and TextString on a multiline
/// attribute does not keep its line breaks reliably, so values go through the attribute's MText.
/// </summary>
internal static class MultilineAttributes
{
  internal static string Value(AttributeReference attribute) =>
    attribute.IsMTextAttribute ? attribute.MTextAttribute.Contents : attribute.TextString;

  internal static string Value(AttributeDefinition definition) =>
    definition.IsMTextAttributeDefinition ? definition.MTextAttributeDefinition.Contents : definition.TextString;

  internal static void SetValue(AttributeReference attribute, string value)
  {
    if (!attribute.IsMTextAttribute)
    {
      attribute.TextString = value;
      return;
    }

    var mtext = attribute.MTextAttribute;
    mtext.Contents = value;
    attribute.MTextAttribute = mtext;
    attribute.UpdateMTextAttribute();
  }

  /// <summary>Makes a definition multiline (top left at its position) or updates an existing multiline one: width in
  /// drawing units (0 = no wrapping), contents with \P line breaks; line spacing exactly 1.0 (5/3 of the text height).</summary>
  internal static void Apply(AttributeDefinition definition, double? width, string? contents, Autodesk.AutoCAD.Geometry.Point3d? location = null)
  {
    if (!definition.IsMTextAttributeDefinition)
    {
      var text = contents ?? definition.TextString;
      definition.IsMTextAttributeDefinition = true;
      var created = definition.MTextAttributeDefinition;
      created.Attachment = AttachmentPoint.TopLeft;
      created.Location = location ?? definition.Position;
      created.TextStyleId = definition.TextStyleId;
      created.TextHeight = definition.Height;
      created.Width = width ?? 0.0;
      created.LineSpacingStyle = LineSpacingStyle.Exactly;   // "at least" makes lines with accented capitals taller; the tools stack notes by line count
      created.LineSpacingFactor = 1.0;
      created.Contents = text;
      definition.MTextAttributeDefinition = created;
      definition.UpdateMTextAttributeDefinition();
      return;
    }

    var mtext = definition.MTextAttributeDefinition;
    if (location != null) mtext.Location = location.Value;   // the copy carries the old location, which would win
    if (width != null) mtext.Width = width.Value;
    if (contents != null) mtext.Contents = contents;
    mtext.TextHeight = definition.Height;
    mtext.TextStyleId = definition.TextStyleId;
    mtext.LineSpacingStyle = LineSpacingStyle.Exactly;
    mtext.LineSpacingFactor = 1.0;
    definition.MTextAttributeDefinition = mtext;
    definition.UpdateMTextAttributeDefinition();
  }
}
