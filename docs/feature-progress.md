# Nomori Marketplace: tiến độ theo feature map

Tài liệu theo dõi (trace) tiến độ so với `nopCommerce/docs/nomori-complete-feature-map.md`. Cập nhật mỗi khi xong hoặc hoãn một slice.

- **Cập nhật lần cuối:** 2026-10-02
- **Quy ước:** mỗi slice có tài liệu thiết kế trong `docs/modules/` (mục 7 của feature map). Code chỉ sinh sau khi có tài liệu.
- **Trạng thái:** `Xong` (backend + Angular), `Một phần` (còn slice con chưa làm), `Hoãn` (có quyết định), `Chưa làm`.
- **Lưu ý về kiểm chứng:** tất cả slice đều có unit test cho service và migration. Các store SQL và luồng HTTP/Angular **chưa có test tự động**, cần test tay theo guide trong tài liệu từng module.

## 1. Bảng trạng thái

| ID | Module | Trạng thái | Tài liệu | Ghi chú |
|---|---|---|---|---|
| F00 | Nền tảng runtime, API, migration | Xong | `decisions.md`, `database-guidelines.md` | |
| F01 | Cấu hình, settings | Xong | `decisions.md` | Cài đặt lưu trong DB (settings runtime) chưa có, dùng `IOptions` |
| F02 | Xác thực | Một phần | `modules/authentication.md` | Đăng ký, đăng nhập, quên mật khẩu, xác thực email, OTP email. Chưa có: đăng nhập bên thứ ba, MFA app |
| F03 | Phân quyền | Xong | `modules/authorization.md` | Role, permission, audit log |
| F04 | Hồ sơ khách hàng | Một phần | `modules/customer-profile.md` | Chưa có: GDPR, tin nhắn riêng |
| F05 | Vendor / Shop | Một phần | `modules/vendors*.md` | Xong: quản trị vendor, đăng ký shop, thành viên shop. **Hoãn:** `vendor-shop-settings`, sản phẩm, đơn hàng, đối soát của vendor (PRD đã có) |
| F06 | Multi-store | **Hoãn** | | Quyết định 2026-10-01: sàn một domain, làm khi cần nhiều domain/brand |
| F07 | Localization, tiền tệ, đơn vị, múi giờ | Chưa làm | | Cần trước F14 (giá) |
| F08 | Media | Một phần | `modules/f08a-media.md` | **F08-A xong, đã merge** (ảnh, upload có kiểm soát). Còn F08-B (thumbnail, quét), F08-C (file riêng tư, download) |
| F09 | Taxonomy toàn sàn, manufacturer | Một phần | `modules/f09a-taxonomy.md` | **F09-A xong** (chờ test tay và merge): toàn vẹn cây, luật xóa, cờ `RestrictFromVendors`, validator chọn danh mục cho seller, ràng buộc DB. Còn slug/SEO (F24), template và lọc (F13), localization (F07) |
| F10 | Sản phẩm, quyền sở hữu | Một phần | | Product CRUD đã có. `VendorId` còn nullable, **F10-A** (ShopId bắt buộc, kiểm soát quyền) chưa làm |
| F11 | Biến thể, thuộc tính, thông số | Một phần | | Product attribute và specification attribute đã có. Chưa có: tổ hợp biến thể, ảnh sản phẩm, tier price |
| F12 | Tồn kho | Chưa làm | | Cần ledger/reservation trước khi làm giỏ hàng |
| F13 | Tìm kiếm, lọc | Chưa làm | | |
| F14–F31 | Giá, khuyến mãi, giỏ hàng, đặt hàng, thanh toán, vận chuyển, đổi trả, thông báo, CMS, SEO, affiliate, báo cáo, import/export, plugin, job nền, theme, AI | Chưa làm | | Xem feature map |

## 2. Quyết định đã chốt

| Ngày | Quyết định | Ảnh hưởng |
|---|---|---|
| 2026-10-01 | F06 (multi-store) hoãn. Hệ thống một domain | F07, F23, F24 thiết kế theo một cửa hàng |
| 2026-10-01 | Các slice nhỏ của F05 (shop settings và các slice sau) làm sau, ưu tiên nền tảng dùng chung trước | Media, taxonomy, product ownership đi trước |
| 2026-09-26 | Vendor chỉ được tạo qua đơn đăng ký đã duyệt. Mỗi tài khoản thuộc tối đa một shop. Mọi thành viên ngang quyền | `modules/vendors.md` |
| 2026-10-02 | Taxonomy do nền tảng sở hữu. Seller chỉ đọc và chọn, không có quyền ghi. Hạn chế theo từng category qua `RestrictFromVendors` (theo nopCommerce). Không làm allow-list theo từng shop | `modules/f09a-taxonomy.md` |
| 2026-10-01 | Media lưu trong SQL Server sau interface `IMediaStore`. Ảnh công khai. Kiểm tra bằng chữ ký file, không tin content-type. Không nhận SVG | `modules/f08a-media.md` |

## 3. Thứ tự đề xuất tiếp theo

Theo mục 6 của feature map (M05 recovery plan):

1. ~~F08-A Media~~ (xong, đã merge)
2. ~~F09-A Taxonomy toàn sàn~~ (xong, chờ test tay và merge)
3. **F10-A** Sở hữu sản phẩm: `ShopId` bắt buộc, kiểm soát quyền ở mọi endpoint của người bán, dùng ảnh `product` (F08-A) và `ITaxonomyService.ValidateSelectionAsync(..., Seller)` (F09-A).
4. F10-B Duyệt và xuất bản sản phẩm.
5. F11-A/B Ảnh sản phẩm, biến thể.
6. F12-A Tồn kho, F13-A Tìm kiếm.
7. F07 (phần tiền tệ, đơn vị, múi giờ) trước F14.
8. Quay lại F05: `vendor-shop-settings`, `vendor-products`, `vendor-orders`, `vendor-settlement`.

## 4. Nợ kỹ thuật và việc treo

| Mục | Nơi | Module liên quan |
|---|---|---|
| Ảnh đã upload nhưng không gắn vào đâu (mồ côi) chưa được dọn | `modules/f08a-media.md` | F29 |
| Thumbnail, kích thước ảnh, quét mã độc | `modules/f08a-media.md` | F08-B |
| Ghi chú nội bộ của vendor có API nhưng chưa có giao diện | `modules/vendors.md` | F05 |
| Thông báo cho admin khi có đơn đăng ký mới | `modules/vendors.md` | F22 |
| `extractError` ở trang admin catalog trước đây không đọc `fieldErrors` (đã sửa ở F08-A) | | |
| Form sửa sản phẩm của admin không gửi `vendorId`, nên lưu sản phẩm đang có shop sẽ xóa liên kết shop. Sửa trong F10-A (ShopId bắt buộc) | `admin-catalog.page.ts`, `ProductService.UpdateAsync` | F10-A |
| Xem sản phẩm công khai theo category chưa gồm category con, chưa cache | `modules/f09a-taxonomy.md` | F13 |
| Test tích hợp API (`Api.Tests`) rất ít; không có test E2E | | F26/F29 |

## 5. Nhật ký

| Ngày | Nhánh | Nội dung |
|---|---|---|
| 2026-09-30 | `feat/vendor/upload-spec-vendors` | F05: đăng ký shop, thành viên shop (BE + FE). Đã merge vào `main`/`master` |
| 2026-10-01 | `feat/media/foundation` | F08-A: upload và phân phối ảnh, gắn vào category, manufacturer, vendor (BE + FE). Đã merge |
| 2026-10-02 | `feat/taxonomy/foundation` | F09-A: toàn vẹn cây danh mục, luật xóa, hạn chế seller, validator chọn danh mục, ràng buộc DB, audit (BE + FE) |
