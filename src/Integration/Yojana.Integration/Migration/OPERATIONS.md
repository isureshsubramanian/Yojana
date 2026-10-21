# Migration extraction operations

Migration extraction is a read-only integration with the incumbent ERP. Give the extraction principal only `SELECT`
permission on the objects listed by the source-test endpoint. Do not grant data-change, schema-change, ownership or
server-administration permissions.

The `separate` route opens the encrypted source connection directly. The `cross_database` route uses the product
server login and three-part object names, so that login also needs read access to the configured source database. The
`linked_server` route uses four-part names and the linked server's mapped security context. Linked-server extraction
is deliberately not wrapped in a distributed transaction: the source side is read-only, and every staging table is
committed independently before the batch advances.

Every connection is opened with `ApplicationIntent=ReadOnly`. Command timeout is configured per migration source.
Connection strings are tenant-key-encrypted credentials; they must not be copied into operational notes or logs.

Licence confirmation is separate from database permission. A source remains blocked until a tenant administrator
records the customer's contract reference and confirmation note, unless that tenant has deliberately changed its
default licence-position setting.
