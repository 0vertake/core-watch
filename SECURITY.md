# Security Notes

`SPECIFIKACIJA.md` requires reliable client-server communication, encrypted and digitally signed messages, replay protection, DoS resilience, and documentation of risks introduced by using concrete network addresses. This file documents how CoreWatch satisfies those requirements.

## Network Model

All client-server communication uses HTTP/REST through `CoreWatch.Ingress` instead of calling every service directly.

- Docker Compose exposes ingress on `http://localhost:5080`.
- Local non-Docker runs expose ingress on `http://localhost:5000`.
- Minikube exposes ingress through NodePort `30080`.
- Internal service-to-service communication uses Docker or Kubernetes DNS names such as `http://ingestion:5001` and `http://notification:5002`.

## Payload Confidentiality

`SensorClient` serializes each measurement payload to JSON and encrypts it with AES before sending it to `IngestionService`.

The demo AES key is `core-watch-student-demo-key` and can be configured through:

- `Security__AesKey` for `IngestionService`
- `COREWATCH_AES_KEY` for `SensorClient`
- `core-watch-secret` in the Kubernetes manifest

The shared key is acceptable for the project demonstration. A production deployment should store the key in a managed secret store and rotate it regularly.

## Sender Identity

Every simulated sensor owns an RSA key pair. The client signs the security envelope fields below:

```text
SensorId|MessageId|TimestampUtc|Iv|EncryptedPayload
```

`IngestionService` verifies the RSA signature with the public key from the envelope before decrypting the payload. If the signature is invalid, the message is rejected.

## Payload Integrity

After decryption, `IngestionService` verifies that the decrypted payload belongs to the same `SensorId` as the envelope. This prevents a valid envelope from being reused with a mismatched payload.

## Replay Protection

Each envelope contains:

- `TimestampUtc`, the send time
- `MessageId`, a monotonically increasing per-sensor message identifier

The ingestion service rejects messages when the timestamp is outside the allowed freshness window or when `MessageId` is less than or equal to the last accepted message ID for that sensor.

## DoS Protection

`IngestionService` applies a per-sensor in-memory rate limiter. If one `SensorId` sends more than 10 messages in a one-second window:

- the sensor is deactivated,
- `BlockedUntilUtc` is set for 30 seconds,
- the service attempts to activate a standby sensor to keep 5 sensors active.

This satisfies the project requirement without adding an external rate-limit package.

## Known Demo Limitations

- HTTP does not provide transport encryption. A production deployment should terminate TLS at ingress.
- The AES key is intentionally visible in demo configuration. Production should use secret management and key rotation.
- Sensor public keys are accepted from incoming envelopes for simpler demonstration. Production should pre-register public keys or certificates.
- Rate limiting is in-memory per `IngestionService` instance. A multi-replica deployment should use shared storage or gateway-level rate limiting.
- PostgreSQL demo credentials are static. Production should use stronger credentials and isolated secrets.
