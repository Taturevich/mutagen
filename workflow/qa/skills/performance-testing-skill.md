# Performance Testing Skill

Use this skill when a change may affect API throughput, server resource use, browser loading, responsiveness, or database work. Performance checks complement correctness tests; they do not replace them.

## Current Repository Coverage

The repo has two separate measurement paths:

- `perf.cmd --scenario-name <name> --duration-seconds <seconds>` starts the API in `Testing` with a fresh SQLite database and collects .NET runtime/application counters. It is a collector, not a load generator. Generate traffic separately while it runs. Output is written under `artifacts/perf`. Histogram collection defaults to 100 tracked series (`--max-histograms`); raise it when endpoint/label cardinality exceeds that, or select only the needed providers with `--counters` to keep collection overhead bounded.
- `perf-trace.cmd --launch-api --database-path <path> --duration-seconds <seconds> --scenario-name <name>` launches the API under `dotnet-trace` with CLR GC sampled-allocation events and allocation stacks enabled from process startup. Generate the representative workload during the capture, then open the `.nettrace` in PerfView or Visual Studio and inspect **Memory > GC Heap Alloc Ignore Free (Coarse Sampling) Stacks** for method-level attribution. `--process-id <pid>` attaches to an existing API for GC events, but may not provide allocation call stacks because the runtime allocation path was not configured at startup. Keep traces short because allocation tracing adds overhead.
- `e2e.cmd -IncludePerformance -PerformanceArtifactsRoot artifacts/performance-e2e` runs the Playwright performance category. It is excluded from ordinary E2E runs. The primary browser journeys are customer booking and staff reservation creation/review. Each journey uses the real UI, while setup seeds a configurable appointment dataset into the isolated E2E database. A separate authenticated API probe measures concurrent reads; an accounting request-count budget remains a focused regression guard. Only the accounting request count currently has a fixed numeric budget, so latency percentiles remain diagnostic until repeatable baselines are recorded.
- `e2e.cmd --include-lighthouse --use-static-client --ui-base-url http://127.0.0.1:6129 --api-health-url http://127.0.0.1:6129/health/ready` runs the full E2E suite, then collects three mobile Lighthouse runs for each public, staff, and client-profile route against the same production client build and isolated test database. This is opt-in because it adds several minutes; sanitized JSON/HTML reports and a metric summary are written locally to `artifacts/lighthouse`. Set `VERONIKA_STUDIO_LIGHTHOUSE_RUNS` to change the sample count, or set `VERONIKA_STUDIO_LIGHTHOUSE_ROUTES` to a comma-separated subset such as `staff,client-profile` during focused iteration. Authenticated routes use the E2E-only staff account. Every audited run must score at least 90 in Performance; a below-green result fails the command after preserving the reports.

The browser artifact records FCP and navigation/resource timing plus LCP, INP, and CLS with attribution from Google's `web-vitals` package. Playwright injects the package before application scripts and returns metric callbacks to the test. INP requires real interactions and may be finalized on page hide; the flow therefore performs the booking/reservation interactions before collecting its artifact. First Contentful Paint remains useful diagnostic data, but is not a Core Web Vital. Soft-navigation reporting is enabled for SPA interactions where the browser supports it.

## Measurement Procedure

1. Define the user path and suspected cost before collecting data. Keep the URL, build mode, environment, database contents, browser version, viewport, and workload constant between baseline and candidate runs. Update Playwright and its browser intentionally; compare that tooling update separately from application performance changes.
2. Build and run using repo-owned commands. Use the performance UI category explicitly when browser behavior is in scope; use the static client for browser measurements so Vite development-mode loading does not distort paint and layout metrics. For example: `e2e.cmd -IncludePerformance -UseStaticClient -UiBaseUrl http://127.0.0.1:6129 -ApiHealthUrl http://127.0.0.1:6129/health/ready -PerformanceArtifactsRoot artifacts/performance-e2e`. Use `perf.cmd` when runtime counters are needed. Check each command's `--help` if flags have changed.
3. Warm up the path, then capture multiple comparable runs. Report sample count and environment alongside p50/p95/p99 latency, throughput, failures, and relevant CPU, allocation/GC, heap, thread-pool, and database signals. Avoid conclusions from one short run or a single maximum.
4. Compare baseline and candidate artifacts by endpoint/scenario. Treat failed requests, missing samples, and unexpected request-count growth as findings even when average latency looks stable.
5. Separate browser lab data from field data. Current Core Web Vitals are LCP, INP, and CLS. Good field thresholds are evaluated at the 75th percentile: LCP <= 2.5 s, INP <= 200 ms, and CLS <= 0.1. Lab page loads can measure LCP and CLS; INP needs user interactions. Lighthouse TBT is a useful lab proxy for responsiveness, not an INP measurement.
6. If a result regresses, profile the affected layer before optimizing. Use counters to find the pressure signal, then collect a trace/profile for CPU or allocation investigations rather than guessing from aggregate latency.
   To investigate method-level managed allocation attribution, capture a short `perf-trace.cmd` run while exercising only the target journey, then inspect sampled allocation stacks in the `.nettrace` using PerfView or Visual Studio. Confirm method frames exist before drawing conclusions; some traces expose allocation samples and types without usable call stacks. EventPipe allocation stacks are sampled estimates, not exact per-request byte accounting.
7. Record the exact command, configuration, artifacts, sample counts, comparison, and skipped checks. Do not call the fixed browser API probe a capacity, stress, or soak test.

## Scenario Controls and Artifacts

The browser scenarios accept these environment variables:

- `VERONIKA_STUDIO_PERF_ITERATIONS` (default 8)
- `VERONIKA_STUDIO_PERF_API_CONCURRENCY` (default 8)
- `VERONIKA_STUDIO_PERF_API_ROUNDS` (default 10)
- `VERONIKA_STUDIO_PERF_DATASET_APPOINTMENTS` (default 60)

The E2E command can set a stable artifact directory through `-PerformanceArtifactsRoot`. Preserve artifacts outside transient test output when comparing runs. Do not increase concurrency against production; use a controlled local or explicitly approved test environment.

## Improvement Backlog

- Add explicit response/error and latency budgets only after collecting a repeatable baseline on the supported environment.
- Add representative sustained load separately if capacity or soak behavior becomes an acceptance requirement; the concurrent API probe is a bounded local load check, not a capacity or soak test.

## Primary References

- [Core Web Vitals](https://web.dev/articles/vitals)
- [LCP and INP measurement APIs reach Baseline Newly available](https://web.dev/blog/lcp-and-inp-are-now-baseline-newly-available)
- [Getting started with measuring Web Vitals](https://web.dev/articles/vitals-measurement-getting-started)
- [.NET `dotnet-counters` diagnostics](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters)
- [ASP.NET Core metrics overview](https://learn.microsoft.com/en-us/aspnet/core/metrics/overview?view=aspnetcore-10.0)
- [Playwright browser support and updates](https://playwright.dev/docs/browsers)
- [GoogleChrome `web-vitals` API and soft navigation reporting](https://github.com/GoogleChrome/web-vitals/blob/main/README.md)
