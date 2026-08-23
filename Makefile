# Makefile for StationSuitabilityOverlay.
#
# Wraps the awkward parts of this project's build: the C# toolchain is Windows-only
# and has to be driven through PowerShell from WSL, building is also deploying, and
# the running game holds a lock on the deployed DLL. See CLAUDE.md for the reasoning.
#
# Run `make` for the target list.

SHELL := /bin/bash
.SHELLFLAGS := -eu -o pipefail -c

PROJECT     := dotnet/StationSuitabilityOverlay.csproj
TESTS       := tests/SuitabilityScoring.Tests
UI_MODULE   := dotnet/UI/StationSuitabilityOverlay.mjs
CONFIG      ?= Release
OUTPUT      := dotnet/bin/$(CONFIG)/net48/StationSuitabilityOverlay.dll

USERDATA    := /mnt/c/Users/maxbl/AppData/LocalLow/Colossal Order/Cities Skylines II
DEPLOYED    := $(USERDATA)/Mods/StationSuitabilityOverlay/StationSuitabilityOverlay.dll
MOD_LOG     := $(USERDATA)/Logs/StationSuitabilityOverlay.Mod.log
UI_LOG      := $(USERDATA)/Logs/UI.log

# The toolchain resolves CSII_TOOLPATH from the Windows user environment, so the
# build has to run as a Windows process rather than under WSL's dotnet.
DOTNET_WIN  := powershell.exe -NoProfile -Command

.DEFAULT_GOAL := help
.PHONY: help build debug test check-ui verify deploy wait-for-game status logs errors clean

help: ## Show this help
	@echo "StationSuitabilityOverlay — targets:"
	@grep -E '^[a-z-]+:.*?## ' $(MAKEFILE_LIST) \
		| sed -e 's/:.*## /\t/' \
		| awk -F'\t' '{ printf "  \033[36m%-14s\033[0m %s\n", $$1, $$2 }'
	@echo
	@echo "  CONFIG=Debug changes the configuration (default: Release)."

build: ## Compile and deploy to the game's Mods folder (close the game first)
	$(DOTNET_WIN) "dotnet build $(PROJECT) -c $(CONFIG)"

debug: ## Compile and deploy the Debug configuration
	@$(MAKE) --no-print-directory build CONFIG=Debug

test: ## Run the offline test harness (exit code = number of failures)
	dotnet run --project $(TESTS)

check-ui: ## Syntax-check the UI module, which is never compiled
	node --check $(UI_MODULE)

verify: check-ui test build ## Everything a change should pass before a run
	@$(MAKE) --no-print-directory status

# The game locks the deployed DLL, and ModPostProcessor returns a misleading exit
# code if it runs too soon after the game closes. So: wait for the process to go,
# let the filesystem settle, build, then judge the result by comparing sizes.
deploy: wait-for-game ## Wait for the game to close, then build and confirm the deploy
	@echo "Game closed; letting the filesystem settle..."
	@sleep 25
	@for attempt in 1 2 3; do \
		$(DOTNET_WIN) "dotnet build $(PROJECT) -c $(CONFIG)" || true; \
		if [[ -f "$(DEPLOYED)" && -f "$(OUTPUT)" ]] \
			&& [[ "$$(stat -c%s "$(DEPLOYED)")" == "$$(stat -c%s "$(OUTPUT)")" ]]; then \
			echo "Deployed $$(stat -c%s "$(DEPLOYED)") bytes on attempt $$attempt."; \
			exit 0; \
		fi; \
		echo "Attempt $$attempt did not land; retrying..."; \
		sleep 20; \
	done; \
	echo "Deploy failed: $(DEPLOYED) does not match $(OUTPUT)." >&2; \
	exit 1

wait-for-game:
	@while tasklist.exe 2>/dev/null | grep -qi 'Cities2.exe'; do \
		echo "Waiting for Cities: Skylines II to close..."; \
		sleep 15; \
	done

status: ## Compare the built DLL against the deployed one
	@if [[ ! -f "$(OUTPUT)" ]]; then echo "Not built: $(OUTPUT)"; exit 1; fi
	@echo "built    $$(stat -c%s "$(OUTPUT)") bytes  $(OUTPUT)"
	@if [[ -f "$(DEPLOYED)" ]]; then \
		echo "deployed $$(stat -c%s "$(DEPLOYED)") bytes  $(DEPLOYED)"; \
		if [[ "$$(stat -c%s "$(DEPLOYED)")" == "$$(stat -c%s "$(OUTPUT)")" ]]; \
			then echo "-> up to date"; \
			else echo "-> STALE, run 'make deploy'"; fi; \
	else \
		echo "deployed (absent)"; \
	fi

logs: ## Follow the mod log
	@tail -f "$(MOD_LOG)"

errors: ## Show warnings and errors from the mod and UI logs
	@echo "== StationSuitabilityOverlay.Mod.log =="
	@grep -nE 'ERROR|WARN|Exception' "$(MOD_LOG)" || echo "  (none)"
	@echo "== UI.log =="
	@grep -inE 'error|exception' "$(UI_LOG)" || echo "  (none)"

clean: ## Remove build output
	rm -rf dotnet/bin dotnet/obj $(TESTS)/bin $(TESTS)/obj
