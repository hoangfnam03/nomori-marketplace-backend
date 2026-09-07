---
name: module-report
description: Summarize a Nomori backend feature into a concise technical module report after implementation or review.
---

Use the current diff, tests and docs. Do not invent details; write `Chưa có dữ liệu` when information is missing.

## Module: [Tên Module]
**Trạng thái:** [Hoàn thành / Đang phát triển / Blocked]
**Thời gian hoàn thành:** [YYYY-MM-DD]

### 1. Luồng nghiệp vụ chính (Business Flow)
- [2-3 bullets]

### 2. Thiết kế Cơ sở dữ liệu (Database Design)
- Các bảng mới được thêm: [Tên bảng hoặc Chưa có dữ liệu]
- Các trường quan trọng: [Liệt kê ngắn gọn]
- Logic Migration đáng chú ý: [Nếu có]

### 3. API Contract (Backend)
- Endpoint chính: [method + path]
- DTO/Command/Query: [Tên type hoặc Chưa có dữ liệu]
- Lỗi và phân quyền: [Mô tả ngắn]

### 4. Technical Debt & Vấn đề cần tối ưu
- [Gaps, performance risks, deferred work hoặc Chưa có dữ liệu]

### 5. Validation
- Backend: [commands/tests]
- Contract/E2E: [commands/tests hoặc Chưa có dữ liệu]
