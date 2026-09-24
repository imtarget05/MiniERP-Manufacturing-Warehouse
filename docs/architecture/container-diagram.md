# Architecture: Container Diagram (C4 Level 2)
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** ARC-CON-002  

---

## 1. Container Architecture Diagram

```mermaid
flowchart TD
    subgraph Client Tier
        BROWSER["Web Browser / Tablet (Factory Dashboard: HTML5, CSS3, JS)"]
        SCANNER["Handheld Barcode Scanner (Browser / Embedded Webview)"]
    end

    subgraph Container Cluster (Docker Compose)
        UI["Web UI Container (Nginx Alpine - Port 8080)"]
        API["Web API Container (ASP.NET Core 8 - Port 5000)"]
        DB["Database Container (Oracle Database 23c Free - Port 1521)"]
    end

    subgraph External Systems
        HELPDESK["Enterprise IT Helpdesk (Node.js Portal - Port 3000)"]
    end

    BROWSER -->|"HTTP/HTTPS (Port 8080)"| UI
    SCANNER -->|"REST API Calls + Bearer JWT"| API
    UI -->|"Static Assets (HTML/CSS/JS)"| BROWSER
    BROWSER -->|"REST API Calls (Port 5000)"| API
    API -->|"Oracle Managed Data Access (Dapper) Port 1521"| DB
    API -->|"Fail-soft HTTP Client (Outbox)"| HELPDESK
```

---

## 2. Container Responsibilities

1. **`minierp-ui` (Port 8080):**
   - Nginx Alpine web server hosting static SPA assets (`index.html`, `app.js`, `styles.css`).
   - Serves warehouse operations UI, KPI overview, barcode scanning forms, and genealogy visualizer.
2. **`minierp-api` (Port 5000):**
   - ASP.NET Core 8 Minimal API hosting 57 route endpoints.
   - Executes authentication, token generation, PBKDF2 hashing, idempotency validation, and authorization middleware.
   - Communicates with Oracle Database using Dapper micro-ORM.
3. **`minierp-oracle` (Port 1521):**
   - Oracle Database 23c Free container hosting dedicated PDB `FREEPDB1` and schema `erp_user`.
   - Encapsulates 31 relational tables and 3 PL/SQL packages executing atomic transaction logic.
