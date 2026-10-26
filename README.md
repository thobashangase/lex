# Little Shop

A demo online shop built in the provided folders: .NET 10 Web API, Angular 21 standalone frontend, Bootstrap 5, EF Core and SQLite, with RabbitMQ for serialized writes and Redis for reference-data caching. Angular 21 supports the installed Node 22.14 runtime ([compatibility](https://angular.dev/reference/versions)).

## Run with Docker (quick start)

With Docker Desktop running (or Docker Engine with Compose v2.17+), run from the repository root:

```sh
docker compose up --build -d --wait
```

Open http://localhost:8090. Docker builds both apps, starts RabbitMQ and Redis, seeds the catalog through the write consumer, and waits for healthy services. Only Docker is required on your machine; the SDK and Node dependencies are installed in build containers. The first build downloads images and dependencies and requires internet access.

The frontend serves the Angular production build through nginx and forwards `/api` to the API on the internal Compose network. SQLite data lives in the `shop-data` named volume and survives container rebuilds and shutdowns. RabbitMQ commands persist in the `rabbitmq-data` named volume, with a stable broker hostname across container rebuilds. The broker and Redis are only accessible on the internal Compose network. Redis is a disposable cache with a 64 MB memory limit and no data volume; SQLite remains the source of truth. The API runs as the non-root `app` user and is not published directly to the host.

```sh
docker compose logs -f     # Follow logs; Ctrl+C exits the log viewer
docker compose down        # Stop and remove containers, retaining orders
```

For a different port, use `SHOP_PORT=8090 docker compose up --build -d --wait` and open http://localhost:8090. Run the startup command again after source changes to rebuild the images.

To reset the demo, `docker compose down --volumes` deletes the SQLite and RabbitMQ volumes **including all orders and queued commands**. Start again to recreate and seed the database. Container data is separate from the database used by the manual setup below.

## Run locally without Docker

Prerequisites: .NET SDK 10, Node.js 22.12+ within the 22.x line, npm, and a running RabbitMQ broker. Redis defaults to `localhost:6379`; override with `Redis__ConnectionString` (a StackExchange.Redis connection string). Redis is optional for availability: reads fall back to SQLite when it is unavailable. The API defaults to localhost:5672, virtual host `/`, username `shop` and password `shop-demo-password`. Configure other connections using `RabbitMq__Host`, `RabbitMq__Port`, `RabbitMq__VirtualHost`, `RabbitMq__Username` and `RabbitMq__Password`. These default credentials are for the demo.

If you do not have RabbitMQ installed, start a local broker in another terminal:

```sh
docker run --rm -p 127.0.0.1:5672:5672 -e RABBITMQ_DEFAULT_USER=shop -e RABBITMQ_DEFAULT_PASS=shop-demo-password rabbitmq:4-management-alpine
```

From the repository root:

```sh
dotnet restore Shop.sln
dotnet run --project src/backend/Shop.Api
```

In a second terminal:

```sh
cd src/frontend
npm ci
npm start
```

Open http://localhost:4200. The Angular development server forwards `/api` to http://localhost:5050. To deploy, serve the built frontend and reverse proxy `/api` to the backend under the same origin.

```sh
dotnet test Shop.sln
cd src/frontend
npm run build
```

## Demo behaviour

- Browse and search six seeded products, filter categories, adjust quantities (1–99), remove items, and review a server-calculated quote before checking out.
- Prices use ZAR and integer cents. Prices include applicable taxes; no separate tax is added. Delivery costs R65, with free delivery from R1,000 inclusive. No real payment or customer address is collected.
- Statuses and permitted transitions live in the `OrderStatuses` and `OrderStatusTransitions` lookup tables, rather than an enum. Orders use `Pending`, `Confirmed`, `Fulfilled` and `Cancelled` statuses. New checkouts start at `Pending`. My orders shows newest first, with item prices, delivery, total and colour-coded status; Refresh status fetches the current stored status. Pending and Confirmed orders offer a Cancel order button. Repeating cancellation returns the same cancelled order; Fulfilled orders return 409 and cannot be cancelled. Cancellation preserves item prices and totals for reference; no real payment or refund occurs. The Change status selector allows Pending → Confirmed and Confirmed → Fulfilled, or cancellation from either active state. Fulfilled and Cancelled are final and cannot be reopened; retrying the same status is a successful no-op. The API validates transitions and detects concurrent changes (409). Status controls are available to the shared demo user; there is no authentication or staff-role separation.
- Everyone uses the same seeded demo customer, Alex (`demo-customer`). There is no authentication: customer identity is fixed on the server. Do not use this demo for private customer data.
- SQLite persists orders across restarts and enforces a unique `(CustomerId, SubmissionKey)` index. The serialized command worker atomically commits the order, its lines and a processed-command receipt. Prices and item names are snapshotted.
- Checkout requires an `Idempotency-Key` UUID header. Repeating it with the same product IDs and quantities returns the original order (200, versus 201 for a new order). Reusing it for a changed cart returns 409. Sorting cart lines does not change the fingerprint. Concurrent submissions are protected by the database constraint.
- The browser stores an in-flight key and cart in localStorage before submission, retains them on network/server errors and reloads, and locks cart edits until a retry confirms the result. Successful checkout clears them. Definitive client errors unlock the cart. Distinct keys represent distinct purchase attempts; identical carts with different keys are permitted.
- Minimal API routes are grouped in `ShopEndpoints` and registered through `app.MapShopEndpoints()` in `Program.cs`. Request binding and business rules return Problem Details for invalid input. The frontend displays validation and connection errors and success messages. Catalog price values in client requests are never trusted.

## Redis reference-data cache

The product catalogue and order status/transition lookup data use cache-aside reads: the first call loads SQLite and populates Redis. Subsequent product/status requests and cart quotes use those cached datasets. Customer orders, checkout pricing and status transition validation remain uncached and authoritative, so stale cached prices or rules cannot authorize a write.

Entries have an absolute one-hour TTL. A background worker also refreshes warmed datasets once their hour is up (checked every five seconds); datasets that have never been requested stay cold. If an expired key is requested sooner, that request reloads it immediately. A per-dataset gate coalesces concurrent first reads. Each application process starts a fresh cache namespace on restart, so old process entries are never reused and the first read reloads current data. Old keys expire naturally within an hour; Redis is not globally flushed.

SQLite triggers transactionally increment durable counters when Products, OrderStatuses or OrderStatusTransitions are inserted, updated or deleted. After a queued write commits, the cache checks these counters and immediately refreshes affected warmed datasets. A single small two-row counter query every five seconds also catches raw SQL or external database edits without querying the full catalogue on each request. External edits may therefore take about five seconds to appear in cached reads. No-op order changes and new orders do not invalidate reference data. The trigger/counter schema is added in place to existing databases.

Redis connection errors, missing/evicted keys and invalid cached JSON fall back to SQLite. Cache writes and refreshes cannot turn a committed order write into a failed order response. Changing a dataset's cache generation prevents an old value reappearing after a Redis outage or failed invalidation. During an outage, requests cause more SQLite reads; Redis reconnects and entries repopulate automatically. This cache uses the demo's single-API-instance topology, not a distributed locking scheme.

Optional local Redis for the manual setup:

```sh
docker run --rm -p 127.0.0.1:6379:6379 redis:7-alpine
```

Inspect keys and TTLs in Compose:

```sh
docker compose exec redis redis-cli --scan --pattern 'shop:reference:*'
# Use a key printed above:
docker compose exec redis redis-cli TTL '<key>'
```

## Queued database writes

All application write endpoints (checkout, cancellation and status changes) publish commands to RabbitMQ; they never call the database write service directly. Schema initialization and demo seeding run in the same consumer before the API serves requests. Product/status lookup reads and cart quotes use the Redis reference cache. Customer orders and authoritative write validation still query SQLite directly.

`shop.database-writes.v1` is durable, uses persistent messages and publisher confirms, and enables a single active consumer with prefetch 1. The consumer completes one command before receiving another, including when other consumers are connected. This serializes application writes and reduces SQLite lock contention. It does not prevent locks from external SQL tools or other applications. Keep a single API instance for this demo; any future consumer replicas must share the same SQLite database, not separate container files.

HTTP requests wait up to 25 seconds for a correlated reply and retain the existing 200/201/400/404/409 responses. Success is returned after database commit. A broker outage, publish failure or reply timeout returns 503. A timeout is an uncertain result: the queued command may still complete. Refresh orders before retrying status changes; checkout retries must retain the original `Idempotency-Key` (the frontend already does this).

The worker commits the mutation and a `ProcessedWriteCommands` receipt together. RabbitMQ redelivery after a lost acknowledgment returns that receipt rather than applying the mutation again. Checkout submission keys provide a second layer of deduplication across separate HTTP requests. Business validation errors are saved as terminal replies. Infrastructure failures are retried three times with a fresh EF scope; exhausted or malformed commands are rejected to `shop.database-writes.failed.v1` for inspection. Failed commands do not replay automatically from the failed queue. After fixing the cause, an operator can inspect/requeue them; first check whether the associated order already completed. Single-node RabbitMQ and its failed queue are demo infrastructure, not a highly available deployment.

The API's `/api/health` endpoint includes queue connection readiness. The .NET client reconnects automatically after broker connection failures; durable commands can be redelivered. There is no in-process direct-write fallback. Command receipts retain original order response snapshots and are not automatically pruned; introduce retention appropriate to the maximum replay window before production use.

Inspect queue state:

```sh
docker compose exec rabbitmq rabbitmqctl list_queues name messages_ready messages_unacknowledged consumers
```

## Storage and seeding

SQLite was chosen over EF InMemory for persistence and real relational constraints/transactions. On startup `SeedData.Initialize` creates the schema with `EnsureCreated` and inserts the catalog only when empty. It also adds four past demo orders (2, 5, 12 and 20 days before first seeding), one for each status, using fixed IDs. Existing databases receive the missing examples on restart. Repeated seeding preserves existing orders, dates and cancellations. The database defaults to `src/backend/Shop.Api/shop.db` when launched with the command above. Override with `ConnectionStrings__Shop` (for example `Data Source=/absolute/path/shop.db;Default Timeout=30`).

To reset the demo, stop the API and delete its `shop.db` and any `shop.db-wal` / `shop.db-shm` companion files; restarting recreates the catalog. This deletes orders. `EnsureCreated` is intentional for the demo. This update includes an idempotent, transactional upgrade that adds status lookup tables to older databases without deleting orders or changing their text status values. New databases have a status foreign key; legacy databases receive SQLite triggers enforcing the same status lookup integrity without rebuilding the orders table. Other schema changes may require a database reset. Production should use reviewed EF migrations, authentication, customer ownership checks, payment integration and inventory management.

## API

| Method | Path | Behaviour |
| --- | --- | --- |
| GET | `/api/health` | API/write-queue readiness |
| GET | `/api/products` | Seeded catalog |
| GET | `/api/order-statuses` | Status names, final-state flags and allowed next statuses |
| PATCH | `/api/orders/{id}/status` | Update status with `{ "status": "Confirmed", "expectedStatus": "Pending" }`; 400 unknown status, 409 invalid transition or stale state |
| POST | `/api/cart/quote` | Authoritative pricing |
| POST | `/api/orders` | Checkout; UUID `Idempotency-Key` header required |
| GET | `/api/orders` | Demo customer's orders, newest first |
| GET | `/api/orders/{id}` | Order details/status or 404 |
| POST | `/api/orders/{id}/cancel` | Cancel Pending/Confirmed orders; 200 on repeat, 409 for Fulfilled, 404 if missing or another customer’s |

Quote and checkout body:

```json
{"items":[{"productId":1,"quantity":1},{"productId":2,"quantity":2}]}
```

`expectedStatus` is optional for API callers; the UI supplies it to detect stale views. Same-status retries return 200, even after the order reaches a final state. Status transitions preserve prices, lines and creation dates.

Responses expose monetary amounts as integer `*Cents` properties and currency `ZAR`. Quantity, product availability, empty/oversized carts and repeated product lines are validated. Invalid JSON/model binding returns 400; changed-payload replay returns 409. Unexpected server errors use Problem Details.

## Tests

`Shop.Tests` uses xUnit, Moq and AutoFixture. It covers delivery thresholds, totals, quantity limits, missing products, empty carts, duplicate lines, invalid keys, replay/conflict behaviour, repeatable seeding, concurrent submissions, status seeding, cancellation eligibility, repeat cancellation, price preservation and customer ownership, allowed and forbidden status transitions, lookup-driven configuration, stale updates, legacy database upgrades, command redelivery, receipt conflicts, transactional rollback, cache misses/hits, hourly expiry/refresh, restart isolation, invalidation, concurrent cold reads, Redis failures and revision triggers. Database tests use real SQLite (including an isolated file for concurrency), so unique constraints are exercised.

Bootstrap provides responsive forms/layout with custom styling. No Bootstrap JavaScript is required.

## Verification in this workspace

Docker builds and Compose startup pass with the API, frontend, RabbitMQ and Redis healthy. The existing SQLite volume upgraded in place with a processed-command receipt table. The running instance remains at http://localhost:8090 (`SHOP_PORT=8090 docker compose up --build -d --wait`).

All 50 business, command-processing and cache tests pass in an isolated .NET SDK container. An additional isolated Compose stack verified 30 concurrent checkout submissions, duplicate-key protection, independent orders, status transitions, conflicting updates, validation, single-active-consumer/prefetch settings and malformed-message dead-lettering. Broker outage checks confirmed reads remain available, writes return 503, and writes recover automatically without restarting the API. Redis integration checks verified first-read population, one-hour TTL, expiry, refresh after raw product/status edits, fresh cache namespaces after API restart and SQLite fallback/repopulation during a Redis outage. Twenty repeated product/status reads and quotes caused no additional product-table queries after warming. Verification data and containers were removed; the running shop's order history was preserved. Browser interactions have not been manually verified.
