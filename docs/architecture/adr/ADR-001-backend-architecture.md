# ADR-001: Backend Architecture — Modular Monolith with Database-Centric Business Logic

## Status
**Accepted** (2026-09-24)

## Context
Manufacturing execution systems (MES) and warehouse management systems (WMS) require sub-second transaction times, strict referential integrity, and atomic multi-row inventory updates. We considered:
1. Microservices architecture with distributed events (RabbitMQ/Kafka).
2. Clean Architecture with Entity Framework Core ORM.
3. Modular Monolith with ASP.NET Core 8 Web API + Dapper + Oracle PL/SQL Stored Procedures.

## Decision
We chose a **Modular Monolith using ASP.NET Core 8 Web API with Dapper micro-ORM and compiled Oracle PL/SQL Packages**.

## Rationale
- **Atomic Multi-Row Transactions:** When a production order completes, BOM explosion consumes 5–10 raw materials and outputs finished goods. In a distributed microservice model, compensating two-phase commits introduce high failure complexity. In Oracle PL/SQL, this executes within a single atomic ACID transaction with row-level locks (`FOR UPDATE`).
- **Autonomous Error Logging:** Oracle's `PRAGMA AUTONOMOUS_TRANSACTION` enables logging failure events to `ERROR_LOG` even when the outer transaction rolls back.
- **Micro-ORM Simplicity:** Dapper provides ultra-low latency with zero ORM overhead, passing typed DTOs directly to stored procedures.

## Consequences
- **Positive:** Maximum transaction consistency; zero distributed transaction failures; sub-50ms API latency.
- **Negative:** Database technology is coupled to Oracle SQL/PL/SQL dialect; requires DBA/PL/SQL engineering proficiency.
