using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>Runs a plugin command by name. C152 (2026-10-21): the commands live in <see cref="CommandRegistry"/> (name,
/// domain, read/write, handler); this was a switch of about 450 cases.</summary>
public static class CommandDispatcher
{
  public static Task<object?> DispatchAsync(string method, JsonObject? parameters, CancellationToken cancellationToken) =>
    CommandRegistry.All.TryGetValue(method, out var command)
      ? command.Handler(parameters, cancellationToken)
      : throw new JsonRpcDispatchException("CIVIL3D.METHOD_NOT_FOUND", $"Plugin method '{method}' is not implemented yet. listCommands lists the commands.");
}
