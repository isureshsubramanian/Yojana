# Integration transport operations

The tenant setting `integration.transport` defaults to `in_process`. Missing settings do not create
carrier runtimes, look up credentials or contact a cloud service. Platform's Wolverine SQL persistence,
durable local queues, inbox transaction and outbound relay remain the default. This module does not
change Platform's bus configuration.

`message_queue` is an opt-in carrier for cross-region delivery, inbound fan-out or an externally hosted
worker. Each immutable credential reference owns an isolated Wolverine carrier runtime, created on
demand. This avoids rebuilding or mutating the product bus's cached transport topology. The shared wire messages and receipts live in `Yojana.Integration.Contracts`, so additional consumers
need no module implementation reference. The carrier has no separate database: the product's SQL-backed Wolverine dispatch envelope owns outgoing durability;
inline sends await broker acknowledgement. Inline receives acknowledge only after the receiver commits
through the normal inbox or outbound relay. Broker redelivery is expected, and never a business acceptance.

## Provision and enable

1. Configure the existing credential encryption provider. Keep its production key in the secrets layer.
2. As a tenant administrator, POST a namespace connection string and rotation deadline to
   `/api/integration/settings/transport/credentials`. The response contains only a credential reference.
   This reuses the existing encrypted credential store and tenant data-key service under purpose
   `integration_message_queue`. The reference occupies the store's connection-id slot; it is a namespace
   profile, not an ERP connection, and there is no connection foreign key. Never reuse or overwrite the
   reference for another namespace. Rotation creates another profile and uses the same drain procedure.
3. Provision a topic `{prefix}-inbound`, its subscription `gateway`, and a queue `{prefix}-outbound`.
   `prefix = yj-{sha256(tenant UUID in lower-case N format)[0:24]}-{sha256(credential reference in
   lower-case N format)[0:24]}`. There is no stable tenant-code port in this boundary, so names use hashes.
   Each additional inbound consumer gets its own subscription; never share `gateway` with an observer.
   Use tenant-specific namespace credentials with rights only over these entities. Runtime provisioning
   is disabled. The health probe requires metadata-read access as well as send/listen rights.
4. GET `/api/integration/settings/transport` for the version, then PUT mode `message_queue`,
   `credentialReference` and `expectedVersion`. The new topology is probed and its receiver starts before
   the setting is saved. A failed change leaves the old setting effective. The health read returns
   `connected`, `auth_failed`, `not_configured` or `unavailable`, with its check time; it is not a sweep
   health result. No connection-health projection is changed by this task.

Tenant administrators change settings and credentials; integration operators may read settings and
health. Setting changes stamp the actor from authenticated claims. No endpoint returns a secret.
Carrier logging providers are disabled, and exceptions crossing its port contain no third-party text
or inner exception. TLS and request-body redaction must remain enabled at the deployment's HTTP boundary.

## Switch and drain

Settings take effect without restart, when a new hand-off resolves its route. Acceptance snapshots the
route before entering its SQL transaction; a concurrent acceptance that already selected the old route
may finish after the switch. The durable dispatch envelope includes that immutable route, so retries
never silently move an old message. A tenant recurring job restores current and retained receivers,
including after a host restart, without a tenant scan during module startup. On other nodes this runs
within a minute. A sender can also restore its receiver on demand.

Previous queue routes and their encrypted credentials remain retained in `drainingRoutes`, even after
switching to local delivery. Do not revoke their credentials or delete their entities while dispatches,
active messages, locked deliveries or dead letters remain. Returning to a retained route removes it
from the drain list. Up to sixteen previous profiles can be retained; automatic destructive retirement
is deliberately absent.

For retirement, schedule maintenance: pause producers and dispatch jobs, let outstanding transactions
finish, drain the old topic subscription and outbound queue, and check their native dead letters plus
the product's Wolverine incoming/outgoing/dead-letter envelopes for that route. Complete or replay every
one before revoking its credential. Only then may an operator remove that route from the setting through
the versioned `ITenantSettings` port, stop its carrier by restarting the affected hosts, and remove the
old entities/credential. Resume producers after all hosts agree on the current version. Never erase the
business inbox or idempotency index. There is no automatic route-retirement endpoint.

The inbox still atomically stores raw payload, claims the change-marker key, supersedes older state and
persists the hand-off. Message identity is also checked within the same connection acceptance lock;
`already_processed` remains the duplicate response. A queue receiver uses that same path, then invokes
the existing idempotent translation handler. Its `IntegrationTransportReceipt` reply preserves `accepted`,
`already_processed` or `superseded` and the accepted row reference; request/reply callers must provision
their own Wolverine reply endpoint. Fire-and-forget sends use broker acknowledgement only.
The translation recovery job remains a safety net for
accepted rows. Outbound dispatches carry a connection wake-up, not authority to post: the existing SQL
outbox row, lease, query-before-create and atomic reply/mapping completion remain authoritative. Repeated
wake-ups are harmless. The route is selected when a pending row is dispatched; an undelivered SQL row
has not yet acquired a carrier route.

Dispatch failure retries use cooldowns from ten seconds to six hours. Exhausted envelopes remain in
Wolverine's durable dead-letter storage for operator replay. Never discard a failed carrier envelope
as evidence that the underlying business row completed.

## External workers

The default host receives both streams. To move outbound execution outside the product network, run
an authorised worker host with the same Integration module, secure SQL connectivity, scoped tenant
credentials/data-key access and the existing per-tenant job scheduling. Disable outbound reception on
the product host with `Integration:Transport:ReceiveOutbound=false`; keep it enabled on the worker.
The worker may disable inbound reception using `Integration:Transport:ReceiveInbound=false`. These
host-role flags require a restart; tenant route changes do not. Arrange tenant job placement so only the
intended tenant's jobs run in each worker. A worker must reach the product's durable SQL store (for
example over a private tunnel); an offline worker without that access is not supported by this carrier.
The transport port admits a future remote execution protocol without changing inbox or relay ownership.

## Validation

`MessageQueueLive` tests are skipped unless `YOJANA_MQ_CONNECTION` is present. The opt-in test requires
provision/delete rights, creates uniquely hashed test entities, sends both streams through Wolverine,
and deletes only those entities. Database tests use a fake carrier and the actual SQL inbox, durable
outbox, relay and encrypted credential store; they require no cloud configuration.
