# Mini ERP — Backup, Recovery & Disaster Recovery (DR) Runbook

Tài liệu thiết kế và quy trình vận hành sao lưu, phục hồi dữ liệu cho hệ thống **MiniERP Manufacturing & Warehouse** kết nối Oracle Database 19c/21c/23c.

---

## 1. Mục Tiêu Vận Hành (RPO / RTO)

| Chỉ Số | Mục Tiêu Demo / Staging | Mục Tiêu Nhà Máy (Production Baseline) | Cơ Chế Đạt Được |
|---|---|---|---|
| **RPO** (Recovery Point Objective) | <= 24 giờ | <= 15 phút | Snapshot hằng ngày + Oracle Archive Redo Logs |
| **RTO** (Recovery Time Objective) | <= 10 phút | <= 30 phút | Container tái khởi động tự động + script Data Pump restore |

---

## 2. Chiến Lược Sao Lưu (Backup Strategy)

Hệ thống cung cấp script chuẩn hóa `scripts/backup-db.sh`:
- **Định dạng sao lưu**:
  1. **Oracle Data Pump Export (`expdp`)**: Xuất schema nhị phân chứa toàn bộ metadata, tables, constraints, sequences, triggers, PL/SQL packages và data.
  2. **Metadata & Snapshot Report**: Lưu trữ commit hash, git branch, số lượng bảng, trạng thái packages, và tồn kho tức thời vào `metadata.json` và `schema_snapshot.txt`.
- **An toàn bảo mật**:
  - Tuyệt đối không in hoặc lưu mật khẩu tài khoản database vào log hoặc console.
  - Sử dụng biến môi trường nội bộ container.

### Lệnh thực hiện sao lưu:
```bash
# Thực hiện sao lưu vào thư mục mặc định (backups/backup_YYYYMMDD_HHMMSS)
bash scripts/backup-db.sh

# Hoặc chỉ định thư mục đích
bash scripts/backup-db.sh /var/backups/minierp
```

---

## 3. Quy Trình Phục Hồi Dữ Liệu (Restoration Procedure)

Script `scripts/restore-db.sh` có **Safety Lock hai lớp**: restore destructive bị từ chối nếu thiếu `ALLOW_DESTRUCTIVE_RESTORE=true` hoặc `CONFIRM_RESTORE=yes`/`--confirm`.

### Các bước phục hồi (dùng cho test/demo; cần backup `.dmp` thật):
```bash
# Bắt buộc cả hai cờ:
ALLOW_DESTRUCTIVE_RESTORE=true CONFIRM_RESTORE=yes \
  bash scripts/restore-db.sh /path/to/backup_directory

# --confirm chỉ thay cho CONFIRM_RESTORE, vẫn cần cờ cho phép destructive:
ALLOW_DESTRUCTIVE_RESTORE=true \
  bash scripts/restore-db.sh --confirm /path/to/backup_directory
```

Quy trình thực tế:
1. Xác thực Oracle PDB (`FREEPDB1`) và metadata/dump đầu vào.
2. Tạo **schema sạch** bằng `DROP USER ... CASCADE` + `CREATE USER`/quyền tối thiểu.
3. Stream `.dmp` vào container bằng quyền đọc của OS user `oracle` để tránh `ORA-27041`.
4. Chạy `impdp` với `table_exists_action=REPLACE`.
5. Query lot đối chiếu `lotRows` trong `metadata.json` (nếu có).
6. Chạy `scripts/verify-backup.sh`; chỉ khi verifier pass mới báo restore thành công.

> Không dùng `run-sql.sh` thay cho restore: đó là seed/migration verification, không phải phục hồi Data Pump.

---

## 4. Quy Trình Nghiệm Thu Tính Toàn Vẹn (Verification)

Một bản backup chỉ được công nhận hợp lệ khi vượt qua bộ kiểm thử `scripts/verify-backup.sh`:
- **Số lượng bảng**: Phải đạt tối thiểu 31 bảng (13 lõi ERP + 7 automation + 10 traceability/security + 1 Helpdesk outbox).
- **Trạng thái Packages**: Cả 3 Package Bodies `ERP_OPERATIONS`, `ERP_AUTOMATION`, `ERP_TRACEABILITY` phải ở trạng thái `VALID`.
- **Dữ liệu hạt nhân**: Các bản ghi định danh bắt buộc (`WH_RAW`, `MAT_RUBBER_01`, `RCV-01`) phải truy vấn thành công.

### Lệnh kiểm tra:
```bash
bash scripts/verify-backup.sh
```
