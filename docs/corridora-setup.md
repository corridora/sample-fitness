# Set up Corridora for Stride

Published 2026-10-11 · Updated 2026-10-11

This guide instruments the Angular, ASP.NET Core and Azure Queue workflow in this repository. It works locally and through the included Azure deployment. Start with your own organization; an authorized Corridora operator supplies separate scoped RUM and backend keys for the maintained **Demo** organization. Demo's canonical UUID is `00000000-0000-4000-8000-000000000201`; the credentials route telemetry, not a public tenant-ID override. No public demo persona should mint deployment keys.

## 1. Choose services and scoped credentials

In your Corridora organization create/select the project containing these service identities:

| Service | Runtime / signals |
| --- | --- |
| `sample-fitness-web` | Corridora browser OTel distribution; fetch CLIENT spans, framework action spans, browser metrics/errors/long tasks |
| `sample-fitness-api` | .NET OTel SDK; explicit SERVER/database/blob/client/PRODUCER spans, logs and metrics |
| `sample-fitness-worker` | Isolated Functions SDK process; CONSUMER/poison spans, correlated logs/metrics; native Azure bridge signals tagged `telemetry.adapter=azure-monitor` |

Namespace defaults to `corridora-samples.fitness`; local environment is `development`, maintained Demo is `demo`. The API service name can be configured; the web/worker defaults remain stable. The SDK resource includes actual assembly/source version and Azure cloud/region/resource identity. Browser resource attributes match that version and namespace.

Create a backend ingestion key scoped to the sample's services/signals and a separate origin-restricted browser RUM key. Backend export uses `X-CORRIDORA-KEY`; a browser key is deliberately visible in `ckid=` on the RUM script URL. Management/query credentials are separate and never embedded in the app. A successful ingestion HTTP response does not prove stored correlation. This sample's verifier deliberately leaves stored Corridora checks to the UI or an appropriately scoped operator query; it does not require broad management credentials.

## 2. Configure through the wizard

```sh
./setup configure
./setup deploy --plan
./setup deploy
./setup run
# In another terminal:
./setup verify
```

Bare `./setup` performs configure, build/start and verification in sequence. `.local/config.json` holds nonsecret answers; `.local/secrets.json` is Git-ignored and mode 600. CI can export the same variables or pass an owner-only JSON `--config` file. The script rejects missing cloud/Demo keys; blank keys in local mode disable export/RUM. It generates a strong control token if absent, never prints its value, and never asks for provider passwords.

| Prompt / setting | Default and purpose |
| --- | --- |
| `SAMPLE_MODE` | `local`; choose `cloud` for real Azure provisioning |
| `CORRIDORA_TARGET` | `own`; `demo` requires operator-provisioned keys and sets environment `demo` |
| `CORRIDORA_OTLP_ENDPOINT` | `https://ingest.corridora.com`; HTTPS base with HTTP localhost permitted in local tests |
| `CORRIDORA_INGEST_KEY` | Masked backend key; cloud/Demo required; backend only |
| `CORRIDORA_RUM_KEY` | Masked browser key; cloud/Demo required; visible script query only |
| `CORRIDORA_RUM_SCRIPT_URL` | `https://rum.corridora.com/corridora-rum.js`; actual RUM distribution |
| `OTEL_SERVICE_NAME` / `OTEL_SERVICE_NAMESPACE` | `sample-fitness-api` / `corridora-samples.fitness` |
| `DEPLOYMENT_ENVIRONMENT` | `development`, automatically `demo` for Demo target |
| `PORT` | `4104` locally; cloud image uses `8080` behind HTTPS ingress |
| `DEMO_DAILY_START_UTC` / `DEMO_WINDOW_SECONDS` | `14:00` / `600`; finite UTC regression window |
| `ENABLE_SYNTHETICS` | `false`; enables the provider's backend timer/watchdog; full browser scheduling is separately enabled in GitHub variables |
| `ENABLE_PROVIDER_FORWARDING` | `false`; provisions metered diagnostic storage/bridge and metric reads |
| `RECOMMENDER_URL` | Empty fixture; optional controlled HTTPS GCP base origin |
| `DOWNSTREAM_AUTH_TOKEN` | Optional masked dependency token, independent of Corridora/control keys |
| Azure subscription/region/group | Existing CLI login, `eastus`, `corridora-fitness`; region must support Flex and selected runtime |
| `EXISTING_RESOURCE_GROUP` / `RESOURCE_PREFIX` | `false` / `fitdemo`; dedicated resources by default; select a shared group explicitly |
| Existing Cosmos account / free-tier eligibility | Empty / `true`; opt into an available provisioned free slot at creation or select an existing eligible account in the shared group |
| `IMAGE_REPOSITORY` | `ghcr.io/corridora/sample-fitness`; your public GHCR repository with existing Docker push login |

Resource names, IDs and deployed `PUBLIC_URL` are discovered after deployment and saved. Storage, Key Vault and Functions are dedicated sample-owned resources with a resumable ownership inventory. Secrets travel to Bicep in a temporary owner-only parameters file removed in `finally`, then live in Key Vault. Container Apps uses Key Vault references; Functions app settings reference the same vault through its managed identity.

## 3. Browser and backend instrumentation

`web/src/index.html` contains a RUM marker before the framework's entry script. `src/Fitness.Api/Program.cs` replaces it with the configured script, browser key, exact origin allowlist, endpoint, web identity, environment, version, trace sample rate one, and long-task capture. Corridora's distribution owns the browser provider. `web/src/main.ts` imports only `@opentelemetry/api`; its action helper wraps the synchronous start of each asynchronous fetch with `context.with`, and ends the action span in `finally`. It does not install a second SDK or generate artificial trace headers.

The frontend and API share one origin. The root HTML response sets CSP to self plus the exact RUM script and ingest origins. Backend credentials never appear in the root HTML, public API or Angular bundle. Initial HTML requests, static assets and standalone script downloads are not asserted to form a browser-parented trace.

`src/Fitness.Core/Telemetry.cs` initializes the actual .NET SDK with resource identity, a parent-based always-on root sampler, official `OtlpExportProtocol.HttpProtobuf` exporters, explicit `ILogger`/SDK log provider and `Meter` provider. SDK traffic goes to `/v1/traces`, `/v1/logs`, `/v1/metrics` with backend `X-CORRIDORA-KEY`. Each exporter request times out at 1.5 seconds. Lifecycle flush is bounded to 750 ms per signal (2.25 seconds total); exporter failure counters/logs remain visible without recursively instrumenting exporter HTTP.

The API middleware detaches its uninstrumented framework Activity and explicitly extracts W3C context before creating the instrumented SERVER span. Database/blob calls and outbound recommender calls have manual CLIENT spans. `SummaryService` creates a PRODUCER span and copies its W3C context into the queue envelope; `SummaryProcessor` creates a CONSUMER span from that explicit parent. Success/retry/poison paths stop and flush spans before returning/acknowledging. Retries continue the producer trace; this single-message workflow does not invent fan-in links.

Outbound propagation is limited to the explicitly configured controlled recommender. It sends only trace metadata and an optional independent dependency bearer token. Redirects are disabled and timeout is three seconds. No backend ingestion/control credential travels downstream. An empty endpoint returns a contract-compatible deterministic fixture. Real advertising/analytics integrations are absent and disabled; fictional fitness records must not be forwarded to vendor trackers.

## 4. Native Azure signals

Enable `ENABLE_PROVIDER_FORWARDING=true` and rerun deployment. Bicep adds a separate LRS diagnostic storage destination with a seven-day lifecycle, Container Apps environment diagnostics, Cosmos resource diagnostics and Key Vault audit diagnostics. The managed identity has diagnostic-blob access and Monitoring Reader for bounded ARM metric reads. This avoids Event Hubs or an always-on collector. Export remains metered storage/API traffic and is not automatically free.

`src/Fitness.Core/AzureMonitorBridge.cs` polls Container Apps' `Requests` metric every 15 minutes and converts actual platform values into SDK metrics with resource/name attributes. It reads at most 20 recent diagnostic blobs per run, caps blob size at 2 MB, and deduplicates blob ETags in Cosmos only after SDK flush completes with no exporter HTTP failure; failed delivery keeps the source replayable. Native log conversion exports safe resource/category/operation metadata, not arbitrary raw payloads that may contain request details. Updating an append-style diagnostic blob can replay older operation records when its ETag changes; investigate repeated platform events by timestamp and source blob. Broad account/subscription activity logs and Functions host traces require additional destinations/permissions and are not silently claimed as exported by the application SDK.

Azure diagnostic delivery is delayed; a 30-minute lookback is a cost-bounded demo baseline and may miss unusually delayed blobs. A bridge failure is observable in Functions host logs; native provider conversion tests do not establish that a live destination has delivered. Use the Portal to confirm categories and actual resource logs in the chosen region. [Azure diagnostic destinations](https://learn.microsoft.com/en-us/azure/azure-monitor/platform/diagnostic-settings), [Container Apps log options](https://learn.microsoft.com/en-us/azure/container-apps/log-options).

## 5. Verify the connected evidence

Run `./setup verify`. It opens a real Playwright browser, clicks the Angular summary actions, proves healthy/poison/control/recovery/expiry behavior, checks the backend trace response, and records a successful daily browser heartbeat. The JSON output includes the public URL, a fresh healthy trace ID and simulated release. It does not claim a shared Corridora database query happened.

In Corridora select the matching environment/project and fresh `sample-fitness-web` session. Find the fetch CLIENT span for `/api/trace-check` or `/api/summaries`. Its **span ID must equal the API SERVER span's parent span ID**, and both must share the trace ID. Follow the PRODUCER → CONSUMER parent relationship, three seeded attempts, poison record, and a healthy recovery summary. Check a correlated application log, `fitness.summary.attempts` metric, and, if enabled, an `azure.platform.log` or `azure.platform.metric` native record. Verify the simulated `deployment` change/recovery event and the unchanged actual `service.version`.

Offline maintainers can prove this without shared credentials:

```sh
# Build API/frontend first using the README commands.
RUM_BUNDLE_PATH=/absolute/path/to/a/real/corridora-rum.js python3 tests/integration.py
```

The local sink validates actual SDK protobuf and the genuine RUM distribution's browser JSON; it asserts CLIENT-to-SERVER and PRODUCER-to-CONSUMER parentage. No synthetic header alone is accepted as browser proof. Without the bundle, that one test reports a skip; backend propagation tests still run. This is local capture proof, not evidence of stored Demo data.

## 6. Replay, rotate and tear down

`./setup scenario --phase regression --duration 120` opens a finite authenticated rollout. `./setup scenario --phase recover --duration 120` restores seeded deserialization immediately. `./setup verify` additionally proves automatic expiry. Daily schedulers can fail, so check `/api/demo/heartbeat` with the control token, run the watchdog and monitor absent results.

For rotation, create new scoped keys in Corridora, rerun `./setup configure`, and redeploy; wait for the new Container Apps revision and worker's Key Vault reference refresh, then verify a fresh session before revoking old keys. Rotate the control/dependency tokens independently. Rerunning setup preserves nonsecret answers and the ownership inventory. For local failures, inspect `.local/integration.log`, `/healthz` exporter failure count, key scopes, endpoint protocol, CSP and installed browser. Azure 403s immediately after provisioning usually require RBAC/Key Vault propagation and a resumed deployment, not wider permanent permissions.

`./setup destroy --plan` previews deletion. Dedicated groups are deleted only when their ownership tag still matches and no unowned resources have appeared. Shared groups/accounts survive; only recorded app children and matching tagged resources are removed. Key Vault soft-delete retention remains seven days. Public GHCR versions are preserved and can be pruned separately through the package owner. Local destroy removes generated data/builds while preserving configuration.
