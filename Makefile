# Makefile for WhereTheyGo.
#
# Wraps the awkward parts of this project's build: the C# toolchain is Windows-only
# and has to be driven through PowerShell from WSL, building is also deploying, and
# the running game holds a lock on the deployed DLL. See CLAUDE.md for the reasoning.
#
# Run `make` for the target list.

SHELL := /bin/bash
.SHELLFLAGS := -eu -o pipefail -c

PROJECT     := dotnet/WhereTheyGo.csproj
TESTS       := tests/WhereTheyGo.Tests
UI_MODULE   := dotnet/Presentation/UI/WhereTheyGo.mjs
CONFIG      ?= Release
OUTPUT      := dotnet/bin/$(CONFIG)/net48/WhereTheyGo.dll

# Where the game keeps its Mods folder and its logs. Read from the same user
# environment variable the csproj imports Mod.props/Mod.targets from, so no
# username is written down here and the Makefile works on anyone's machine.
# Override it in the environment if the game lives somewhere unusual.
CSII_USERDATAPATH ?= $(shell powershell.exe -NoProfile -Command "[Environment]::GetEnvironmentVariable('CSII_USERDATAPATH','User')" 2>/dev/null | tr -d '\r')
USERDATA    := $(shell wslpath -u '$(CSII_USERDATAPATH)' 2>/dev/null)
DEPLOYED    := $(USERDATA)/Mods/WhereTheyGo/WhereTheyGo.dll
MOD_LOG     := $(USERDATA)/Logs/WhereTheyGo.Mod.log
UI_LOG      := $(USERDATA)/Logs/UI.log

# The toolchain resolves CSII_TOOLPATH from the Windows user environment, so the
# build has to run as a Windows process rather than under WSL's dotnet.
DOTNET_WIN  := powershell.exe -NoProfile -Command

# Waits for Windows to release the deployed files. Must be a Windows process: WSL's
# DrvFs ignores Windows share locks, so the same probe from bash reports every file as
# free even while the game has it loaded.
UNLOCK      := powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(shell wslpath -w tools/wait-for-unlock.ps1)" -Path "$(shell wslpath -w '$(USERDATA)/Mods/WhereTheyGo')"

.DEFAULT_GOAL := help
.PHONY: help build compile debug test check-ui check-links verify strict format format-check deploy wait-for-game status logs errors clean errors-all

help: ## Show this help
	@echo "WhereTheyGo targets:"
	@grep -E '^[a-z-]+:.*?## ' $(MAKEFILE_LIST) \
		| sed -e 's/:.*## /\t/' \
		| awk -F'\t' '{ printf "  \033[36m%-14s\033[0m %s\n", $$1, $$2 }'
	@echo
	@echo "  CONFIG=Debug changes the configuration (default: Release)."

build: ## Compile and deploy to the game's Mods folder (close the game first)
	@$(UNLOCK) || { echo "Deploy refused: the game still holds the deployed files. Close it, or use 'make compile' to check the build without deploying." >&2; exit 1; }
	$(DOTNET_WIN) "dotnet build $(PROJECT) -c $(CONFIG)"

# Runs only the compiler (analyzers included): Mod.targets hooks the post-processor
# and the deploy onto AfterBuild, which -t:Compile never reaches, so the running
# game's locked DLL is left alone and bin/ stays as it was deployed.
compile: ## Compile without deploying, safe while the game is running
	$(DOTNET_WIN) "dotnet build $(PROJECT) -c $(CONFIG) -t:Compile -nologo -v:q"

debug: ## Compile and deploy the Debug configuration
	@$(MAKE) --no-print-directory build CONFIG=Debug

test: ## Run the offline test harness (exit code = number of failures)
	dotnet run --project $(TESTS)

check-ui: ## Syntax-check the UI module, which is never compiled
	node --check $(UI_MODULE)

# Locale key parity across the six locale files, every panel key the .mjs asks
# for, and the binding/trigger names in both directions. All three are matched by
# STRING across languages, so a typo is a blank row in the running game and never
# a build error. Same three checks CI runs.
check-links: ## Check the cross-file couplings no compiler sees
	python3 tools/check-consistency.py

verify: check-ui check-links test build ## Everything a change should pass before a run
	@$(MAKE) --no-print-directory status

# The compiler's TreatWarningsAsErrors covers C# diagnostics; MSBuild's own
# --warnaserror also fails on warnings raised by build tasks outside the compiler.
strict: check-ui check-links format-check ## The full gate: locked restore, no warnings from anything, tests
	@$(UNLOCK) || { echo "Refused: the game still holds the deployed files, and this target deploys. Close it first." >&2; exit 1; }
	$(DOTNET_WIN) "dotnet restore $(PROJECT) --locked-mode"
	$(DOTNET_WIN) "dotnet build $(PROJECT) -c $(CONFIG) --no-restore --warnaserror"
	dotnet build $(TESTS) --warnaserror
	dotnet run --project $(TESTS) --no-build

format: ## Apply .editorconfig formatting to both projects
	$(DOTNET_WIN) "dotnet format $(PROJECT)"
	dotnet format $(TESTS)

format-check: ## Fail if either project deviates from .editorconfig
	$(DOTNET_WIN) "dotnet format $(PROJECT) --verify-no-changes"
	dotnet format $(TESTS) --verify-no-changes

# The game locks the deployed DLL, and building IS deploying: MSBuild starts by removing
# the Mods folder, which Windows refuses while the game holds a handle on the DLLs it
# loaded. So: wait for the process to go, wait for the handles to go, build, then judge
# the result by comparing sizes.
#
# Both waits ASK rather than sleep. The old version slept 25 s after the process exited
# and 20 s between retries, which is most of a minute of nothing on a machine where the
# handles are usually free in under a second.
#
# And a failed wait ABORTS. It used to warn and build anyway, which cost a working
# deployment: MSBuild removes the Mods folder before it writes, so it deleted everything
# it could and then failed on the one file the running game still held, leaving no mod
# installed at all. There is nothing to gain by trying; the build cannot win that race.
deploy: wait-for-game ## Wait for the game to close, then build and confirm the deploy
	@$(UNLOCK) || { echo "Deploy refused: the deployed files are still held after the wait." >&2; exit 1; }
	@for attempt in 1 2 3; do \
		$(DOTNET_WIN) "dotnet build $(PROJECT) -c $(CONFIG)" || true; \
		if [[ -f "$(DEPLOYED)" && -f "$(OUTPUT)" ]] \
			&& [[ "$$(stat -c%s "$(DEPLOYED)")" == "$$(stat -c%s "$(OUTPUT)")" ]]; then \
			echo "Deployed $$(stat -c%s "$(DEPLOYED)") bytes on attempt $$attempt."; \
			exit 0; \
		fi; \
		echo "Attempt $$attempt did not land; retrying..."; \
		$(UNLOCK) || true; \
		sleep 2; \
	done; \
	echo "Deploy failed: $(DEPLOYED) does not match $(OUTPUT)." >&2; \
	exit 1

# Polls twice a second rather than every 15 s, and says so once instead of every tick.
wait-for-game:
	@if tasklist.exe 2>/dev/null | grep -qi 'Cities2.exe'; then \
		echo "Waiting for Cities: Skylines II to close..."; \
		while tasklist.exe 2>/dev/null | grep -qi 'Cities2.exe'; do sleep 0.5; done; \
		echo "Game closed."; \
	fi

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
	@echo "== WhereTheyGo.Mod.log =="
	@grep -nE 'ERROR|WARN|Exception' "$(MOD_LOG)" || echo "  (none)"
	@echo "== UI.log (this mod's lines only) =="
	@grep -inE '(error|exception|warn).*wheretheygo|wheretheygo.*(error|exception|warn)' "$(UI_LOG)" || echo "  (none)"

# UI.log is shared by the game and every installed mod, and the game's own bundle
# logs a screenful of CSS complaints at startup (1fr, 4rem, nth-of-type) that have
# nothing to do with this mod - its stylesheet uses none of those. Filtering on the
# mod's own tag is what makes the target usable; `errors-all` is still there for the
# rare case where another mod is the suspect.
errors-all: ## Every error line in UI.log, including the game's and other mods'
	@grep -inE 'error|exception' "$(UI_LOG)" || echo "  (none)"

clean: ## Remove build output and the toolchain's stray pid file
	rm -rf dotnet/bin dotnet/obj $(TESTS)/bin $(TESTS)/obj dotnet/Library
