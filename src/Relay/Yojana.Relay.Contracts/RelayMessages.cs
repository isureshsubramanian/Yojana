namespace Yojana.Relay.Contracts;

public sealed record RelayPoll(int Capacity, string AgentVersion, string HostName, string? ErpVersion = null);
public sealed record RelayPollReply(IReadOnlyList<RelayWork> Items);
public sealed record RelayWork(Guid RequestId, Guid LeaseId, string Method, string RelativePath, string Query,
    IReadOnlyDictionary<string, string[]> Headers, byte[] Body, DateTime ExpiresAtUtc);
public sealed record RelayResult(Guid RequestId, Guid LeaseId, int Status,
    IReadOnlyDictionary<string, string[]> Headers, byte[] Body, bool Denied = false);
public sealed record RelayWebhook(string Method, string Query, IReadOnlyDictionary<string, string[]> Headers, byte[] Body);
public sealed record RelayWebhookReply(int Status, IReadOnlyDictionary<string, string[]> Headers, byte[] Body);
public sealed record RelayPathRule(string Method, string Pattern);
public sealed record RelaySessionDeclaration(string LoginPath, string KeepAlivePath,
    string CompanyField, string UserField, string PasswordField, string TimeoutField,
    IReadOnlyList<string> CookieNames, string? VersionField = null);
public sealed record RelayDeclaration(IReadOnlyList<RelayPathRule> Paths, RelaySessionDeclaration Session,
    IReadOnlyList<string> RequestHeaders, IReadOnlyList<string> ResponseHeaders,
    IReadOnlyList<string> WebhookHeaders);
public sealed record RelayAgentConfiguration(Guid TenantId, Guid RelayId, Uri ProductUrl, Uri ServiceUrl,
    RelayDeclaration Declaration, string Credentials = "site", int Concurrency = 4,
    string? WebhookBind = null, int WebhookTimeoutSeconds = 10);
public sealed record RelaySecrets(string RelaySecret, string Company, string UserName, string Password);

public sealed record RelayLimits
{
    public const int BodyBytes = 8 * 1024 * 1024;
    public const int WireBytes = 12 * 1024 * 1024;
}
