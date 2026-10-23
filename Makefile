COMPOSE = docker compose
DEV_ENV = .env.dev
PROD_ENV = .env.prod
DEV_PROFILE = dev-tools
DEV_COMPOSE = ASPNETCORE_ENVIRONMENT=Development $(COMPOSE) -f docker-compose.yml -f docker-compose.dev.yml --env-file $(DEV_ENV) --profile $(DEV_PROFILE)
PROD_COMPOSE = ASPNETCORE_ENVIRONMENT=Production $(COMPOSE) -f docker-compose.yml --env-file $(PROD_ENV)
SOLUTION = MsContractor.sln

.PHONY: compose-up compose-down compose-down-del compose-up-prod compose-down-prod recreate ps build logs dozzle test health

compose-up:
	$(DEV_COMPOSE) up -d --build --remove-orphans

compose-up-prod:
	$(PROD_COMPOSE) --profile $(DEV_PROFILE) stop redis-insight dozzle
	$(PROD_COMPOSE) up -d --build --remove-orphans

compose-down:
	$(DEV_COMPOSE) down --remove-orphans

compose-down-prod:
	$(PROD_COMPOSE) --profile $(DEV_PROFILE) down --remove-orphans

compose-down-del:
	$(DEV_COMPOSE) down --remove-orphans -v

recreate:
	@test -n "$(SERVICE)" || (echo "Usage: make recreate SERVICE=<service-name>"; exit 1)
	$(DEV_COMPOSE) up -d --build --force-recreate $(SERVICE)


logs:
	$(DEV_COMPOSE) logs -f --tail=200

dozzle:
	$(DEV_COMPOSE) up -d dozzle
	@printf 'Dozzle: http://localhost:%s\n' "$$(sed -n 's/^DOZZLE_PORT=//p' $(DEV_ENV))"

test:
	dotnet test $(SOLUTION)

health:
	@./scripts/health.sh
