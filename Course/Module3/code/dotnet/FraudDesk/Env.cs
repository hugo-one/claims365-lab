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
        var value = Values().TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v : fallback;
        // A value still wrapped in <angle brackets> is the sample's placeholder: treat it as
        // UNSET, the convention Foundry() has always used for FOUNDRY_KEY. Half-editing the
        // file is the common mistake, and this is what turns it into a named error.
        return value.Contains('<') && value.Contains('>') ? "" : value;
    }

    /// <summary>
    /// Which tenant you sign in to, and which environment you read.
    ///
    /// THROWS on purpose when either is still the sample's placeholder. There is no sensible
    /// default for "which directory am I": guessing one signs you in to a tenant you are not a
    /// member of, and Entra's answer (AADSTS50020) names the tenant rather than the setting, so
    /// the mistake reads as a broken lab rather than an unedited config file.
    ///
    /// The trailing slash is trimmed - a pasted URL often carries one, and
    /// <c>{org}//api/data/v9.2</c> 404s without mentioning it.
    /// </summary>
    public static (string tenant, string org) DataverseTarget()
    {
        var tenant = Setting("DATAVERSE_TENANT", "");
        var org = Setting("DATAVERSE_ORG", "");
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(tenant)) missing.Add("DATAVERSE_TENANT");
        if (string.IsNullOrWhiteSpace(org)) missing.Add("DATAVERSE_ORG");
        if (missing.Count > 0)
            throw new Exception(
                "lab/.env still has the sample's <placeholder> for "
                + string.Join(", ", missing) + ".\n"
                + "  Put your own tenant id and Dataverse org URL there, angle brackets removed.\n"
                + "  The tenant id is on the Entra ID overview page; the org URL is your\n"
                + "  environment's, ending .crm<n>.dynamics.com.\n"
                + "  The setup guide in your course materials has both: section 1, and section 6 step 3.");
        return (tenant, org.TrimEnd('/'));
    }

    /// <summary>
    /// The Foundry endpoint and deployment, plus FOUNDRY_KEY when a real one is set.
    ///
    /// The key is OPTIONAL and normally empty: with no key, the desk redeems your Module 3
    /// sign-in for a model-call token instead (<see cref="Dataverse.FoundryTokenAsync"/>), so
    /// there is nothing to be handed out and nothing shared. A real value - your own resource's
    /// key, or a token you minted yourself - overrides that, which is how the lab runs against
    /// a different tenant. The sample's &lt;placeholder&gt; counts as unset, so an untouched
    /// lab/.env is keyless.
    /// </summary>
    public static (string endpoint, string key, string model) Foundry()
    {
        var vals = Values();
        if (!_found)
            throw new Exception("lab/.env not found. It holds the Foundry endpoint, and "
                              + "Modules 2 and 3 both read it. Copy lab/.env.sample to lab/.env.");
        if (vals.Count == 0)
            throw new Exception("lab/.env was found but has no settings in it. Copy "
                              + "lab/.env.sample over it, then replace the <placeholders> with "
                              + "your own values.");

        // The OpenAI-compatible v1 route. Dated api-version values are rejected by this deployment,
        // which is why the base URL is used directly rather than an Azure-style endpoint.
        var endpoint = vals.GetValueOrDefault("FOUNDRY_OPENAI_V1")
                       ?? throw new Exception("FOUNDRY_OPENAI_V1 missing from lab/.env");
        var model = vals.GetValueOrDefault("MODEL_DEPLOYMENT")
                    ?? throw new Exception("MODEL_DEPLOYMENT missing from lab/.env");
        var key = vals.GetValueOrDefault("FOUNDRY_KEY") ?? "";
        if (key.Contains('<')) key = "";        // the sample's placeholder counts as unset
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
