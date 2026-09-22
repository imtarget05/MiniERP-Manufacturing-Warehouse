# PROJECT SPEC — 04-MiniERP-Manufacturing-Warehouse

## 1. Goal

Build một Mini ERP thực tế cho phỏng vấn IT ERP, tập trung:

- C#
- ASP.NET Core
- Oracle
- PL/SQL
- Warehouse
- Manufacturing
- ERP support
- transaction consistency
- incident diagnosis
- audit trail

AI là OPTIONAL phase và chỉ được thêm sau khi ERP core hoạt động.

---

# 2. Interview Story

> Tôi xây một mini ERP cho Warehouse + Manufacturing. Business rules nằm trong application/domain + Oracle/PL-SQL phù hợp; có transaction, inventory history, production orders, BOM, audit log và support incident workflow. AI chỉ hỗ trợ technician chẩn đoán, không tự thay đổi dữ liệu.

---

# 3. Architecture

```text
Web UI / API Client
       |
       v
ASP.NET Core
       |
       +------ Domain / Services
       |
       v
Oracle Database
       |
       +-- Tables
       +-- Views
       +-- PL/SQL Packages
       +-- Procedures
       +-- Functions
       +-- Triggers (only where justified)
```

Optional:

```text
ERP Diagnostic Context
       |
       v
AI Diagnostic Assistant
```

---

# 4. Modules

## 4.1 Master Data

- Item
- Warehouse
- Unit of Measure
- Supplier optional
- Product
- User
- Role

## 4.2 Inventory

- Stock In
- Stock Out
- Transfer
- Adjustment
- Current Balance
- Transaction History

## 4.3 BOM

- BOM header
- BOM detail
- effective status
- material quantity

## 4.4 Production Order

- draft
- released
- in progress
- completed
- cancelled

Flow:

```text
Create PO
-> Validate BOM
-> Check material availability
-> Reserve/consume material
-> Produce finished goods
-> Inventory transactions
-> Complete PO
```

---

# 5. Data Model

Required tables:

```text
APP_USER
APP_ROLE
USER_ROLE

ITEM
WAREHOUSE
INVENTORY_BALANCE
INVENTORY_TRANSACTION

BOM
BOM_DETAIL

PRODUCTION_ORDER
PRODUCTION_ORDER_MATERIAL
PRODUCTION_OUTPUT

AUDIT_LOG
ERROR_LOG
CHANGE_REQUEST
```

Recommended:

- surrogate PK;
- business unique keys;
- indexes;
- created_at;
- updated_at;
- created_by.

---

# 6. PL/SQL Requirements

Create a package if practical:

```text
PKG_INVENTORY
PKG_MANUFACTURING
```

Procedures/functions:

```text
create_stock_in(...)
create_stock_out(...)
transfer_stock(...)
adjust_stock(...)

get_current_stock(...)

create_production_order(...)
release_production_order(...)
complete_production_order(...)
cancel_production_order(...)
```

Must demonstrate:

- transaction;
- exception handling;
- custom error;
- rollback;
- locking/concurrency awareness.

---

# 7. Business Rules

Examples:

1. Stock cannot become negative unless explicitly configured.
2. Production order cannot complete if required materials are unavailable.
3. Completed production order cannot be edited directly.
4. Inventory adjustment requires reason.
5. Every stock movement creates transaction history.
6. Critical actions write audit log.

---

# 8. API Scope

Suggested:

```text
GET    /api/items
POST   /api/items

GET    /api/warehouses
POST   /api/warehouses

GET    /api/inventory/:itemId
POST   /api/inventory/stock-in
POST   /api/inventory/stock-out
POST   /api/inventory/transfer
POST   /api/inventory/adjust

POST   /api/boms
GET    /api/boms/:id

POST   /api/production-orders
POST   /api/production-orders/:id/release
POST   /api/production-orders/:id/complete
POST   /api/production-orders/:id/cancel

GET    /api/audit
GET    /api/errors
```

---

# 9. ERP Support Scenario

Must create reproducible incident cases.

## Incident 001

User report:

```text
Production Order PO001 cannot complete.
```

Diagnosis workflow:

```text
Check application error
-> Check PO status
-> Check BOM
-> Check inventory
-> Identify shortage
-> Explain root cause
-> Resolve correctly
-> Retest
```

Expected finding example:

```text
Required MAT-X: 200
Available MAT-X: 170
Shortage: 30
```

Documentation:

```text
support/incidents/INC-001-production-shortage.md
```

---

# 10. Change Request Scenario

Example:

```text
Business requests:
Allow partial production completion.
```

Must document:

- current behavior;
- requested behavior;
- data impact;
- API impact;
- PL/SQL impact;
- test cases;
- rollback consideration.

---

# 11. Security

Roles:

```text
ERP_USER
WAREHOUSE_STAFF
PRODUCTION_STAFF
ERP_ADMIN
```

Rules:

- warehouse staff cannot administrate users;
- production staff cannot arbitrary-adjust inventory;
- admin actions audited.

---

# 12. Testing

Unit:

- material calculation;
- status transition;
- validation.

Integration:

- Oracle procedures;
- transaction;
- rollback.

Scenario tests:

- stock in/out;
- insufficient inventory;
- complete production;
- duplicate completion attempt;
- concurrent stock operations.

---

# 13. AI OPTIONAL — ERP Diagnostic Assistant

Only build after core ERP DONE.

## Principle

AI is advisory only.

AI cannot:

- execute SQL directly;
- update inventory;
- release/complete production order;
- modify user permission.

Flow:

```text
User/Support Technician
        |
        v
Select incident / PO
        |
        v
Backend builds sanitized diagnostic context
        |
        v
AI
        |
        v
Possible root cause + recommended checks
```

Example output:

```json
{
  "possibleCause": "Insufficient material MAT-X",
  "evidence": [
    "required=200",
    "available=170"
  ],
  "recommendedChecks": [
    "Verify pending stock receipt",
    "Review material reservation",
    "Confirm BOM quantity"
  ]
}
```

Core ERP remains source of truth.

---

# 14. Project Structure

```text
src/
  Api/
  Application/
  Domain/
  Infrastructure/
    Oracle/
  Modules/
    Inventory/
    Manufacturing/
    MasterData/
  Support/
  AI/                 # optional

database/
  ddl/
  packages/
  procedures/
  seed/

tests/
docs/
  architecture/
  business-rules/
  support/
  change-requests/
```

---

# 15. Implementation Phases

## Phase 0 — Setup + Architecture

- solution structure;
- Oracle connectivity;
- migrations/DDL strategy;
- README.

## Phase 1 — Master Data

- item;
- warehouse;
- users/roles.

## Phase 2 — Inventory

- stock in/out;
- transfer;
- adjustment;
- balance;
- transaction history.

## Phase 3 — PL/SQL

- package procedures/functions;
- error handling;
- transaction behavior.

## Phase 4 — BOM

- product BOM;
- material requirements.

## Phase 5 — Production

- create/release/complete/cancel;
- consume materials;
- finished goods.

## Phase 6 — Audit + Support

- audit log;
- error log;
- incident docs.

## Phase 7 — Tests

- integration;
- concurrency-sensitive cases.

## Phase 8 — AI Diagnostic Assistant OPTIONAL

Only after phases 0-7 stable.

---

# 16. Demo Script

Demo:

1. Create materials.
2. Stock in materials.
3. Create BOM.
4. Create production order.
5. Intentionally make material insufficient.
6. Attempt complete -> fail.
7. Show Oracle/business error.
8. Diagnose.
9. Stock in missing material.
10. Complete order.
11. Show consumed material.
12. Show finished goods.
13. Show inventory history.
14. Show audit log.
15. Optional AI diagnosis.

---

# 17. Out of Scope

Do not add before core complete:

- microservices;
- Kubernetes;
- Kafka;
- AI autonomous agent;
- complex accounting;
- payroll;
- full procurement;
- SAP imitation.

---

# 18. Definition of Done

- Oracle schema reproducible;
- PL/SQL actually executes;
- inventory stays consistent;
- production flow works;
- business errors are understandable;
- audit trail exists;
- at least 2 incident cases documented;
- tests cover important rules;
- README includes ERD and process diagram;
- optional AI cannot mutate ERP data.
