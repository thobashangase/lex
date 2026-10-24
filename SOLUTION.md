# Solution

(I have typed this out personally to talk about the solution and tradeoffs,
so as to demostrate understanding the solution and its code. While AI was used for speeding up code generation, I took a duty of being the driver and guide during the work on this project)

## Architecture

The solution is split into a .NET API project for the backend business logic and rules, and data access and operations; and an Angular based user-facing project. This approach accomodates for front-end, back-end and full-stack developers to be able to maintain the solution with less crossovers.

## Design

- Minimal APIs on back-end as this is the recommended approach for .NET 8+ apps, it is cleaner and familiar to developers coming from backgrounds such as Node.js and Python
- For demo purposes, certain data is seeded and setup such that it is available at all times, including after a database truncation or application server restart