# Azure resources and deployment

Published 2026-10-11 · Updated 2026-10-11

`./setup deploy` executes a real deployment using `infra/main.bicep`; `--plan` is an offline preview without account or credential reads. Live deployment shows the authenticated subscription, builds/pushes its own public Linux/amd64 GHCR image, checks anonymous pull access, applies the resource template, publishes an isolated Functions ZIP with OneDeploy, and saves outputs. It never provisions cloud resources during code-generation validation.

## Resource and ownership model

| Resource | Role, limit and telemetry path |
| --- | --- |
| Container Apps Consumption environment/API | Same-origin Angular/API, zero minimum/two maximum replicas, 0.25 CPU/0.5 GiB; SDK OTLP plus optional native diagnostics |
| Cosmos DB NoSQL account/database/container | Opted-in **provisioned** free tier, 400 RU/s shared app database, `/kind` partition, 7-day document TTL; explicit Cosmos SDK CLIENT spans and request charge attributes |
| LRS Storage account | Private exports and Functions packages, summary/poison queues; explicit SDK spans, 15-minute message TTL, export blob lifecycle seven days |
| User-assigned identity | Cosmos data contributor restricted to the app database; Storage Blob Data Owner for Functions host/timer and Queue Data Contributor; no application storage access keys |
| Key Vault | Backend ingest, control and optional dependency token; managed identity Secrets User; seven-day soft-delete retention |
| Functions Flex | .NET 10 isolated, 512 MiB, no Always Ready, max scale 40 (Azure's supported minimum), batch one, three dequeues, five-second retry visibility; explicit consumer spans and bounded flush |
| Optional diagnostic Storage/Monitor | Separate account, seven-day lifecycle, selected native logs and Requests metric bridge; metered reads/exports, 20 recent blobs per run maximum |

The dedicated group is tagged `corridora-owner=<installation ID>`. An existing shared group is opt-in. Existing Cosmos account reuse requires that account in the selected group, free-tier eligibility, and a new app-specific database name; its account/group must survive teardown. Setup refuses prefix collisions with unowned resources. The recorded inventory remains available after partial failure so the next deploy can resume. Do not remove `.local/resources.json` before teardown or ownership evidence is lost.

The deployment identity needs resource deployment, resource-provider registration and role-assignment rights. Runtime permissions are narrower: the shared user-assigned identity reads Key Vault, works with its Cosmos database/queues/blobs, and optionally reads Monitor metrics/diagnostic blobs. No VNet/NAT, Log Analytics workspace, paid ACR, Event Hubs, managed database outside the free Cosmos slot, or always-on collector is created.

## Provider assumptions and checks

Use a region supporting Flex Consumption, 512-MiB workers and runtime `dotnet-isolated` version **`10.0`**. A template's successful compilation cannot guarantee current regional capacity, subscription quotas, Azure Policy acceptance, Cosmos free-tier availability or eventual role propagation. The source was checked against the [isolated .NET support guide](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide), [Functions infrastructure configuration](https://learn.microsoft.com/en-us/azure/azure-functions/functions-infrastructure-as-code), and [sites 2025-03-01 schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.web/2025-03-01/sites). On .NET 10 the worker uses `Azure.Functions.Sdk/1.0.0` with Worker 2.52.0; local queue behavior runs without Functions Core Tools, while the actual isolated host and bindings are validated by build/publish and live deployment after operator provisioning.

The provider grants 1,000 RU/s and 25 GB for one eligible provisioned Cosmos account per subscription. Setup opts in at account creation; serverless billing is a different mode. If another account already occupies the free slot, select that eligible account explicitly in a shared group or stop and revise the budget. [Cosmos free tier](https://learn.microsoft.com/en-us/azure/cosmos-db/free-tier). Functions' maximum instance count cannot be reduced below 40 even though the demo is tiny; keep synthetic traffic bounded and monitor public load. [Flex scale](https://learn.microsoft.com/en-us/azure/azure-functions/flex-consumption-plan), [timer identity requirements](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-timer).

Budget $0–2/month incremental at demo volume, before tax, excluding existing Corridora and domain hosting. Billing alerts are advisory. Provider free grants are shared; optional diagnostic/storage operations and secret access are metered. Review one week's actual usage before enabling scheduled traffic/provider forwarding, and disable those two toggles if the fleet approaches its reserve. Queue/table/blob hosts and storage packages create small residual storage charges even when application instances scale to zero.

## Scheduled browser runner

The public repository includes `.github/workflows/daily.yml` with 14:03 UTC full journey and 14:30 watchdog. Set repository variable `ENABLE_SYNTHETICS=true`, variables `PUBLIC_URL`, `CORRIDORA_RUM_SCRIPT_URL`, and secrets `DEMO_CONTROL_TOKEN`, `CORRIDORA_RUM_KEY`; no backend ingestion key is required by the remote browser runner. The app already owns that backend secret. An optional existing Akamai/external runner can call `python3 synthetics/daily.py` with the same environment. Change workflow cron if changing the UTC window; setup automatically derives Functions timer settings from the configured start but does not rewrite your repository's CI schedule.

GitHub schedule delay/disable behavior means the repository alone cannot promise a daily hosted failure. The provider timer derives and exercises the bad action; its watchdog emits missing-browser evidence and retries a bounded backend incident. A full verified browser heartbeat still requires the runner. [GitHub schedule constraints](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule).

## Teardown and recovery

Run `./setup destroy --plan`, then `./setup destroy`. Shared groups are never removed. In a shared group, recorded app database/RBAC/diagnostic children are deleted before matching tagged top-level resources. Dedicated group deletion is refused if the tag differs or unowned resources appear. Shared Cosmos account changes outside this app's dedicated database/diagnostic setting remain intact. Key Vault retained deletion and public GHCR versions require separate owner maintenance; package deletion is deliberately outside Azure teardown.

If deployment fails after image publication or partial ARM creation, inspect the last saved inventory stage and rerun deploy after fixing the cause. Do not copy inventory ownership IDs between installations. A failed role/secret reference may require propagation time and another deployment; stored keys never belong in printed commands or issue reports.
