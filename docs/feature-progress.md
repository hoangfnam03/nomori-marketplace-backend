# Nomori Marketplace: tiến độ theo feature map

Tài liệu theo dõi (trace) tiến độ so với `nopCommerce/docs/nomori-complete-feature-map.md`. Cập nhật mỗi khi xong hoặc hoãn một slice.

- **Cập nhật lần cuối:** 2026-10-07
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
| F09 | Taxonomy toàn sàn, manufacturer | Một phần | `modules/f09a-taxonomy.md` | **F09-A xong, đã merge**: toàn vẹn cây, luật xóa, cờ `RestrictFromVendors`, validator chọn danh mục cho seller, ràng buộc DB. Còn slug/SEO (F24), template và lọc (F13), localization (F07) |
| F10 | Sản phẩm, quyền sở hữu | Một phần | `modules/f10a-product-ownership.md`, `modules/f10b-product-lifecycle.md`, `modules/f10c-product-content.md` | **F10-A xong, đã merge** (`VendorId` bắt buộc, shop nền tảng, API seller, chuyển shop). **F10-B xong, đã merge** (chưa test tay trên DB thật): 4 trạng thái (nháp, đang bán, ngừng bán, bị admin ẩn), seller đăng bán/ngừng bán, admin ẩn kèm lý do và email, bỏ ẩn khôi phục trạng thái cũ, yêu cầu xem lại. **F10-C xong** (chờ test tay và merge, xem `modules/f10c-product-content.md`): mô tả HTML lọc an toàn, SKU/GTIN/MPN (SKU duy nhất theo shop), lịch đăng bán, sản phẩm liên quan (cùng shop, tối đa 12), copy sản phẩm. Cặp F10 đóng lại ở slice này |
| F11 | Biến thể, thuộc tính, thông số | Một phần | `modules/f11a-product-pictures.md`, `modules/f11b-product-variants-specs-tags.md` | **F11-B xong** (chờ test tay và merge): seller tự quản lý biến thể (tối đa 3 thuộc tính, 20 giá trị, 100 tổ hợp, SKU duy nhất theo shop, tồn kho sản phẩm = tổng tồn kho tổ hợp), thông số và tag; API công khai về thuộc tính/thông số/tag chỉ trả sản phẩm đang hiển thị; storefront chọn giá trị và hiện giá, tồn kho của tổ hợp. **F11-A xong, đã merge** (PR #13; chưa test tay trên DB thật): ảnh sản phẩm có thứ tự (tối đa 10, ảnh đầu là ảnh chính), đăng bán cần ít nhất 1 ảnh, ảnh hiện ở danh sách seller, storefront. Product attribute và specification attribute đã có. Chưa có: ảnh theo tổ hợp và copy kèm biến thể (F11-C), tier price (F14) |
| F12 | Tồn kho | Chưa làm | | Cần ledger/reservation trước khi làm giỏ hàng |
| F13 | Tìm kiếm, lọc | Chưa làm | | |
| F14–F31 | Giá, khuyến mãi, giỏ hàng, đặt hàng, thanh toán, vận chuyển, đổi trả, thông báo, CMS, SEO, affiliate, báo cáo, import/export, plugin, job nền, theme, AI | Chưa làm | | Xem feature map |

## 2. Quyết định đã chốt

| Ngày | Quyết định | Ảnh hưởng |
|---|---|---|
| 2026-10-01 | F06 (multi-store) hoãn. Hệ thống một domain | F07, F23, F24 thiết kế theo một cửa hàng |
| 2026-10-01 | Các slice nhỏ của F05 (shop settings và các slice sau) làm sau, ưu tiên nền tảng dùng chung trước | Media, taxonomy, product ownership đi trước |
| 2026-09-26 | Vendor chỉ được tạo qua đơn đăng ký đã duyệt. Mỗi tài khoản thuộc tối đa một shop. Mọi thành viên ngang quyền | `modules/vendors.md` |
| 2026-10-04 | `Product.Status` là nguồn sự thật duy nhất (nháp, đang bán, ngừng bán, bị admin ẩn); `Published` thành cột tính toán. Bỏ ẩn khôi phục trạng thái trước khi ẩn. Lý do ẩn gửi email cho shop và hiển thị cho shop nhưng **không** ghi vào audit log | `modules/f10b-product-lifecycle.md` |
| 2026-10-03 | Mỗi sản phẩm thuộc đúng một shop. Sản phẩm của nền tảng thuộc shop "Nomori Official" (`Vendor.IsPlatformShop`), không dùng 0 hoặc null. Chủ sở hữu chỉ đổi qua thao tác chuyển shop của admin. Route seller là `/vendors/{id}/products`, người ngoài shop nhận 404 | `modules/f10a-product-ownership.md` |
| 2026-10-02 | Taxonomy do nền tảng sở hữu. Seller chỉ đọc và chọn, không có quyền ghi. Hạn chế theo từng category qua `RestrictFromVendors` (theo nopCommerce). Không làm allow-list theo từng shop | `modules/f09a-taxonomy.md` |
| 2026-10-01 | Media lưu trong SQL Server sau interface `IMediaStore`. Ảnh công khai. Kiểm tra bằng chữ ký file, không tin content-type. Không nhận SVG | `modules/f08a-media.md` |

## 3. Thứ tự đề xuất tiếp theo

Theo mục 6 của feature map (M05 recovery plan):

1. ~~F08-A Media~~ (xong, đã merge)
2. ~~F09-A Taxonomy toàn sàn~~ (xong, đã merge)
3. ~~F10-A Sở hữu sản phẩm~~ (xong, đã merge)
4. ~~F10-B Vòng đời sản phẩm~~ (xong, đã merge)
5. ~~F11-A Ảnh sản phẩm~~ (xong, đã merge)
5b. ~~F10-C Nội dung sản phẩm~~ (xong, chờ test tay và merge). 5c. ~~F11-B Biến thể, thông số, tag của seller~~ (xong, chờ test tay và merge). Tiếp theo: **F12-A** tồn kho (ledger, giữ chỗ) hoặc **F13-A** tìm kiếm, lọc theo thông số và tag. Xem `modules/vendor-products-prd.md` (US-A3, A4).
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
| Email liên hệ của shop nền tảng là giá trị giữ chỗ `platform@nomori.local`, admin cần sửa sau khi migrate | `modules/f10a-product-ownership.md` | Vận hành |
| Trang admin catalog chưa có bộ lọc sản phẩm theo shop (API đã hỗ trợ `vendorId`) | `admin-catalog.page.ts` | F10-C |
| Sản phẩm đang bán từ trước F11-A không có ảnh vẫn giữ trạng thái đang bán; luật "có ảnh" chỉ áp khi seller đổi trạng thái. Admin tick Published vẫn không bị luật ảnh | `modules/f11a-product-pictures.md` | F11-A |
| Mô tả sản phẩm cũ (trước F10-C) chưa được lọc lại; chỉ lọc khi lưu. Storefront bind qua `[innerHTML]` nên Angular lọc thêm một lớp | `modules/f10c-product-content.md` | F29 |
| Tổ hợp biến thể chưa có index duy nhất cho SKU (chỉ kiểm tra ở service), chưa chặn xóa giá trị đang có trong đơn (chưa có đơn) | `modules/f11b-product-variants-specs-tags.md` | F12, F16 |
| Test service (`Services.Tests`) không chạy được trên máy này do Windows App Control chặn DLL; code compile, cần chạy `dotnet test` trên máy khác hoặc CI | | Môi trường |
| Sản phẩm cũ chưa publish được chuyển thành "nháp" (không phân biệt được với "ngừng bán") | `modules/f10b-product-lifecycle.md` | Ghi chú triển khai |
| Xem sản phẩm công khai theo category chưa gồm category con, chưa cache | `modules/f09a-taxonomy.md` | F13 |
| Test tích hợp API (`Api.Tests`) rất ít; không có test E2E | | F26/F29 |

## 5. Nhật ký

| Ngày | Nhánh | Nội dung |
|---|---|---|
| 2026-09-30 | `feat/vendor/upload-spec-vendors` | F05: đăng ký shop, thành viên shop (BE + FE). Đã merge vào `main`/`master` |
| 2026-10-01 | `feat/media/foundation` | F08-A: upload và phân phối ảnh, gắn vào category, manufacturer, vendor (BE + FE). Đã merge |
| 2026-10-02 | `feat/taxonomy/foundation` | F09-A: toàn vẹn cây danh mục, luật xóa, hạn chế seller, validator chọn danh mục, ràng buộc DB, audit (BE + FE). Đã merge |
| 2026-10-03 | `feat/product-ownership/foundation` | F10-A: sở hữu sản phẩm, shop nền tảng, API sản phẩm cho seller, chuyển shop, ẩn sản phẩm của shop bị tắt (BE + FE). **Sửa lỗi:** form sửa sản phẩm của admin làm mất liên kết shop. Nâng ngưỡng cảnh báo CSS của Angular lên 6 kB (lỗi 8 kB). Đã merge |
| 2026-10-04 | `feat/product-lifecycle/foundation` | F10-B: vòng đời 4 trạng thái, đăng bán/ngừng bán, admin ẩn/bỏ ẩn kèm email, yêu cầu xem lại, lọc "chờ xem lại" (BE + FE). Đã merge (PR #12) |
| 2026-10-05 | `feat/product-pictures/foundation` | F11-A: bảng `ProductPicture`, API `PUT .../pictures`, luật "cần ít nhất 1 ảnh để đăng bán", ảnh chính ở danh sách và storefront, gallery ở trang chi tiết, mục ảnh trong form seller (BE + FE). Chưa commit |
| 2026-10-06 | `feat/product-content/foundation` | F10-C: cột `Sku`, `Gtin`, `ManufacturerPartNumber`, lịch đăng bán, bảng `ProductRelation`, lọc HTML mô tả (HtmlSanitizer), API `PUT .../related` và `POST .../copy`, storefront hiện mô tả HTML và sản phẩm liên quan, form seller có SKU/GTIN/lịch/liên quan/Copy (BE + FE). Chưa commit |
| 2026-10-07 | `feat/product-variants/foundation` | F11-B: API seller cho biến thể, thông số, tag (`PUT .../variants`, `.../specs`, `.../tags`, `GET .../product-options`), kiểm tra tổ hợp, SKU duy nhất gồm cả SKU tổ hợp, tồn kho sản phẩm = tổng tổ hợp, **sửa lỗ hổng:** API công khai `/products/{id}/attributes|specs|tags` trước đây trả cả sản phẩm nháp/bị ẩn. Trang seller `/vendor/products/:id/details`, storefront chọn biến thể (BE + FE). **Sửa lỗi:** storefront đọc sai dạng dữ liệu thuộc tính công khai (`m.mapping.id`). Chưa commit |
