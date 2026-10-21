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
    public const string ServiceSupervisor = "service_supervisor";
    public const string ServiceManager = "service_manager";
    public const string ShippingSupervisor = "shipping_supervisor";
    public const string ProductionSupervisor = "production_supervisor";
    public const string ItemMasterOwner = "item_master_owner";
    public const string Quality = "quality";
    public const string InventoryController = "inventory_controller";
    public const string OrderDeskSupervisor = "order_desk_supervisor";
    public const string LabelAdministrator = "label_administrator";
    public const string CertificateAdministrator = "certificate_administrator";
    public const string LegalReviewer = "legal_reviewer";
    public const string FinanceReview = "finance_review";
    public const string Technician = "technician";
    public const string CustomerService = "customer_service";
    public const string WarehouseLead = "warehouse_lead";
    public const string IntegrationOperator = "integration_operator";

    /// <summary>Tenant administration (user and connector setup); a platform role, not an ADR approval role.</summary>
    public const string TenantAdministrator = "tenant_administrator";

    public static readonly IReadOnlyList<string> All =
    [
        SerialAdministrator, ServiceDesk, ServiceSupervisor, ServiceManager, ShippingSupervisor, ProductionSupervisor,
        ItemMasterOwner, Quality, InventoryController, OrderDeskSupervisor, LabelAdministrator, CertificateAdministrator, LegalReviewer, FinanceReview,
        Technician, CustomerService, WarehouseLead, IntegrationOperator, TenantAdministrator,
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
    public const string ServiceSupervisor = Prefix + YojanaRole.ServiceSupervisor;
    public const string ServiceManager = Prefix + YojanaRole.ServiceManager;
    public const string ShippingSupervisor = Prefix + YojanaRole.ShippingSupervisor;
    public const string ProductionSupervisor = Prefix + YojanaRole.ProductionSupervisor;
    public const string ItemMasterOwner = Prefix + YojanaRole.ItemMasterOwner;
    public const string Quality = Prefix + YojanaRole.Quality;
    public const string InventoryController = Prefix + YojanaRole.InventoryController;
    public const string OrderDeskSupervisor = Prefix + YojanaRole.OrderDeskSupervisor;
    public const string LabelAdministrator = Prefix + YojanaRole.LabelAdministrator;
    public const string CertificateAdministrator = Prefix + YojanaRole.CertificateAdministrator;
    public const string LegalReviewer = Prefix + YojanaRole.LegalReviewer;
    public const string FinanceReview = Prefix + YojanaRole.FinanceReview;
    public const string Technician = Prefix + YojanaRole.Technician;
    public const string CustomerService = Prefix + YojanaRole.CustomerService;
    public const string WarehouseLead = Prefix + YojanaRole.WarehouseLead;
    public const string TenantAdministrator = Prefix + YojanaRole.TenantAdministrator;
    public const string IntegrationOperator = Prefix + YojanaRole.IntegrationOperator;

    /// <summary>Tenant administrators and integration operators may work integration exceptions.</summary>
    public const string IntegrationOperations = "integration_operations";

    /// <summary>Read-only top-bar integration state for administrators, operators and service managers.</summary>
    public const string IntegrationIndicator = "integration_indicator";

    /// <summary>Operational label printing across service, warehouse, shipping and production roles.</summary>
    public const string LabelPrinter = "label_printer";

    /// <summary>Quality and certificate administrators may sign and reprint certificates.</summary>
    public const string CertificateIssuer = "certificate_issuer";

    /// <summary>Who designs customer e-mail templates and saves drafts (P9-T20); publishing is tenant administrators only.</summary>
    public const string CustomerMailDesigner = "customer_mail_designer";

    /// <summary>Who e-mails customers from a record, such as the unit's What's covered sheet (P9-T20).</summary>
    public const string CustomerMailSender = "customer_mail_sender";

    /// <summary>Any signed-in user with a selected tenant.</summary>
    public const string TenantMember = "tenant_member";

    /// <summary>A signed-in external principal bound to one tenant and one customer party.</summary>
    public const string CustomerPortal = "customer_portal";
}

/// <summary>Claim types issued by the platform's authentication.</summary>
public static class YojanaClaim
{
    public const string UserId = "yojana:user_id";
    public const string SecurityStamp = "yojana:security_stamp";
    public const string SessionId = "yojana:session_id";
    public const string TenantId = "yojana:tenant_id";
    public const string TenantCode = "yojana:tenant_code";
    public const string Role = "yojana:role";
    public const string CustomerPartyRef = "yojana:customer_party_ref";
    public const string PasswordChangeRequired = "yojana:password_change_required";
}
