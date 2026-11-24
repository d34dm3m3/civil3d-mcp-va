using System.Collections.Concurrent;
using System.Reflection;

namespace Civil3DMcpPlugin;

/// <summary>
/// Narrow compatibility boundary for Civil 3D members that are unavailable in
/// the referenced managed API or vary between supported host versions.
/// Documented Autodesk members should be called directly instead.
/// </summary>
internal static class Civil3DCompatibility
{
  internal readonly record struct ParameterShape(Type Type);
  private sealed record CachedProperty(PropertyInfo? Value);
  private sealed record CachedMethods(MethodInfo[] Values);
  private sealed record CachedField(FieldInfo? Value);
  private sealed record CachedType(Type? Value);

  private readonly record struct PropertyKey(Type Type, string Name, bool IsStatic);
  private readonly record struct MethodKey(Type Type, string Name, bool IsStatic, int ArgumentCount);
  private readonly record struct MethodFamilyKey(Type Type, string Name, bool IsStatic);
  private readonly record struct LoadedStaticMethodKey(string Name, Type FirstParameterType, int ArgumentCount);

  private static readonly ConcurrentDictionary<PropertyKey, CachedProperty> PropertyCache = new();
  private static readonly ConcurrentDictionary<PropertyKey, CachedField> FieldCache = new();
  private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ScalarPropertyCache = new();
  private static readonly ConcurrentDictionary<MethodKey, CachedMethods> MethodCache = new();
  private static readonly ConcurrentDictionary<MethodFamilyKey, CachedMethods> MethodFamilyCache = new();
  private static readonly ConcurrentDictionary<LoadedStaticMethodKey, CachedMethods> LoadedStaticMethodCache = new();
  private static readonly ConcurrentDictionary<string, CachedType> TypeCache = new(StringComparer.Ordinal);

  public static T? GetPropertyValue<T>(object? target, string propertyName)
  {
    var raw = GetPropertyValue(target, propertyName);
    if (raw == null)
    {
      return default;
    }

    if (raw is T typed)
    {
      return typed;
    }

    try
    {
      var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
      if (targetType.IsInstanceOfType(raw))
      {
        return (T)(object)raw;
      }

      return (T)(object)Convert.ChangeType(raw, targetType)!;
    }
    catch
    {
      return default;
    }
  }

  public static object? GetPropertyValue(object? target, string propertyName)
  {
    if (target == null)
    {
      return null;
    }

    var property = ResolveAccessor(target.GetType(), propertyName, forWrite: false);
    if (property == null)
    {
      return null;
    }

    try
    {
      return property.GetValue(target);
    }
    catch
    {
      return null;
    }
  }

  public static object? GetIndexedPropertyValue(object? target, string propertyName, params object?[] indexes)
  {
    if (target == null)
    {
      return null;
    }

    var property = ResolveProperty(target.GetType(), propertyName, isStatic: false);
    try
    {
      return property?.GetValue(target, indexes);
    }
    catch
    {
      return null;
    }
  }

  public static object? GetFieldValue(object? target, string fieldName)
  {
    if (target == null)
    {
      return null;
    }

    var type = target.GetType();
    var key = new PropertyKey(type, fieldName, IsStatic: false);
    var field = FieldCache.GetOrAdd(key, static item =>
      new CachedField(item.Type.GetField(item.Name, BindingFlags.Public | BindingFlags.Instance))).Value;
    try
    {
      return field?.GetValue(target);
    }
    catch
    {
      return null;
    }
  }

  public static bool TrySetProperty(object? target, string propertyName, object? value)
  {
    if (target == null)
    {
      return false;
    }

    var property = ResolveAccessor(target.GetType(), propertyName, forWrite: true);
    if (property?.CanWrite != true)
    {
      return false;
    }

    try
    {
      var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
      var converted = value == null || targetType.IsInstanceOfType(value)
        ? value
        : targetType.IsEnum && value is string enumText
          ? Enum.Parse(targetType, enumText, ignoreCase: true)
          : Convert.ChangeType(value, targetType);
      property.SetValue(target, converted);
      return true;
    }
    catch
    {
      return false;
    }
  }

  /// <summary>Public instance properties whose type is a Civil 3D API class (the style collections and their
  /// groups under CivilDocument.Styles); C78 walks them to list the style collections of a drawing.</summary>
  public static IReadOnlyList<string> GetCivilReferencePropertyNames(object target) =>
    target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !p.PropertyType.IsValueType && p.PropertyType != typeof(string)
        && (p.PropertyType.Namespace ?? "").StartsWith("Autodesk.Civil", StringComparison.Ordinal))
      .Select(p => p.Name)
      .ToList();

  /// <summary>A public instance property by name, ignoring case (style collection paths typed by hand, C78).
  /// Found is false when there is no such property; exceptions from the getter propagate.</summary>
  public static (bool Found, object? Value) GetPropertyIgnoreCase(object target, string propertyName)
  {
    var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
    if (property == null || property.GetIndexParameters().Length > 0) return (false, null);
    return (true, property.GetValue(target));
  }

  /// <summary>Public, non-abstract, non-generic top-level classes of baseType's assembly that derive from baseType.
  /// C91: the Civil settings types SettingsRoot.GetSettings&lt;T&gt; takes (T : SettingsAmbient, about 200 in Civil 3D
  /// 2026), listed from the host's assembly rather than hard-coded, so a new release's types are found too.</summary>
  public static IReadOnlyList<Type> GetPublicSubclasses(Type baseType) =>
    SubclassCache.GetOrAdd(baseType, static type =>
    {
      Type[] exported;
      try
      {
        exported = type.Assembly.GetExportedTypes();
      }
      catch (ReflectionTypeLoadException exception)
      {
        exported = exception.Types.Where(t => t != null).Cast<Type>().ToArray();
      }

      return exported
        .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition && !t.IsNested && t.IsSubclassOf(type))
        .OrderBy(t => t.Name, StringComparer.Ordinal)
        .ToArray();
    });

  private static readonly ConcurrentDictionary<Type, Type[]> SubclassCache = new();

  /// <summary>Calls a public parameterless generic instance method with one type argument chosen at run time.
  /// C91: SettingsRoot.GetSettings&lt;T&gt;() for the settings type a caller names. An exception thrown by the method
  /// itself propagates unwrapped (not as TargetInvocationException); a missing method is MissingMethodException.</summary>
  public static object? InvokeGenericMethod(object target, string methodName, Type typeArgument)
  {
    var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
      .FirstOrDefault(m => m.Name == methodName && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0)
      ?? throw new MissingMethodException(target.GetType().Name, methodName);
    try
    {
      return method.MakeGenericMethod(typeArgument).Invoke(target, null);
    }
    catch (TargetInvocationException exception) when (exception.InnerException != null)
    {
      System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
      throw;
    }
  }

  /// <summary>C98e: one display component of a Civil style (Display tab): its view direction (Plan, Profile, Section,
  /// Model), its name, and a getter that returns the component's display style for a style object.</summary>
  internal sealed record DisplayComponentGetter(string View, string Component, Func<object, object?> Get);

  private static readonly string[] DisplayViews = { "Plan", "Profile", "Section", "Model" };
  private static readonly System.Text.RegularExpressions.Regex DisplayMethodName =
    new(@"^Get(?<component>[A-Za-z0-9]*?)DisplayStyle(?<view>Plan|Profile|Section|Model)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
  private static readonly ConcurrentDictionary<(Type Style, Type Component), DisplayComponentGetter[]> DisplayGetterCache = new();

  /// <summary>C98e: the display components of a style type, from its public instance methods that return componentType
  /// and are named Get{Component}DisplayStyle{View}: with one enum parameter every enum value is a component
  /// (ProfileStyle.GetDisplayStyleProfile(ProfileDisplayStyleProfileType): Line, Curve, LineExtension, …); without
  /// parameters the component is the name part, or "Default" (ProfileStyle.GetChain3DDisplayStyleModel: Chain3D,
  /// AlignmentStyle.GetDisplayStyleSection: Default). Views in the order Plan, Profile, Section, Model; the enum
  /// components in enum order before the named ones. An exception of the getter itself propagates unwrapped.</summary>
  public static IReadOnlyList<DisplayComponentGetter> GetDisplayComponentGetters(Type styleType, Type componentType) =>
    DisplayGetterCache.GetOrAdd((styleType, componentType), static key =>
    {
      var found = new List<(int View, int Group, int Order, DisplayComponentGetter Getter)>();
      foreach (var method in key.Style.GetMethods(BindingFlags.Public | BindingFlags.Instance))
      {
        if (method.ReturnType != key.Component || method.IsGenericMethodDefinition) continue;
        var match = DisplayMethodName.Match(method.Name);
        if (!match.Success) continue;
        var view = match.Groups["view"].Value;
        var prefix = match.Groups["component"].Value;
        var viewIndex = Array.IndexOf(DisplayViews, view);
        var parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
          var name = prefix.Length > 0 ? prefix : "Default";
          found.Add((viewIndex, 1, 0, new DisplayComponentGetter(view, name, target => InvokeUnwrapped(method, target, null))));
        }
        else if (parameters.Length == 1 && parameters[0].ParameterType.IsEnum)
        {
          var enumType = parameters[0].ParameterType;
          var order = 0;
          foreach (var value in Enum.GetValues(enumType).Cast<object>().Distinct())
          {
            var name = (prefix.Length > 0 ? prefix + "." : "") + Enum.GetName(enumType, value);
            var argument = new[] { value };
            found.Add((viewIndex, 0, order++, new DisplayComponentGetter(view, name, target => InvokeUnwrapped(method, target, argument))));
          }
        }
      }

      return found
        .OrderBy(f => f.View).ThenBy(f => f.Group).ThenBy(f => f.Order).ThenBy(f => f.Getter.Component, StringComparer.Ordinal)
        .GroupBy(f => (f.Getter.View, f.Getter.Component.ToUpperInvariant())).Select(g => g.First().Getter)
        .ToArray();
    });

  private static object? InvokeUnwrapped(MethodInfo method, object target, object?[]? arguments)
  {
    try
    {
      return method.Invoke(target, arguments);
    }
    catch (TargetInvocationException exception) when (exception.InnerException != null)
    {
      System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
      throw;
    }
  }

  /// <summary>C98e: public instance properties of propertyType with a getter and a setter whose names end with one of
  /// the suffixes, e.g. the marker styles of a profile style (VIntersectionPointMarkerStyle) or of a feature line style
  /// (SectionMarkerStyleId). Read and written with GetPropertyValue and TrySetProperty.</summary>
  public static IReadOnlyList<string> GetReadWritePropertyNames(Type type, Type propertyType, params string[] suffixes) =>
    type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .Where(p => p.PropertyType == propertyType && p.GetIndexParameters().Length == 0
        && p.GetGetMethod() != null && ResolveAccessor(type, p.Name, forWrite: true) != null
        && suffixes.Any(s => p.Name.EndsWith(s, StringComparison.Ordinal)))
      .Select(p => p.Name)
      .Distinct(StringComparer.Ordinal)
      .OrderBy(n => n, StringComparer.Ordinal)
      .ToList();

  /// <summary>Simple public instance properties (string, bool, int, double, enum) with type and whether they
  /// can be written, for generic option editing (pipe and structure style options).</summary>
  public static IReadOnlyList<(string Name, Type Type, bool CanWrite)> GetSimplePropertyInfo(object target) =>
    target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
      .Where(property =>
      {
        var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        return propertyType.IsEnum || propertyType == typeof(string) || propertyType == typeof(bool)
          || propertyType == typeof(int) || propertyType == typeof(double);
      })
      .Select(property => (property.Name, property.PropertyType, property.CanWrite))
      .ToList();

  public static IReadOnlyDictionary<string, object?> GetReadableScalarProperties(object target)
  {
    var properties = ScalarPropertyCache.GetOrAdd(target.GetType(), static type =>
      type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
        .Where(property =>
        {
          var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
          return propertyType == typeof(string)
            || propertyType == typeof(bool)
            || propertyType == typeof(int)
            || propertyType == typeof(double);
        })
        .ToArray());

    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
    foreach (var property in properties)
    {
      try
      {
        values[property.Name] = property.GetValue(target);
      }
      catch
      {
        // Some Autodesk wrappers throw for state-dependent getters. Omit them.
      }
    }

    return values;
  }

  /// <summary>C93: a property of a COM automation object (IDispatch, e.g. the AcadDocument behind a Document), which
  /// has no managed metadata to reflect on; null when the object or the property is not available.</summary>
  public static object? GetComProperty(object? comObject, string propertyName)
  {
    if (comObject == null)
    {
      return null;
    }

    try
    {
      return comObject.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, null, comObject, null);
    }
    catch
    {
      return null;
    }
  }

  /// <summary>C97: calls a method of a COM automation object (IDispatch, e.g. AcadDocument.Save/SaveAs); the COM
  /// error itself is thrown, not the reflection wrapper.</summary>
  /// <summary>C149: the exception behind reflection and task wrappers (TargetInvocationException, AggregateException).</summary>
  public static Exception UnwrapInvocation(Exception ex)
  {
    var root = ex;
    while (root is TargetInvocationException or AggregateException && root.InnerException != null) root = root.InnerException;
    return root;
  }

  public static object? InvokeComMethod(object comObject, string methodName, params object?[] arguments)
  {
    try
    {
      return comObject.GetType().InvokeMember(methodName, BindingFlags.InvokeMethod, null, comObject, arguments);
    }
    catch (TargetInvocationException ex) when (ex.InnerException != null)
    {
      System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
      throw;
    }
  }

  public static object? InvokeMethod(object? target, string methodName, params object?[] arguments)
  {
    if (target == null)
    {
      return null;
    }

    TryInvokeCandidates(target.GetType(), target, methodName, isStatic: false, arguments, out var result);
    return result;
  }

  public static bool TryInvokeMethod(object? target, string methodName, out object? result, params object?[] arguments)
  {
    result = null;
    return target != null
      && TryInvokeCandidates(target.GetType(), target, methodName, isStatic: false, arguments, out result);
  }

  public static object? InvokeStaticMethod(Type type, string methodName, params object?[] arguments)
  {
    TryInvokeCandidates(type, null, methodName, isStatic: true, arguments, out var result);
    return result;
  }

  public static bool TryInvokeStaticMethod(Type type, string methodName, out object? result, params object?[] arguments)
  {
    return TryInvokeCandidates(type, null, methodName, isStatic: true, arguments, out result);
  }

  public static bool TryInvokeStaticOverloads(
    Type type,
    string methodName,
    Func<ParameterShape[], object?[]?> argumentBuilder,
    out object? result,
    out object?[]? invokedArguments)
  {
    result = null;
    invokedArguments = null;
    var key = new MethodFamilyKey(type, methodName, IsStatic: true);
    var methods = MethodFamilyCache.GetOrAdd(key, static item =>
      new CachedMethods(item.Type.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.Name == item.Name)
        .OrderBy(method => method.GetParameters().Length)
        .ToArray())).Values;

    foreach (var method in methods)
    {
      var shapes = method.GetParameters()
        .Select(parameter => new ParameterShape(
          parameter.ParameterType.IsByRef
            ? parameter.ParameterType.GetElementType()!
            : parameter.ParameterType))
        .ToArray();
      var arguments = argumentBuilder(shapes);
      if (arguments == null)
      {
        continue;
      }

      try
      {
        result = method.Invoke(null, arguments);
        invokedArguments = arguments;
        return true;
      }
      catch (ArgumentException)
      {
      }
      catch (TargetParameterCountException)
      {
      }
      catch (TargetInvocationException)
      {
        // The runtime-selected overload rejected its synthesized defaults.
        // Continue so another compatible Civil 3D overload can be attempted.
      }
    }

    return false;
  }

  public static bool TryInvokeLoadedStaticMethod(
    string methodName,
    Type firstParameterType,
    out object? result,
    params object?[] arguments)
  {
    result = null;
    var key = new LoadedStaticMethodKey(methodName, firstParameterType, arguments.Length);
    var methods = LoadedStaticMethodCache.GetOrAdd(key, static item =>
    {
      var matches = new List<MethodInfo>();
      foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
      {
        Type[] types;
        try
        {
          types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
          types = exception.Types.Where(type => type != null).Cast<Type>().ToArray();
        }
        catch
        {
          continue;
        }

        foreach (var type in types)
        {
          matches.AddRange(type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == item.Name)
            .Where(method => method.GetParameters().Length == item.ArgumentCount)
            .Where(method => method.GetParameters().Length > 0
              && method.GetParameters()[0].ParameterType.IsAssignableFrom(item.FirstParameterType)));
        }
      }
      return new CachedMethods(matches.ToArray());
    }).Values;

    foreach (var method in methods)
    {
      try
      {
        result = method.Invoke(null, arguments);
        return true;
      }
      catch (ArgumentException)
      {
      }
      catch (TargetParameterCountException)
      {
      }
    }
    return false;
  }

  public static Type? FindLoadedType(params string[] fullNames)
  {
    foreach (var fullName in fullNames)
    {
      var cached = TypeCache.GetOrAdd(fullName, static candidateName =>
      {
        var assemblyQualifiedType = Type.GetType(candidateName, throwOnError: false, ignoreCase: false);
        if (assemblyQualifiedType != null)
        {
          return new CachedType(assemblyQualifiedType);
        }

        var commaIndex = candidateName.IndexOf(',');
        var normalizedName = commaIndex >= 0
          ? candidateName[..commaIndex].Trim()
          : candidateName;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
          var type = assembly.GetType(normalizedName, throwOnError: false, ignoreCase: false);
          if (type != null)
          {
            return new CachedType(type);
          }
        }

        return new CachedType(null);
      });
      if (cached.Value != null)
      {
        return cached.Value;
      }
    }

    return null;
  }

  private static PropertyInfo? ResolveProperty(Type type, string propertyName, bool isStatic)
  {
    var key = new PropertyKey(type, propertyName, isStatic);
    return PropertyCache.GetOrAdd(key, static item =>
    {
      var flags = BindingFlags.Public | (item.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
      return new CachedProperty(item.Type.GetProperty(item.Name, flags));
    }).Value;
  }

  // Civil 3D overrides single accessors: StyleBase.Name has only a setter over the readable
  // Civil DBObject.Name, Part.Name only a getter. Type.GetProperty returns the most-derived
  // declaration, which lacks the other accessor, so a reflective read returned null (C12b).
  // Walk the hierarchy for the first non-indexed declaration that has the accessor needed;
  // invoking a base declaration still dispatches virtually to the override.
  private static PropertyInfo? ResolveAccessor(Type type, string propertyName, bool forWrite)
  {
    var key = new PropertyKey(type, (forWrite ? "set:" : "get:") + propertyName, IsStatic: false);
    return PropertyCache.GetOrAdd(key, item =>
    {
      const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
      for (var current = item.Type; current != null; current = current.BaseType)
      {
        foreach (var property in current.GetProperties(flags))
        {
          if (property.Name == propertyName
            && property.GetIndexParameters().Length == 0
            && (forWrite ? property.CanWrite : property.CanRead))
          {
            return new CachedProperty(property);
          }
        }
      }

      return new CachedProperty(null);
    }).Value;
  }

  private static bool TryInvokeCandidates(
    Type type,
    object? target,
    string methodName,
    bool isStatic,
    object?[] arguments,
    out object? result)
  {
    result = null;
    var key = new MethodKey(type, methodName, isStatic, arguments.Length);
    var methods = MethodCache.GetOrAdd(key, static item =>
    {
      var flags = BindingFlags.Public | (item.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
      return new CachedMethods(item.Type
        .GetMethods(flags)
        .Where(method => method.Name == item.Name && method.GetParameters().Length == item.ArgumentCount)
        .ToArray());
    }).Values;

    foreach (var method in methods)
    {
      try
      {
        result = method.Invoke(target, arguments);
        return true;
      }
      catch (ArgumentException)
      {
        // Try the next overload. Invocation exceptions from a compatible
        // overload are allowed to propagate to the command's explicit error path.
      }
      catch (TargetParameterCountException)
      {
      }
    }

    return false;
  }
}
