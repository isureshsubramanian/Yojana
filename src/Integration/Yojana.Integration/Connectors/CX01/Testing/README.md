# Connector edge harnesses

The database test project owns the connector-neutral `IConnectorEdgeHarness` contract and the single
`ConnectorEdgeScenarios.All` catalogue. Protocol paths, object names, field names and fixture keys belong only in a
connector's `Testing` folder. The scenario runner deals only in canonical messages and business outcomes.

To add a connector:

1. Implement `IConnectorEdgeHarness` over its in-process simulator. `StartAsync` and `StopAsync` own the simulator;
   `CreateConnectionAsync` registers and configures a connection; the fault methods make each edge deterministic;
   and `ReadRemoteAsync` reports the remote result without exposing protocol details.
2. Expose a public static `ConnectorEdgeHarnessManifest Manifest`. Populate its capability dictionary from the
   connector's normal capability declaration, not a second test-only matrix.
3. Add one test class whose facts call `ConnectorEdgeScenarioRunner` with the scenario ids in
   `ConnectorEdgeScenarios.All`. Use `ConnectorEdgeFactAttribute`; an absent or unsupported required capability is
   then a visible skip carrying the reason, never a passing test.
4. Add a `Live` harness and test class using the same scenario ids. Gate it on connection credentials and a fixture
   file. If a customer sandbox cannot deterministically drive a fault, put that scenario and its reason in the live
   manifest's exclusions so the acceptance report shows it as not applicable.

The CX01 live fixture is named by `YOJANA_CX01_EDGE_SCENARIO`. It contains the sandbox record keys, fictional
references, a base64 webhook signing secret, the recorded document-posting evidence, and `supported_scenarios`.
Fault-heavy scenarios also need `fault_controller_uri`; its test-only controller scripts remote availability,
closed-period rejection, owner edits, dropped or blocked replies, on-hand values, and exposes `state`. Only declare
scenarios that are safe to mutate in that sandbox. The normal URL, user and password environment variables are also
required. Without all four environment variables every live row is skipped at discovery.
