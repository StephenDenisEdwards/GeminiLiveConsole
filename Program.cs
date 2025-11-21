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
		//var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");

		var configBuilder = new ConfigurationBuilder();
		configBuilder.AddUserSecrets<Program>(); // loads secrets.json
		var configuration = configBuilder.Build();

		var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? configuration["GoogleGemini:ApiKey"] ?? "YOUR_API_KEY"; // Replace or set env var or user secret.

		if (string.IsNullOrWhiteSpace(apiKey))
		{
			Console.WriteLine("ERROR: Please set GEMINI_API_KEY environment variable.");
			return;
		}

		// ? Correct Gemini Live WebSocket endpoint (bidi)
		var wsUrl =
			$"wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key={Uri.EscapeDataString(apiKey)}";
		//var wsUrl =
		//  $"wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key={Uri.EscapeDataString(apiKey)}";

		using var ws = new ClientWebSocket();
		var cts = new CancellationTokenSource();

		Console.WriteLine("Connecting to Gemini Live...");
		await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
		Console.WriteLine("Connected.\n");

		// 1) Send setup message (BidiGenerateContentSetup)
		var setupMessage = new
		{
			setup = new
			{
				// ?? Use a Live-capable model you actually have access to:
				model = "models/gemini-2.0-flash-exp",
				//model = "models/gemini-2.5-flash-native-audio-preview-09-2025",
				generationConfig = new
				{
					// We want TEXT back, not audio
					responseModalities = new[] { "TEXT" }
				},
				// systemInstruction must be a Content object (parts -> text), not a raw string
				systemInstruction = new
				{
					parts = new[]
					{
						new { text = "You are a helpful assistant. Listen to the user speaking and reply in text." }
					}
				}
			}
		};

		string setupJson = JsonSerializer.Serialize(setupMessage);

		await ws.SendAsync(
			Encoding.UTF8.GetBytes(setupJson),
			WebSocketMessageType.Text,
			endOfMessage: true,
			cancellationToken: CancellationToken.None);

		Console.WriteLine("Sent setup message.");
		Console.WriteLine("Press ENTER to start recording, ENTER again to stop.\n");
		Console.ReadLine();

		// Start receive loop (runs concurrently)
		var receiveTask = ReceiveLoopAsync(ws, cts.Token);

		// 2) Start microphone capture and stream binary audio frames
		await StreamMicrophoneAsync(ws);

		// 3) Tell Gemini the audio stream has ended (BidiGenerateContentRealtimeInput.audioStreamEnd)
		var endMessage = new
		{
			realtimeInput = new
			{
				audioStreamEnd = true
			}
		};

		string endJson = JsonSerializer.Serialize(endMessage);

		await ws.SendAsync(
			Encoding.UTF8.GetBytes(endJson),
			WebSocketMessageType.Text,
			endOfMessage: true,
			cancellationToken: CancellationToken.None);

		Console.WriteLine("\nSent audioStreamEnd. Waiting a bit for final responses...");

		// Wait a short time for final responses
		await Task.Delay(3000);
		cts.Cancel();

		if (ws.State == WebSocketState.Open)
		{
			await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
		}

		await receiveTask;
		Console.WriteLine("Connection closed. Press ENTER to exit.");
		Console.ReadLine();
	}

	// --- Microphone streaming using NAudio ---
	private static async Task StreamMicrophoneAsync(ClientWebSocket ws)
	{
		// We ask NAudio for 16kHz, 16-bit, mono.
		// Many devices support this directly; if not, you'd add a resampler.
		var waveIn = new WaveInEvent
		{
			WaveFormat = new WaveFormat(16000, 16, 1) // 16kHz, 16-bit, mono
		};

		Console.WriteLine("Recording... (press ENTER to stop)");

		// We’ll stop recording when user presses ENTER
		var stopCts = new CancellationTokenSource();

		// IMPORTANT: event handler will send binary audio chunks to the WebSocket
		waveIn.DataAvailable += async (s, a) =>
		{
			if (ws.State != WebSocketState.Open) return;
			// Each DataAvailable callback is a "chunk" of audio; send as Binary
			try
			{
				await ws.SendAsync(
					new ArraySegment<byte>(a.Buffer, 0, a.BytesRecorded),
					WebSocketMessageType.Binary,
					endOfMessage: true,
					cancellationToken: CancellationToken.None);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Send error] {ex.Message}");
			}
		};

		waveIn.StartRecording();

		// Wait until user hits ENTER
		await Task.Run(() => Console.ReadLine(), stopCts.Token)
				  .ContinueWith(_ => { }, TaskScheduler.Default);

		waveIn.StopRecording();
		waveIn.Dispose();

		Console.WriteLine("Stopped recording.");
	}

	// --- Receive JSON text messages from Gemini ---
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
					Console.WriteLine("?? JSON from Gemini:");
					Console.WriteLine(json);
					Console.WriteLine();

					// TODO: parse json to extract actual text:
					// serverContent.candidates[0].content.parts[*].text
				}
				else if (result.MessageType == WebSocketMessageType.Binary)
				{
					// If you ever request AUDIO responses, you’d handle binary here.
					Console.WriteLine("[Received binary data]");
				}
			}
		}
		catch (OperationCanceledException)
		{
			// normal on cancellation
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Receive error] {ex.Message}");
		}
	}
}


//using GeminiLiveConsole;

//class Program
//{
//    private const string ModelName = "gemini-2.5-flash-native-audio-preview-09-2025";

//    static async Task Main()
//    {
//        var apiKey = "AIzaSyBkGwMOLWFQaYmkXYiv7P4C2zMkLA44zyo"; //Environment.GetEnvironmentVariable("API_KEY");
//        if (string.IsNullOrWhiteSpace(apiKey))
//        {
//            Console.WriteLine("Missing API_KEY environment variable. Set it then restart shell.");
//            return;
//        }

//        var mgr = new LiveSessionManager(apiKey, ModelName);
//        mgr.OnTranscript += t => Console.WriteLine($"Transcript: {t}");
//        mgr.OnIntent += i => Console.WriteLine($"Intent: {i.Type} | {i.Text} | {i.Answer}");
//        mgr.OnVolume += v => { /* Could visualize volume */ };
//        mgr.OnError += e => Console.WriteLine($"Error: {e.Message}");
//        mgr.OnDisconnect += () => Console.WriteLine("Disconnected.");

//        Console.CancelKeyPress += async (_, e) =>
//        {
//            e.Cancel = true;
//            await mgr.DisconnectAsync();
//        };

//        Console.WriteLine("Connecting...");
//        await mgr.ConnectAsync();
//        Console.WriteLine("Connected. Press Ctrl+C to stop.");
//        await Task.Delay(Timeout.Infinite);
//    }
//}

//using System;
//using System.Net.WebSockets;
//using System.Text;
//using System.Threading;
//using System.Threading.Tasks;
//using System.IO;

//namespace GeminiLiveAudioToText
//{
//	class Program
//	{
//		static async Task Main(string[] args)
//		{
//			string endpoint = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
//			string model = "gemini-live-2.5-flash-preview";  // adjust to the model you have access to
//			string token = "YOUR_EPHEMERAL_TOKEN_OR_API_KEY";  // whichever mode you use

//			using (var ws = new ClientWebSocket())
//			{
//				// If using token in header
//				ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
//				// OR if query parameter: endpoint += $"?access_token={token}";

//				await ws.ConnectAsync(new Uri(endpoint), CancellationToken.None);
//				Console.WriteLine("WebSocket connected");

//				// Send setup
//				var setupObj = new
//				{
//					setup = new
//					{
//						model = model,
//						generationConfig = new
//						{
//							responseModalities = new[] { "TEXT" }
//						},
//						systemInstruction = "You are a helpful assistant that listens to audio and replies in text."
//					}
//				};
//				string setupJson = System.Text.Json.JsonSerializer.Serialize(setupObj);
//				await ws.SendAsync(
//					Encoding.UTF8.GetBytes(setupJson),
//					WebSocketMessageType.Text, true, CancellationToken.None);

//				Console.WriteLine("Setup sent");

//				// Now: stream audio
//				// Example: read from a WAV or PCM file
//				string audioFilePath = "input16k16bitpcm.raw";  // or convert WAV to raw PCM 16kHz 16-bit LE
//				const int chunkSize = 16000 * 2 * 1 / 10; // e.g., 100ms chunks => sampleRate*bytesPerSample*channels*duration
//				byte[] buffer = new byte[chunkSize];

//				using (var fs = File.OpenRead(audioFilePath))
//				{
//					int bytesRead;
//					while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
//					{
//						await ws.SendAsync(
//							new ArraySegment<byte>(buffer, 0, bytesRead),
//							WebSocketMessageType.Binary, true, CancellationToken.None);

//						await Task.Delay(100);  // pacing – adjust depending on your capture pipeline
//					}
//				}

//				// Optionally send a message / flag indicating end of audio
//				var endObj = new
//				{
//					realtimeInput = new
//					{
//						audioStreamEnd = true
//					}
//				};
//				string endJson = System.Text.Json.JsonSerializer.Serialize(endObj);
//				await ws.SendAsync(
//					Encoding.UTF8.GetBytes(endJson),
//					WebSocketMessageType.Text, true, CancellationToken.None);

//				Console.WriteLine("Audio stream ended; now waiting for response");

//				// Receive loop
//				var recvBuffer = new byte[8192];
//				while (ws.State == WebSocketState.Open)
//				{
//					var result = await ws.ReceiveAsync(new ArraySegment<byte>(recvBuffer), CancellationToken.None);
//					if (result.MessageType == WebSocketMessageType.Close)
//					{
//						Console.WriteLine("Server requested close");
//						await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", CancellationToken.None);
//					}
//					else if (result.MessageType == WebSocketMessageType.Text)
//					{
//						string json = Encoding.UTF8.GetString(recvBuffer, 0, result.Count);
//						Console.WriteLine("Received TEXT message:");
//						Console.WriteLine(json);
//						// parse out the actual response text from the JSON – the schema docs define where the .text field sits
//					}
//					else if (result.MessageType == WebSocketMessageType.Binary)
//					{
//						// unlikely for text output mode
//						Console.WriteLine("Received unexpected binary data");
//					}
//				}

//				Console.WriteLine("Connection closed");
//			}
//		}
//	}
//}
//using System;
//using System.IO;
//using System.Net.Http;
//using System.Net.Http.Headers;
//using System.Net.WebSockets;
//using System.Text;
//using System.Text.Json;
//using System.Threading;
//using System.Threading.Tasks;
//using Microsoft.Extensions.Configuration;

//namespace GeminiLiveAudioToText
//{
//	class Program
//	{
//		static async Task Main(string[] args)
//		{
//			var configBuilder = new ConfigurationBuilder();
//			configBuilder.AddUserSecrets<Program>(); // loads secrets.json
//			var configuration = configBuilder.Build();

//			var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? configuration["GoogleGemini:ApiKey"] ?? "YOUR_API_KEY"; // Replace or set env var or user secret.

//			// string apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
//			//			?? throw new InvalidOperationException("Set GEMINI_API_KEY env var");

//			string token = await CreateEphemeralTokenAsync(apiKey);

//			string websocketUrl = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
//			using (var ws = new ClientWebSocket())
//			{
//				ws.Options.SetRequestHeader("Authorization", $"Token {token}");
//				await ws.ConnectAsync(new Uri(websocketUrl), CancellationToken.None);
//				Console.WriteLine("WebSocket connected.");

//				// Send setup message
//				var setupObj = new
//				{
//					setup = new
//					{
//						model = "gemini-2.5-flash-native-audio-preview-09-2025",   // or whichever your account supports
//						generationConfig = new
//						{
//							responseModalities = new[] { "TEXT" }
//						},
//						systemInstruction = "You are a helpful assistant listening to audio and responding in text."
//					}
//				};
//				string setupJson = JsonSerializer.Serialize(setupObj);
//				await ws.SendAsync(Encoding.UTF8.GetBytes(setupJson),
//								  WebSocketMessageType.Text, true, CancellationToken.None);
//				Console.WriteLine("Setup sent.");

//				// Stream audio from file (example)
//				string audioFile = "input16k16bitpcm.raw";  // ensure format: 16kHz, 16-bit LE, mono
//				const int bytesPerSample = 2; // 16bit
//				const int sampleRate = 16000;
//				int chunkMilliseconds = 200;
//				int chunkSize = sampleRate * bytesPerSample * chunkMilliseconds / 1000;
//				byte[] buffer = new byte[chunkSize];

//				using (var fs = File.OpenRead(audioFile))
//				{
//					int bytesRead;
//					while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
//					{
//						await ws.SendAsync(new ArraySegment<byte>(buffer, 0, bytesRead),
//										   WebSocketMessageType.Binary, true, CancellationToken.None);
//						await Task.Delay(chunkMilliseconds);
//					}
//				}

//				// Signal end of audio stream
//				var endObj = new
//				{
//					realtimeInput = new
//					{
//						audioStreamEnd = true
//					}
//				};
//				string endJson = JsonSerializer.Serialize(endObj);
//				await ws.SendAsync(Encoding.UTF8.GetBytes(endJson),
//								  WebSocketMessageType.Text, true, CancellationToken.None);
//				Console.WriteLine("Audio streaming finished. Waiting for responses...");

//				// Receive loop
//				var receiveBuffer = new byte[8192];
//				while (ws.State == WebSocketState.Open)
//				{
//					var result = await ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), CancellationToken.None);
//					if (result.MessageType == WebSocketMessageType.Close)
//					{
//						Console.WriteLine("Server requested close.");
//						await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", CancellationToken.None);
//					}
//					else if (result.MessageType == WebSocketMessageType.Text)
//					{
//						string json = Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);
//						Console.WriteLine("Received TEXT message:");
//						Console.WriteLine(json);
//						// TODO: parse the JSON to extract the actual model text (e.g., serverContent.text or similar)
//					}
//					else if (result.MessageType == WebSocketMessageType.Binary)
//					{
//						Console.WriteLine("Received unexpected binary data.");
//					}
//				}

//				Console.WriteLine("Connection closed.");
//			}
//		}

//		static async Task<string> CreateEphemeralTokenAsync(string apiKey)
//		{
//			using var client = new HttpClient();
//			client.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);
//			client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

//			var now = DateTime.UtcNow;
//			//var body = new
//			//{
//			//	config = new
//			//	{
//			//		uses = 1,
//			//		expireTime = now.AddMinutes(30).ToString("o"),
//			//		newSessionExpireTime = now.AddMinutes(1).ToString("o"),
//			//		liveConnectConstraints = new
//			//		{
//			//			model = "gemini-2.5-flash-native-audio-preview-09-2025",
//			//			config = new
//			//			{
//			//				responseModalities = new[] { "TEXT" }
//			//			}
//			//		}
//			//	}
//			//};

//			var body = new
//			{
//				ttl = "1800s",
//				capabilities = new[]
//				{
//					new
//					{
//						methods = new[] { "google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent" },
//						model = "models/gemini-2.5-flash-native-audio-preview-09-2025"
//					}
//				}
//			};

//			var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
//			//var resp = await client.PostAsync("https://generativelanguage.googleapis.com/v1alpha/authTokens:create", content);
//			var resp = await client.PostAsync("https://generativelanguage.googleapis.com/v1beta/tokens:generate", content);

//			resp.EnsureSuccessStatusCode();

//			var respJson = await resp.Content.ReadAsStringAsync();
//			using var doc = JsonDocument.Parse(respJson);
//			string tokenName = doc.RootElement.GetProperty("name").GetString()
//							   ?? throw new InvalidOperationException("Token name missing in response");
//			Console.WriteLine($"Ephemeral token created: {tokenName}");
//			return tokenName;
//		}
//	}
//}
