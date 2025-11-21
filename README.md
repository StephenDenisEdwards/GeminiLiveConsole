# GeminiLiveConsole

C# console prototype mirroring the TypeScript `LiveManager`.

## Features

- Captures microphone audio (16 kHz, mono, 16-bit PCM) with NAudio.
- Streams audio frames over WebSocket to Gemini Live (placeholder protocol).
- Parses transcripts and tool/function calls (`report_intent`).
- Computes RMS for optional volume visualization.

## Environment

Set your API key:

```powershell
setx API_KEY your_key_here
```

Restart the terminal after setting.

## Build & Run

```powershell
cd dotnet/GeminiLiveConsole
dotnet restore
dotnet run
```

Press Ctrl+C to stop.

## Placeholders / TODO

- WebSocket message schema may differ from official Gemini Live spec.
- Authentication: If Live requires OAuth bearer, add Authorization header before connect.
- Add reconnection/backoff and structured logging.
- Replace inline tool declaration with any future server-provided format.

## Intent Handling

When a `report_intent` function call arrives the session manager logs the text, type, and answer then sends an acknowledgement `toolResponse`.

## Safety

Do not commit real API keys. Use environment variables or a secret manager for production.

## Key Rotation

- Re-issue API keys and OAuth credentials on a regular cadence (per sprint at minimum) to keep stale secrets invalidated.
- Use `gcloud auth application-default revoke` followed by `print-access-token` before exporting a new `GEMINI_OAUTH_TOKEN`, and clear the old value from the environment.
- When service accounts are involved, revoke the old JSON key immediately after promoting the replacement.

## Next Test

1. Run `gcloud auth application-default print-access-token` to mint a fresh bearer token.
2. Export it as `GEMINI_OAUTH_TOKEN` in the current PowerShell session and ensure the WebSocket URI no longer appends `?key=...`.
3. From `dotnet/GeminiLiveConsole`, run `dotnet run` and watch for either a successful `Connected to Gemini Live` log or updated diagnostics.

---



Here’s the situation in very plain, step‑by‑step terms.

What you expected:
You run your C# app. It tries to open a special “live” WebSocket connection to Gemini so you can stream microphone audio and get real‑time transcription + intent detection.

What actually happened:
The WebSocket handshake (the first hello) comes back with HTTP 404 (Not Found) instead of 101 (Switching Protocols). That means the server refused to upgrade the connection—so no live session starts.

Why 404 really matters:
404 here almost never means “your model name is wrong”. It means “the URL + auth combo you used does not match any live endpoint the server is willing to talk to.” In other words: you knocked on the wrong door (or knocked correctly but didn’t show the right ID).

What is already proven OK:
You did a normal REST GET (the preflight) to check the model. It worked. That proves:

Your API key is valid.
The model name exists for basic REST calls.
Key difference between REST and Live:
Regular REST calls can use api key in the query (?key=...). Live streaming very often needs stronger auth (OAuth Bearer token) OR a different initialization pattern (sometimes you first create a “session” via POST, get back a session ID, then connect with that ID). Just slapping ?key= onto the WebSocket URL isn’t enough.

So the “root” problem:
We are treating the live WebSocket like a simple REST endpoint. The live preview almost certainly wants either:

An OAuth access token in an Authorization header.
Or a special session URL returned by a prior “create live session” API call.
Or a slightly different path/version than the one we guessed.
Why guesses fail:
Because Google’s TypeScript SDK hides these details. It internally sets the right headers, path, maybe negotiates a session. We tried to mimic it manually without the exact spec—so we’re missing one piece (auth method or session handshake).

Why adding more URL variants didn’t help:
If the server requires an Authorization header, changing “v1beta” to “v1” or adding/removing ?key= won’t fix it—the handshake still looks “wrong” to the server.

What you should NOT waste time on:

Re‑typing the model name again and again.
Randomly changing buffer sizes or audio formats (that affects streaming after the connection, not the handshake).
Adding weird subprotocol strings unless the docs specifically say so.
What you DO need:
One of these (depending on official docs):
A. Get an OAuth access token (service account or user flow) with the correct scope (likely generative-language) and send it as:
Authorization: Bearer `<token>`
B. Call a REST endpoint to “start” or “create” a live session—use its returned WebSocket URL instead of crafting your own.
C. Use the official SDK (Node/TypeScript) for live and bridge its output to your C# app (simplest if docs are unclear).

Plain “next move” choices:
Option 1: Obtain OAuth token → patch C# to send it (we added a slot for bearerToken).
Option 2: Find docs mentioning a “create live session” endpoint; we add that call, parse session ID/URL, then connect.
Option 3: Stop fighting the unknown protocol—use the @google/genai TypeScript live client in a tiny sidecar process; pipe transcripts/intents to C# via local HTTP/gRPC/Named Pipes.
Option 4: Wait for official .NET guidance / spec instead of reverse-engineering.

Easiest immediate path (least unknowns):
Use the JS SDK for Live (it’s already working in your TypeScript app). Treat C# as the consumer of events—no auth headache because the SDK does it.

Summary in one sentence:
Your WebSocket fails (404) because the live endpoint needs a different authentication/session handshake than a plain ?key= URL, and without the exact protocol you’re knocking on the wrong door.

Tell me which option (1–4) you want to pursue and I’ll implement or outline the exact steps.


---




Here’s the simplest mental model: an API key alone is often not enough for the Live (streaming) endpoint; you need an OAuth 2.0 access token (“Bearer” token) with the proper scope so the server knows who you are and that you’re allowed to open a live session.

Steps (pick ONE path)

Fast developer machine path (gcloud CLI)
Install Google Cloud SDK if you don’t have it.
Run: gcloud auth login (sign in)
Run: gcloud auth application-default login (sets ADC)
Then whenever you need a bearer token:
gcloud auth application-default print-access-token
That output string is your Bearer token. Set it as an env var:
PowerShell:
setx GEMINI_OAUTH_TOKEN (gcloud auth application-default print-access-token)
(Restart shell so your app can read the variable.)
Service account JWT path (headless)
In Google Cloud Console:
Enable the Generative Language API (same project as your model access).
Create a Service Account (IAM & Admin > Service Accounts).
Grant it a role that allows generative language usage (for Gemini experimental endpoints that may be “Generative Language User” or a broader AI role—if uncertain start with roles/viewer + API-specific user role).
Create a JSON key for that service account and download it.
Use that JSON to mint an access token:
Scope: https://www.googleapis.com/auth/generative-language
Token endpoint: https://oauth2.googleapis.com/token
Flow: Create a signed JWT (RS256) with:
iss: service account email
sub: service account email
aud: https://oauth2.googleapis.com/token
iat: current time
exp: iat + 3600
scope: https://www.googleapis.com/auth/generative-language
POST form: grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer assertion=<signed_jwt>
Response contains access_token you use as Bearer.
User OAuth (auth code or device code)
Create OAuth Client ID (Web or Desktop).
Use an OAuth library to request an access token with the scope https://www.googleapis.com/auth/generative-language.
After user consents you get refresh + access token. Refresh periodically.
Recommended (least friction): gcloud path while developing. Move to service account for production.

C# snippet for service account (using Google.Apis.Auth):
Add package:
dotnet add package Google.Apis.Auth

Code:
using Google.Apis.Auth.OAuth2;
var credential = GoogleCredential.FromFile("service-account.json")
.CreateScoped("https://www.googleapis.com/auth/generative-language");
var token = await credential.GetAccessTokenForRequestAsync();
Console.WriteLine(token);

Then pass token into your GeminiLiveClient constructor (bearerToken).

Using in WebSocket:
_ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");

Important distinctions:

API key: identifies the project, limited to simple model calls.
OAuth access token: ties identity + permissions, required for privileged endpoints (like live streaming).
Refresh: The access token expires (~1 hour). Re-fetch before expiration.
Troubleshooting checklist:

401 Unauthorized: bad/expired token.
403 Forbidden: token valid but lacks permission (scope or role).
404 on handshake: still using wrong endpoint or missing required auth (re-check you removed ?key= when using pure bearer).
Mixed success: try both header Authorization and x-goog-api-key (some previews layer both).
After you get the token:
PowerShell quick test:
$env:GEMINI_OAUTH_TOKEN = (gcloud auth application-default print-access-token)
dotnet run

Do you want me to patch Program.cs to automatically read GEMINI_OAUTH_TOKEN and pass it to the client? Just say “patch it” and I’ll apply.
