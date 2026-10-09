# Nomori Marketplace: tổng quan module

Danh sách các module đang có trong code (backend và Angular). Chi tiết tiến độ từng slice và các phần còn thiếu nằm ở `feature-progress.md`, còn thiết kế của từng module nằm ở `docs/modules/`.

- **Thống kê ngày:** 2026-10-05, trên nhánh `feat/orders/selection-and-receipt`.
- **Cách thống kê:** dựa trên thư mục code (`Api/Modules`, `Services`, các trang Angular) và tài liệu trong `docs/modules/`. Chưa kiểm tra lại từng module có đủ chức năng hay không.

## 1. Tài khoản và người dùng

| Module | Chức năng chính | Giao diện | Tài liệu |
|---|---|---|---|
| Authentication | Đăng ký, đăng nhập (mật khẩu và OTP), xác thực email, quên/đặt lại mật khẩu, đổi email | Trang đăng nhập, đăng ký, quên mật khẩu | `modules/authentication.md` |
| Authorization | Vai trò (admin, vendor, customer), quyền, audit log | Trang 403 | `modules/authorization.md` |
| Customer profile | Hồ sơ, avatar, sổ địa chỉ, dữ liệu tài khoản | `/customer/profile`, dữ liệu tài khoản | `modules/customer-profile.md` |

## 2. Cấu hình hệ thống

| Module | Chức năng chính | Giao diện | Tài liệu |
|---|---|---|---|
| F07-A Currency | Tiền tệ chính, tiền tệ hiển thị theo tỷ giá, làm tròn | Admin tiền tệ, bộ chọn tiền tệ ở storefront | `modules/f07a-currency.md` |
| F07-C Countries & addresses | Quốc gia, tỉnh/bang, kiểm tra địa chỉ theo thư mục | `/admin/countries` | `modules/f07c-countries-addresses.md` |
| F07-E Tax | Nhóm thuế, thuế suất theo quốc gia hoặc tỉnh/bang, thuế trên đơn | `/admin/tax` | `modules/f07e-tax.md` |
| F08-A Media | Upload ảnh lên MinIO/S3 bằng presigned URL | Dùng chung | `modules/f08a-media.md` |

## 3. Shop (vendor)

| Module | Chức năng chính | Giao diện | Tài liệu |
|---|---|---|---|
| Vendors | Đăng ký mở shop, admin duyệt đơn đăng ký, thành viên shop, cài đặt shop (kể cả avatar) | Đăng ký bán hàng, `/vendor/*`, admin quản lý shop và đơn đăng ký | `modules/vendors*.md`, `modules/vendor-shop-settings-prd.md` |

## 4. Sản phẩm và catalog

| Module | Chức năng chính | Giao diện | Tài liệu |
|---|---|---|---|
| F09-A Taxonomy | Danh mục, thương hiệu | Admin catalog | `modules/f09a-taxonomy.md` |
| F10-A/B/C Product | Shop sở hữu sản phẩm, vòng đời (nháp, đang bán, ngừng bán, bị admin ẩn), nội dung sản phẩm | Vendor, admin | `modules/f10a-*.md`, `f10b-*.md`, `f10c-*.md` |
| F11-A Product pictures | Ảnh sản phẩm có thứ tự, ảnh chính | Vendor | `modules/f11a-product-pictures.md` |
| F11-B Variants, specs, tags | Biến thể, thông số kỹ thuật, tag | Vendor, admin | `modules/f11b-product-variants-specs-tags.md` |
| F12-A Inventory | Tồn kho, giữ hàng tạm, sổ cái nhập xuất kho | Vendor | `modules/f12a-inventory.md` |
| F13-A Catalog search | Tìm kiếm và bộ lọc (danh mục, giá, thương hiệu, thông số, tag) | Trang danh sách sản phẩm | `modules/f13a-catalog-search.md` |
| F14-A Pricing | Giá bán, giá đặc biệt, giá theo số lượng | Vendor | `modules/f14a-pricing.md` |

## 5. Mua hàng

| Module | Chức năng chính | Giao diện | Tài liệu |
|---|---|---|---|
| F15-A Discount codes | Mã giảm giá của sàn và của từng shop | `/admin/discounts`, `/vendor/discounts` | `modules/f15a-discount-codes.md` |
| F16-A Cart | Giỏ hàng nhóm theo shop, chọn từng dòng để mua (mặc định chưa chọn dòng nào) | `/storefront/cart` | `modules/f16a-cart.md` |
| F17-A Checkout | Xem trước và đặt hàng (chống đặt trùng, trừ kho), chỉ mua các dòng đã chọn (`cartItemIds`) | `/storefront/checkout` | `modules/f17a-checkout.md` |
| F18-A Orders | Đơn tổng và đơn của từng shop; shop xử lý đơn; khách bấm "Đã nhận được hàng" thì đơn chuyển Đã giao → Hoàn thành | `/customer/orders`, `/vendor/orders`, `/admin/orders` | `modules/f18a-orders.md`, `modules/vendor-orders-prd.md` |
| F19-A Payments | Phương thức và trạng thái thanh toán (COD, sandbox) | `/admin/payments` | `modules/f19a-payments.md` |
| F20-A Shipping rates | Phí ship theo shop và khu vực, miễn phí ship từ một mức tạm tính | `/vendor/shipping` | `modules/f20a-shipping-rates.md` |

## 6. Hạ tầng dùng chung

Email (gửi mail), ApplicationInfo, Security.

## 7. Mới có tài liệu, chưa có code

- **Vendor settlement** (đối soát và trả tiền cho shop): mới có PRD `modules/vendor-settlement-prd.md`, trong code chưa có service hay API nào.

## 8. Đã nhắc tới nhưng chưa làm

- Job tự động chuyển đơn sang Hoàn thành khi khách không bấm "Đã nhận được hàng".
- Trả hàng và hoàn tiền (F21).
- Quản lý phí ship cho shop của nền tảng "Nomori Official": shop này chưa có thành viên nào nên hiện không ai nhập phí ship được qua giao diện.
