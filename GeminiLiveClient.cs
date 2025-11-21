using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;

namespace GeminiLiveConsole;

public sealed class GeminiLiveClient : IAsyncDisposable
{
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string? _bearerToken; // Optional OAuth2 access token if required by preview
    private ClientWebSocket _ws = new();
    private CancellationTokenSource? _cts;
    private Task? _recvTask;

    public event Action? OnOpen;
    public event Action? OnClose;
    public event Action<Exception>? OnError;
    public event Action<LiveServerMessage>? OnMessage;
    public bool IsConnected { get; private set; }

    public GeminiLiveClient(string apiKey, string model, string? bearerToken = null)
    {
        _apiKey = apiKey;
        _model = model;
        _bearerToken = bearerToken;
    }

    private IEnumerable<Uri> BuildCandidateUris()
    {
        // We try several permutations because preview endpoints may differ.
        var bases = new[]
        {
            "wss://generativelanguage.googleapis.com/v1beta/live:connect",
            "wss://generativelanguage.googleapis.com/v1/live:connect"
        };

        foreach (var b in bases)
        {
            // Pattern A: key only
            yield return new Uri(b + "?key=" + Uri.EscapeDataString(_apiKey));
            // Pattern B: no query (if server expects header-only)
            yield return new Uri(b);
        }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? lastEx = null;
        foreach (var candidate in BuildCandidateUris())
        {
            try
            {
                // Fresh socket per attempt to avoid stale state
                if (_ws.State != WebSocketState.None && _ws.State != WebSocketState.Closed)
                {
                    try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "retry", CancellationToken.None); } catch { }
                }
                _ws.Dispose();
                _ws = new ClientWebSocket();
                Console.WriteLine($"[Connect Attempt] {candidate}");
                if (_bearerToken is not null)
                {
                    // Requires .NET 8 for SetRequestHeader.
                    try { _ws.Options.SetRequestHeader("Authorization", $"Bearer {_bearerToken}"); } catch { /* ignore if unsupported */ }
                    // Some services may still need API key header separate from query.
                    try { _ws.Options.SetRequestHeader("x-goog-api-key", _apiKey); } catch { /* ignore */ }
                }
                await _ws.ConnectAsync(candidate, _cts.Token);
                Console.WriteLine("[Connect] Handshake succeeded.");
                IsConnected = true;
                OnOpen?.Invoke();
                await SendSetupFrameAsync(_cts.Token);
                _recvTask = Task.Run(ReceiveLoopAsync);
                return; // success
            }
            catch (WebSocketException wsex)
            {
                Console.Error.WriteLine($"[Handshake Fail] {candidate} -> {wsex.Message}");
                lastEx = wsex;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Connect ERROR] {candidate} -> {ex.Message}");
                lastEx = ex;
            }
        }
        OnError?.Invoke(lastEx ?? new InvalidOperationException("All connection attempts failed."));
        await CloseInternalAsync();
    }

    private async Task SendSetupFrameAsync(CancellationToken ct)
    {
        var setup = new
        {
            setup = new
            {
                model = $"models/{_model}",
                config = new
                {
                    responseModalities = new[] { "AUDIO" },
                    inputAudioTranscription = new { },
                    systemInstruction = @"You are a dedicated Conversation Monitor and Assistant. Detect QUESTION or IMPERATIVE and call report_intent. Ignore casual speech.",
                    tools = new[]
                    {
                        new { functionDeclarations = new[] { new {
                            name = "report_intent",
                            description = "Report detected question or imperative command.",
                            parameters = new {
                                type = "OBJECT",
                                properties = new {
                                    text = new { type = "STRING" },
                                    type = new { type = "STRING", enumValues = new []{"QUESTION","IMPERATIVE"} },
                                    answer = new { type = "STRING" }
                                },
                                required = new []{"text","type","answer"}
                            }
                        } } }
                    }
                }
            }
        };
        await SendJsonAsync(setup, ct);
    }

    public async Task SendAudioChunkAsync(byte[] pcm16, CancellationToken ct = default)
    {
        if (!IsConnected) return;
        var frame = new
        {
            realtimeInput = new
            {
                media = new
                {
                    mimeType = "audio/pcm;rate=16000;channels=1",
                    data = Convert.ToBase64String(pcm16)
                }
            }
        };
        await SendJsonAsync(frame, ct);
    }

    public async Task SendToolResponseAsync(ToolFunctionCall call, CancellationToken ct = default)
    {
        var payload = new
        {
            toolResponse = new
            {
                functionResponses = new[]
                {
                    new { id = call.Id, name = call.Name, response = new { result = "logged" } }
                }
            }
        };
        await SendJsonAsync(payload, ct);
    }

    private async Task SendJsonAsync(object obj, CancellationToken ct)
    {
        var json = JsonConvert.SerializeObject(obj);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts!.IsCancellationRequested)
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await CloseInternalAsync();
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var txt = Encoding.UTF8.GetString(ms.ToArray());
                LiveServerMessage? message = null;
                try { message = JsonConvert.DeserializeObject<LiveServerMessage>(txt); }
                catch (Exception dex) { OnError?.Invoke(dex); }
                if (message != null) OnMessage?.Invoke(message);
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
            await CloseInternalAsync();
        }
    }

    public async Task DisconnectAsync()
    {
        await CloseInternalAsync();
    }

    private async Task CloseInternalAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None); } catch { }
        }
        IsConnected = false;
        OnClose?.Invoke();
        _cts?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseInternalAsync();
        _ws.Dispose();
        _cts?.Dispose();
    }
}
