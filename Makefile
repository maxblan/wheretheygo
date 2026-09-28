# Makefile for WhereTheyGo.
#
# Wraps the awkward parts of this project's build: the C# toolchain is Windows-only,
# building is also deploying, and the running game holds a lock on the deployed DLL.
# See CLAUDE.md for the reasoning behind each rule below.
#
# Where it runs:
#   - Windows, with GNU Make; the recipes go through PowerShell.
#   - WSL, driving the Windows dotnet through powershell.exe.
#   - Linux without Windows: the offline half only (test, check-ui, check-links, and
#     the test project's format). The mod project reads CSII_TOOLPATH from the Windows
#     user environment, and a Linux dotnet gets the csproj's editor-only fallback, which
#     warns (WTG0002), deploys nothing and cannot produce a mod. The targets that need
#     the mod project refuse here rather than pretend.
#
# Run `make` for the target list.

PROJECT     := dotnet/WhereTheyGo.csproj
TESTS       := tests/WhereTheyGo.Tests
UI_MODULE   := dotnet/Presentation/UI/WhereTheyGo.mjs
CONFIG      ?= Release
OUTPUT      := dotnet/bin/$(CONFIG)/net48/WhereTheyGo.dll

# Said in one place because three targets say it. No apostrophes: it is quoted by
# bash on one platform and by PowerShell on the other.
UNLOCK_REFUSED := Deploy refused: the game still holds the deployed files. Close it, or use make compile to check the build without deploying.
MOD_NEEDS_WINDOWS := the mod project needs the Windows modding toolchain (CSII_TOOLPATH), so it builds on Windows or from WSL only. On Linux the offline half runs: make test, check-ui, check-links, format.


# ---------------------------------------------------------------------------
# Platform detection
# ---------------------------------------------------------------------------

ifeq ($(OS),Windows_NT)

PLATFORM := windows
PYTHON   ?= python

# GNU Make on Windows takes sh.exe when one is on PATH (Git for Windows, MSYS2) and
# cmd.exe otherwise. Pin it, so a recipe behaves the same on every machine; every
# recipe in this branch then goes through PowerShell, so nothing depends on cmd syntax.
SHELL := cmd.exe
.SHELLFLAGS := /C

# Where the game keeps its Mods folder and its logs: the same user environment
# variable the csproj imports Mod.props/Mod.targets from, so no username is written
# down here. Override it in the environment if the game lives somewhere unusual.
CSII_USERDATAPATH ?= $(shell powershell.exe -NoProfile -Command "[Environment]::GetEnvironmentVariable('CSII_USERDATAPATH','User')")
USERDATA      := $(CSII_USERDATAPATH)
MODS_DIR      := $(USERDATA)/Mods/WhereTheyGo
UNLOCK_SCRIPT := tools/wait-for-unlock.ps1

MAIN_BUILD        = dotnet build "$(PROJECT)" -c "$(CONFIG)"
MAIN_COMPILE      = dotnet build "$(PROJECT)" -c "$(CONFIG)" -t:Compile -nologo -v:q
MAIN_RESTORE      = dotnet restore "$(PROJECT)" --locked-mode
MAIN_STRICT_BUILD = dotnet build "$(PROJECT)" -c "$(CONFIG)" --no-restore --warnaserror
MAIN_FORMAT       = dotnet format "$(PROJECT)"
MAIN_FORMAT_CHECK = dotnet format "$(PROJECT)" --verify-no-changes

else

SHELL := /bin/bash
.SHELLFLAGS := -eu -o pipefail -c

PYTHON ?= python3

IS_WSL := $(shell grep -qi microsoft /proc/version 2>/dev/null && echo 1 || echo 0)

ifeq ($(IS_WSL),1)

PLATFORM := wsl

CSII_USERDATAPATH ?= $(shell powershell.exe -NoProfile -Command "[Environment]::GetEnvironmentVariable('CSII_USERDATAPATH','User')" 2>/dev/null | tr -d '\r')
USERDATA := $(shell wslpath -u '$(CSII_USERDATAPATH)' 2>/dev/null)
MODS_DIR := $(USERDATA)/Mods/WhereTheyGo

# Waits for Windows to release the deployed files. Must be a WINDOWS process: WSL's
# DrvFs ignores Windows share locks, so the same probe from bash reports every file as
# free even while the game has it loaded.
UNLOCK_PROBE := powershell.exe -NoProfile -ExecutionPolicy Bypass \
	-File "$(shell wslpath -w tools/wait-for-unlock.ps1)" \
	-Path "$(shell wslpath -w '$(MODS_DIR)')"

# The toolchain resolves CSII_TOOLPATH from the Windows user environment, so the mod
# project has to build as a Windows process rather than under WSL's dotnet.
DOTNET_WIN := powershell.exe -NoProfile -Command

MAIN_BUILD        = $(DOTNET_WIN) "dotnet build $(PROJECT) -c $(CONFIG)"
MAIN_COMPILE      = $(DOTNET_WIN) "dotnet build $(PROJECT) -c $(CONFIG) -t:Compile -nologo -v:q"
MAIN_RESTORE      = $(DOTNET_WIN) "dotnet restore $(PROJECT) --locked-mode"
MAIN_STRICT_BUILD = $(DOTNET_WIN) "dotnet build $(PROJECT) -c $(CONFIG) --no-restore --warnaserror"
MAIN_FORMAT       = $(DOTNET_WIN) "dotnet format $(PROJECT)"
MAIN_FORMAT_CHECK = $(DOTNET_WIN) "dotnet format $(PROJECT) --verify-no-changes"

else

PLATFORM := linux

# Only the log and status targets can use this here, and it has to be given:
#
#   make errors CSII_USERDATAPATH=/path/to/the/game's/userdata
#
CSII_USERDATAPATH ?=
USERDATA := $(CSII_USERDATAPATH)
MODS_DIR := $(USERDATA)/Mods/WhereTheyGo

# Every command that needs the mod project refuses, with the reason. A Linux dotnet
# would run the csproj's editor-only fallback, whose DLL must never stand in for the
# deployable one (CLAUDE.md, "Commands").
REFUSE_MOD := { echo "Refused: $(MOD_NEEDS_WINDOWS)" >&2; exit 1; }

MAIN_BUILD        = $(REFUSE_MOD)
MAIN_COMPILE      = $(REFUSE_MOD)
MAIN_RESTORE      = $(REFUSE_MOD)
MAIN_STRICT_BUILD = $(REFUSE_MOD)
MAIN_FORMAT       = echo "Skipping the mod project: $(MOD_NEEDS_WINDOWS)"
MAIN_FORMAT_CHECK = echo "Skipping the mod project: $(MOD_NEEDS_WINDOWS)"

endif
endif

DEPLOYED := $(MODS_DIR)/WhereTheyGo.dll
MOD_LOG  := $(USERDATA)/Logs/WhereTheyGo.Mod.log
UI_LOG   := $(USERDATA)/Logs/UI.log


# ---------------------------------------------------------------------------
# The targets, once each. This block is the table `make` prints (the `##` text) and
# holds every target's prerequisites. The recipes follow further down, one per
# platform where they differ, and carry no `##`, so each target is listed once.
#
# Prerequisites are ORDERED on purpose: wait for the game process to go, then for
# the handles to go, then build. .NOTPARALLEL keeps that order under -j.
# ---------------------------------------------------------------------------

.DEFAULT_GOAL := help
.NOTPARALLEL:
.PHONY: help platform require-userdata unlock wait-for-game build compile debug test \
	check-ui check-links verify strict format format-check deploy status logs errors \
	errors-all clean

help: ## Show this help
platform: ## Show the detected platform (windows, wsl or linux)
build: unlock ## Compile and deploy to the game's Mods folder (close the game first)
compile: ## Compile without deploying, safe while the game is running
debug: ## Compile and deploy the Debug configuration
test: ## Run the offline test harness (exit code = number of failures)
check-ui: ## Syntax-check the UI module, which is never compiled
check-links: ## Check the cross-file couplings no compiler sees
verify: check-ui check-links test build ## Everything a change should pass before a run
strict: check-ui check-links format-check unlock ## The full gate: locked restore, no warnings from anything, tests
format: ## Apply .editorconfig formatting to both projects
format-check: ## Fail if either project deviates from .editorconfig
deploy: wait-for-game unlock ## Wait for the game to close, then build and confirm the deploy
status: require-userdata ## Compare the built DLL against the deployed one
logs: require-userdata ## Follow the mod log
errors: require-userdata ## Show warnings and errors from the mod and UI logs
errors-all: require-userdata ## Every error line in UI.log, including the game's and other mods'
clean: ## Remove build output


# ---------------------------------------------------------------------------
# Recipes shared by every platform
# ---------------------------------------------------------------------------

platform:
	@echo $(PLATFORM)

# Runs only the compiler (analyzers included): Mod.targets hooks the post-processor
# and the deploy onto AfterBuild, which -t:Compile never reaches, so the running
# game's locked DLL is left alone and bin/ stays as it was deployed.
compile:
	$(MAIN_COMPILE)

build:
	$(MAIN_BUILD)

debug:
	@$(MAKE) --no-print-directory build CONFIG=Debug

test:
	dotnet run --project "$(TESTS)"

check-ui:
	node --check "$(UI_MODULE)"

# Locale key parity across the six locale files, every panel key the .mjs asks
# for, and the binding/trigger names in both directions. All three are matched by
# STRING across languages, so a typo is a blank row in the running game and never
# a build error. Same three checks CI runs.
check-links:
	$(PYTHON) tools/check-consistency.py

verify:
	@$(MAKE) --no-print-directory status

# The compiler's TreatWarningsAsErrors covers C# diagnostics; MSBuild's own
# --warnaserror also fails on warnings raised by build tasks outside the compiler.
strict:
	$(MAIN_RESTORE)
	$(MAIN_STRICT_BUILD)
	dotnet build "$(TESTS)" --warnaserror
	dotnet run --project "$(TESTS)" --no-build

format:
	$(MAIN_FORMAT)
	dotnet format "$(TESTS)"

format-check:
	$(MAIN_FORMAT_CHECK)
	dotnet format "$(TESTS)" --verify-no-changes


# ---------------------------------------------------------------------------
# The unlock probe, and why a failed wait ABORTS
#
# The game locks the deployed DLL, and building IS deploying: MSBuild starts by
# removing the Mods folder, which Windows refuses while the game holds a handle on
# the DLLs it loaded. Building anyway once cost a working deployment: MSBuild deleted
# everything it could, failed on the one file the game still held, and left no mod
# installed at all. There is nothing to gain by trying; the build cannot win that race.
# ---------------------------------------------------------------------------

ifeq ($(PLATFORM),windows)

require-userdata:
	@powershell.exe -NoProfile -Command "if ([string]::IsNullOrWhiteSpace('$(USERDATA)')) { [Console]::Error.WriteLine('CSII_USERDATAPATH is not configured.'); exit 1 }"

unlock: require-userdata
	@powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& '$(UNLOCK_SCRIPT)' -Path '$(MODS_DIR)'; if ($$LASTEXITCODE -ne 0) { [Console]::Error.WriteLine('$(UNLOCK_REFUSED)'); exit 1 }"

else ifeq ($(PLATFORM),wsl)

require-userdata:
	@if [[ -z "$(strip $(USERDATA))" ]]; then \
		echo "CSII_USERDATAPATH is not configured." >&2; \
		exit 1; \
	fi

unlock: require-userdata
	@$(UNLOCK_PROBE) || { echo "$(UNLOCK_REFUSED)" >&2; exit 1; }

else

require-userdata:
	@if [[ -z "$(strip $(USERDATA))" ]]; then \
		echo "CSII_USERDATAPATH is not configured." >&2; \
		exit 1; \
	fi

unlock:
	@$(REFUSE_MOD)

endif


# ---------------------------------------------------------------------------
# Wait for Cities: Skylines II
#
# Polls twice a second rather than every 15 s, and says so once instead of every tick.
# ---------------------------------------------------------------------------

ifeq ($(PLATFORM),windows)

wait-for-game:
	@powershell.exe -NoProfile -Command "$$shown = $$false; while (Get-Process -Name Cities2 -ErrorAction SilentlyContinue) { if (-not $$shown) { Write-Host 'Waiting for Cities: Skylines II to close...'; $$shown = $$true }; Start-Sleep -Milliseconds 500 }; if ($$shown) { Write-Host 'Game closed.' }"

else ifeq ($(PLATFORM),wsl)

wait-for-game:
	@if tasklist.exe 2>/dev/null | grep -qi 'Cities2.exe'; then \
		echo "Waiting for Cities: Skylines II to close..."; \
		while tasklist.exe 2>/dev/null | grep -qi 'Cities2.exe'; do sleep 0.5; done; \
		echo "Game closed."; \
	fi

else

# -x matches the process NAME in full (Cities2 native, Cities2.exe under Proton).
# Not -f: that searches whole command lines, and the shell running this recipe has
# the pattern in its own, so it would find itself and wait forever.
wait-for-game:
	@if pgrep -x 'Cities2(\.exe)?' >/dev/null; then \
		echo "Waiting for Cities: Skylines II to close..."; \
		while pgrep -x 'Cities2(\.exe)?' >/dev/null; do sleep 0.5; done; \
		echo "Game closed."; \
	fi

endif


# ---------------------------------------------------------------------------
# Deploy
#
# Judged by comparing the built and deployed sizes, not by the exit code: the
# post-processor returns a spurious code when it runs before the game has released
# its handles. Both waits ASK rather than sleep; the handles are usually free in
# under a second.
# ---------------------------------------------------------------------------

ifeq ($(PLATFORM),windows)

deploy:
	@powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$$deployed = '$(DEPLOYED)'; $$output = '$(OUTPUT)'; for ($$attempt = 1; $$attempt -le 3; $$attempt++) { & dotnet build '$(PROJECT)' -c '$(CONFIG)'; if ((Test-Path -LiteralPath $$deployed) -and (Test-Path -LiteralPath $$output)) { $$deployedSize = (Get-Item -LiteralPath $$deployed).Length; $$outputSize = (Get-Item -LiteralPath $$output).Length; if ($$deployedSize -eq $$outputSize) { Write-Host ('Deployed {0} bytes on attempt {1}.' -f $$deployedSize, $$attempt); exit 0 } }; Write-Host ('Attempt {0} did not land; retrying...' -f $$attempt); & '$(UNLOCK_SCRIPT)' -Path '$(MODS_DIR)'; Start-Sleep -Seconds 2 }; [Console]::Error.WriteLine('Deploy failed: $(DEPLOYED) does not match $(OUTPUT).'); exit 1"

else ifeq ($(PLATFORM),wsl)

deploy:
	@for attempt in 1 2 3; do \
		$(MAIN_BUILD) || true; \
		if [[ -f "$(DEPLOYED)" && -f "$(OUTPUT)" ]] \
			&& [[ "$$(stat -c%s "$(DEPLOYED)")" == "$$(stat -c%s "$(OUTPUT)")" ]]; then \
			echo "Deployed $$(stat -c%s "$(DEPLOYED)") bytes on attempt $$attempt."; \
			exit 0; \
		fi; \
		echo "Attempt $$attempt did not land; retrying..."; \
		$(UNLOCK_PROBE) || true; \
		sleep 2; \
	done; \
	echo "Deploy failed: $(DEPLOYED) does not match $(OUTPUT)." >&2; \
	exit 1

else

deploy:
	@$(REFUSE_MOD)

endif


# ---------------------------------------------------------------------------
# Status and logs
#
# UI.log is shared by the game and every installed mod, and the game's own bundle
# logs a screenful of CSS complaints at startup (1fr, 4rem, nth-of-type) that have
# nothing to do with this mod; its stylesheet uses none of those. Filtering on the
# mod's own tag is what makes `errors` usable; `errors-all` is still there for the
# rare case where another mod is the suspect. The mod-log half is case-SENSITIVE on
# both platforms: the log writes its levels in capitals, and a lower-case "error" is
# ordinary prose.
# ---------------------------------------------------------------------------

ifeq ($(PLATFORM),windows)

status:
	@powershell.exe -NoProfile -Command "$$output = '$(OUTPUT)'; $$deployed = '$(DEPLOYED)'; if (-not (Test-Path -LiteralPath $$output)) { Write-Host 'Not built: $(OUTPUT)'; exit 1 }; $$builtSize = (Get-Item -LiteralPath $$output).Length; Write-Host ('built    {0} bytes  {1}' -f $$builtSize, $$output); if (Test-Path -LiteralPath $$deployed) { $$deployedSize = (Get-Item -LiteralPath $$deployed).Length; Write-Host ('deployed {0} bytes  {1}' -f $$deployedSize, $$deployed); if ($$builtSize -eq $$deployedSize) { Write-Host '-> up to date' } else { Write-Host '-> STALE, run make deploy' } } else { Write-Host 'deployed (absent)' }"

logs:
	@powershell.exe -NoProfile -Command "Get-Content -LiteralPath '$(MOD_LOG)' -Wait"

errors:
	@powershell.exe -NoProfile -Command "Write-Host '== WhereTheyGo.Mod.log =='; if (Test-Path -LiteralPath '$(MOD_LOG)') { $$hits = Select-String -LiteralPath '$(MOD_LOG)' -CaseSensitive -Pattern 'ERROR|WARN|Exception'; if ($$hits) { $$hits } else { Write-Host '  (none)' } } else { Write-Host '  (file absent)' }; Write-Host '== UI.log (this mod only) =='; if (Test-Path -LiteralPath '$(UI_LOG)') { $$hits = Select-String -LiteralPath '$(UI_LOG)' -Pattern '(error|exception|warn).*wheretheygo|wheretheygo.*(error|exception|warn)'; if ($$hits) { $$hits } else { Write-Host '  (none)' } } else { Write-Host '  (file absent)' }"

errors-all:
	@powershell.exe -NoProfile -Command "if (Test-Path -LiteralPath '$(UI_LOG)') { $$hits = Select-String -LiteralPath '$(UI_LOG)' -Pattern 'error|exception'; if ($$hits) { $$hits } else { Write-Host '  (none)' } } else { Write-Host '  (file absent)' }"

clean:
	@powershell.exe -NoProfile -Command "Remove-Item -Recurse -Force -ErrorAction SilentlyContinue 'dotnet/bin','dotnet/obj','$(TESTS)/bin','$(TESTS)/obj','dotnet/Library'"

else

status:
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

logs:
	@tail -f "$(MOD_LOG)"

errors:
	@echo "== WhereTheyGo.Mod.log =="
	@grep -nE 'ERROR|WARN|Exception' "$(MOD_LOG)" || echo "  (none)"
	@echo "== UI.log (this mod's lines only) =="
	@grep -inE '(error|exception|warn).*wheretheygo|wheretheygo.*(error|exception|warn)' "$(UI_LOG)" || echo "  (none)"

errors-all:
	@grep -inE 'error|exception' "$(UI_LOG)" || echo "  (none)"

clean:
	rm -rf dotnet/bin dotnet/obj "$(TESTS)/bin" "$(TESTS)/obj" dotnet/Library

endif


# ---------------------------------------------------------------------------
# Help: the `##` lines of the target index above, in their order.
# ---------------------------------------------------------------------------

ifeq ($(PLATFORM),windows)

help:
	@powershell.exe -NoProfile -Command "Write-Host 'WhereTheyGo targets:'; Get-Content -LiteralPath '$(firstword $(MAKEFILE_LIST))' | ForEach-Object { if ($$_ -match '^([a-z-]+):.*?## (.*)$$') { '  {0,-14} {1}' -f $$Matches[1], $$Matches[2] } }; Write-Host ''; Write-Host '  CONFIG=Debug changes the configuration (default: Release).'; Write-Host '  Platform: $(PLATFORM)'"

else

help:
	@echo "WhereTheyGo targets:"
	@grep -E '^[a-z-]+:.*?## ' $(firstword $(MAKEFILE_LIST)) \
		| sed -e 's/:.*## /\t/' \
		| awk -F'\t' '{ printf "  \033[36m%-14s\033[0m %s\n", $$1, $$2 }'
	@echo
	@echo "  CONFIG=Debug changes the configuration (default: Release)."
	@echo "  Platform: $(PLATFORM)"

endif
