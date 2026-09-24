# Repository Guidelines

## Project Structure & Module Organization

`MsContractor.sln` contains seven .NET 10 services in `src/Services/`, including Gateway, Vendor, Catalog Sync, Duplicates Merge, and MoySklad Egress. Shared HTTP/Kafka contracts and infrastructure live in `src/Shared/`. Keep code in the service layer that owns it: `Controllers` handle HTTP, `Services` hold business rules, `Repositories` own persistence, and Egress `Gateways` call MoySklad. The Vue 3/Vite iframe is in `src/frontend-Iframe/`; backend tests are in `tests/`. Local infrastructure is defined in `docker-compose.yml`, and test-data scripts are in `scripts_for_test_data/`.

## Build, Test, and Development Commands

Run commands from the repository root unless noted:

```bash
dotnet build MsContractor.sln -c Release    # build backend
make test                                   # run solution tests
make compose-up                             # build and start dev stack using .env.dev
make health                                 # check readiness and infrastructure
make compose-down                           # stop the dev stack
cd src/frontend-Iframe && npm ci && npm run build  # build UI
```

Use `npm run dev` in `src/frontend-Iframe/` for the Vite development server. Avoid `make compose-down-del` unless volume deletion is intended.

## Coding Style & Naming Conventions

Use four-space indentation for C# and follow existing .NET naming: PascalCase types/methods, camelCase locals, and namespaces matching directories. Keep interfaces beside their layer implementations and use explicit constructors for dependency-bearing classes. Vue/JavaScript files use the existing two-space, single-quote style. No repository-wide formatter or linter configuration is present; match nearby code and keep changes focused.

## Testing Guidelines

Backend tests use xUnit. Name files `<Subject>Tests.cs` and methods by behavior, such as `PrepareAsync_HonorsCancellationBeforeTheNextBatch`. Run a focused project with `dotnet test tests/MsContractor.Sync.Tests/MsContractor.Sync.Tests.csproj` or the Vendor test project, then `make test` for broader changes. Add tests for changed business rules, account isolation, persistence, and failure paths. There is no frontend test script; validate UI changes with `npm run build` and manual review.

## Commit & Pull Request Guidelines

Recent commits use short, descriptive subjects in English or Russian; there is no enforced prefix convention. State the affected flow and action clearly, for example `Fix purchasereturn relation restore`. In pull requests, describe behavior, affected services/contracts, configuration or migration changes, and tests run. Link the relevant issue when one exists and include screenshots for visible UI changes.

## Security & Configuration

Keep credentials out of commits and logs. Use local environment files for development settings; do not expose their values in examples or diagnostics. Route all MoySklad JSON API calls through MoySklad Egress and preserve account-scoped access checks and internal authentication headers.
