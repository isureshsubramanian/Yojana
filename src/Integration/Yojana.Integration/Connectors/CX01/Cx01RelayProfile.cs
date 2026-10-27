using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Integration.Data;
using Yojana.Integration.Relays;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Tenancy;
using Yojana.Relay.Contracts;

namespace Yojana.Integration.Connectors.CX01;

public sealed class Cx01RelayProfile(Cx01Settings settings, ITenantContext tenant, IServiceProvider services) : IRelayConnectorProfile
{
    public async Task<RelayConnectionOptions> OptionsAsync(Guid connectionId, CancellationToken ct)
    {
        var configured = await settings.GetAsync(connectionId, ct);
        return new(configured.Transport, configured.RelayCredentials, configured.RelayWebhookUrl,
            configured.RelayOfflineAfterSeconds, configured.RelayRequestTimeoutSeconds);
    }

    public async Task<RelayAgentConfiguration> ConfigurationAsync(Guid connectionId, Guid tenantId, Guid relayId,
        Uri productUrl, CancellationToken ct)
    {
        var configured = await settings.GetAsync(connectionId, ct);
        return new(tenantId, relayId, productUrl, configured.ServiceUri, Declaration(configured, await ModeAsync(connectionId, ct)),
            configured.RelayCredentials, WebhookBind: configured.RelayWebhookUrl);
    }

    public async Task ProbeAsync(Guid connectionId, string release, CancellationToken ct)
    {
        var configured = await settings.GetAsync(connectionId, ct);
        if (configured.Transport != "site_relay") return;
        await settings.SetAsync(connectionId, configured with { Release = release, WebhooksAvailable = false }, ct);
        var probed = await services.GetRequiredService<Cx01Client>().ProbeReleaseAsync(connectionId, ct);
        // Reload settings after the remote wait so a concurrent administrator edit is preserved.
        configured = await settings.GetAsync(connectionId, ct);
        if (configured.Transport == "site_relay" && configured.Release == release)
            await settings.SetAsync(connectionId, configured with { WebhooksAvailable = probed.WebhooksAvailable }, ct);
    }

    private async Task<string> ModeAsync(Guid connectionId, CancellationToken ct)
    {
        var db = services.GetRequiredService<IntegrationDbContext>();
        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleOrDefaultAsync(
            db.ConnectionGatewaySettings.Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId).Select(x => x.OutboundMode), ct) ?? "reference_only";
    }

    public static RelayDeclaration Declaration(Cx01ConnectionSettings configured, string outboundMode = "reference_only")
    {
        var rules = new List<RelayPathRule>();
        const string key = @"\((?:[0-9]+|'(?:[^'/%\\\r\n]|'')*')\)";
        // Approval-governed document types are read by the review tracker to find the document added from a draft.
        var collections = Cx01Protocol.Entities.Values.Select(entity => entity.Collection)
            .Concat(["CustomerEquipmentCards", "SerialNumberDetails"])
            .Concat(Cx01DocumentCatalogue.Collections)
            .Concat(Cx01ApprovalCatalogue.Targets.Select(target => target.Collection))
            .Distinct(StringComparer.Ordinal).ToList();
        foreach (var collection in collections)
        {
            var path = Regex.Escape(Cx01Protocol.Root + collection);
            rules.Add(new("GET", path + "(?:" + key + ")?"));
            if (collection is "CustomerEquipmentCards" or "Returns") rules.Add(new("PATCH", path + key));
        }
        if (outboundMode == "documents" && configured.DocumentsAcceptance is not null)
        {
            foreach (var collection in Cx01DocumentCatalogue.Collections)
                rules.Add(new("POST", Regex.Escape(Cx01Protocol.Root + collection)));
            foreach (var collection in Cx01AttachmentMapper.LinkCollections.Where(x => x is not ("Returns" or "CustomerEquipmentCards")))
                rules.Add(new("PATCH", Regex.Escape(Cx01Protocol.Root + collection) + key));
        }
        foreach (var path in new[] { Cx01Protocol.Root, Cx01Protocol.Metadata,
                     Cx01Protocol.Root + Cx01Protocol.UserFields, Cx01Protocol.Root + "ItemWarehouseInfoCollection",
                     Cx01Protocol.Root + Cx01Protocol.EventNotifications,
                     Cx01Protocol.Root + Cx01Protocol.Currencies }) rules.Add(new("GET", Regex.Escape(path)));
        rules.Add(new("POST", Regex.Escape(Cx01Protocol.Root + Cx01Protocol.AdminInfo)));
        // Approval handling: read-only template survey; drafts held for review are created and read, never changed.
        rules.Add(new("GET", Regex.Escape(Cx01Protocol.Root + Cx01Protocol.ApprovalTemplates)));
        var drafts = Regex.Escape(Cx01Protocol.Root + Cx01Protocol.Drafts);
        rules.Add(new("GET", drafts + "(?:" + key + ")?"));
        if (outboundMode == "documents" && configured.DocumentsAcceptance is not null) rules.Add(new("POST", drafts));
        if (configured.RelayCredentials == "cloud") rules.Add(new("POST", Regex.Escape(Cx01Protocol.Login)));
        var subscription = Regex.Escape(Cx01Protocol.Root + Cx01Protocol.EventSubscriptions);
        rules.Add(new("GET", subscription + "(?:" + key + ")?"));
        rules.Add(new("POST", subscription));
        rules.Add(new("DELETE", subscription + key));
        rules.Add(new("POST", subscription + key + "/(?:Pause|Resume)"));
        rules.Add(new("POST", Regex.Escape(Cx01Protocol.Root + Cx01Protocol.SqlQueries)));
        var query = Regex.Escape(Cx01Protocol.Root + Cx01Protocol.SqlQueries + "('" + Cx01Protocol.SerialLinkQueryCode + "')");
        rules.Add(new("GET", query));
        rules.Add(new("GET", query + "/List"));
        if (configured.SerialLinkViewEnabled && configured.SerialLinkViewName is { } view)
            rules.Add(new("GET", Regex.Escape(Cx01Protocol.Root + view)));
        return new(rules, new(Cx01Protocol.Login, Cx01Protocol.Root, "CompanyDB", "UserName", "Password",
                "SessionTimeout", [Cx01Protocol.SessionCookie, Cx01Protocol.RouteCookie], "Version"),
            ["Accept", "Content-Type", "Cookie", "If-Match", "Prefer"],
            ["Content-Type", "Set-Cookie", "Retry-After", "ETag", "Location"],
            [Cx01Protocol.HandshakeTokenHeader, Cx01Protocol.SignatureHeader, "Content-Type"]);
    }

    public async Task<RelayWebhookReply> WebhookAsync(Guid connectionId, RelayWebhook request, CancellationToken ct)
    {
        if (request.Method is not ("GET" or "POST")) return new(405, new Dictionary<string, string[]>(), []);
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = request.Method;
        // Route and tenant come from the authenticated relay, never from the local messenger's query string.
        context.Request.QueryString = new("?tenant=" + tenant.TenantId);
        context.Request.Body = new MemoryStream(request.Body, false);
        var declaration = Declaration(await settings.GetAsync(connectionId, ct));
        foreach (var pair in request.Headers.Where(pair => declaration.WebhookHeaders.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)))
            context.Request.Headers[pair.Key] = pair.Value;
        using var output = new MemoryStream();
        context.Response.Body = output;
        var result = await Cx01WebhookEndpoints.ReceiveAsync(connectionId, context.Request, context.Response,
            services.GetRequiredService<ITenantContextSetter>(), services.GetRequiredService<IntegrationDbContext>(),
            settings, services.GetRequiredService<IConnectionCredentialStore>(),
            services.GetRequiredService<ConnectionCredentialProtector>(), services.GetRequiredService<IMessageBus>(), ct);
        await result.ExecuteAsync(context);
        return new(context.Response.StatusCode, context.Response.Headers
            .Where(pair => pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Select(value => value ?? "").ToArray()), output.ToArray());
    }
}
