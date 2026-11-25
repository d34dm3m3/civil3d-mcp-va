using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>Runs a plugin command by name. C152 (2026-10-21): the commands live in <see cref="CommandRegistry"/> (name,
/// domain, read/write, handler); this was a switch of about 450 cases.</summary>
public static class CommandDispatcher
{
  public static async Task<object?> DispatchAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
  {
    if (!CommandRegistry.All.TryGetValue(method, out var command))
      throw new JsonRpcDispatchException("CIVIL3D.METHOD_NOT_FOUND", $"Plugin method '{method}' is not implemented yet. listCommands lists the commands.");
    // C211: the creating commands take an optional script id (id, idData), written here after the command so that every
    // one of them (and every call in a batch) behaves the same; checked before the command runs.
    var scriptId = ScriptIdCommands.CreatingCommands.Contains(method) ? ScriptIdCommands.ReadRequested(parameters) : null;
    // C222: profile: true (or kommun) fills the values the caller left out from the rule profile before the command
    // runs, here so that the commands themselves stay unchanged; the answer says what came from the profile.
    Dictionary<string, object?>? profileReport = null;
    if (RulesProfile.ProfileCommands.Contains(method) && RulesProfile.Requested(parameters))
      (parameters, profileReport) = RulesProfile.ApplyDefaults(method, parameters);
    var result = await command.Handler(parameters, cancellationToken);
    if (profileReport != null) result = RulesProfile.Annotate(result, profileReport);
    return scriptId is { } wanted ? await ScriptIdCommands.TagCreatedAsync(result, wanted.Id, wanted.Data) : result;
  }
}
