// Dataverse access: sign in as yourself, read the book, write the referral.
//
// DEMONSTRATION CODE - part of the Claims 365 training lab, written to be read rather than
// deployed. See Desk.cs for what a production system would add.
//
// Same two points as the Python track, and they are the module, not plumbing:
//   1. You sign in as YOU. No shared secret, no service account, so the agent's tools run with your
//      permissions - which is why you can read the claims book and cannot edit it.
//   2. Everyone shares one book. It is read-only to you, and every write is a NEW row stamped with
//      your UPN, so nobody overwrites anybody.
//
// Sections, in the order the desk uses them: config and sign-in, HTTP, the book, the two writes.
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FraudDesk;

public static class Dataverse
{
    // Tenant and org come from `lab/.env` (see Env.cs) because they are environment-specific.
    // ClientId does not: it is the Azure CLI public client, a constant every tenant shares.
    static readonly (string Tenant, string Org) Target = Env.DataverseTarget();
    static string Tenant => Target.Tenant;
    public static string Org => Target.Org;
    const string ClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";   // Azure CLI public client
    static string Api => $"{Org}/api/data/v9.2";

    // One HttpClient for the process: a new one per request exhausts sockets under the burst of
    // short-lived calls a hop makes.
    static readonly HttpClient Http = new();

    // Beside the binary rather than in a home directory, so deleting the repo deletes the session.
    static readonly string CachePath =
        Path.Combine(AppContext.BaseDirectory, ".m3_token.json");

    // Access tokens, expiries and UPN for the life of ONE process. The refresh token on disk is
    // what survives - and it is ONE sign-in: the Dataverse token and the model-call token below
    // are both redeemed from it, one per audience.
    static string? _access;
    static DateTimeOffset _expires = DateTimeOffset.MinValue;
    static string _upn = "unknown";

    /// <summary>The only thing worth keeping on disk. An access token would be stale by the next command.</summary>
    record Cache(string refresh_token);

    /// <summary>
    /// Store the refresh token, readable only by you where the OS supports it. It is worth ~90 days
    /// of access as you. Unix modes mean nothing on Windows, which relies on profile permissions.
    /// </summary>
    static void SaveRefreshToken(string refreshToken)
    {
        File.WriteAllText(CachePath, JsonSerializer.Serialize(new Cache(refreshToken)));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(CachePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// POST a form-encoded body to Entra and return the parsed JSON. The status is deliberately not
    /// surfaced: while polling, `400 authorization_pending` is the normal case.
    /// </summary>
    static async Task<JsonElement> PostFormAsync(string url, Dictionary<string, string> form)
    {
        using var resp = await Http.PostAsync(url, new FormUrlEncodedContent(form));
        var body = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>
    /// Interactive sign-in, once per machine or Codespace. Device code is the flow that works
    /// everywhere this lab runs: a Codespace has no browser to redirect to and cannot listen on
    /// localhost. `offline_access` earns the refresh token later commands reuse.
    /// </summary>
    public static async Task DeviceLoginAsync()
    {
        var dc = await PostFormAsync(
            $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/devicecode",
            new() { ["client_id"] = ClientId, ["scope"] = $"{Org}/.default offline_access" });
        if (!dc.TryGetProperty("user_code", out var code))
            throw new Exception("could not start sign-in: " + dc);

        Console.WriteLine(new string('=', 72));
        Console.WriteLine("  SIGN IN AS YOURSELF");
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"  1. open {dc.GetProperty("verification_uri").GetString()}");
        Console.WriteLine($"  2. enter code  {code.GetString()}");
        Console.WriteLine("  3. sign in with the Claims 365 account from your joining pack");
        Console.WriteLine(new string('=', 72));

        // Poll until you finish in the browser. `interval` is the server's instruction: poll
        // faster and it answers `slow_down` instead of a token.
        var deadline = DateTime.UtcNow.AddSeconds(dc.GetProperty("expires_in").GetInt32());
        var interval = dc.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval));
            var tok = await PostFormAsync(
                $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token",
                new()
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = ClientId,
                    ["device_code"] = dc.GetProperty("device_code").GetString()!,
                });
            if (tok.TryGetProperty("access_token", out _))
            {
                SaveRefreshToken(tok.GetProperty("refresh_token").GetString()!);
                Console.WriteLine("signed in. session cached next to the binary, and gitignored.");
                return;
            }
            if (tok.GetProperty("error").GetString() != "authorization_pending")
                throw new Exception("sign-in failed: " + tok);
        }
        throw new Exception("sign-in timed out");
    }

    /// <summary>
    /// Exchange the cached refresh token for an access token scoped to one resource. Shared by
    /// <see cref="TokenAsync"/> (Dataverse) and <see cref="FoundryTokenAsync"/> (model calls):
    /// ONE sign-in, redeemed once per audience. Entra rotates the refresh token on use, so the
    /// rotated one is saved each time.
    /// </summary>
    static async Task<JsonElement> RedeemAsync(string resource)
    {
        if (!File.Exists(CachePath))
            throw new Exception("not signed in. run:  dotnet run -- login");
        var rt = JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath))!.refresh_token;
        var tok = await PostFormAsync(
            $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token",
            new()
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = rt,
                ["scope"] = $"{resource}/.default offline_access",
            });
        if (!tok.TryGetProperty("access_token", out _))
        {
            // A dead refresh token is unrecoverable, so delete it rather than fail the same way
            // on every future command.
            File.Delete(CachePath);
            throw new Exception("session expired. run:  dotnet run -- login");
        }
        if (tok.TryGetProperty("refresh_token", out var nrt))
            SaveRefreshToken(nrt.GetString()!);
        return tok;
    }

    /// <summary>
    /// Decode a JWT payload WITHOUT verifying the signature - acceptable here and not in a
    /// server: the token just came over TLS from Entra and is read for a name and an expiry,
    /// not to make an access decision. The receiving service verifies it on every call.
    /// A JWT payload is base64URL, not base64: two characters differ and the padding is gone.
    /// </summary>
    static JsonElement JwtClaims(string jwt)
    {
        var parts = jwt.Split('.')[1];
        var pad = parts.PadRight(parts.Length + (4 - parts.Length % 4) % 4, '=')
                       .Replace('-', '+').Replace('_', '/');
        return JsonDocument.Parse(Convert.FromBase64String(pad)).RootElement.Clone();
    }

    /// <summary>
    /// A valid Dataverse token, refreshed from the cached refresh token when this process holds
    /// none. Also decodes it to learn who you are: the UPN stamps every referral row, which is
    /// what keeps everyone's results apart - read from the token, so it cannot be typed wrong
    /// or borrowed.
    /// </summary>
    static async Task<string> TokenAsync()
    {
        if (_access is not null && _expires > DateTimeOffset.UtcNow.AddMinutes(2)) return _access;
        var tok = await RedeemAsync(Org);
        _access = tok.GetProperty("access_token").GetString();
        var claims = JwtClaims(_access!);
        _expires = DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty("exp").GetInt64());
        _upn = claims.TryGetProperty("upn", out var u) ? u.GetString()!
             : claims.TryGetProperty("unique_name", out var un) ? un.GetString()! : "unknown";
        return _access!;
    }

    // The model endpoint's resource. A constant like ClientId, not config: it names Azure's
    // Cognitive Services API surface and is the same value in every tenant.
    const string FoundryResource = "https://cognitiveservices.azure.com";

    static string? _foundryAccess;
    static DateTimeOffset _foundryExpires = DateTimeOffset.MinValue;

    /// <summary>
    /// A model-call token from the SAME sign-in, so nobody hands you a key. The refresh token
    /// `login` cached is redeemed once for Dataverse and once for the model endpoint: model
    /// calls are authorised - and attributable - as you, exactly like the data reads. A real
    /// FOUNDRY_KEY in lab/.env overrides this (see <see cref="Env.Foundry"/>), which is also
    /// the route for running the lab against your own tenant.
    /// </summary>
    public static async Task<string> FoundryTokenAsync()
    {
        if (_foundryAccess is not null && _foundryExpires > DateTimeOffset.UtcNow.AddMinutes(2))
            return _foundryAccess;
        var tok = await RedeemAsync(FoundryResource);
        _foundryAccess = tok.GetProperty("access_token").GetString();
        _foundryExpires = DateTimeOffset.FromUnixTimeSeconds(
            JwtClaims(_foundryAccess!).GetProperty("exp").GetInt64());
        return _foundryAccess!;
    }

    /// <summary>Your UPN, as Dataverse sees you. Signs in first if this process has not yet.</summary>
    public static async Task<string> WhoAmIAsync() { await TokenAsync(); return _upn; }

    // A string value made safe for an OData filter: quotes doubled, then percent-encoded. Two
    // parsers see this value, and each has a character that breaks it. OData escapes a single
    // quote by doubling it - a claimant called O'Neill would otherwise produce a syntax error
    // that reads like a permissions problem. The URL splits on & - a repairer called
    // Eccles Panel & Paint would otherwise truncate the query at the ampersand. Encoding here,
    // once, covers both.
    static string Esc(string v) => Uri.EscapeDataString(v.Replace("'", "''"));

    /// <summary>Build one Web API request with the bearer token and the OData headers.</summary>
    static async Task<HttpRequestMessage> RequestAsync(HttpMethod m, string path, string? body)
    {
        var req = new HttpRequestMessage(m, $"{Api}/{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        req.Headers.Add("OData-MaxVersion", "4.0");
        req.Headers.Add("OData-Version", "4.0");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return req;
    }

    // Retry transient failures: a hop fires a burst of short-lived connections and Dataverse
    // throttles a busy environment. A 400 or 403 comes straight back, since repeating a rejected
    // request only delays the explanation.
    static bool Transient(System.Net.HttpStatusCode s) =>
        (int)s is 429 or 502 or 503 or 504;

    /// <summary>
    /// GET an OData path and return the rows, with the retry rule applied. Throws on anything that
    /// is not a success: a tool that swallowed a 403 would report "nobody is connected to this
    /// claimant", and a wrong answer that looks right is the worst outcome here.
    /// </summary>
    public static async Task<List<JsonElement>> QueryAsync(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            // A new request per attempt: an HttpRequestMessage cannot be sent twice.
            using var req = await RequestAsync(HttpMethod.Get, path, null);
            try
            {
                using var resp = await Http.SendAsync(req);
                var raw = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode)
                    return JsonDocument.Parse(raw).RootElement.GetProperty("value")
                        .EnumerateArray().Select(e => e.Clone()).ToList();
                if (Transient(resp.StatusCode) && attempt < 4)
                {
                    await Task.Delay(2000 * attempt);
                    continue;
                }
                throw new Exception($"Dataverse read failed {(int)resp.StatusCode}: " +
                                    raw[..Math.Min(300, raw.Length)]);
            }
            catch (HttpRequestException) when (attempt < 4)
            {
                await Task.Delay(2000 * attempt);
            }
        }
    }

    /// <summary>Create a row. No retry: a POST is not idempotent, and a duplicated referral is worse
    /// than a failed one.</summary>
    static async Task PostAsync(string set, object body)
    {
        using var req = await RequestAsync(HttpMethod.Post, set, JsonSerializer.Serialize(body));
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await resp.Content.ReadAsStringAsync();
            throw new Exception($"write to {set} failed {(int)resp.StatusCode}: " +
                                raw[..Math.Min(300, raw.Length)]);
        }
    }

    // Named once so every query returns the same shape and adding a column is one edit.
    const string ClaimCols = "cp_bookclaimref,cp_claimantref,cp_repairername,cp_claimtype," +
                             "cp_amountclaimed,cp_claimstatus,cp_incidentdate,cp_incidentpostcode";
    const string ClaimantCols = "cp_claimantref,cp_fullname,cp_email,cp_phone," +
                                "cp_bankaccount,cp_postcode";

    /// <summary>A string column, or "". Dataverse omits empty columns rather than returning
    /// null, so a plain GetString would throw on a sparse row.</summary>
    public static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()! : "";

    /// <summary>A numeric column, or 0 when absent. Same reason as <see cref="Str"/>.</summary>
    public static double Num(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : 0;

    /// <summary>One claim by reference, or null. Intake uses this to refuse a claim that is not real.</summary>
    public static async Task<JsonElement?> GetClaimAsync(string claimRef)
    {
        var rows = await QueryAsync(
            $"cp_bookclaims?$select={ClaimCols}&$filter=cp_bookclaimref eq '{Esc(claimRef)}'");
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>One claimant by reference, or null. This is where the identifiers a ring shares live.</summary>
    public static async Task<JsonElement?> GetClaimantAsync(string claimantRef)
    {
        var rows = await QueryAsync(
            $"cp_claimants?$select={ClaimantCols}&$filter=cp_claimantref eq '{Esc(claimantRef)}'");
        return rows.Count > 0 ? rows[0] : null;
    }

    // Far above anything this book can produce. It exists because these values are chosen by a
    // MODEL, and asking about hundreds at once builds a URL past what Dataverse accepts, failing
    // with an error that never mentions length.
    const int MaxFilterTerms = 150;

    // A hop is a set operation. Doing it one person at a time costs a model round trip each, and
    // round trips are what an agent's bill is made of. These do a whole hop in three queries.

    /// <summary>
    /// `col eq 'a' or col eq 'b' or ...`, or "" when there is nothing to ask. Sorted and
    /// de-duplicated so the same hop always produces the same URL. Callers must treat "" as "ask
    /// nothing": a bare `$filter=` is a 400.
    /// </summary>
    static string OrFilter(string col, IEnumerable<string> values)
    {
        var uniq = values.Where(v => !string.IsNullOrEmpty(v)).Distinct().OrderBy(v => v).ToList();
        if (uniq.Count > MaxFilterTerms)
            throw new ArgumentException(
                $"asked about {uniq.Count} values in one query, more than the limit of "
                + $"{MaxFilterTerms}. A filter that long exceeds the URL length Dataverse accepts, "
                + "and fails with an error that does not mention length.");
        return string.Join(" or ", uniq.Select(v => $"{col} eq '{Esc(v)}'"));
    }

    /// <summary>Many claimants in one query. The first half of a hop: who are the people we have?</summary>
    public static async Task<List<JsonElement>> ClaimantsByRefsAsync(IEnumerable<string> refs)
    {
        var f = OrFilter("cp_claimantref", refs);
        return string.IsNullOrEmpty(f) ? new()
            : await QueryAsync($"cp_claimants?$select={ClaimantCols}&$filter={f}");
    }

    /// <summary>
    /// Everyone holding ANY of these values. The second half of a hop. The attribute names are the
    /// agent-facing vocabulary, so a tool description never teaches the model a Dataverse schema.
    /// </summary>
    public static async Task<List<JsonElement>> ClaimantsSharingAnyAsync(
        string attribute, IEnumerable<string> values)
    {
        var col = attribute switch
        {
            "bank_account" => "cp_bankaccount",
            "phone" => "cp_phone",
            "postcode" => "cp_postcode",
            _ => throw new ArgumentException("attribute must be bank_account, phone or postcode"),
        };
        var f = OrFilter(col, values);
        return string.IsNullOrEmpty(f) ? new()
            : await QueryAsync($"cp_claimants?$select={ClaimantCols}&$filter={f}");
    }

    /// <summary>Every claim belonging to any of these claimants. The exposure figure is summed from
    /// these rows, in C#, never by a model.</summary>
    public static async Task<List<JsonElement>> ClaimsOfManyAsync(IEnumerable<string> refs)
    {
        var f = OrFilter("cp_claimantref", refs);
        return string.IsNullOrEmpty(f) ? new()
            : await QueryAsync($"cp_bookclaims?$select={ClaimCols}&$filter={f}");
    }

    /// <summary>Every claim at one repairer. Joined by NAME, so no relationship metadata is
    /// needed.</summary>
    public static async Task<List<JsonElement>> ClaimsByRepairerAsync(string repairer) =>
        await QueryAsync(
            $"cp_bookclaims?$select={ClaimCols}&$filter=cp_repairername eq '{Esc(repairer)}'");

    /// <summary>A UTC timestamp identifying one investigation, scoped to one person by the
    /// referral reference.</summary>
    public static string NewRunId() => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

    /// <summary>
    /// Write the referral header: one row per person per run. `cp_investigator` comes from
    /// <see cref="WhoAmIAsync"/> rather than an argument, so the row records who actually ran it.
    /// </summary>
    public static async Task WriteReferralAsync(
        string referralRef, string seedClaim, string decision, int ringSize, int claimCount,
        double exposure, int hops, string approvedBy, string evidence, string language,
        string runId)
    {
        await PostAsync("cp_fraudreferrals", new Dictionary<string, object?>
        {
            ["cp_referralref"] = referralRef,
            ["cp_seedclaimref"] = seedClaim,
            ["cp_decision"] = decision,
            ["cp_ringsize"] = ringSize,
            ["cp_claimcount"] = claimCount,
            ["cp_exposure"] = exposure,
            ["cp_hops"] = hops,
            ["cp_investigator"] = await WhoAmIAsync(),
            ["cp_approvedby"] = approvedBy,
            ["cp_language"] = language,
            ["cp_runid"] = runId,
            ["cp_evidence"] = evidence.Length > 3999 ? evidence[..3999] : evidence,
        });
    }

    /// <summary>
    /// One evidence line: this claim is in this referral, and why. These make the referral
    /// auditable rather than a summary row with a number on it.
    /// </summary>
    public static async Task WriteReferralClaimAsync(
        string referralRef, string claimRef, string claimantRef, string reason, string strength)
    {
        var id = $"{referralRef}|{claimRef}";
        await PostAsync("cp_referralclaims", new Dictionary<string, object?>
        {
            ["cp_referralclaimref"] = id.Length > 139 ? id[..139] : id,
            ["cp_referralref"] = referralRef,
            ["cp_bookclaimref"] = claimRef,
            ["cp_claimantref"] = claimantRef,
            ["cp_linkreason"] = reason.Length > 199 ? reason[..199] : reason,
            ["cp_linkstrength"] = strength,
        });
    }

    /// <summary>
    /// Every referral YOU wrote, newest first. The payoff of stamping the UPN: "mine" is a filter,
    /// not a race. Everyone can read everyone's rows and nobody can change them.
    /// </summary>
    public static async Task<List<JsonElement>> MyReferralsAsync()
    {
        var me = Esc(await WhoAmIAsync());
        return await QueryAsync(
            "cp_fraudreferrals?$select=cp_referralref,cp_decision,cp_ringsize,cp_claimcount," +
            "cp_exposure,cp_hops,cp_approvedby,cp_language,cp_runid" +
            $"&$filter=cp_investigator eq '{me}'&$orderby=createdon desc");
    }
}
