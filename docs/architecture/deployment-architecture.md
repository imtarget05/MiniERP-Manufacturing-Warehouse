# Architecture: Deployment Architecture Specification
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** ARC-DEP-006  

---

## 1. Deployment Topology Diagram

```mermaid
flowchart TD
    subgraph Plant Floor Network (VLAN 20)
        SCAN["Rugged Handheld Barcode Scanners"]
        HMI["Line-Side HMI Touchscreens"]
    end

    subgraph Office / Management Network (VLAN 10)
        DESK["Planners & Manager Desktops (Web Browser)"]
    end

    subgraph Demilitarized Zone / Ingress
        REV["Reverse Proxy / SSL Terminator (Port 443)"]
    end

    subgraph Docker Application Server (Ubuntu Host)
        NGINX["Nginx Container (minierp-ui:8080)"]
        API["ASP.NET Core Web API (minierp-api:5000)"]
        DB["Oracle Database 23c Free (minierp-oracle:1521)"]
        VOL["Persistent Volume: oracle_data"]
    end

    SCAN --> REV
    HMI --> REV
    DESK --> REV
    REV --> NGINX
    REV --> API
    API --> DB
    DB --> VOL
```

---

## 2. Environment Configurations

| Configuration Setting | Development (`Development`) | Testing / Staging (`Staging`) | Production-Like (`Production`) |
|---|---|---|---|
| **ASPNETCORE_ENVIRONMENT** | `Development` | `Staging` | `Production` |
| **Database Container** | `gvenzl/oracle-free:slim` | `gvenzl/oracle-free:slim` | Oracle Enterprise / Free dedicated |
| **Swagger UI** | Enabled at root (`/`) | Enabled at `/swagger` | Disabled or internal VPN only |
| **CORS Whitelist** | `localhost:5000`, `localhost:8080` | `staging.plant.corp` | `https://minierp.plant.corp` |
| **Logging Level** | `Debug` / `Information` | `Information` | `Warning` / `Error` (Structured JSON) |
