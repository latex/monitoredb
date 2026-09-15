.PHONY: build-server build-agent dev up down logs publish-server install-server-linux uninstall-server-linux

RID ?= $(shell uname -m | sed 's/x86_64/linux-x64/;s/aarch64/linux-arm64/')

build-server:
	cargo build --release -p monitoredb-server

build-agent:
	cargo build --release -p monitoredb-agent

dev-server:
	cargo run -p monitoredb-server

dev-agent:
	cargo run -p monitoredb-agent -- --server http://localhost:3000

docker-build:
	docker build -f Dockerfile.server -t monitoredb-server:latest .
	docker build -f Dockerfile.agent -t monitoredb-agent:latest .

up:
	docker compose up -d

down:
	docker compose down

logs:
	docker compose logs -f

clean:
	cargo clean

# --- Servidor .NET / Linux ---------------------------------------------------
publish-server:
	dotnet publish monitoredb.dotnet/Monitoredb.ApiServer/Monitoredb.ApiServer.csproj \
		-c Release -r $(RID) --self-contained false \
		-p:SatelliteResourceLanguages=en -p:DebugType=None \
		-o publish/server

install-server-linux:
	sudo ./scripts/install-server-linux.sh

uninstall-server-linux:
	sudo ./scripts/uninstall-server-linux.sh
