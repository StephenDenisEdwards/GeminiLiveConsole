using GeminiLiveConsole;

class Program
{
    private const string ModelName = "gemini-2.5-flash-native-audio-preview-09-2025";

    static async Task Main()
    {
        var apiKey = "AIzaSyBkGwMOLWFQaYmkXYiv7P4C2zMkLA44zyo"; //Environment.GetEnvironmentVariable("API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("Missing API_KEY environment variable. Set it then restart shell.");
            return;
        }

        var mgr = new LiveSessionManager(apiKey, ModelName);
        mgr.OnTranscript += t => Console.WriteLine($"Transcript: {t}");
        mgr.OnIntent += i => Console.WriteLine($"Intent: {i.Type} | {i.Text} | {i.Answer}");
        mgr.OnVolume += v => { /* Could visualize volume */ };
        mgr.OnError += e => Console.WriteLine($"Error: {e.Message}");
        mgr.OnDisconnect += () => Console.WriteLine("Disconnected.");

        Console.CancelKeyPress += async (_, e) =>
        {
            e.Cancel = true;
            await mgr.DisconnectAsync();
        };

        Console.WriteLine("Connecting...");
        await mgr.ConnectAsync();
        Console.WriteLine("Connected. Press Ctrl+C to stop.");
        await Task.Delay(Timeout.Infinite);
    }
}
