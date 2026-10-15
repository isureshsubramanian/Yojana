namespace Yojana.Platform.Contracts.Security;

/// <summary>
/// The roles named in ADR-0001 "Controlled corrections and bulk operations" (approval column) and the
/// workbench table. Role names are claims; authorisation policies carry the same names
/// (<see cref="YojanaPolicy"/>). Add a role here and in the policy registration together.
/// </summary>
public static class YojanaRole
{
    public const string SerialAdministrator = "serial_administrator";
    public const string ServiceDesk = "service_desk";
    public const string ServiceManager = "service_manager";
    public const string ShippingSupervisor = "shipping_supervisor";
    public const string ProductionSupervisor = "production_supervisor";
    public const string Quality = "quality";
    public const string InventoryController = "inventory_controller";
    public const string OrderDeskSupervisor = "order_desk_supervisor";
    public const string LabelAdministrator = "label_administrator";
    public const string FinanceReview = "finance_review";
    public const string Technician = "technician";

    /// <summary>Tenant administration (user and connector setup); a platform role, not an ADR approval role.</summary>
    public const string TenantAdministrator = "tenant_administrator";

    public static readonly IReadOnlyList<string> All =
    [
        SerialAdministrator, ServiceDesk, ServiceManager, ShippingSupervisor, ProductionSupervisor, Quality,
        InventoryController, OrderDeskSupervisor, LabelAdministrator, FinanceReview, Technician, TenantAdministrator,
    ];

    public static bool IsKnown(string role) => All.Contains(role, StringComparer.Ordinal);
}

/// <summary>Authorisation policy names. One policy per role; a policy requires a tenant and that role.</summary>
public static class YojanaPolicy
{
    public const string Prefix = "role:";

    public static string ForRole(string role) => Prefix + role;

    public const string SerialAdministrator = Prefix + YojanaRole.SerialAdministrator;
    public const string ServiceDesk = Prefix + YojanaRole.ServiceDesk;
    public const string ServiceManager = Prefix + YojanaRole.ServiceManager;
    public const string ShippingSupervisor = Prefix + YojanaRole.ShippingSupervisor;
    public const string ProductionSupervisor = Prefix + YojanaRole.ProductionSupervisor;
    public const string Quality = Prefix + YojanaRole.Quality;
    public const string InventoryController = Prefix + YojanaRole.InventoryController;
    public const string OrderDeskSupervisor = Prefix + YojanaRole.OrderDeskSupervisor;
    public const string LabelAdministrator = Prefix + YojanaRole.LabelAdministrator;
    public const string FinanceReview = Prefix + YojanaRole.FinanceReview;
    public const string Technician = Prefix + YojanaRole.Technician;
    public const string TenantAdministrator = Prefix + YojanaRole.TenantAdministrator;

    /// <summary>Any signed-in user with a selected tenant.</summary>
    public const string TenantMember = "tenant_member";
}

/// <summary>Claim types issued by the platform's authentication.</summary>
public static class YojanaClaim
{
    public const string UserId = "yojana:user_id";
    public const string TenantId = "yojana:tenant_id";
    public const string TenantCode = "yojana:tenant_code";
    public const string Role = "yojana:role";
}
