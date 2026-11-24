using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Civil3DMcpPlugin;

public sealed class RpcTcpServer
{
  private const int MaxRequestBytes = 1024 * 1024;
  private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(30);

  private readonly int _port;
  private readonly Func<string, CancellationToken, Task<string>> _handler;
  private readonly CancellationTokenSource _cts = new();
  private TcpListener? _listener;
  private Task? _acceptLoop;

  public RpcTcpServer(int port, Func<string, CancellationToken, Task<string>> handler)
  {
    _port = port;
    _handler = handler;
  }

  public void Start()
  {
    _listener = new TcpListener(IPAddress.Loopback, _port);
    _listener.Start();
    _acceptLoop = Task.Run(AcceptLoopAsync, _cts.Token);
  }

  public void Stop()
  {
    try
    {
      _cts.Cancel();
      _listener?.Stop();
      _acceptLoop?.Wait(TimeSpan.FromSeconds(1));
    }
    catch
    {
    }
  }

  private async Task AcceptLoopAsync()
  {
    while (!_cts.IsCancellationRequested)
    {
      TcpClient? client = null;
      try
      {
        client = await _listener!.AcceptTcpClientAsync(_cts.Token);
        _ = Task.Run(() => ProcessClientAsync(client, _cts.Token), _cts.Token);
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch
      {
        client?.Dispose();
      }
    }
  }

  private async Task ProcessClientAsync(TcpClient client, CancellationToken cancellationToken)
  {
    try
    {
      using (client)
      await using (var stream = client.GetStream())
      {
        var request = await ReadSingleJsonObjectAsync(stream, cancellationToken);
        if (string.IsNullOrWhiteSpace(request))
        {
          return;
        }

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var disconnectMonitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var disconnectMonitor = MonitorClientDisconnectAsync(
          stream,
          requestCancellation,
          disconnectMonitorCancellation.Token);

        // C93: work registered by the request to run once the client has the answer (document_close).
        var answer = AnswerScope.Begin();
        var answerWritten = false;
        try
        {
          var response = await _handler(request, requestCancellation.Token);
          disconnectMonitorCancellation.Cancel();
          var clientDisconnected = await disconnectMonitor;
          if (clientDisconnected)
          {
            PluginLog.Debug("RpcTcpServer", "Skipped response write because the client disconnected.");
            return;
          }

          var responseBytes = Encoding.UTF8.GetBytes(response);
          await stream.WriteAsync(responseBytes, cancellationToken);
          await stream.FlushAsync(cancellationToken);
          answerWritten = true;
        }
        finally
        {
          answer.Complete(answerWritten);
        }
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      // Normal plugin shutdown.
    }
    catch (TimeoutException)
    {
      PluginLog.Warn("RpcTcpServer", $"Closed a client that did not send a complete request within {RequestReadTimeout.TotalSeconds:0} seconds.");
    }
    catch (OperationCanceledException)
    {
      PluginLog.Debug("RpcTcpServer", "Client disconnected and its queued request was cancelled.");
    }
    catch (InvalidDataException ex)
    {
      PluginLog.Warn("RpcTcpServer", ex.Message);
    }
    catch (Exception ex)
    {
      PluginLog.Error("RpcTcpServer", "Client request processing failed.", ex);
    }
  }

  private static async Task<string> ReadSingleJsonObjectAsync(NetworkStream stream, CancellationToken cancellationToken)
  {
    var buffer = new byte[8192];
    using var requestBuffer = new MemoryStream();
    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    readTimeout.CancelAfter(RequestReadTimeout);

    try
    {
      while (!readTimeout.IsCancellationRequested)
      {
        var bytesRead = await stream.ReadAsync(buffer, readTimeout.Token);
        if (bytesRead <= 0)
        {
          break;
        }

        if (requestBuffer.Length + bytesRead > MaxRequestBytes)
        {
          throw new InvalidDataException($"RPC request exceeds the {MaxRequestBytes}-byte limit.");
        }

        await requestBuffer.WriteAsync(buffer.AsMemory(0, bytesRead), readTimeout.Token);
        var requestBytes = requestBuffer.GetBuffer();
        var requestLength = checked((int)requestBuffer.Length);

        try
        {
          using var _ = JsonDocument.Parse(
            new ReadOnlyMemory<byte>(requestBytes, 0, requestLength));
          return Encoding.UTF8.GetString(requestBytes, 0, requestLength);
        }
        catch (JsonException)
        {
        }
      }
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && readTimeout.IsCancellationRequested)
    {
      throw new TimeoutException("RPC request read timed out.");
    }

    return Encoding.UTF8.GetString(
      requestBuffer.GetBuffer(),
      0,
      checked((int)requestBuffer.Length));
  }

  private static async Task<bool> MonitorClientDisconnectAsync(
    NetworkStream stream,
    CancellationTokenSource requestCancellation,
    CancellationToken monitorCancellation)
  {
    var probe = new byte[1];
    try
    {
      while (!monitorCancellation.IsCancellationRequested)
      {
        var bytesRead = await stream.ReadAsync(probe, monitorCancellation);
        if (bytesRead == 0)
        {
          requestCancellation.Cancel();
          return true;
        }

        // The protocol permits one JSON request per connection. Ignore any
        // trailing bytes while continuing to watch for the peer's FIN.
      }
    }
    catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
    {
      return false;
    }
    catch (IOException)
    {
      requestCancellation.Cancel();
      return true;
    }
    catch (SocketException)
    {
      requestCancellation.Cancel();
      return true;
    }

    return false;
  }
}

/// <summary>
/// C93 (2026-10-10): one per request. A request may register one step that has to wait until the client has the
/// answer: document_close closes the drawing only after the answer has been written (requested: never
/// close before the answer has been sent). The server completes the scope in a finally, with whether a result (not an error) was written; the step
/// is told so and skips its work otherwise. Should the server never complete it, the step is completed as not written
/// after a minute, so whatever it holds (the host gate) is never kept for good.
/// </summary>
internal sealed class AnswerScope
{
  private static readonly AsyncLocal<AnswerScope?> CurrentScope = new();
  private static readonly TimeSpan CompletionDeadline = TimeSpan.FromSeconds(60);

  private readonly object _sync = new();
  private Action<bool>? _afterAnswer;
  private bool _completed;
  private bool _result;

  /// <summary>The scope of the request running on this async flow, null outside a TCP request (a test).</summary>
  internal static AnswerScope? Current => CurrentScope.Value;

  /// <summary>False once the answer has been handled (a job started by the request still carries the scope).</summary>
  internal bool IsOpen
  {
    get
    {
      lock (_sync) return !_completed;
    }
  }

  internal static AnswerScope Begin()
  {
    var scope = new AnswerScope();
    CurrentScope.Value = scope;
    return scope;
  }

  /// <summary>Marks the answer as a result; an error answer never runs the step.</summary>
  internal void MarkResult()
  {
    lock (_sync) _result = true;
  }

  /// <summary>Registers the step (one per request); false when one is registered already or the answer is done.</summary>
  internal bool TryRegister(Action<bool> afterAnswer)
  {
    lock (_sync)
    {
      if (_afterAnswer != null || _completed) return false;
      _afterAnswer = afterAnswer;
    }

    _ = Task.Delay(CompletionDeadline).ContinueWith(_ => Complete(false), TaskScheduler.Default);
    return true;
  }

  internal void Complete(bool answerWritten)
  {
    Action<bool>? afterAnswer;
    bool written;
    lock (_sync)
    {
      if (_completed) return;
      _completed = true;
      afterAnswer = _afterAnswer;
      _afterAnswer = null;
      written = answerWritten && _result;
    }

    if (afterAnswer == null) return;
    try
    {
      afterAnswer(written);
    }
    catch (Exception ex)
    {
      PluginLog.Error("RpcTcpServer", "The step after the answer failed to start.", ex);
    }
  }
}
