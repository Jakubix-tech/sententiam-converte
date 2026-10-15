using SententiamConverte.Core.Sdk;

namespace SententiamConverte.Api;

/// <summary>Wer die API aufruft und was er darf.</summary>
public sealed record ApiUser(string Name, bool CanWrite, bool IsAdmin = false);

/// <summary>Einstellungen der REST-API – vom Gastgeber (Desktop-App oder Teamserver) gesetzt.</summary>
public sealed class ApiOptions : SdkOptions
{
    /// <summary>Prüft den Zugangsschlüssel (Bearer); null = abgelehnt.</summary>
    public required Func<string, ApiUser?> Authorize { get; init; }
}
