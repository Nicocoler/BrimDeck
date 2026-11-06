using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

internal static class StoLlmTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        Auth(root, check);
        Mapping(check);
        Catalog(root, check);
        await Fetch(root, check);
    }

    private static string Jwt(long expUnix)
    {
        static string B64(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        return $"eyJhbGciOiJub25lIn0.{B64($"{{\"exp\":{expUnix}}}")}.x";
    }

    private static void Auth(string root, Action<string, bool> check)
    {
        var now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
        check("JWT with exp more than 60s ahead is valid",
            StoLlmAuth.IsJwtValid(Jwt(now.ToUnixTimeSeconds() + 120), now));
        check("JWT within 60s of expiry is invalid",
            !StoLlmAuth.IsJwtValid(Jwt(now.ToUnixTimeSeconds() + 30), now));
        check("Malformed JWT is invalid",
            !StoLlmAuth.IsJwtValid("not.a.jwt", now) && !StoLlmAuth.IsJwtValid("", now));

        var home = Path.Combine(root, "sto-watt-home");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, ".env"), "# c\nDEVOPS_STOTOKEN=" + Jwt(now.ToUnixTimeSeconds() + 3600) + "\nOTHER=1\n");
        check("Reads DEVOPS_STOTOKEN from sto-watt .env",
            StoLlmAuth.ReadEnvToken(home, now) is { Length: > 10 });
        File.WriteAllText(Path.Combine(home, ".env"), "DEVOPS_STOTOKEN=" + Jwt(now.ToUnixTimeSeconds() - 10) + "\n");
        check("Expired env token is ignored", StoLlmAuth.ReadEnvToken(home, now) is null);

        check("Manual secret wins over env and bridge",
            StoLlmAuth.ResolveToken("  pasted-token  ", home, _ => Task.FromResult<string?>(null), now).Result == "pasted-token");

        var valid = Jwt(now.ToUnixTimeSeconds() + 3600);
        File.WriteAllText(Path.Combine(home, ".env"), "DEVOPS_STOTOKEN=" + valid + "\n");
        check("Env is used when manual secret is empty",
            StoLlmAuth.ResolveToken("", home, _ => Task.FromResult<string?>("bridge"), now).Result == valid);

        check("Bridge is used when env missing",
            StoLlmAuth.ResolveToken("", Path.Combine(root, "missing-home"), _ => Task.FromResult<string?>(" from-bridge "), now).Result == "from-bridge");
        check("No sources yields null",
            StoLlmAuth.ResolveToken("", Path.Combine(root, "missing-home"), _ => Task.FromResult<string?>(null), now).Result is null);

        check("Auto available when .sto-watt folder exists",
            StoLlmAuth.IsAutoAvailable(home, () => false));
        check("Auto available when probe says proxy is up",
            StoLlmAuth.IsAutoAvailable(Path.Combine(root, "no-watt"), () => true));
        check("Auto unavailable when neither present",
            !StoLlmAuth.IsAutoAvailable(Path.Combine(root, "no-watt"), () => false));
    }

    private static void Mapping(Action<string, bool> check)
    {
        // Filled in Task 2.
    }

    private static void Catalog(string root, Action<string, bool> check)
    {
        // Filled in Task 3.
    }

    private static async Task Fetch(string root, Action<string, bool> check)
    {
        // Filled in Task 4.
        await Task.CompletedTask;
    }
}
