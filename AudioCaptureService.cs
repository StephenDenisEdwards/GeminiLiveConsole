using NAudio.Wave;

namespace GeminiLiveConsole;

public sealed class AudioCaptureService : IDisposable
{
    private WaveInEvent? _waveIn;
    public event Action<byte[]>? OnAudioChunk;
    private readonly int _sampleRate;

    public AudioCaptureService(int sampleRate = 16000) => _sampleRate = sampleRate;

    public void Start()
    {
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(_sampleRate, 16, 1),
            BufferMilliseconds = 100
        };
        _waveIn.DataAvailable += HandleData;
        _waveIn.StartRecording();
    }

    private void HandleData(object? sender, WaveInEventArgs e)
    {
        var chunk = new byte[e.BytesRecorded];
        Array.Copy(e.Buffer, chunk, e.BytesRecorded);
        OnAudioChunk?.Invoke(chunk);
    }

    public void Stop()
    {
        if (_waveIn == null) return;
        _waveIn.DataAvailable -= HandleData;
        _waveIn.StopRecording();
        _waveIn.Dispose();
        _waveIn = null;
    }

    public void Dispose() => Stop();
}
