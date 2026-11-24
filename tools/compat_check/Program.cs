using Civil3DMcpPlugin;

// Standalone check of Civil3DCompatibility property resolution (C12b).
// Civil 3D's StyleBase overrides only the setter of DBObject.Name; Part overrides only the getter.
// Run: dotnet build -o out && dotnet out/compat_check.dll
var failures = 0;
void Check(string name, bool ok, string detail = "")
{
  if (!ok) failures++;
  Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name} {detail}");
}

var style = new StyleLike();
var read = Civil3DCompatibility.GetPropertyValue(style, "Name");
Check("reads Name through a setter-only override", Equals(read, "bas"), $"got={read ?? "null"}");

Check("writes Name through a setter-only override", Civil3DCompatibility.TrySetProperty(style, "Name", "ny") && style.Raw == "ny!");

var part = new PartLike();
Check("writes Name through a getter-only override", Civil3DCompatibility.TrySetProperty(part, "Name", "x"), $"raw={part.Raw}");
Check("reads Name through a getter-only override", Equals(Civil3DCompatibility.GetPropertyValue(part, "Name"), "part:x"));

Check("missing property still returns null", Civil3DCompatibility.GetPropertyValue(style, "Nope") == null);

Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

class BaseObj
{
  protected string _n = "bas";
  public string Raw => _n;
  public virtual string Name { get => _n; set => _n = value; }
}

class StyleLike : BaseObj
{
  public override string Name { set => _n = value + "!"; }
}

class PartLike : BaseObj
{
  public override string Name { get => "part:" + _n; }
}
