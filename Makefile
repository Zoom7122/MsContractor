COMPOSE = docker compose
DEV_ENV = .env.dev
DEV_PROFILE = dev-tools
DEV_COMPOSE = $(COMPOSE) --env-file $(DEV_ENV) up -d --build --profile $(DEV_PROFILE) 
SOLUTION = MsContractor.sln

.PHONY: compose ps build logs dozzle test health

compose:
	docker compose --env-file .env.dev --profile dev-tools up -d --build


logs:
	docker compose logs -f --tail=200

dozzle:
	$(DEV_COMPOSE) up -d dozzle
	@printf 'Dozzle: http://localhost:%s\n' "$$(sed -n 's/^DOZZLE_PORT=//p' $(DEV_ENV))"

test:
	dotnet test $(SOLUTION)

health:
	@./scripts/health.sh
