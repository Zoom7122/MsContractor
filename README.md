# MsContractor

## Development commands

- `make compose` — starts the development stack, including Dozzle.
- `make ps` — shows the development containers and their state.
- `make build` — builds the development Docker images without starting containers.
- `make logs` — follows combined container logs (last 200 lines included).
- `make dozzle` — starts only Dozzle and prints its local URL.
- `make test` — runs all .NET tests in `MsContractor.sln`.
- `make health` — performs HTTP readiness checks for backend services plus PostgreSQL, Redis, Kafka, and Dozzle checks.

### Dozzle

Open Dozzle at http://localhost:8089. It provides container logs, CPU, memory and network usage, and container state/health information.

Dozzle is intended only for local development. It uses the `dev-tools` Docker Compose profile and is not started in production.
