# Little Shop

A demo online shop built in the provided folders: .NET 10 Web API, Angular 21 standalone frontend, Bootstrap 5, EF Core and SQLite. Angular 21 supports the installed Node 22.14 runtime ([compatibility](https://angular.dev/reference/versions)).

## Run with Docker (quick start)

With Docker Desktop running (or Docker Engine with Compose v2.17+), run from the repository root:

```sh
docker compose up --build -d --wait
```

Open http://localhost:8080. Docker builds both apps, seeds the catalog, and waits for healthy services. Only Docker is required on your machine; the SDK and Node dependencies are installed in build containers. The first build downloads images and dependencies and requires internet access.

The frontend serves the Angular production build through nginx and forwards `/api` to the API on the internal Compose network. SQLite data lives in the `shop-data` named volume and survives container rebuilds and shutdowns. The API runs as the non-root `app` user and is not published directly to the host.

```sh
docker compose logs -f     # Follow logs; Ctrl+C exits the log viewer
docker compose down        # Stop and remove containers, retaining orders
```

For a different port, use `SHOP_PORT=8090 docker compose up --build -d --wait` and open http://localhost:8090. Run the startup command again after source changes to rebuild the images.

To reset the demo, `docker compose down --volumes` deletes the SQLite volume **and all orders**. Start again to recreate and seed the database. Container data is separate from the database used by the manual setup below.

## Run locally without Docker

Prerequisites: .NET SDK 10, Node.js 22.12+ within the 22.x line, npm.

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
- Orders start at `Confirmed`. My orders shows newest first, with item prices, delivery, total and status; Refresh status fetches the current stored status. There is no fulfilment simulator or administration UI.
- Everyone uses the same seeded demo customer, Alex (`demo-customer`). There is no authentication: customer identity is fixed on the server. Do not use this demo for private customer data.
- SQLite persists orders across restarts and enforces a unique `(CustomerId, SubmissionKey)` index. A single atomic save stores the order and its lines. Prices and item names are snapshotted.
- Checkout requires an `Idempotency-Key` UUID header. Repeating it with the same product IDs and quantities returns the original order (200, versus 201 for a new order). Reusing it for a changed cart returns 409. Sorting cart lines does not change the fingerprint. Concurrent submissions are protected by the database constraint.
- The browser stores an in-flight key and cart in localStorage before submission, retains them on network/server errors and reloads, and locks cart edits until a retry confirms the result. Successful checkout clears them. Definitive client errors unlock the cart. Distinct keys represent distinct purchase attempts; identical carts with different keys are permitted.
- Validation uses Problem Details responses, data annotations and business rules. The frontend displays validation and connection errors and success messages. Catalog price values in client requests are never trusted.

## Storage and seeding

SQLite was chosen over EF InMemory for persistence and real relational constraints/transactions. On startup `SeedData.Initialize` creates the schema with `EnsureCreated` and inserts the catalog only when empty. It can be run repeatedly. The database defaults to `src/backend/Shop.Api/shop.db` when launched with the command above. Override with `ConnectionStrings__Shop` (for example `Data Source=/absolute/path/shop.db;Default Timeout=30`).

To reset the demo, stop the API and delete its `shop.db` and any `shop.db-wal` / `shop.db-shm` companion files; restarting recreates the catalog. This deletes orders. `EnsureCreated` is intentional for the demo; schema changes require resetting the database. Production should use reviewed EF migrations, authentication, customer ownership checks, payment integration and inventory management.

## API

| Method | Path | Behaviour |
| --- | --- | --- |
| GET | `/api/products` | Seeded catalog |
| POST | `/api/cart/quote` | Authoritative pricing |
| POST | `/api/orders` | Checkout; UUID `Idempotency-Key` header required |
| GET | `/api/orders` | Demo customer's orders, newest first |
| GET | `/api/orders/{id}` | Order details/status or 404 |

Quote and checkout body:

```json
{"items":[{"productId":1,"quantity":1},{"productId":2,"quantity":2}]}
```

Responses expose monetary amounts as integer `*Cents` properties and currency `ZAR`. Quantity, product availability, empty/oversized carts and repeated product lines are validated. Invalid JSON/model binding returns 400; changed-payload replay returns 409. Unexpected server errors use Problem Details.

## Tests

`Shop.Tests` uses xUnit, Moq and AutoFixture. It covers delivery thresholds, totals, quantity limits, missing products, empty carts, duplicate lines, invalid keys, replay/conflict behaviour, repeatable seeding, and concurrent submissions. Database tests use real SQLite (including an isolated file for concurrency), so unique constraints are exercised.

Bootstrap provides responsive forms/layout with custom styling. No Bootstrap JavaScript is required.

## Verification in this workspace

Docker image builds and Compose startup pass, with both containers healthy. HTTP smoke checks pass for the Angular page, nginx API routing, six seeded products, pricing and the free-delivery threshold, invalid carts, required checkout keys, order history, and missing-order responses. No orders were created during these checks. Port 8087 was occupied in this workspace, so the verified running instance uses http://localhost:8090 (`SHOP_PORT=8090 docker compose up --build -d --wait`).

The API and test project compile with zero warnings/errors, and the Angular production build passes. The test runner could not execute inside the filesystem sandbox because MSBuild requires a local IPC socket; permission to run the tests outside the sandbox was declined. Test outcomes and browser interactions are therefore unverified. Run `dotnet test Shop.sln` locally to execute the suite.
