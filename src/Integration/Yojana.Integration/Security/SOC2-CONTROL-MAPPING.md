# Security controls and service-organisation carve-out

This document describes the integration gateway controls in plain language. It is supporting material for customer
auditors, not a certification or a substitute for the customer's own risk assessment.

## CC6 — logical access

- Each connection uses a dedicated, least-privilege service identity. Customers provision only the permissions needed
  for the enabled integration capabilities and remove the identity when the connection is archived.
- Connection credentials are encrypted with a tenant data key. Each tenant data key is wrapped by a separately managed
  key-encryption key. Ciphertext is bound to its tenant, connection and purpose, so moving it changes the authentication
  data and decryption fails.
- Data-key versions and old wrapped keys are retained for authorised historical decryption. Credential and key rotation
  metadata supports scheduled rotation and advance expiry alerts.
- Worker concurrency and future rate limits are isolated by tenant and connection.

## CC7 — system operations and security events

- Credential changes, connection lifecycle changes, webhook signature failures and permission changes are connection
  events. Credential plaintext is never revealed by an administrative read operation.
- Each event includes the hash of the preceding event and its own content hash. Verification detects deletion,
  insertion, reordering or alteration in the observed stream.
- The local development sink appends daily files without rewriting them. Production immutable retention is supplied by
  a storage implementation of the same audit port after that dependency is approved.
- Webhook authentication uses a bounded timestamp window, HMAC-SHA256, constant-time comparison and overlapping active
  secrets during rotation. Each connector supplies the exact protocol-defined bytes that the sender signed, including
  its timestamp where the protocol requires it; protocol-specific layouts do not enter the shared verifier.

## CC9 — risk mitigation and external services

- The operator maintains an inventory of external application and infrastructure service organisations, reviews their
  assurance reports and material service changes at least annually, and records remediation decisions.
- Connection permissions, credential age, expiry, failed webhook authentication and audit-chain verification are
  reviewed as part of operating controls. Incident procedures preserve the audit stream and rotate affected secrets.

## Carve-out method and complementary controls

External application services and infrastructure services are sub-service organisations under the carve-out method:
their controls are not presented as controls operated by this product. The product operator remains responsible for
selecting and monitoring those services and for responding to identified risks.

Complementary sub-service-organisation controls include authenticating and authorising API requests, protecting the
service's own infrastructure, and providing availability and security evidence. Complementary customer controls include
provisioning a least-privilege integration identity, approving enabled capabilities, keeping customer administrators
restricted, rotating credentials when alerted, and promptly revoking archived or compromised connections. A customer
auditor should evaluate these controls together with the controls above and the customer's own procedures.
