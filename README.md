# CoreWatch

CoreWatch is a SNUS 2026 project that implements a distributed temperature monitoring system for critical industrial environments. The system simulates sensor nodes, ingests signed and encrypted measurements, stores all data in PostgreSQL, raises alarms, and calculates a simplified BFT-style consensus value from trusted measurements.

The project specification is kept in `SPECIFIKACIJA.md` and is the source of truth for the implemented requirements.

## Features

- Simulates 8 temperature sensors while keeping exactly 5 sensors active at any moment.
- Sends active sensor measurements every 1-10 seconds over HTTP/REST.
- Detects alarm priorities `1`, `2`, and `3` at the sensor level and prints them with distinct console colors.
- Stores sensors, measurements, alarms, and consensus values in PostgreSQL through Entity Framework Core.
- Marks sensors as inactive when no message is received for 10 seconds and activates standby sensors automatically.
- Supports manual 30-second sensor blocking and a DoS/rate-limit test command.
- Encrypts sensor payloads with AES and signs messages with RSA.
- Rejects replayed messages using `TimestampUtc` and monotonically increasing `MessageId` values.
- Calculates a consensus value once per minute from `GOOD` measurements only.
- Provides Docker Compose and Minikube deployment manifests.

## Architecture

CoreWatch is split into small services with a single ingress point:

- `CoreWatch.SensorClient` simulates sensors, signs/encrypts measurements, and sends them to ingress.
- `CoreWatch.Ingress` exposes the public HTTP entry point and routes traffic to internal services.
- `CoreWatch.IngestionService` receives measurements, validates security checks, persists data, monitors sensor availability, and forwards alarms.
- `CoreWatch.NotificationService` receives alarm events and broadcasts them through SignalR.
- `CoreWatch.ConsensusService` runs as a background worker and writes one consensus value per minute.
- `CoreWatch.Shared` contains shared DTOs, EF Core models, database context, and cryptography helpers.

```text
SensorClient
    |
    v
Ingress (:5080 in Docker Compose, :30080 in Minikube)
    |
    +--> IngestionService --> PostgreSQL
    |         |
    |         +--> NotificationService --> SignalR /alarms
    |
    +--> /api/sensors and /api/reports

ConsensusService --> PostgreSQL
```

## Project Structure

```text
.
├── CoreWatch.slnx
├── Dockerfile
├── docker-compose.yml
├── k8s/
│   └── core-watch.yaml
├── src/
│   ├── CoreWatch.ConsensusService/
│   ├── CoreWatch.IngestionService/
│   ├── CoreWatch.Ingress/
│   ├── CoreWatch.NotificationService/
│   ├── CoreWatch.SensorClient/
│   └── CoreWatch.Shared/
├── docs/
│   └── screenshots/
├── SECURITY.md
└── SPECIFIKACIJA.md
```

## Requirements

- .NET SDK 10.0
- Docker with Docker Compose v2
- PostgreSQL 16 when running services without Docker
- Minikube and kubectl for Kubernetes deployment

## Run With Docker Compose

Start the server-side system:

```bash
docker compose up --build
```

Start the sensor simulator in a separate terminal through the Docker Compose ingress port:

```bash
dotnet run --project src/CoreWatch.SensorClient -- http://localhost:5080
```

Useful `SensorClient` commands:

```text
/status          show sensor status
/block sensor-1  temporarily block a sensor for 30 seconds
/flood sensor-1  send more than 10 messages per second for the DoS/rate-limit test
/exit            stop the simulator
```

## Run Without Docker

Create a local PostgreSQL database named `corewatch` with username `postgres` and password `postgres`.

Run each command in a separate terminal:

```bash
dotnet run --project src/CoreWatch.NotificationService --urls http://0.0.0.0:5002
dotnet run --project src/CoreWatch.IngestionService --urls http://0.0.0.0:5001
dotnet run --project src/CoreWatch.ConsensusService
dotnet run --project src/CoreWatch.Ingress --urls http://0.0.0.0:5000
dotnet run --project src/CoreWatch.SensorClient -- http://localhost:5000
```

EF Core uses `EnsureCreated`, so the database schema is created automatically when `IngestionService` or `ConsensusService` starts.

## Run On Minikube

Point Docker to Minikube:

```bash
eval $(minikube docker-env)
```

Build the images referenced by `k8s/core-watch.yaml`:

```bash
docker build -t core-watch-ingestion:latest --build-arg PROJECT=src/CoreWatch.IngestionService/CoreWatch.IngestionService.csproj --build-arg DLL=CoreWatch.IngestionService.dll .
docker build -t core-watch-notification:latest --build-arg PROJECT=src/CoreWatch.NotificationService/CoreWatch.NotificationService.csproj --build-arg DLL=CoreWatch.NotificationService.dll .
docker build -t core-watch-consensus:latest --build-arg PROJECT=src/CoreWatch.ConsensusService/CoreWatch.ConsensusService.csproj --build-arg DLL=CoreWatch.ConsensusService.dll .
docker build -t core-watch-ingress:latest --build-arg PROJECT=src/CoreWatch.Ingress/CoreWatch.Ingress.csproj --build-arg DLL=CoreWatch.Ingress.dll .
```

Apply the manifests:

```bash
kubectl apply -f k8s/core-watch.yaml
```

The ingress service is exposed through NodePort `30080`. Start the client with the Minikube address:

```bash
dotnet run --project src/CoreWatch.SensorClient -- http://<minikube-ip>:30080
```

## API Surface

- `POST /api/ingest` accepts signed and encrypted sensor measurements.
- `GET /api/sensors` returns registered sensor state.
- `POST /api/sensors/{sensorId}/block` blocks a sensor for 30 seconds.
- `GET /api/reports` returns measurement, alarm, active sensor, and consensus counters.
- `/alarms` exposes the SignalR alarm hub through ingress.

## Database

Main tables:

- `Sensors` stores sensor configuration, activity state, keys, and replay tracking.
- `Measurements` stores raw sensor measurements and consensus measurements.
- `Alarms` stores alarm events raised by measurements.
- `ConsensusValues` stores one calculated consensus value per minute.

## Demonstrating The Specification

- Start Docker Compose and run `SensorClient`; `/status` shows exactly 5 active sensors.
- Run `/block sensor-1`; a standby sensor becomes active while the blocked sensor is unavailable for 30 seconds.
- Observe colored sensor output for alarm priorities `1`, `2`, and `3`.
- Run `/flood sensor-1`; the ingestion service temporarily blocks the sensor after more than 10 messages in one second.
- Wait at least one minute; `ConsensusService` writes a consensus row to PostgreSQL.
- Inspect `/api/reports` or the database tables to confirm persisted measurements, alarms, and consensus values.

Screenshots of the running system are stored in `docs/screenshots/`.

## Security

The secure communication design is documented in `SECURITY.md`. It covers AES payload encryption, RSA signatures, replay protection, DoS/rate limiting, concrete network addressing, and known demo limitations.
