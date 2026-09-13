.PHONY: build-server build-agent dev up down logs

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
