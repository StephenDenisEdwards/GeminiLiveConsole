using System;
using System.IO;
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
		// --- Load API key (env var first, then user secrets) ---
		var configBuilder = new ConfigurationBuilder();
		configBuilder.AddUserSecrets<Program>();
		var configuration = configBuilder.Build();

		var apiKey =
			Environment.GetEnvironmentVariable("GEMINI_API_KEY") ??
			configuration["GoogleGemini:ApiKey"];

		if (string.IsNullOrWhiteSpace(apiKey))
		{
			Console.WriteLine("ERROR: Please set GEMINI_API_KEY env var or GoogleGemini:ApiKey in user secrets.");
			return;
		}

		// --- WebSocket endpoint for Live API (bidiGenerateContent) ---
		var wsUrl =
			$"wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key={Uri.EscapeDataString(apiKey)}";

		using var ws = new ClientWebSocket();
		using var cts = new CancellationTokenSource();

		Console.WriteLine("Connecting to Gemini Live...");
		await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
		Console.WriteLine("Connected.\n");

		// --- Setup message (model + response TEXT + input transcription) ---
		var setupMessage = new
		{
			setup = new
			{
				model = "models/gemini-2.0-flash-exp",   // the only bidi-capable model you have
				generationConfig = new
				{
					responseModalities = new[] { "TEXT" }
				},
				inputAudioTranscription = new { },      // enable transcription of input audio
				systemInstruction = new
				{
					parts = new[]
					{
						new
						{
							text = "You are a helpful assistant. Listen to the user speaking and reply in text."
						}
					}
				}
			}
		};

		await SendJsonAsync(ws, setupMessage, cts.Token);
		Console.WriteLine("Sent setup message.");

		// Start receiving immediately so we see setupComplete & responses
		var receiveTask = ReceiveLoopAsync(ws, cts.Token);

		Console.WriteLine("Press ENTER to start recording, ENTER again to stop.\n");
		Console.ReadLine();

		await StreamMicrophoneAsync(ws, cts.Token);

		// Signal end of audio stream
		var endMessage = new
		{
			realtimeInput = new
			{
				audioStreamEnd = true
			}
		};

		await SendJsonAsync(ws, endMessage, cts.Token);
		Console.WriteLine("\nSent audioStreamEnd. Waiting a bit for final responses...");

		await Task.Delay(3000, cts.Token);

		cts.Cancel();
		if (ws.State == WebSocketState.Open)
		{
			await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
		}

		await receiveTask;
		Console.WriteLine("Connection closed. Press ENTER to exit.");
		Console.ReadLine();
	}

	// --- Microphone capture & streaming ---
	private static async Task StreamMicrophoneAsync(ClientWebSocket ws, CancellationToken ct)
	{
		// 16kHz, 16-bit, mono, as required by the API
		var waveIn = new WaveInEvent
		{
			WaveFormat = new WaveFormat(16000, 16, 1)
		};

		Console.WriteLine("Recording... (press ENTER to stop)");

		// We’ll stop recording when the user presses ENTER
		var stopTcs = new TaskCompletionSource();

		waveIn.DataAvailable += async (s, a) =>
		{
			if (ct.IsCancellationRequested || ws.State != WebSocketState.Open)
				return;

			try
			{
				// Base64-encode the PCM chunk
				string base64 = Convert.ToBase64String(a.Buffer, 0, a.BytesRecorded);

				var audioFrame = new
				{
					realtimeInput = new
					{
						audio = new
						{
							mimeType = "audio/pcm;rate=16000",
							data = base64
						}
					}
				};

				await SendJsonAsync(ws, audioFrame, ct);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Send error] {ex.Message}");
			}
		};

		waveIn.RecordingStopped += (s, a) =>
		{
			if (a.Exception != null)
				Console.WriteLine($"Recording stopped with error: {a.Exception.Message}");
			stopTcs.TrySetResult();
		};

		waveIn.StartRecording();

		// Wait for user to hit ENTER to stop recording
		await Task.Run(() => Console.ReadLine(), ct)
				  .ContinueWith(_ => { }, TaskScheduler.Default);

		waveIn.StopRecording();
		await stopTcs.Task;
		waveIn.Dispose();

		Console.WriteLine("Stopped recording.");
	}

	// --- Helper to send any JSON payload as a single text frame ---
	private static async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken ct)
	{
		var json = JsonSerializer.Serialize(payload);
		// Uncomment if you want to debug outgoing JSON:
		//Console.WriteLine(">> OUT:");
		//Console.WriteLine(json);
		//Console.WriteLine();

		var bytes = Encoding.UTF8.GetBytes(json);
		await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken: ct);
	}

	private static readonly JsonSerializerOptions JsonOpts = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	// --- Receive loop: parse messages and print text + usage ---
	private static async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
	{
		var buffer = new byte[16 * 1024];

		try
		{
			while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
			{
				using var ms = new MemoryStream();
				WebSocketReceiveResult? result;

				// Accumulate fragments until EndOfMessage
				do
				{
					result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);

					if (result.MessageType == WebSocketMessageType.Close)
					{
						Console.WriteLine($"[Server closed] {result.CloseStatus} {result.CloseStatusDescription}");
						return;
					}

					ms.Write(buffer, 0, result.Count);
				}
				while (!result.EndOfMessage);

				// Interpret the whole message payload as UTF-8 JSON
				var data = ms.ToArray();
				var json = Encoding.UTF8.GetString(data);

				try
				{
					var msg = JsonSerializer.Deserialize<GeminiMessage>(json, JsonOpts);
					var wroteAnything = false;

					// Stream partial text chunks as they arrive
					var parts = msg?.ServerContent?.ModelTurn?.Parts;
					if (parts is { Length: > 0 })
					{
						foreach (var p in parts)
						{
							if (!string.IsNullOrEmpty(p.Text))
							{
								Console.Write(p.Text);
								wroteAnything = true;
							}
						}
					}

					// When Gemini signals the turn is complete, add a newline and show usage
					if (msg?.ServerContent?.TurnComplete == true)
					{
						Console.WriteLine();
						if (msg.UsageMetadata is not null)
						{
							var u = msg.UsageMetadata;
							Console.WriteLine($"Tokens: prompt={u.PromptTokenCount}, response={u.ResponseTokenCount}, total={u.TotalTokenCount}");
						}
						wroteAnything = true;
					}

					// Fallback: print raw JSON for unrecognized shapes
					if (!wroteAnything)
					{
						Console.WriteLine();
						Console.WriteLine("Unrecognized message payload (raw):");
						Console.WriteLine(json);
						Console.WriteLine();
					}
				}
				catch (JsonException)
				{
					// Not JSON or unexpected format; show raw payload for troubleshooting
					Console.WriteLine();
					Console.WriteLine("Non-JSON or unparseable payload:");
					Console.WriteLine(json);
					Console.WriteLine();
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

// --- DTOs for Gemini Live responses ---
internal sealed class GeminiMessage
{
	public ServerContent? ServerContent { get; set; }
	public UsageMetadata? UsageMetadata { get; set; }
}

internal sealed class ServerContent
{
	public ModelTurn? ModelTurn { get; set; }
	public bool? TurnComplete { get; set; }
}

internal sealed class ModelTurn
{
	public Part[]? Parts { get; set; }
}

internal sealed class Part
{
	public string? Text { get; set; }
}

internal sealed class UsageMetadata
{
	public int PromptTokenCount { get; set; }
	public int ResponseTokenCount { get; set; }
	public int TotalTokenCount { get; set; }
	public TokenDetail[]? PromptTokensDetails { get; set; }
	public TokenDetail[]? ResponseTokensDetails { get; set; }
}

internal sealed class TokenDetail
{
	public string? Modality { get; set; }
	public int TokenCount { get; set; }
}
