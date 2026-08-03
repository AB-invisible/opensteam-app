using System.Net.Http.Json;

using System.Text.Json.Serialization;



namespace ManifestApp.Core;



public sealed class PairingClient(HttpClient http, SettingsStore settingsStore)

{

    public async Task<PairingRequestResult> RequestCodeAsync(CancellationToken cancellationToken = default)

    {

        var machineId = GetOrCreateMachineId();

        var payload = new

        {

            machineId,

            os = "Windows",

            version = typeof(PairingClient).Assembly.GetName().Version?.ToString() ?? "1.0.0",

        };



        string? lastError = null;



        foreach (var baseUrl in OpenSteamApiEndpoint.GetCandidates(settingsStore))

        {

            try

            {

                using var rsp = await http.PostAsJsonAsync($"{baseUrl}/api/v2/pairing/request", payload, cancellationToken)

                    .ConfigureAwait(false);

                var body = await rsp.Content.ReadFromJsonAsync<PairingRequestResponse>(cancellationToken: cancellationToken)

                    .ConfigureAwait(false);



                if (rsp.StatusCode == System.Net.HttpStatusCode.NotFound)

                {

                    lastError = "Pairing API not found — server may need a rebuild.";

                    continue;

                }



                if (rsp.StatusCode == System.Net.HttpStatusCode.Conflict)

                {

                    return PairingRequestResult.Fail(

                        body?.Error ?? "This device already has an API key. Run /key show in Discord.");

                }



                if (!rsp.IsSuccessStatusCode)

                {

                    lastError = body?.Error ?? $"Pairing request failed ({(int)rsp.StatusCode}).";

                    continue;

                }



                if (string.IsNullOrWhiteSpace(body?.Code))

                {

                    lastError = "Pairing service returned an empty code.";

                    continue;

                }



                OpenSteamApiEndpoint.PersistPreferredBaseUrl(baseUrl);

                return PairingRequestResult.FromCode(body.Code.Trim().ToUpperInvariant(), body.ExpiresAt);

            }

            catch (Exception ex)

            {

                lastError = ex.Message;

            }

        }



        return PairingRequestResult.Fail(lastError ?? "Could not reach OpenSteam.");

    }



    public async Task<PairingStatusResult> PollStatusAsync(string code, CancellationToken cancellationToken = default)

    {

        var machineId = GetOrCreateMachineId();

        var url =

            $"{OpenSteamApiEndpoint.ResolvePrimary(settingsStore)}/api/v2/pairing/status?code={Uri.EscapeDataString(code)}&machineId={Uri.EscapeDataString(machineId)}";



        try

        {

            using var rsp = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            var body = await rsp.Content.ReadFromJsonAsync<PairingStatusResponse>(cancellationToken: cancellationToken)

                .ConfigureAwait(false);

            var status = body?.Status?.Trim().ToLowerInvariant() ?? "invalid";



            return status switch

            {

                "ready" when !string.IsNullOrWhiteSpace(body?.ApiKey) =>

                    PairingStatusResult.Ready(body!.ApiKey!.Trim()),

                "pending" => PairingStatusResult.Pending(),

                "expired" => PairingStatusResult.Expired(),

                "revoked" => PairingStatusResult.Fail("This key was revoked."),

                _ => PairingStatusResult.Fail(body?.Error ?? "Invalid pairing code."),

            };

        }

        catch (Exception ex)

        {

            return PairingStatusResult.Fail(ex.Message);

        }

    }



    private string GetOrCreateMachineId()

    {

        var s = settingsStore.Load();

        if (!string.IsNullOrWhiteSpace(s.MachineId))

            return s.MachineId;

        s.MachineId = Guid.NewGuid().ToString("N");

        settingsStore.Save(s);

        return s.MachineId;

    }



    private sealed class PairingRequestResponse

    {

        [JsonPropertyName("code")] public string? Code { get; set; }

        [JsonPropertyName("expiresAt")] public string? ExpiresAt { get; set; }

        [JsonPropertyName("error")] public string? Error { get; set; }

    }



    private sealed class PairingStatusResponse

    {

        [JsonPropertyName("status")] public string? Status { get; set; }

        [JsonPropertyName("apiKey")] public string? ApiKey { get; set; }

        [JsonPropertyName("error")] public string? Error { get; set; }

    }

}



public readonly record struct PairingRequestResult(bool Success, string? Code, string? ExpiresAt, string? Error)

{

    public static PairingRequestResult FromCode(string code, string? expiresAt) => new(true, code, expiresAt, null);

    public static PairingRequestResult Fail(string error) => new(false, null, null, error);

}



public readonly record struct PairingStatusResult(

    string Kind,

    string? ApiKey,

    string? Error)

{

    public static PairingStatusResult Pending() => new("pending", null, null);

    public static PairingStatusResult Ready(string key) => new("ready", key, null);

    public static PairingStatusResult Expired() => new("expired", null, "Pairing code expired.");

    public static PairingStatusResult Fail(string error) => new("error", null, error);

}

