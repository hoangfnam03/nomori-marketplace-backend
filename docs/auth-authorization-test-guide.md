# Nomori Auth / Authorization Test Guide

Tài liệu này dùng để kiểm tra vertical slice Authentication + Authorization trên môi trường local. Các giá trị bên dưới chỉ dành cho database phát triển; không dùng email/password thật.

## 1. Kiến trúc cần hiểu trước khi test

### Backend

1. Angular gọi `/api` qua proxy đến ASP.NET Core API.
2. `GET /api/v1/auth/csrf` tạo CSRF cookie `Nomori.Csrf` và trả request token.
3. Login xác thực email/password, sau đó API phát HttpOnly cookie `Nomori.Auth.Development`.
4. Cookie chỉ chứa identity tối thiểu. Ở mỗi request, cookie validation đọc customer từ database và từ chối session nếu customer inactive, deleted, locked hoặc `RequireReLogin`.
5. Authorization policy lấy customer id từ cookie, truy vấn permission hiệu lực qua role mapping và trả `401` hoặc `403`.
6. Password recovery chỉ lưu hash token. Reset password được consume atomically trong database và đánh dấu session cũ cần đăng nhập lại.

### Frontend

1. `AuthFacade` gọi `AuthApiService`; cookie không được lưu trong `localStorage`.
2. CSRF interceptor tự lấy token trước các request `POST`, `PUT`, `PATCH`, `DELETE`.
3. `authGuard` bảo vệ `/auth/account`; `permissionGuard` bảo vệ `/admin` để điều hướng UX.
4. Backend vẫn là security boundary. Bỏ guard ở frontend không thể bypass permission API.
5. Admin page gọi role API và hiển thị loading, error, forbidden và success state.

## 2. Khởi động hệ thống

Terminal backend:

```powershell
dotnet run --project src/Nomori.Marketplace.DbMigrator/Nomori.Marketplace.DbMigrator.csproj
dotnet run --project src/Nomori.Marketplace.Api/Nomori.Marketplace.Api.csproj
```

Terminal frontend:

```powershell
npm install
npm start
```

Mở:

- Frontend: `http://localhost:4200`
- Swagger: `https://localhost:7014/swagger/index.html`
- OpenAPI: `https://localhost:7014/openapi/v1.json`

Nếu HTTPS certificate local chưa được trust, mở API URL trên browser trước và chấp nhận certificate cho môi trường development.

## 3. Chuẩn bị Postman

Bật cookie jar của Postman và dùng cùng host `localhost:7014` cho mọi request.

1. Gọi `GET https://localhost:7014/api/v1/auth/csrf`.
2. Lưu response field `token`.
3. Xác nhận cookie `Nomori.Csrf` tồn tại.
4. Với mọi request thay đổi state, gửi header:

```text
X-CSRF-TOKEN: <token>
```

Token header không đủ nếu cookie CSRF bị thiếu hoặc khác host/scheme.

## 4. Test Authentication

Dùng dữ liệu:

```json
{
  "email": "qa.nomori@example.test",
  "password": "Password!123",
  "newPassword": "Password!456"
}
```

Password hợp lệ mặc định phải có ít nhất 12 ký tự, chữ hoa, chữ thường, số và ký tự đặc biệt. Backend là nơi enforce cuối cùng; FE chỉ hiển thị validation sớm.

### 4.1 Password policy

```http
GET /api/v1/auth/password/policy
```

Kỳ vọng `200` và policy hiện tại. Endpoint này giúp FE/tooling biết cấu hình, không phải cơ chế bảo mật.

### 4.2 Register

```http
POST /api/v1/auth/register
Content-Type: application/json
X-CSRF-TOKEN: <token>

{
  "email": "qa.nomori@example.test",
  "password": "Password!123"
}
```

Kỳ vọng `201 Created`. Customer được gán role `Registered`; password chỉ lưu dạng hash.

Test password yếu:

```json
{
  "email": "weak.nomori@example.test",
  "password": "password"
}
```

Kỳ vọng `400` với `errors.password`. Test đăng ký trùng email kỳ vọng `409`.

### 4.3 Login và session

```http
POST /api/v1/auth/login
Content-Type: application/json
X-CSRF-TOKEN: <token>

{
  "email": "QA.Nomori@Example.Test",
  "password": "Password!123",
  "rememberMe": false
}
```

Kỳ vọng `204` và cookie `Nomori.Auth.Development`.

```http
GET /api/v1/auth/session
```

Kỳ vọng `200` với `isAuthenticated: true`. API không trả password, salt hoặc security stamp.

FE tương ứng: login form gửi credentials, interceptor gửi cookie tự động, `AuthFacade` refresh session và shell hiển thị email.

### 4.4 Permissions của Registered user

```http
GET /api/v1/auth/permissions
```

Kỳ vọng `200` với `auth.authenticated` và `auth.permissions.read`.

Không có authentication cookie: `401`.

### 4.5 Lockout

Gửi login sai password 5 lần liên tiếp.

- Mỗi lần trả `401 auth.invalid_credentials`.
- Sau ngưỡng, customer bị lock mặc định 15 phút.
- Login đúng password trong thời gian lockout vẫn trả `401` generic.

Cookie session đang tồn tại nhưng customer bị lock cũng bị cookie validation từ chối ở request tiếp theo.

### 4.6 Change password và re-login

```http
POST /api/v1/auth/password/change
Content-Type: application/json
X-CSRF-TOKEN: <token>

{
  "currentPassword": "Password!123",
  "newPassword": "Password!456"
}
```

Kỳ vọng `204`, cookie hiện tại bị sign out. Gọi `/auth/session` trả anonymous; password cũ không login được, password mới login được.

Thử đặt lại password cũ trong password history kỳ vọng `400 auth.password_recently_used`.

### 4.7 Forgot/reset password

```http
POST /api/v1/auth/password/forgot
Content-Type: application/json
X-CSRF-TOKEN: <token>

{
  "email": "qa.nomori@example.test"
}
```

Kỳ vọng production trả `202` với message generic. Development trả thêm `token` để test local; token này không được bật ở production.

Reset:

```http
POST /api/v1/auth/password/reset
Content-Type: application/json
X-CSRF-TOKEN: <token>

{
  "token": "<token>",
  "newPassword": "Password!789"
}
```

Kỳ vọng `204`. Dùng lại token kỳ vọng `401`. Token hết hạn/không tồn tại kỳ vọng `401`. Reset password mới cũng invalidates các session cũ.

### 4.8 Logout và CSRF

```http
POST /api/v1/auth/logout
X-CSRF-TOKEN: <token>
```

Kỳ vọng `204`, cookie auth bị clear và session trở thành anonymous.

Lặp lại các request state-changing nhưng bỏ `X-CSRF-TOKEN`: kỳ vọng `400`.

## 5. Test Authorization và role management

### 5.1 Chuẩn bị Administrator local

Đăng ký/login một user, lấy `customerId` từ session, sau đó chạy SQL local sau khi migration đã hoàn tất:

```sql
INSERT INTO CustomerCustomerRoleMapping (CustomerId, CustomerRoleId)
SELECT c.Id, r.Id
FROM Customer c
CROSS JOIN CustomerRole r
WHERE c.Email = 'qa.nomori@example.test'
  AND r.SystemName = 'Administrator'
  AND NOT EXISTS (
      SELECT 1
      FROM CustomerCustomerRoleMapping existingMapping
      WHERE existingMapping.CustomerId = c.Id
        AND existingMapping.CustomerRoleId = r.Id
  );
```

Đăng nhập lại để cookie/session mới được dùng.

### 5.2 Đọc role catalog

```http
GET /api/v1/admin/authorization/roles
```

Kỳ vọng `200`, gồm `Registered` và `Administrator`. User không có `admin.roles.read` nhận `403`.

### 5.3 Đọc role của customer

```http
GET /api/v1/admin/authorization/customers/1/roles
```

Thay `1` bằng customer id thật. Kỳ vọng `200` hoặc `404` nếu customer không tồn tại.

### 5.4 Gán role

```http
PUT /api/v1/admin/authorization/customers/2/roles
Content-Type: application/json
X-CSRF-TOKEN: <token>

{
  "roleSystemNames": ["Registered"]
}
```

Kỳ vọng `200`, mapping được thay thế trong transaction.

Các failure path:

- customer không tồn tại: `404`;
- role không tồn tại/inactive: `400`;
- danh sách role rỗng: `400`;
- administrator tự xoá role Administrator: `403`;
- thiếu permission: `403`;
- thiếu auth cookie: `401`;
- thiếu CSRF header/cookie: `400`.

FE tương ứng: mở `/admin`, load role catalog, nhập customer id, load roles hiện tại và save. Nếu chỉ có session hợp lệ nhưng thiếu permission, FE chuyển đến `/auth/forbidden`; API vẫn chặn độc lập.

## 6. Validation commands

Backend:

```powershell
dotnet build src/Nomori.Marketplace.Api/Nomori.Marketplace.Api.csproj --no-restore --disable-build-servers -m:1
dotnet test Nomori.Marketplace.sln --no-restore --disable-build-servers -m:1
```

Frontend:

```powershell
npm run typecheck
npm run build
```

Nếu build lại xuất hiện lỗi prerender với API relative URL `/api/v1/auth/session`, kiểm tra cấu hình SSR/API base URL và đảm bảo đang dùng code `AuthFacade` có server guard; bản hiện tại đã tránh request session trong prerender.

## 7. Những phần chưa thuộc MVP này

- Resource-level/store-level ACL.

## 8. Email verification

Registration creates an unverified customer and sends a one-time verification link. Login is rejected with `403 auth.email_not_verified` until the link is used. In Development, when email delivery is disabled, the `201` response includes `verificationToken` for local testing.

Verify it with:

```http
GET /api/v1/auth/email/verify?token=<verificationToken>
```

Expected: `204 No Content`. Reusing or expiring the token returns `400 auth.email_verification_invalid`. The Angular route is `/auth/verify-email?token=...`.

Resend with `POST /api/v1/auth/email/verification/send` and the email in the JSON body. Production responses are generic. For real delivery set `Email:Enabled=true` and configure SMTP; for local testing use Mailpit and open the captured link.

## 9. Email OTP

Email OTP is opt-in per customer. The default rules are six digits, 10-minute expiry, five failed attempts and a 60-second resend delay. OTP values are never stored in plaintext.

After email verification, while authenticated, call `POST /api/v1/auth/otp/setup`. Copy `challengeId` and, when email is disabled in Development, `developmentCode`. Confirm with `POST /api/v1/auth/otp/enable`:

```json
{ "challengeId": "<challengeId>", "code": "<six-digit-code>" }
```

The next password login returns `202 Accepted` with `otpRequired: true`. Verify the login challenge using `POST /api/v1/auth/login/otp/verify`:

```json
{ "challengeId": "<login-challenge-id>", "code": "<six-digit-code>", "rememberMe": false }
```

Expected: `204 No Content` and an authentication cookie. Wrong, expired and locked codes return `auth.otp_invalid`, `auth.otp_expired` and `auth.otp_locked`. Angular uses `/auth/login-otp`; account controls are on `/auth/account`. Disable with `POST /api/v1/auth/otp/disable`.

## 10. Audit log

`AuditLog` stores UTC time, actor/target customer IDs, event name, IP address and safe JSON details. Passwords, OTP values and raw tokens are excluded. Events include registration, login success/failure, logout, password recovery/reset/change, email verification, OTP, and role replacement.

Administrators with `admin.audit.read` can inspect recent entries:

```http
GET /api/v1/admin/authorization/audit-logs?take=100
```

## 11. Validation commands

```powershell
dotnet build src/Nomori.Marketplace.Api/Nomori.Marketplace.Api.csproj
dotnet test Nomori.Marketplace.sln
npm run typecheck
npm run build
```
