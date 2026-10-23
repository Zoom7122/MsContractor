using System.Net.Http.Headers;
using System.Text;

namespace DocumentRelationsGenerator.MoySklad;

/// <summary>
/// Holds the secret only long enough to build the Authorization header. It is not serializable and
/// its string form never contains the secret, so accidental logging prints a placeholder.
/// </summary>
public sealed class MoySkladCredentials
{
    private readonly string scheme;
    private readonly string parameter;

    private MoySkladCredentials(string scheme, string parameter, string kind)
    {
        this.scheme = scheme;
        this.parameter = parameter;
        Kind = kind;
    }

    /// <summary>"token" or "login/password"; safe to print.</summary>
    public string Kind { get; }

    public static MoySkladCredentials FromToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new GeneratorException("MS_TOKEN is empty.");
        return new("Bearer", token.Trim(), "token");
    }

    public static MoySkladCredentials FromLogin(string login, string password)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
            throw new GeneratorException("Set MS_LOGIN and MS_PASSWORD (or MS_TOKEN).");
        if (login.Contains(':')) throw new GeneratorException("MS_LOGIN must not contain a colon.");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{password}"));
        return new("Basic", encoded, "login/password");
    }

    public AuthenticationHeaderValue CreateHeader() => new(scheme, parameter);

    public override string ToString() => $"MoySkladCredentials({Kind}, value hidden)";
}
