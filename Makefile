.PHONY: sandbox build up down logs example example-file test

# Build the untrusted-code sandbox image on the host (required before `up`).
sandbox:
	docker build -t agent-sandbox:latest -f sandbox/Dockerfile.sandbox sandbox

# Compile the .NET solution locally (requires the .NET 8 SDK).
build:
	dotnet build AgentSandbox.sln -c Release

up: sandbox
	docker compose up -d --build

down:
	docker compose down

logs:
	docker compose logs -f api worker

# Start a pure-math run.
example:
	curl -s -X POST http://localhost:8000/runs \
		-F 'task=Compute the eigenvalues of [[2,1],[1,2]] and explain what they mean.' \
		-F 'tenant=acme' | tee /dev/stderr | \
		python3 -c 'import sys,json;print("\nrun:",json.load(sys.stdin)["run_id"])'

# Start a data-analysis run with an uploaded CSV.
example-file:
	curl -s -X POST http://localhost:8000/runs \
		-F 'task=Find the correlation between columns x and y in data.csv.' \
		-F 'tenant=acme' \
		-F 'files=@examples/data.csv' | tee /dev/stderr

test:
	dotnet test AgentSandbox.sln
