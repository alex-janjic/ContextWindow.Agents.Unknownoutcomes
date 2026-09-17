# ContextWindow.Agents.UnknownOutcomes

Treat timed-out agent writes as unknown, not failed. This fault fixture puts a PostgreSQL operation ledger around one refund tool and tests real worker termination separately from downstream commit. Supported recovery must return the original receipt; unsupported recovery must remain pending without sending another write. It is a correctness experiment, not universal exactly-once delivery.

## Usage

`examples/RefundTool.cs` is compiled into the test project. Its `RefundTool.RunAsync(client, executor, approvedWrite, capability)` creates a Microsoft Agent Framework `ChatClientAgent` and binds the trusted workflow's operation identity outside the model's tool arguments. The fixture supplies a scripted `IChatClient` that generates a fresh tool-call ID on every worker invocation. No model or paid API is called. Render the returned `ToolResult`, never unverified model prose.

```csharp
var ledger = new Ledger();
var operationId = await ledger.BindIntentAsync("north-shop", "approved-refund-intent");
var write = new Write("north-shop", operationId, "order-731", 4850, "USD");
using var http = new HttpClient { BaseAddress = downstreamAddress, Timeout = TimeSpan.FromSeconds(5) };
var executor = new Executor(ledger, http);
var result = await RefundTool.RunAsync(client, executor, write, Capability.Lookup);
```

## Requirements and run

.NET 10 SDK, PostgreSQL 18, loopback TCP access, permission to spawn/kill child processes. Pinned direct dependencies are in Directory.Packages.props (Npgsql, Microsoft Agent Framework, xUnit and test tooling). The Agent Framework version is a pinned preview; review adapter API changes before upgrading. No provider credentials. `CW_POSTGRES` is the only connection setting; set it through the environment to a disposable existing database. Never use a production database. SQL command timeout is 30 seconds; HTTP timeout is 5 seconds; child waits are 30 seconds.

```text
dotnet restore tests/ContextWindow.Agents.UnknownOutcomes.Tests/ContextWindow.Agents.UnknownOutcomes.Tests.csproj
dotnet build tests/ContextWindow.Agents.UnknownOutcomes.Tests/ContextWindow.Agents.UnknownOutcomes.Tests.csproj
dotnet test tests/ContextWindow.Agents.UnknownOutcomes.Tests/ContextWindow.Agents.UnknownOutcomes.Tests.csproj --logger "console;verbosity=detailed"
dotnet run --project tests/ContextWindow.Agents.UnknownOutcomes.Tests/ContextWindow.Agents.UnknownOutcomes.Tests.csproj
dotnet pack src/ContextWindow.Agents.UnknownOutcomes --configuration Release
```

`dotnet test` is the one-command proof entry point; console `run` repeats the same assertions and prints measured counts, original/recovered receipts and duration. No illustrative success output is checked into source. Captured output is an execution artifact of the actual run. The single xUnit integration test contains the full ordered matrix to avoid resetting a shared fixture concurrently.

### Optional local containers

Set `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB` and `CW_POSTGRES` for the disposable compose network (database host is `postgres`). Do not commit them. Run `docker compose up --build --abort-on-container-exit --exit-code-from fixture`. This starts PostgreSQL and a .NET test container. `docker compose down --volumes` removes only those local containers/volumes. The coding harness uses its provided PostgreSQL sandbox instead and does not launch Docker. Image release tags are pinned in compose/Dockerfile; they are not immutable digest pins.

Every full proof run drops/recreates **only** `unknown_ledger` and `unknown_downstream`, twice to test initialization against an existing schema. Do not run independent proof suites simultaneously on the same database. Worker and downstream restarts never reset either schema. No public-schema drops, database creation/deletion, or EnsureCreated calls. The shared PostgreSQL server is a fixture convenience: distinct schemas/connections/transactions enforce the nontransactional boundary; this is not proof against a PostgreSQL server crash.

## Executable architecture and rules

```text
Trusted workflow -> durable intent binding -> Agent Framework tool (fresh invocation ID)
  -> ledger: prepared -> atomic claim: dispatching + owner/version
  -> HTTP downstream: commit mutation + receipt (+ original key in idempotency mode)
  <- response [can be truncated after commit]
  -> ledger: receipt + succeeded in one conditional SQL update
  -> application: stored receipt or structured pending
```

`prepared` permits one conditional initial claim. `dispatching` permits no new initial claim. A timeout or recovery encounter converts dispatching to `unknown`; it does not matter whether the original worker is dead or paused. Idempotency permits same-key replay. Lookup permits receipt reads only (including on an authoritative miss: no write). Neither capability permits no redispatch. Success is final, receipt-gated and protected by a database constraint. Owner/version provide audit information; correctness uses monotonic conditional state predicates, not expiring leases. Late validated receipts can complete unknown; a late timeout cannot overwrite success.

The fake downstream idempotency contract is deliberately stronger than many real APIs: keys/receipts never expire and are transactionally retained with the mutation, with serialization of concurrent same-key calls. The ledger additionally stores a replay deadline; tests expire it and require a hold. The local deadline is a conservative cutoff, **not** proof that a real finite-retention provider protects delayed requests. Do not enable replay against such a provider without an arrival/retention guarantee; otherwise use lookup or hold. No provider-specific guarantee is asserted.

Lookup uses primary durable mutation records, not an index that lags. An injected lookup miss tests that even eventual/inconclusive absence never authorizes a send. The fake service intentionally offers no authentication: trusted tenant context is supplied by the fixture. Production adapters must authenticate/authorize that context, keep capability configuration server-owned, and independently deploy/protect the ledger and downstream stores. The durable intent table demonstrates stable workflow identity, not approval UX or an authorization policy. A new explicit approved intent allows an intentional identical action. Changed arguments or capabilities under the old operation ID are rejected before network activity.

## Acceptance matrix

The baseline uses the same idempotency service and commit-then-truncated-response fault but retries with a fresh key: assert two mutations. For each of None / Lookup / Idempotency, the protected matrix exercises:

| Fault | Restart behavior |
|---|---|
| Prepared persisted | All modes claim initial dispatch once; one mutation + receipt |
| Dispatching persisted, no HTTP yet | Idempotency replays once; other modes hold with zero sends/mutations |
| Downstream committed, response lost | One mutation; idempotency/lookup return original receipt; None remains pending |
| Receipt received, not locally saved | Kill real worker; same capability-dependent result |
| Receipt/success saved | Kill real worker; all return stored receipt without another HTTP write |

Also asserted: repeated recovery; persisted state history; two competing worker processes; recovery overlapping a paused sender; late receipt and late timeout; changed arguments on completed operation; equal arguments with different intent IDs; tenant separation; expired replay deadline; lookup miss despite committed mutation; wrong-operation receipt; deterministic PostgreSQL receipt-update failure; downstream process restart preserving idempotency. Paused-worker overlap uses task barriers to resume exactly at dispatch; crash cases use real subprocess kill and a new process. The receipt-store outage is a rejected SQL update, not a full database-server outage. Total database unavailability surfaces an error, never unsupported success.

## Measurements and maintenance

The runner prints actual wall duration, parent peak working set, and largest sampled child working set. The child metric is not a lifetime peak or aggregate RSS. PostgreSQL resources, machine-wide CPU and container resource minima are not measured. At most two workers plus one downstream process run concurrently. These numbers describe this fixture, not throughput or production sizing.

The write-tool adapter team owns capability declarations and recovery tests. Re-review on retention, lookup, middleware or orchestration identity changes. Unsupported outcomes belong in an owned reconciliation queue; do not offer a fresh-key manual retry. Trusted matching receipt evidence is required to resolve a hold as succeeded.

License: MIT.
