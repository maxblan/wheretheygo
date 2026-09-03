# Verification pipeline. See README.md in this directory.

.PHONY: help bootstrap lean gen build-subject verify-all instance clean

help:
	@echo "targets:"
	@echo "  bootstrap      install the project-local toolchain (SCIP exact + viprchk)"
	@echo "  lean           install Lean 4 (pinned) and build the VERIFIED certificate checker"
	@echo "  gen            regenerate the canonical instances (must be byte-identical)"
	@echo "  build-subject  build the C# subject runner"
	@echo "  verify-all     run the full pipeline over every instance (exit 0 = all pass)"
	@echo "  instance I=x   run one instance, e.g. make instance I=sites-greedy-gap"
	@echo "  clean          remove run artifacts (keeps the toolchain)"

bootstrap:
	./bootstrap.sh

lean:
	./bootstrap-lean.sh

gen:
	python3 generators/gen_instances.py

build-subject:
	dotnet build -c Release subject/Subject.csproj

verify-all: build-subject
	python3 run.py

instance: build-subject
	python3 run.py $(I)

clean:
	rm -rf runs
