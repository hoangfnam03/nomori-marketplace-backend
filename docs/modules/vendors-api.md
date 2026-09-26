# Thiết kế API: Module Vendors

| | |
|---|---|
| **Module** | Vendors, gồm đăng ký vendor và thành viên vendor |
| **Cập nhật** | 2026-09-26 |
| **Liên quan** | [vendors-prd.md](vendors-prd.md) (yêu cầu), [vendors.vi.md](vendors.vi.md) (thiết kế kỹ thuật) |

---

## 1. Quy ước chung

### Base URL và định dạng

- Base URL: `/api/v1`. Frontend gọi qua proxy `/api`.
- Request và response dùng JSON, tên field theo camelCase.
- Thời gian luôn là UTC, định dạng ISO 8601, ví dụ `2026-09-26T08:15:00Z`. Tên field thời gian có hậu tố `Utc`.
- Giá trị enum được trả về dạng chuỗi camelCase, ví dụ `"pending"`, `"pendingSetup"`.

### Một tài nguyên, một bộ API

Mỗi tài nguyên (đơn đăng ký, vendor, thành viên, ghi chú) chỉ có **một bộ route**, dùng chung cho admin, thành viên shop và khách. Không tách thành `/admin/...` hay `/vendor/portal/...`. Với từng request, API xác định người gọi thuộc loại nào:

| Loại người gọi | Điều kiện |
|---|---|
| **Khách vãng lai** | Chưa đăng nhập |
| **Khách hàng** | Đã đăng nhập |
| **Thành viên của vendor `{id}`** | Có permission `vendor.portal` và `Customer.VendorId = {id}` |
| **Admin** | Có permission `vendor.manage` (chỉ cấp cho role `Administrator`) |

Từ loại người gọi, API quyết định 3 việc: **có được gọi không**, **thấy những bản ghi nào**, và **thấy những field nào**. Các field chỉ dành cho admin hoặc thành viên có giá trị `null` với người gọi khác (xem mục 8).

Trong code, loại người gọi được tính **một lần cho mỗi request** bằng `IVendorAccessContext`, gồm `IsAdmin`, `CustomerId` và `MemberVendorId`. Controller và service chỉ đọc context này. Mỗi DTO có đúng một hàm map, nhận context làm tham số, để không lộ field ra sai người.

### Xác thực và CSRF

- Xác thực bằng cookie HttpOnly. Frontend gửi request kèm `withCredentials`.
- Mọi request ghi dữ liệu (`POST`, `PUT`, `DELETE`) phải có header `X-CSRF-TOKEN`. Token lấy từ `GET /api/v1/auth/csrf`. Controller đánh dấu các action này bằng `[ValidateAntiForgeryToken]`.
- Customer ID và vendor ID của người gọi **luôn lấy từ phiên đăng nhập**, không bao giờ nhận từ body.

### Mã trạng thái HTTP

| Mã | Khi nào |
|---|---|
| `200 OK` | Đọc hoặc cập nhật thành công, có body |
| `201 Created` | Tạo mới thành công, có header `Location` |
| `204 No Content` | Thành công, không có body |
| `400 Bad Request` | Dữ liệu không hợp lệ, trả về `ValidationProblemDetails` |
| `401 Unauthorized` | Endpoint cần đăng nhập nhưng người gọi chưa đăng nhập |
| `403 Forbidden` | Người gọi **được phép thấy** tài nguyên nhưng không được làm thao tác này, hoặc thiếu hay sai CSRF token |
| `404 Not Found` | Không có tài nguyên, **hoặc người gọi không được phép thấy nó**. Trả `404` để không lộ việc tài nguyên có tồn tại |
| `409 Conflict` | Vi phạm quy tắc nghiệp vụ. `detail` chứa mã lỗi (mục 9) |

### Định dạng lỗi

Lỗi validate (`400`):

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "shopName": ["Shop name is already in use."]
  }
}
```

Lỗi nghiệp vụ (`409`):

```json
{
  "title": "Vendor application failed",
  "status": 409,
  "detail": "vendor_application.already_pending"
}
```

Frontend hiển thị thông báo dựa trên `detail`, không dựa trên `title`.

### Phân trang

Query: `page` (mặc định `1`) và `pageSize` (mặc định `20`, tối đa `100`).

```json
{ "items": [], "totalCount": 42, "page": 1, "pageSize": 20, "totalPages": 3 }
```

---

## 2. Danh sách endpoint

Có **16 endpoint** cho module Vendors, thay cho 24 endpoint nếu tách route admin và portal. Ngoài ra có 2 thay đổi ở API xác thực.

| # | Method | Route | Ai được gọi | CSRF |
|---|---|---|---|---|
| **Đơn đăng ký** | | | | |
| 1 | `POST` | `/vendor-applications` | Khách hàng | có |
| 2 | `GET` | `/vendor-applications` | Khách hàng (đơn của mình), admin (tất cả) | |
| 3 | `GET` | `/vendor-applications/{id}` | Người nộp đơn, admin | |
| 4 | `PUT` | `/vendor-applications/{id}` | Người nộp đơn | có |
| 5 | `PUT` | `/vendor-applications/{id}/status` | Người nộp đơn (hủy), admin (duyệt, từ chối) | có |
| **Vendor** | | | | |
| 6 | `GET` | `/vendors` | Mọi người | |
| 7 | `GET` | `/vendors/{id}` | Mọi người | |
| 8 | `PUT` | `/vendors/{id}` | Admin | có |
| 9 | `DELETE` | `/vendors/{id}` | Admin | có |
| **Thành viên** | | | | |
| 10 | `GET` | `/vendors/{id}/members` | Thành viên của `{id}`, admin | |
| 11 | `POST` | `/vendors/{id}/members` | Thành viên của `{id}` | có |
| 12 | `POST` | `/vendors/{id}/members/{customerId}/setup-email` | Thành viên của `{id}` | có |
| 13 | `DELETE` | `/vendors/{id}/members/{customerId}` | Thành viên của `{id}`, admin | có |
| **Ghi chú nội bộ** | | | | |
| 14 | `GET` | `/vendors/{id}/notes` | Admin | |
| 15 | `POST` | `/vendors/{id}/notes` | Admin | có |
| 16 | `DELETE` | `/vendors/{id}/notes/{noteId}` | Admin | có |
| **Xác thực (thay đổi)** | | | | |
| 17 | `GET` | `/auth/session` | Mọi người | |
| 18 | `POST` | `/auth/password/reset` | Mọi người | có |

### Các endpoint cũ được thay thế

| Endpoint cũ | Thay bằng |
|---|---|
| `GET /vendors`, `GET /admin/vendors` | #6 `GET /vendors` |
| `GET /vendors/{id}`, `GET /admin/vendors/{id}`, `GET /vendor/portal` | #7 `GET /vendors/{id}`. Frontend lấy `vendorId` từ #17 `GET /auth/session` |
| `PUT /admin/vendors/{id}`, `DELETE /admin/vendors/{id}` | #8, #9 |
| `GET/POST/DELETE /admin/vendors/{id}/notes...` | #14–#16 |
| `POST /admin/vendors` | Bỏ. Vendor chỉ được tạo khi duyệt đơn |
| `POST /admin/vendors/{id}/customer`, `DELETE /admin/vendors/{id}/customer/{customerId}` | Bỏ. Gỡ thành viên dùng #13 |

---

## 3. Đơn đăng ký

Controller: `VendorApplicationController`, route `api/v1/vendor-applications`, `[Authorize]`.

### Vòng đời trạng thái

```text
pending ──► approved     (admin, qua #5)
        ├─► rejected     (admin, qua #5, bắt buộc có lý do)
        └─► cancelled    (người nộp đơn, qua #5)
```

Chỉ đơn `pending` mới được sửa (#4) hoặc đổi trạng thái (#5). `approved`, `rejected` và `cancelled` là trạng thái cuối.

### 3.1. `POST /vendor-applications`: nộp đơn

**Request** `SubmitVendorApplicationRequest`

| Field | Kiểu | Bắt buộc | Quy tắc |
|---|---|---|---|
| `shopName` | string | có | trim; 1–400 ký tự; không trùng (không phân biệt hoa thường) với vendor chưa xóa hoặc đơn `pending` khác |
| `email` | string | có | trim, chuyển thành chữ thường; đúng định dạng email; tối đa 320 ký tự |
| `phoneNumber` | string | có | tối đa 50 ký tự; chỉ gồm chữ số, khoảng trắng, `+`, `-`, `(`, `)` |
| `description` | string \| null | không | chuỗi rỗng được lưu thành `null` |
| `taxCode` | string \| null | không | tối đa 50 ký tự |
| `businessAddress` | string \| null | không | tối đa 1000 ký tự |

```json
{
  "shopName": "Mai Ceramics",
  "email": "shop@maiceramics.vn",
  "phoneNumber": "+84 901 234 567",
  "description": "Gốm thủ công Bát Tràng.",
  "taxCode": "0101234567",
  "businessAddress": "12 Bát Tràng, Gia Lâm, Hà Nội"
}
```

**Response `201`**: `VendorApplicationResponse` (mục 8.1), header `Location: /api/v1/vendor-applications/{id}`.

**Lỗi**

| Mã | `detail` / field | Khi nào |
|---|---|---|
| `400` | `errors.shopName`, `errors.email`, … | Dữ liệu không hợp lệ, hoặc trùng tên shop |
| `409` | `vendor_application.email_not_verified` | Tài khoản chưa xác thực email |
| `409` | `vendor_application.already_vendor` | Tài khoản đang thuộc một shop |
| `409` | `vendor_application.already_pending` | Đã có đơn `pending`, kể cả khi hai request gửi đến cùng lúc |

Audit: `vendor.application_submitted`.

### 3.2. `GET /vendor-applications`: danh sách đơn

| Người gọi | Thấy những đơn nào |
|---|---|
| Khách hàng | Chỉ đơn của chính mình |
| Admin | Tất cả đơn |

**Query**

| Tham số | Kiểu | Mặc định | Ghi chú |
|---|---|---|---|
| `status` | `pending` \| `approved` \| `rejected` \| `cancelled` | (tất cả) | |
| `search` | string | | Tìm theo `shopName`, email của đơn hoặc email tài khoản người nộp. Chỉ áp dụng cho admin; với khách hàng thì bỏ qua |
| `page`, `pageSize` | int | `1`, `20` | |

**Sắp xếp**: `createdOnUtc` giảm dần (đơn mới nhất lên trước). Riêng admin khi lọc `status=pending` thì sắp xếp tăng dần, để đơn chờ lâu nhất lên trước.

**Response `200`**: trang dữ liệu gồm các `VendorApplicationResponse`. Các field chỉ dành cho admin có giá trị `null` với khách hàng.

**Cách frontend dùng**
- Trang đăng ký mở shop: `GET /vendor-applications?pageSize=1` để lấy đơn gần nhất. Nếu `items` rỗng thì hiện form.
- Trang duyệt đơn của admin: `GET /vendor-applications?status=pending`.

**Lỗi**: `400` khi `status` không hợp lệ.

### 3.3. `GET /vendor-applications/{id}`: chi tiết đơn

**Response `200`**: `VendorApplicationResponse`.

**Lỗi**: `404` khi không có đơn, hoặc người gọi không phải người nộp đơn và cũng không phải admin.

### 3.4. `PUT /vendor-applications/{id}`: sửa đơn

Chỉ người nộp đơn được sửa, và chỉ khi đơn còn `pending`.

**Request**: giống 3.1. Tất cả field được gửi lại đầy đủ.

**Response `200`**: `VendorApplicationResponse`.

**Lỗi**

| Mã | `detail` / field | Khi nào |
|---|---|---|
| `400` | `errors.*` | Dữ liệu không hợp lệ. Khi kiểm tra trùng tên shop, bỏ qua chính đơn này |
| `403` | | Admin gọi sửa đơn của người khác. Admin chỉ đổi trạng thái được, không sửa nội dung đơn |
| `404` | | Không có đơn, hoặc người gọi không được thấy đơn |
| `409` | `vendor_application.not_pending` | Đơn không còn `pending` |

Audit: `vendor.application_updated`, chỉ lưu tên các field đã đổi.

### 3.5. `PUT /vendor-applications/{id}/status`: đổi trạng thái đơn

Một endpoint duy nhất cho **duyệt**, **từ chối** và **hủy**.

**Request** `ChangeVendorApplicationStatusRequest`

| Field | Kiểu | Dùng khi | Quy tắc |
|---|---|---|---|
| `status` | `approved` \| `rejected` \| `cancelled` | luôn có | Trạng thái đích |
| `reason` | string \| null | `rejected` | **Bắt buộc** khi `rejected`; trim; 1–2000 ký tự. Với trạng thái khác phải để `null` |
| `shopName` | string \| null | `approved` | Không bắt buộc. Nếu có thì dùng thay tên trong đơn, với cùng quy tắc như 3.1 |
| `adminComment` | string \| null | `approved` | Không bắt buộc. Ghi vào `Vendor.AdminComment` |

Ví dụ:

```json
{ "status": "approved", "shopName": "Mai Ceramics Official", "adminComment": "Đã xác minh qua điện thoại." }
```

```json
{ "status": "rejected", "reason": "Mã số thuế không khớp với tên doanh nghiệp." }
```

```json
{ "status": "cancelled" }
```

**Quyền theo trạng thái đích**

| `status` | Ai được làm | Người khác gọi thì trả |
|---|---|---|
| `approved` | Admin | `403` nếu là người nộp đơn; `404` nếu không được thấy đơn |
| `rejected` | Admin | như trên |
| `cancelled` | Người nộp đơn | `403` nếu là admin nhưng không phải người nộp đơn |

**Xử lý**

- **`approved`**, trong một transaction:
  1. Tạo `Vendor` với `Active = true`, `DisplayOrder = 0`.
  2. `Customer.VendorId` = vendor mới.
  3. Thêm role `Vendors` cho người nộp nếu chưa có.
  4. Đơn chuyển sang `approved`, ghi `vendorId`, `reviewedByCustomerId` và `reviewedOnUtc`.

  Sau khi commit, hệ thống gửi email báo duyệt. Audit: `vendor.application_approved`.
- **`rejected`**: đơn chuyển sang `rejected`, ghi `rejectReason`, `reviewedByCustomerId` và `reviewedOnUtc`. Sau đó gửi email kèm lý do. Audit: `vendor.application_rejected`.
- **`cancelled`**: đơn chuyển sang `cancelled`. Audit: `vendor.application_cancelled`.

**Response `200`**: `VendorApplicationResponse` sau khi đổi. Khi `approved`, response có `vendorId`; frontend dùng giá trị này để mở `/vendors/{vendorId}` nếu cần.

**Lỗi**

| Mã | `detail` / field | Khi nào |
|---|---|---|
| `400` | `errors.status` | `status` không hợp lệ, hoặc là `pending` |
| `400` | `errors.reason` | Thiếu lý do khi `rejected`, hoặc có lý do khi không phải `rejected` |
| `400` | `errors.shopName` | Tên shop dùng để duyệt không hợp lệ hoặc bị trùng |
| `403` | | Người gọi được thấy đơn nhưng không được chuyển sang trạng thái này |
| `404` | | Không có đơn, hoặc người gọi không được thấy đơn |
| `409` | `vendor_application.not_pending` | Đơn đã ở trạng thái cuối |
| `409` | `vendor_application.applicant_already_vendor` | Khi duyệt: người nộp đã thuộc một shop |

---

## 4. Vendor

Controller: `VendorController`, route `api/v1/vendors`.

### Người gọi thấy những gì

| Người gọi | Thấy những vendor nào | Field riêng (mục 8.2) |
|---|---|---|
| Khách vãng lai, khách hàng | Vendor `active`, chưa xóa | `null` |
| Thành viên của vendor `{id}` | Như trên, **cộng thêm** vendor của mình dù đang tắt | Có giá trị, **chỉ với vendor của mình** |
| Admin | Mọi vendor chưa xóa | Có giá trị |

Vendor đã xóa (`Deleted = 1`) không hiện với bất kỳ ai.

### 4.1. `GET /vendors`: danh sách vendor

`[AllowAnonymous]`. Nếu request có cookie đăng nhập, API vẫn đọc để xác định người gọi.

**Query**

| Tham số | Kiểu | Mặc định | Ghi chú |
|---|---|---|---|
| `search` | string | | Tìm theo tên vendor; admin tìm được thêm theo email |
| `active` | bool | | **Chỉ admin dùng được.** Người khác gửi tham số này thì bị bỏ qua, và luôn chỉ nhận vendor đang hoạt động |
| `page`, `pageSize` | int | `1`, `20` | |

**Sắp xếp**: `displayOrder` tăng dần, rồi đến `name`.

**Response `200`**: trang dữ liệu gồm các `VendorResponse` (mục 8.2).

**Cache**: response thay đổi theo người gọi, nên không được cache chung. Endpoint trả header `Cache-Control: private, no-store` khi người gọi đã đăng nhập.

### 4.2. `GET /vendors/{id}`: chi tiết vendor

`[AllowAnonymous]`.

**Response `200`**: `VendorResponse`.

**Lỗi**: `404` khi không có vendor, vendor đã xóa, hoặc vendor đang tắt mà người gọi không phải admin hay thành viên của vendor đó.

**Cách frontend dùng**
- Storefront: `GET /vendors/{id}`.
- Vendor portal: đọc `vendorId` từ `GET /auth/session` (mục 7.1), rồi gọi `GET /vendors/{vendorId}`.
- Trang admin: `GET /vendors/{id}`.

### 4.3. `PUT /vendors/{id}`: sửa vendor

`[Authorize]`, `[HasPermission(PermissionCodes.VendorManage)]`. Chỉ admin được gọi. Việc người bán tự sửa thông tin shop thuộc module sau.

**Request** `UpdateVendorRequest`

| Field | Kiểu | Bắt buộc | Quy tắc |
|---|---|---|---|
| `name` | string | có | trim; 1–400 ký tự |
| `email` | string | có | trim, chuyển thành chữ thường; tối đa 320 ký tự |
| `description` | string \| null | không | |
| `adminComment` | string \| null | không | |
| `active` | bool | không | mặc định `true` |
| `displayOrder` | int | không | mặc định `0` |

**Response `200`**: `VendorResponse`.

**Lỗi**: `400 errors.*`; `401`; `403`; `404`.

### 4.4. `DELETE /vendors/{id}`: xóa vendor

`[Authorize]`, `[HasPermission(PermissionCodes.VendorManage)]`.

**Xử lý**, trong một transaction:

1. `Vendor.Deleted = true`.
2. Với mọi thành viên: `VendorId = NULL`, gỡ role `Vendors`, `RequireReLogin = true`.

**Response `204`**. **Lỗi**: `401`, `403`, `404`.

Audit: `vendor.member_removed` cho từng thành viên, với `byAdmin: true`.

---

## 5. Thành viên

Controller: `VendorMemberController`, route `api/v1/vendors/{id}/members`, `[Authorize]`.

Nếu người gọi không phải admin và không phải thành viên của vendor `{id}`, mọi endpoint ở mục này trả `404`.

### 5.1. `GET /vendors/{id}/members`: danh sách thành viên

**Người được gọi**: thành viên của `{id}`, admin.

**Response `200`**: mảng `VendorMemberResponse` (mục 8.3), sắp xếp theo `createdOnUtc` tăng dần. Không phân trang, vì mỗi shop có tối đa 20 thành viên.

```json
[
  {
    "customerId": 12,
    "email": "mai@example.com",
    "firstName": "Mai",
    "lastName": "Nguyễn",
    "status": "active",
    "isCurrentUser": true,
    "createdOnUtc": "2026-09-20T03:00:00Z",
    "lastLoginDateUtc": "2026-09-26T07:40:00Z"
  },
  {
    "customerId": 31,
    "email": "staff@maiceramics.vn",
    "firstName": "Lan",
    "lastName": "Trần",
    "status": "pendingSetup",
    "isCurrentUser": false,
    "createdOnUtc": "2026-09-26T08:15:00Z",
    "lastLoginDateUtc": null
  }
]
```

### 5.2. `POST /vendors/{id}/members`: tạo tài khoản thành viên

**Người được gọi**: chỉ thành viên của `{id}`. Admin gọi thì nhận `403`, vì admin không tạo tài khoản thành viên (PRD D8).

**Request** `CreateVendorMemberRequest`

| Field | Kiểu | Bắt buộc | Quy tắc |
|---|---|---|---|
| `email` | string | có | trim, chuyển thành chữ thường; đúng định dạng; tối đa 320 ký tự; **chưa có tài khoản nào dùng, kể cả tài khoản đã xóa** |
| `firstName` | string \| null | không | tối đa 100 ký tự |
| `lastName` | string \| null | không | tối đa 100 ký tự |

```json
{ "email": "staff@maiceramics.vn", "firstName": "Lan", "lastName": "Trần" }
```

**Xử lý**, trong một transaction: tạo `Customer` (`Active = true`, `EmailVerified = false`, mật khẩu ngẫu nhiên 32 byte đã hash), cấp role `Registered` và `Vendors`, đặt `VendorId = {id}`, tạo token thiết lập có hạn 72 giờ. Sau khi commit, hệ thống gửi email đặt mật khẩu.

**Response `201`**: `VendorMemberCreatedResponse`, gồm các field của `VendorMemberResponse` và thêm:

| Field | Kiểu | Ghi chú |
|---|---|---|
| `developmentSetupToken` | string \| null | Chỉ có giá trị khi chạy ở Development **và** `Email.Enabled = false`. Giống cách API đăng ký tài khoản trả token xác thực |

**Lỗi**

| Mã | `detail` / field | Khi nào |
|---|---|---|
| `400` | `errors.email`, `errors.firstName`, `errors.lastName` | Dữ liệu không hợp lệ |
| `403` | | Admin gọi |
| `404` | | Không có vendor, hoặc người gọi không phải thành viên |
| `409` | `vendor_member.email_already_exists` | Email đã có tài khoản |
| `409` | `vendor_member.limit_reached` | Shop đã có `MaxMembersPerVendor` (20) thành viên |

Audit: `vendor.member_created`.

### 5.3. `POST /vendors/{id}/members/{customerId}/setup-email`: gửi lại email kích hoạt

**Người được gọi**: chỉ thành viên của `{id}`. Admin gọi thì nhận `403`.

**Request**: không có body.

**Xử lý**: vô hiệu các token thiết lập chưa dùng của tài khoản đó, tạo token mới có hạn 72 giờ, rồi gửi email.

**Response `200`**: `{ "developmentSetupToken": string | null }`, cùng quy tắc như 5.2.

**Lỗi**: `403` khi admin gọi; `404` khi tài khoản không thuộc vendor `{id}`; `409 vendor_member.already_active`.

Audit: `vendor.member_setup_resent`.

### 5.4. `DELETE /vendors/{id}/members/{customerId}`: gỡ thành viên hoặc rời shop

**Người được gọi**: thành viên của `{id}`, admin. Thành viên dùng `customerId` của chính mình để rời shop.

**Xử lý**, trong một transaction: `VendorId = NULL`, gỡ role `Vendors`, `RequireReLogin = true`.

**Response `204`**. Nếu người gọi tự rời shop, frontend tải lại session và permission rồi chuyển về `/storefront`.

**Lỗi**: `404` khi tài khoản không thuộc vendor `{id}`; `409 vendor_member.last_member` khi đó là thành viên cuối cùng. Quy tắc này áp dụng cả với admin; muốn đóng shop thì xóa vendor (4.4).

Audit: `vendor.member_removed`, với `self` và `byAdmin`.

---

## 6. Ghi chú nội bộ

Controller: `VendorNoteController`, route `api/v1/vendors/{id}/notes`, `[Authorize]`, `[HasPermission(PermissionCodes.VendorManage)]`. Chỉ admin được gọi. Nội dung giữ nguyên như API ghi chú hiện có, chỉ đổi route.

| Endpoint | Request | Response |
|---|---|---|
| `GET /vendors/{id}/notes?page=&pageSize=` | | trang dữ liệu gồm các `VendorNoteResponse`, hoặc `404` |
| `POST /vendors/{id}/notes` | `{ "note": string }` (bắt buộc) | `201` `VendorNoteResponse`, `400` hoặc `404` |
| `DELETE /vendors/{id}/notes/{noteId}` | | `204`, hoặc `404` nếu ghi chú không thuộc vendor `{id}` |

`VendorNoteResponse`: `id`, `vendorId`, `note`, `createdOnUtc`.

---

## 7. Thay đổi ở API xác thực

### 7.1. `GET /auth/session`: thêm `vendorId`

`SessionResponse` có thêm field:

| Field | Kiểu | Ghi chú |
|---|---|---|
| `vendorId` | int \| null | Vendor mà tài khoản đang thuộc. `null` nếu chưa đăng nhập, không thuộc shop nào, hoặc shop đã bị xóa |

```json
{ "isAuthenticated": true, "customerId": 12, "email": "mai@example.com", "emailVerified": true, "emailOtpEnabled": false, "vendorId": 15 }
```

### 7.2. `POST /auth/password/reset`: bật `EmailVerified`

Request (`{ "token": string, "newPassword": string }`) và response (`204`) giữ nguyên.

**Đổi hành vi:** đặt mật khẩu thành công thì tài khoản được đặt `EmailVerified = true` và `EmailVerifiedOnUtc = now`, nếu trước đó chưa xác thực. Thay đổi này áp dụng cho mọi lần đặt lại mật khẩu.

Link kích hoạt thành viên có dạng `{FrontendBaseUrl}/auth/reset-password?token=...&setup=1`. Tham số `setup=1` chỉ để frontend hiển thị chữ "Đặt mật khẩu"; backend không đọc tham số này.

---

## 8. Schema response

### 8.1. `VendorApplicationResponse`

| Field | Kiểu | Ai thấy giá trị |
|---|---|---|
| `id` | int | mọi người được xem đơn |
| `shopName` | string | mọi người được xem đơn |
| `email` | string | mọi người được xem đơn |
| `phoneNumber` | string | mọi người được xem đơn |
| `description` | string \| null | mọi người được xem đơn |
| `taxCode` | string \| null | mọi người được xem đơn |
| `businessAddress` | string \| null | mọi người được xem đơn |
| `status` | `pending` \| `approved` \| `rejected` \| `cancelled` | mọi người được xem đơn |
| `rejectReason` | string \| null | mọi người được xem đơn; chỉ có giá trị khi `rejected` |
| `vendorId` | int \| null | mọi người được xem đơn; chỉ có giá trị khi `approved` |
| `createdOnUtc`, `updatedOnUtc` | datetime | mọi người được xem đơn |
| `reviewedOnUtc` | datetime \| null | mọi người được xem đơn |
| `customerId` | int \| null | **admin** |
| `customerEmail` | string \| null | **admin** |
| `customerUsername` | string \| null | **admin** |
| `reviewedByCustomerId` | int \| null | **admin** |

### 8.2. `VendorResponse`

| Field | Kiểu | Ai thấy giá trị |
|---|---|---|
| `id` | int | mọi người |
| `name` | string | mọi người |
| `email` | string | mọi người |
| `description` | string \| null | mọi người |
| `pictureId` | int | mọi người |
| `displayOrder` | int | mọi người |
| `active` | bool \| null | **admin, thành viên của vendor này** |
| `addressId` | int \| null | **admin, thành viên của vendor này** |
| `createdOnUtc` | datetime \| null | **admin, thành viên của vendor này** |
| `updatedOnUtc` | datetime \| null | **admin, thành viên của vendor này** |
| `adminComment` | string \| null | **admin** |

### 8.3. `VendorMemberResponse`

| Field | Kiểu | Ghi chú |
|---|---|---|
| `customerId` | int | |
| `email` | string | |
| `firstName` | string \| null | |
| `lastName` | string \| null | |
| `status` | `pendingSetup` \| `active` | `active` khi tài khoản đã đăng nhập ít nhất một lần |
| `isCurrentUser` | bool | `true` cho dòng của chính người gọi |
| `createdOnUtc` | datetime | |
| `lastLoginDateUtc` | datetime \| null | |

---

## 9. Mã lỗi nghiệp vụ

Mã lỗi nằm trong `ProblemDetails.detail` của response `409`, và được khai báo làm hằng số trong `Nomori.Marketplace.Core.Vendors.VendorErrors`.

| Mã lỗi | Endpoint | Ý nghĩa | Gợi ý thông báo trên giao diện |
|---|---|---|---|
| `vendor_application.email_not_verified` | 3.1 | Chưa xác thực email | "Vui lòng xác thực email trước khi đăng ký mở shop." |
| `vendor_application.already_vendor` | 3.1 | Đang thuộc một shop | "Tài khoản của bạn đã thuộc một shop." |
| `vendor_application.already_pending` | 3.1 | Đã có đơn chờ duyệt | "Bạn đã có một đơn đang chờ duyệt." |
| `vendor_application.not_pending` | 3.4, 3.5 | Đơn đã được xử lý | "Đơn này đã được xử lý." |
| `vendor_application.applicant_already_vendor` | 3.5 | Người nộp đã thuộc một shop | "Người nộp đơn đã thuộc một shop khác." |
| `vendor_member.email_already_exists` | 5.2 | Email đã có tài khoản | "Email đã được sử dụng." |
| `vendor_member.limit_reached` | 5.2 | Shop đã đủ thành viên | "Shop đã đủ 20 thành viên." |
| `vendor_member.already_active` | 5.3 | Thành viên đã kích hoạt | "Thành viên này đã kích hoạt tài khoản." |
| `vendor_member.last_member` | 5.4 | Thành viên cuối cùng | "Shop phải còn ít nhất một thành viên." |

---

## 10. Luồng gọi API mẫu

### Đăng ký và được duyệt

```text
Khách A:  GET  /auth/csrf
Khách A:  POST /vendor-applications                               → 201 { id: 7, status: "pending" }
Admin:    GET  /vendor-applications?status=pending                → 200 { items: [ { id: 7, customerEmail: "a@...", ... } ] }
Admin:    PUT  /vendor-applications/7/status { status: "approved" } → 200 { id: 7, status: "approved", vendorId: 15 }
Khách A:  GET  /auth/session                                      → 200 { vendorId: 15, ... }
Khách A:  GET  /auth/permissions                                  → có "vendor.portal"
Khách A:  GET  /vendors/15                                        → 200 { id: 15, active: true, ... }
```

### Bị từ chối rồi nộp lại

```text
Admin:    PUT  /vendor-applications/7/status { status: "rejected", reason: "..." } → 200 { status: "rejected" }
Khách A:  GET  /vendor-applications?pageSize=1                    → 200 { items: [ { id: 7, status: "rejected", rejectReason: "..." } ] }
Khách A:  POST /vendor-applications                               → 201 { id: 8, status: "pending" }
```

### Thêm thành viên

```text
A:  POST   /vendors/15/members { email: "b@example.com" }   → 201 { customerId: 31, status: "pendingSetup" }
B:  (mở link trong email)
B:  POST   /auth/password/reset { token, newPassword }      → 204
B:  POST   /auth/login                                      → 200
B:  GET    /auth/session                                    → 200 { vendorId: 15, ... }
A:  GET    /vendors/15/members                              → B có status "active"
B:  DELETE /vendors/15/members/12                           → 204 (B gỡ A)
```
