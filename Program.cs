using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using NAudio.Wave;

class Program
{
	static async Task Main()
	{
		var configBuilder = new ConfigurationBuilder();
		configBuilder.AddUserSecrets<Program>();
		var configuration = configBuilder.Build();

		var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? configuration["GoogleGemini:ApiKey"] ?? "YOUR_API_KEY";
		if (string.IsNullOrWhiteSpace(apiKey)) { Console.WriteLine("ERROR: Please set GEMINI_API_KEY environment variable."); return; }

		var wsUrl = $"wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key={Uri.EscapeDataString(apiKey)}";
		using var ws = new ClientWebSocket();
		var cts = new CancellationTokenSource();

		Console.WriteLine("Connecting to Gemini Live...");
		await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
		Console.WriteLine("Connected.\n");

		// Setup: typical Content parts format
		var setupMessage = new
		{
			setup = new
			{
				model = "models/gemini-2.0-flash-exp",
				generationConfig = new { responseModalities = new[] { "TEXT" } },
				systemInstruction = new { parts = new[] { new { text = "You are a helpful assistant. Listen to the user speaking and reply in text." } } }
			}
		};


		await SendJsonAsync(ws, setupMessage);
		Console.WriteLine("Sent setup message.");
		Console.WriteLine("Press ENTER to start recording, ENTER again to stop.\n");
		Console.ReadLine();

		var receiveTask = ReceiveLoopAsync(ws, cts.Token);
		await StreamMicrophoneAsync(ws);

		// audioStreamEnd still camelCase per earlier usage
		var endMessage = new { realtimeInput = new { audioStreamEnd = true } };
		await SendJsonAsync(ws, endMessage);
		Console.WriteLine("\nSent audioStreamEnd. Waiting a bit for final responses...");

		await Task.Delay(3000);
		cts.Cancel();
		if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
		await receiveTask;
		Console.WriteLine("Connection closed. Press ENTER to exit.");
		Console.ReadLine();
	}

	private static async Task StreamMicrophoneAsync(ClientWebSocket ws)
	{
		var waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
		Console.WriteLine("Recording... (press ENTER to stop)");
		var stopCts = new CancellationTokenSource();

		waveIn.DataAvailable += async (s, a) =>
		{
			if (ws.State != WebSocketState.Open) return;
			try
			{
				var base64 = Convert.ToBase64String(a.Buffer, 0, a.BytesRecorded);
				// Correct realtimeInput structure: input.parts.inlineData
				var audioFrame = new
				{
					realtimeInput = new
					{
						audio = new
						{
							//mimeType = "audio/raw;encoding=LINEAR16;rate=16000",
							mimeType = "audio/pcm;rate=16000",
							data = Convert.ToBase64String(a.Buffer, 0, a.BytesRecorded)
						}
					}
					//realtimeInput = new
					//{
					//	input = new
					//	{
					//		parts = new[]
					//		{
					//			new { inlineData = new { mimeType = "audio/pcm;rate=16000", data = base64 } }
					//		}
					//	}
					//}
				};

	

				await SendJsonAsync(ws, audioFrame);
			}
			catch (Exception ex) { Console.WriteLine($"[Send error] {ex.Message}"); }
		};

		waveIn.StartRecording();
		await Task.Run(() => Console.ReadLine(), stopCts.Token).ContinueWith(_ => { }, TaskScheduler.Default);
		waveIn.StopRecording();
		waveIn.Dispose();
		Console.WriteLine("Stopped recording.");
	}

	private static async Task SendJsonAsync(ClientWebSocket ws, object payload)
	{
		
		var json = JsonSerializer.Serialize(payload);
		//Console.WriteLine(">> SETUP:");
		//Console.WriteLine(json);
		//Console.WriteLine();
		var bytes = Encoding.UTF8.GetBytes(json);
		await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
	}

	private static async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
	{
		var buffer = new byte[16 * 1024];
		try
		{
			while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
			{
				var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
				if (result.MessageType == WebSocketMessageType.Close)
				{
					Console.WriteLine($"[Server closed] {result.CloseStatus} {result.CloseStatusDescription}");
					break;
				}
				if (result.MessageType == WebSocketMessageType.Text)
				{
					var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
					Console.WriteLine();
					Console.WriteLine("JSON from Gemini:");
					Console.WriteLine(json);
					Console.WriteLine();
				}
				else if (result.MessageType == WebSocketMessageType.Binary)
				{
					Console.WriteLine("[Received unexpected binary data]");
				}
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) { Console.WriteLine($"[Receive error] {ex.Message}"); }
	}
}
