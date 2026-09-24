# Enterprise Risk Register & Mitigation Strategy
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** PM-RSK-005  

---

## 1. Risk Assessment Matrix

Risks are quantified using Probability ($P: 1\text{ to }5$) and Impact ($I: 1\text{ to }5$) scores. Score = $P \times I$.

| Risk ID | Risk Description | Category | Prob (1-5) | Impact (1-5) | Score | Mitigation & Contingency Strategy | Owner |
|:---:|---|---|:---:|:---:|:---:|---|---|
| **RSK-01** | **Incorrect Legacy Inventory Migration:** Existing spreadsheet balances contain invalid SKU codes or negative numbers, corrupting initial ERP ledger balances. | Technical / Data | 3 | 4 | 12 | Implement multi-stage migration script with pre-validation rules, item code foreign-key checks, and total quantity reconciliation ($Delta = 0$). | Migration Lead |
| **RSK-02** | **Warehouse User Scanner Resistance:** Operators resist scanning lot barcodes and bin locations due to perceived slowness compared to paper tallies. | Organizational | 3 | 3 | 9 | Conduct hands-on training with rugged handhelds; streamline UI to require $< 3$ taps per transaction; provide live feedback tones for successful scans. | Warehouse Mgr |
| **RSK-03** | **Database Failure / Data Corruption:** Oracle container crash or volume corruption leads to lost production order histories or stock records. | Technical / Infra | 1 | 5 | 5 | Implement daily automated Oracle Data Pump exports (`backup-db.sh`), conduct regular restore drills (`restore-db.sh`), and verify integrity. | DevOps / DBA |
| **RSK-04** | **Deployment Failure on Cutover Day:** Cutover downtime exceeds the maintenance window, preventing morning shift from issuing materials. | Operational | 2 | 4 | 8 | Formulate comprehensive Cutover Checklist and proven Rollback Plan; test migration in staging sandbox 48 hours prior to go-live. | Project Manager |
| **RSK-05** | **Race Condition on Barcode Scanning:** Double-tapping scanner button causes duplicate stock issues or negative inventory. | Technical / App | 4 | 3 | 12 | Implement `REQUEST_IDEMPOTENCY` database table with SHA-256 request payload hashing to discard duplicate replays automatically. | Tech Lead |
| **RSK-06** | **External Helpdesk Outage Blocks ERP:** Failure or network timeout of the third-party IT Helpdesk portal causes core ERP stock mutations to hang or fail. | Architecture | 3 | 4 | 12 | Decouple Helpdesk integration via an asynchronous outbox pattern (`HELPDESK_DELIVERY`) with retry capabilities and fail-soft fallback. | Tech Lead |

---

## 2. Risk Monitoring Protocol

- All active risks are reviewed weekly during the Project Status Meeting.
- Risks scoring $\ge 10$ require an active mitigation plan tested in CI/CD or staging environments.
