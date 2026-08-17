// Configuration and small parsing helpers. The C# twin of `m3_env.py`.
//
// DEMONSTRATION CODE - part of the Claims 365 training lab.
//
// Everything environment-specific comes from `lab/.env`, the same file the Python track reads.
// Nothing here is a secret in source.
using System.Text.Json;

namespace FraudDesk;

public static class Env
{
    // The course defaults. Not secrets: a tenant id names a directory and an org URL a database,
    // and neither grants anything on its own. Defaults rather than constants, so pointing the lab
    // at another environment is a config change instead of a source edit.
    public const string DefaultTenant = "30d4eca5-0b84-4868-89d5-b0fc78c105c4";
    public const string DefaultOrg = "https://org3425656b.crm4.dynamics.com";

    static Dictionary<string, string>? _cache;
    static bool _found;                     // was a file located at all, as opposed to an empty one

    /// <summary>
    /// Parse `lab/.env` into a dictionary, searching upward from the binary rather than the working
    /// directory, which depends on where <c>dotnet run</c> was invoked.
    ///
    /// Returns an EMPTY dictionary when there is no file: only the values without a default treat
    /// that as fatal, so the decision to throw belongs to the caller.
    /// </summary>
    static Dictionary<string, string> Values()
    {
        if (_cache is not null) return _cache;
        _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        FileInfo? envFile = null;
        while (dir is not null && envFile is null)
        {
            var candidate = Path.Combine(dir.FullName, "lab", ".env");
            if (File.Exists(candidate)) envFile = new FileInfo(candidate);
            dir = dir.Parent;
        }
        if (envFile is null) return _cache;
        _found = true;

        foreach (var line in File.ReadAllLines(envFile.FullName))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#') || !t.Contains('=')) continue;
            var i = t.IndexOf('=');
            _cache[t[..i].Trim()] = t[(i + 1)..].Trim().Trim('"');
        }
        return _cache;
    }

    /// <summary>
    /// One setting: the process environment first, then `lab/.env`, then the supplied default.
    /// Environment first so a shell variable can override the file for a single run.
    /// </summary>
    static string Setting(string name, string fallback)
    {
        var fromProcess = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(fromProcess)) return fromProcess;
        return Values().TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v : fallback;
    }

    /// <summary>
    /// Which tenant you sign in to, and which environment you read. Never throws: both have a
    /// working default, so `dotnet run -- login` works before you have pasted a Foundry key.
    ///
    /// The trailing slash is trimmed - a pasted URL often carries one, and
    /// <c>{org}//api/data/v9.2</c> 404s without mentioning it.
    /// </summary>
    public static (string tenant, string org) DataverseTarget() =>
        (Setting("DATAVERSE_TENANT", DefaultTenant),
         Setting("DATAVERSE_ORG", DefaultOrg).TrimEnd('/'));

    /// <summary>
    /// The Foundry endpoint, key and deployment. Throws if any is missing: there is no sensible
    /// default for a key, and guessing would surface later as an unexplained 401.
    /// </summary>
    public static (string endpoint, string key, string model) Foundry()
    {
        var vals = Values();
        if (!_found)
            throw new Exception("lab/.env not found. It holds the Foundry endpoint and key, and "
                              + "Modules 2 and 3 both read it. Copy lab/.env.sample to lab/.env.");
        if (vals.Count == 0)
            throw new Exception("lab/.env was found but has no settings in it. Copy "
                              + "lab/.env.sample over it and paste your FOUNDRY_KEY.");

        // The OpenAI-compatible v1 route. Dated api-version values are rejected by this deployment,
        // which is why the base URL is used directly rather than an Azure-style endpoint.
        var endpoint = vals.GetValueOrDefault("FOUNDRY_OPENAI_V1")
                       ?? throw new Exception("FOUNDRY_OPENAI_V1 missing from lab/.env");
        var key = vals.GetValueOrDefault("FOUNDRY_KEY")
                  ?? throw new Exception("FOUNDRY_KEY missing from lab/.env");
        var model = vals.GetValueOrDefault("MODEL_DEPLOYMENT")
                    ?? throw new Exception("MODEL_DEPLOYMENT missing from lab/.env");
        return (endpoint, key, model);
    }
}

public static class Json
{
    /// <summary>
    /// Parse a model reply that should be JSON. Models occasionally wrap it in a fenced block even
    /// when told not to, and a crash on the fence reads like a schema problem rather than a
    /// formatting one.
    /// </summary>
    public static JsonElement Object(string text)
    {
        var t = (text ?? "").Trim();
        if (t.StartsWith("```"))
        {
            var first = t.IndexOf('\n');
            if (first >= 0) t = t[(first + 1)..];
            var fence = t.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) t = t[..fence];
            t = t.Trim();
        }
        // Trim anything either side of the outermost braces. Belt and braces alongside the fence
        // strip above: a leading "Here is the JSON:" is rarer than a fence but just as fatal.
        var open = t.IndexOf('{');
        var close = t.LastIndexOf('}');
        if (open >= 0 && close > open) t = t[open..(close + 1)];
        return JsonDocument.Parse(t).RootElement.Clone();
    }
}
