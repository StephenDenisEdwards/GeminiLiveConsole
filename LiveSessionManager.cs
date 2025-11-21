namespace GeminiLiveConsole;

public sealed class LiveSessionManager
{
    private readonly GeminiLiveClient _client;
    private readonly AudioCaptureService _audio;

    public event Action<string>? OnTranscript;
    public event Action<DetectedIntent>? OnIntent;
    public event Action<double>? OnVolume;
    public event Action<Exception>? OnError;
    public event Action? OnDisconnect;

    public LiveSessionManager(string apiKey, string model)
    {
        _client = new GeminiLiveClient(apiKey, model);
        _audio = new AudioCaptureService(16000);

        _client.OnOpen += () => _audio.Start();
        _client.OnMessage += HandleMessage;
        _client.OnError += e => OnError?.Invoke(e);
        _client.OnClose += () =>
        {
            _audio.Stop();
            OnDisconnect?.Invoke();
        };

        _audio.OnAudioChunk += async chunk =>
        {
            // Volume RMS
            var rms = ComputeRms(chunk);
            OnVolume?.Invoke(rms);
            await _client.SendAudioChunkAsync(chunk);
        };
    }

    public Task ConnectAsync(CancellationToken ct = default) => _client.ConnectAsync(ct);
    public Task DisconnectAsync() => _client.DisconnectAsync();

    private void HandleMessage(LiveServerMessage msg)
    {
        var transcript = msg.ServerContent?.InputTranscription?.Text;
        if (!string.IsNullOrWhiteSpace(transcript))
            OnTranscript?.Invoke(transcript);

        if (msg.ToolCall?.FunctionCalls != null)
        {
            foreach (var fc in msg.ToolCall.FunctionCalls)
            {
                if (fc.Name == "report_intent" && fc.Args != null)
                {
                    var intent = new DetectedIntent
                    {
                        Text = fc.Args.TryGetValue("text", out var t) ? t?.ToString() ?? "" : "",
                        Type = fc.Args.TryGetValue("type", out var tp) && tp?.ToString() == "QUESTION"
                            ? IntentType.QUESTION : IntentType.IMPERATIVE,
                        Answer = fc.Args.TryGetValue("answer", out var ans) ? ans?.ToString() ?? "" : ""
                    };
                    OnIntent?.Invoke(intent);
                    _ = _client.SendToolResponseAsync(fc); // ack
                }
            }
        }
    }

    private static double ComputeRms(byte[] pcm16)
    {
        int samples = pcm16.Length / 2;
        if (samples == 0) return 0;
        double sumSq = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(pcm16[2 * i] | (pcm16[2 * i + 1] << 8));
            double norm = s / 32768.0;
            sumSq += norm * norm;
        }
        return Math.Sqrt(sumSq / samples);
    }
}
