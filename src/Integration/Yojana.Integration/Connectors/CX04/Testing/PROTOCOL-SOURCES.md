# CX04 simulator protocol sources

This ledger records only facts encoded by the simulator. It was transcribed from
`docs/plan/research/P10-T05-cx04-protocol-facts.md`; no new research was performed. All sources were read
2026-10-23. `ASSUMED` rows remain customer-acceptance checks.

| Fact | Encoded value | Confidence | Research | Public source |
|---|---|---|---|---|
| F01 | Password and authorization-code grants are served | documented | §2 | [A1] |
| F02 | `client_credentials` returns `unsupported_grant_type` | documented absence | §2 | [A1], [A6] |
| F05 | Token endpoint is `/identity/connect/token` | documented | §2 | [A5], [A6], [M11] |
| F06 | Authorization endpoint is `/identity/connect/authorize` | documented | §2 | [A6] |
| F07 | Password grant accepts the documented form fields | documented | §2 | [M11] |
| F08 | `api`, `offline_access`, and `api:concurrent_access` scopes are accepted | documented | §2 | [A6], [M11] |
| F09 | Token body contains access token, bearer type, lifetime, scope and optional refresh token | documented | §2 | [M11] |
| F10 | Access lifetime defaults to 3,600 seconds and is configurable | documented example; fixed value inferred | §2 | [A5], [M11] |
| F11 | Refresh token rotates and the prior token is rejected | documented | §2 | [A5] |
| F12 | Absolute refresh chain defaults to 30 days from initial authentication | documented | §2 | [A6] |
| F13 | Sliding refresh chain defaults to 90 days and is selectable | inferred | §2 | [M14] |
| F14 | A grant owns one session and refresh reuses it | documented | §2 | [A6] |
| F16 | Governor models sessions, parallel requests and requests per minute | documented | §3 | [A2] |
| F17 | Requests/minute are 50/100/150/300 | documented | §3 | [L1] |
| F18 | Queue sizes are 5/10/20/40 | documented | §3 | [L1] |
| F19 | Parallel requests are 1/2/3/8 | documented | §3 | [L1] |
| F20 | Session limits are 5/10/20/scalable | documented | §3 | [L1] |
| F24 | Delay begins at half the per-minute cap | documented | §3 | [A2] |
| F25 | Delay is window time left divided by requests still allowed | documented | §3 | [A2] |
| F26 | Excess concurrency queues; logout bypasses the queue | documented | §3 | [A2] |
| F27 | Queue overflow or ten minutes queued returns a decline; declines are not counted | documented | §3 | [A2] |
| F28 | Queueing has no interim caller signal | documented | §3 | [A2] |
| F29 | Licence decline uses HTTP 429 | documented | §3 | [A3], [A7], [A8], [M9], [M10] |
| F30 | 429 defaults to `{}` without `Retry-After`; shaped/header variant is configurable | ASSUMED | §3 | none |
| F31 | Per-tier queue is the default; fixed 20 is configurable | inferred | §3 | [A2], [L1] |
| F34 | Lines/transaction limits are 1,000/2,000/2,500/50,000 | documented | §3 | [L1] |
| F35 | Serials/document limits are 2,000/4,000/7,500/50,000 | documented | §3 | [L1] |
| F39 | Contract endpoint route contains endpoint name and version | documented | §4 | [A7], [A3] |
| F40 | Discovery returns build/database versions and endpoint list | documented | §4 | [A12] |
| F41 | Discovery includes `Default/24.200.001` | documented | §4 | [A13] |
| F47 | Contract fields use `value`; records expose `id`, `rowNumber` and `_links` | documented; `rowNumber` inferred | §4 | [A7], [M9], [T1] |
| F48 | PUT creates or updates by keys, `id`, or filter identity | documented | §4 | [A7], [M10] |
| F49 | `If-None-Match: *` and `If-Match: *` produce 412 on failed precondition | documented | §4 | [A7], [M10] |
| F50 | A missing update key can create a record | inferred | §4 | [C7] |
| F51 | `id` wins when keys and `id` disagree; key-wins is configurable | ASSUMED | §4 | none |
| F52 | Detail row with `delete: true` is removed during PUT | documented | §4 | [M10], [C8] |
| F53 | Details and allocations appear only when explicitly expanded | documented | §4 | [A7], [A9], [M5], [M6], [M7], [M8] |
| F54 | GET and PUT return 200 with entity JSON | documented | §4 | [A7], [M10] |
| F55 | 400/401/403/404/412/422/429/500 status families are represented | documented | §4 | [A7], [A8], [M10] |
| F56 | 422 echoes the entity with a failing field `error` | documented | §4 | [A7] |
| F57 | 500 has `message`, `exceptionMessage`, and `exceptionType` | inferred | §4 | [C5] |
| F60 | Receipt allocations use details, receipt quantity, location and quantity | documented | §4.1 | [M7] |
| F61 | Transfer receipt uses the receipt entity and allocation shape | documented | §4.1 | [M8] |
| F62 | Shipment details accept allocations with location, quantity and serial | documented | §4.1 | [M5] |
| F63 | RC sales returns accept detail allocations | documented | §4.1 | [M6] |
| F64 | Allocation serial field is `LotSerialNbr` | documented | §4.1 | [M5], [M6] |
| F65 | Default requires line quantity and allocations summing to it | documented example | §4.1 | [M7], [M8] |
| F66 | Allocation-only mode reproduces the reported doubling when both are sent | inferred | §4.1 | [C1] |
| F67 | Serial allocation quantity defaults to one; validation can be disabled | ASSUMED | §4.1 | none |
| F68 | RM defaults to the RC allocation shape; support can be disabled | ASSUMED | §4.1 | none |
| F72 | File upload uses a record `files:put` template and binary body | documented | §4.2 | [M9] |
| F73 | File URL contains endpoint, class, view, record id and filename | documented | §4.2 | [M9] |
| F74 | Detail rows expose their own file upload link | documented | §4.2 | [M9], [C10] |
| F75 | `PX-CbFileComment` is optional and limited to 500 characters | documented | §4.2 | [M9] |
| F76 | File success is 204 with `Location` | documented | §4.2 | [M9] |
| F77 | Attachment limit defaults to 25 MiB and is configurable | ASSUMED | §4.2 | none |
| F78 | Action route is `POST <Entity>/<Action>` | documented | §4.2 | [A8] |
| F79 | Running action returns 202 plus `Location`; completion is 204; unknown is 404 | documented | §4.2 | [A8] |
| F80 | Action location is polled with GET | documented | §4.2 | [A8] |
| F81 | Running poll defaults to 202 and is configurable | ASSUMED | §4.2 | none |
| F82 | Other requests in the action's session fail while it runs | documented | §4.2 | [A8] |
| F83 | Shipment action is `ConfirmShipment`; receipt release name is configurable | documented / ASSUMED | §4.2 | [A8] |
| F84 | Disabled action returns a 500 `PXActionDisabledException` | inferred | §4.2 | [C2], [C5] |
| F85 | Cookie login is POST `/entity/auth/login`, returns 204 and a cookie | documented | §5 | [A3] |
| F88 | Logout is POST `/entity/auth/logout` and frees the session | documented; exact path inferred | §5 | [A4], [T1] |
| F89 | An omitted logout leaves a session consuming the limit | documented | §5 | [A3], [A4] |
| F90 | Idle sessions are reaped after ten minutes only once the limit is reached | documented | §5 | [A4] |
| F93 | Below-limit idle sessions default to never reaped; reaping is configurable | ASSUMED | §5 | none |
| F94 | A plain API grant uses one token-tracked session | documented / inferred | §5 | [A6], [T1] |
| F95 | Push payload identifies its per-document query | documented | §6 | [A14] |
| F97 | Inserted/deleted rows carry the configured query fields | documented | §6 | [A14], [M2] |
| F98 | Push body has `Inserted`, `Deleted`, `Query`, `CompanyId`, `Id`, `TimeStamp`, `AdditionalInfo` | documented | §6 | [M2] |
| F99 | Inserted and deleted arrays represent query membership changes | documented | §6 | [M2] |
| F100 | Query and company identify the query and tenant | documented | §6 | [M2] |
| F101 | Id supports dedupe and timestamp supports ordering | documented | §6 | [M2] |
| F102 | Delivery order is not guaranteed; reverse-order fault is available | documented absence | §6 | [M2], [A15] |
| F103 | Automatic delivery makes at most five attempts total | documented | §6 | [A15], [M3] |
| F104 | Retry delay is configurable and defaults to 30 seconds | ASSUMED | §6 | none |
| F105 | Error replies trigger retry | documented | §6 | [A15] |
| F106 | Error after processing can produce a duplicate | documented | §6 | [A15], [M3] |
| F107 | Delivery is modelled as at-least-once | inferred | §6 | F101, F105, F106 |
| F108 | Failed items are manually resendable and purged after two days | documented | §6 | [M3] |
| F109 | Truncated failed items cannot be resent | documented | §6 | [M3] |
| F110 | Pre-send loss is not put in the failed list | documented / inferred | §6 | [M3] |
| F112 | Aggregate push queries are rejected at setup | documented | §6 | [M4] |
| F114 | Push authentication is a configurable static shared-secret header | ASSUMED | §6 | none |
| F117 | Modified-since filter uses datetimeoffset comparison | documented | §7 | [A9] |
| F118 | List paging uses `$top` and `$skip` | documented | §7 | [A9] |
| F119 | Paging defaults to entity-key order; seeded order is configurable | ASSUMED | §7 | none |
| F120 | Purchase receipt exposes `LastModifiedDateTime` in the pinned family | documented | §7 | [A13] |
| F121 | Sales order uses `LastModified` in the pinned version | documented | §7 | [A13] |
| F122 | Shipment/transfer order default to `LastModifiedDateTime`; each mapping is configurable | ASSUMED | §7 | [A13] partial |
| F125 | Items can be untracked, lot-tracked, or serial-tracked | documented | §8 | [M12] |
| F126 | Serial assignment is When Received or When Used | documented | §8 | [M12] |
| F127 | Auto-numbering depends on an auto-increment segment | documented | §8 | [M12] |
| F128 | Tracking may include expiration and issue method | documented | §8 | [M12] |
| F129 | When Used refuses serials on receipt and permits them on issue | inferred | §8 | [M12] |
| F131 | Manufacturing reads use a separate endpoint and configurable seeded version | inferred | §8 | [M13], [C13] |
| F132 | Generate on a Move returns `PXActionDisabledException` | inferred | §8 | [C2] |
| F133 | Production completion fields are seeded rather than invented as a fixed catalogue | ASSUMED | §8 | [C13] |

[A1]: https://help.acumatica.com/Wiki/ShowWiki.aspx?pageid=a8f71c44-9f5c-4af8-9d47-bc815c8a58e7
[A2]: https://help.acumatica.com/Wiki/ShowWiki.aspx?pageid=7a796856-3dec-4a4f-abf8-171324c9642b
[A3]: https://help.acumatica.com/Wiki/ShowWiki.aspx?pageid=0322492a-de9b-4443-b256-923c48aff31d
[A4]: https://help.acumatica.com/Wiki/ShowWiki.aspx?pageid=f6701ec1-2296-4155-8242-03d29006bf6e
[A5]: https://help.acumatica.com/Wiki/ShowWiki.aspx?pageid=2a5781c6-b661-409c-82f3-8adafd6a332d
[A6]: https://help.acumatica.com/Wiki/ShowWiki.aspx?pageid=a8fbe87f-647a-48dd-b713-de6c123a4de4
[A7]: https://help.acumatica.com/Wiki/ShowWiki.aspx?pageid=9d966d50-a0a1-4456-a9ff-1cc2159d48d4
[A8]: https://help.acumatica.com/Wiki/ShowWiki.aspx?pageid=91bf9106-062a-47a8-be1f-b48517a54324
[A9]: https://help.acumatica.com/Wiki/ShowWiki.aspx?pageid=775ca16b-cba6-4c1d-89d5-c1df7833bfea
[A12]: https://help.acumatica.com/Wiki/ShowWiki.aspx?pageid=da0fe495-b2e5-4d34-ab5a-5829e408f684
[A13]: https://help.acumatica.com/Wiki/ShowWiki.aspx?pageid=b49d1af2-80a7-4271-b421-ab64173eead8
[A14]: https://help.acumatica.com/Wiki/ShowWiki.aspx?pageid=562ccb34-3b08-45c4-9cf9-d51a8a54ae01
[A15]: https://help.acumatica.com/Wiki/ShowWiki.aspx?pageid=87648532-3d21-4e16-b555-c906ebac151b
[L1]: https://www.acumatica.com/media/2025/04/Acumatica-Licensing-Guide-April-2025-Final.pdf
[M2]: https://help.myob.com.au/advanced/Docs/Published/IntegrationDevelopmentGuide/IntegrationDev_PushNotifications_Format.html
[M3]: https://help.myob.com.au/advanced/Docs/Published/IntegrationDevelopmentGuide/IntegrationDev_PushNotifications_FailedNotifications.html
[M4]: https://help.myob.com.au/advanced/Docs/Published/IntegrationDevelopmentGuide/IntegrationDev_PushNotifications_RequirementsToQuery.html
[M5]: https://help.myob.com.au/advanced/Docs/Published/IntegrationDevelopmentGuide/IntegrationDev_RESTExample_Shipment_CreateForSOWithAllocations.html
[M6]: https://help.myob.com.au/advanced/Docs/Published/IntegrationDevelopmentGuide/IntegrationDev_RESTExample_SalesOrder_UpdateReturnOrderWithAllocations.html
[M7]: https://help.myob.com.au/advanced/Docs/Published/IntegrationDevelopmentGuide/IntegrationDev_RESTExample_PurchaseReceipt_InsertLinesLocation.html
[M8]: https://help.myob.com.au/advanced/Docs/Published/IntegrationDevelopmentGuide/IntegrationDev_RESTExample_PurchaseReceipt_CreateTransferReceiptWithAlloc.html
[M9]: https://help.myob.com.au/advanced/Docs/Published/IntegrationDevelopmentGuide/IntegrationDev_RESTExample_Basic_Put_Attachment.html
[M10]: https://help.myob.com.au/advanced/Docs/Published/IntegrationDevelopmentGuide/IntegrationDev_RESTExample_Basic_Update_Record.html
[M11]: https://help.myob.com.au/advanced/Docs/Published/IntegrationDevelopmentGuide/OAuthOIDC_ResourceOwner_AccessToken.html
[M12]: https://help.myob.com.au/advanced/Docs/Published/UserGuide/IN_20_70_00.html
[M13]: https://help.myob.com.au/advanced/Docs/Published/ReleaseNotes/RN_2025_R1_Developer_ManufacturingEndpoint.html
[M14]: https://help.myob.com.au/advanced/Docs/Published/IntegrationDevelopmentGuide/OAuthOIDC_Registration_SlidingExpiration.html
[C1]: https://community.acumatica.com/develop-integrations-with-web-services-apis-289/po-receipt-quantities-are-doubled-31468
[C2]: https://community.acumatica.com/manufacturing-89/create-lot-serial-allocations-for-a-manufacturing-move-transaction-through-the-contract-based-rest-api-35889
[C5]: https://community.acumatica.com/develop-integrations-with-web-services-apis-289/rest-api-500-error-creating-a-bill-6154
[C7]: https://community.acumatica.com/develop-integrations-with-web-services-apis-289/rest-api-trying-to-add-details-to-an-existing-purchasereceipt-24059
[C8]: https://community.acumatica.com/develop-integrations-with-web-services-apis-289/delete-budget-line-via-rest-deletes-matching-accounts-ignoring-subaccount-8839
[C10]: https://community.acumatica.com/develop-integrations-with-web-services-apis-289/add-files-to-salesorder-line-item-using-rest-api-8638
[C13]: https://community.acumatica.com/manufacturing-89/manufacturing-rest-api-endpoint-documentation-26114
[T1]: https://www.apideck.com/blog/how-to-integrate-with-the-acumatica-api.md
