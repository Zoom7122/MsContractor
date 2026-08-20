COMPOSE = docker compose
DEV_ENV = .env.dev
DEV_PROFILE = dev-tools
DEV_COMPOSE = $(COMPOSE) --env-file $(DEV_ENV) up -d --build --profile $(DEV_PROFILE) 
SOLUTION = MsContractor.sln

.PHONY: compose recreate ps build logs dozzle test health

compose-up:
	docker compose --env-file .env.dev --profile dev-tools up -d --build

compose-down:
	docker compose --env-file .env.dev --profile dev-tools down 

compose-down-del:
	docker compose --env-file .env.dev --profile dev-tools down -v

recreate:
	@test -n "$(SERVICE)" || (echo "Usage: make recreate SERVICE=<service-name>"; exit 1)
	$(COMPOSE) --env-file $(DEV_ENV) --profile $(DEV_PROFILE) up -d --build --force-recreate $(SERVICE)


logs:
	docker compose logs -f --tail=200

dozzle:
	$(DEV_COMPOSE) up -d dozzle
	@printf 'Dozzle: http://localhost:%s\n' "$$(sed -n 's/^DOZZLE_PORT=//p' $(DEV_ENV))"

test:
	dotnet test $(SOLUTION)

health:
	@./scripts/health.sh
