---
name: module-report
description: Summarize a Nomori backend feature into a concise technical module report after implementation or review.
---

Use the current diff, tests and docs. Do not invent details; write `Chưa có dữ liệu` when information is missing. For each implemented behavior, include concrete test scenarios that a developer can execute or adapt. Derive scenarios from the actual API, tests and business rules; do not invent endpoints, DTOs, status codes or test results.

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

### 6. Kịch bản kiểm thử minh họa
List the most useful scenarios for understanding and validating the module. Cover the applicable categories below and omit categories that do not apply:
- Happy path: valid input completes the primary business flow.
- Validation and boundary: required fields, malformed values, normalization, duplicate data, limits and empty/not-found cases.
- Authentication and authorization: unauthenticated, authenticated, insufficient permission and cross-user access where applicable.
- Security and abuse: CSRF, rate limiting, lockout, replay, sensitive-data exposure and safe error responses where applicable.
- Persistence and consistency: transaction rollback, idempotency, concurrency or migration constraints where applicable.

For every scenario, use this format:
1. **[Scenario name]**
   - Điều kiện trước: [state, seed data hoặc Chưa có dữ liệu]
   - Request/action: [method + path + payload/headers hoặc thao tác]
   - Kết quả mong đợi: [HTTP status, response/error code, state change and security behavior]

Prefer 5-10 scenarios for a small module and more when the risk or number of endpoints requires it. Mark scenarios that are already covered by an automated test and name the test when known. Separate verified results from recommended scenarios.
