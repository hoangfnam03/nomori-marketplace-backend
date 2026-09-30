# PRD: Cài đặt shop (Vendor Shop Settings)

| | |
|---|---|
| **Sản phẩm** | Nomori Marketplace |
| **Module** | Vendor Shop Settings |
| **Cập nhật** | 2026-09-26 |
| **Phụ thuộc** | [Vendors](vendors-prd.md). Logo và ảnh bìa cần module Lưu file (Q3) |
| **Module liên quan** | [Sản phẩm vendor](vendor-products-prd.md), [Đơn hàng vendor](vendor-orders-prd.md), [Đối soát và hoa hồng](vendor-settlement-prd.md) |

---

## 1. Mục đích

- Thành viên shop tự cập nhật thông tin hiển thị của shop, không cần nhờ admin.
- Thành viên shop tạm ngừng bán khi cần (chế độ tạm nghỉ).
- Admin vẫn giữ quyền kiểm soát: khóa shop và sửa mọi thông tin.

## 2. Phạm vi

### Trong phạm vi

- Xem và sửa thông tin shop: tên, email liên hệ, số điện thoại, mô tả, địa chỉ kinh doanh, mã số thuế.
- Bật và tắt chế độ tạm nghỉ.
- Logo và ảnh bìa shop (khi đã có module Lưu file).
- Hiển thị thông tin shop trên storefront.
- Admin khóa và mở khóa shop.

### Ngoài phạm vi

- Thông tin tài khoản ngân hàng nhận tiền. Phần này thuộc module [Đối soát và hoa hồng](vendor-settlement-prd.md).
- Chính sách vận chuyển và đổi trả của shop.
- Tên miền riêng, giao diện shop tùy biến.
- Đổi tên shop phải qua admin duyệt (Q1).

## 3. Vai trò

| Vai trò | Quyền trong module |
|---|---|
| **Thành viên shop** | Xem và sửa thông tin shop của mình, bật hoặc tắt tạm nghỉ |
| **Admin** | Xem và sửa thông tin mọi shop, khóa và mở khóa shop, ghi chú nội bộ |
| **Khách** | Xem thông tin công khai của shop đang hoạt động |

Mọi thành viên shop ngang quyền (theo quyết định D1 của module Vendors).

## 4. User story và tiêu chí chấp nhận

### Epic A: Thành viên sửa thông tin shop

**US-A1.** Là thành viên shop, tôi muốn sửa thông tin shop, để khách thấy thông tin đúng.

- [ ] Trang "Cài đặt shop" trong vendor portal hiện thông tin hiện tại của shop.
- [ ] Sửa được các field: tên shop\*, email liên hệ\*, số điện thoại\*, mô tả, địa chỉ kinh doanh, mã số thuế. Dấu \* là bắt buộc.
- [ ] Tên shop không được trùng với shop khác (không phân biệt hoa thường).
- [ ] Lưu thành công thì storefront hiện thông tin mới ngay.
- [ ] Không sửa được các field chỉ dành cho admin: trạng thái khóa, thứ tự hiển thị, ghi chú nội bộ.
- [ ] Mỗi lần lưu được ghi audit log: ai sửa, sửa những field nào.

**US-A2.** Là thành viên shop, tôi muốn tải lên logo và ảnh bìa, để shop dễ nhận diện.

- [ ] Logo: ảnh JPG, PNG hoặc WebP, tối đa 2 MB, hiển thị dạng vuông.
- [ ] Ảnh bìa: ảnh JPG, PNG hoặc WebP, tối đa 5 MB, tỷ lệ 4:1.
- [ ] Có thể xóa logo hoặc ảnh bìa; khi đó hiển thị ảnh mặc định.
- [ ] Story này chỉ làm khi đã có module Lưu file (Q3).

### Epic B: Tạm nghỉ

**US-B1.** Là thành viên shop, tôi muốn tạm ngừng bán mà không phải đóng shop.

- [ ] Có nút bật và tắt "Tạm nghỉ", kèm lời nhắn không bắt buộc cho khách (tối đa 200 ký tự), ví dụ "Shop nghỉ Tết đến 05/02".
- [ ] Khi tạm nghỉ: trang shop vẫn hiện, kèm thông báo tạm nghỉ và lời nhắn; sản phẩm vẫn xem được nhưng **không thêm vào giỏ hàng được**.
- [ ] Đơn hàng đã đặt trước khi tạm nghỉ vẫn phải được xử lý bình thường.
- [ ] Tắt tạm nghỉ thì bán lại ngay.

### Epic C: Admin kiểm soát shop

**US-C1.** Là admin, tôi muốn khóa shop vi phạm.

- [ ] Khóa shop bắt buộc nhập lý do. Lý do được gửi email cho mọi thành viên shop.
- [ ] Khi bị khóa: trang shop và sản phẩm của shop không hiện trên storefront; khách không đặt được hàng.
- [ ] Thành viên vẫn đăng nhập được vendor portal, thấy thông báo "Shop đang bị khóa" và lý do; vẫn xử lý được đơn đã đặt và xem đối soát; không sửa được thông tin shop và sản phẩm.
- [ ] Thành viên không tự tắt được trạng thái khóa. Chỉ admin mở khóa được.

**US-C2.** Là admin, tôi muốn sửa thông tin shop khi cần.

- [ ] Admin sửa được mọi field, kể cả thứ tự hiển thị và ghi chú nội bộ.
- [ ] Mỗi lần admin sửa được ghi audit log.

### Epic D: Storefront

**US-D1.** Là khách, tôi muốn xem trang shop.

- [ ] Trang shop hiện: logo, ảnh bìa, tên, mô tả, ngày tham gia, danh sách sản phẩm đang bán.
- [ ] Email, số điện thoại, mã số thuế và địa chỉ kinh doanh **không** hiện công khai (Q2).
- [ ] Shop bị khóa hoặc đã xóa thì trả trang "Không tìm thấy".

## 5. Yêu cầu chức năng

| ID | Yêu cầu | Story |
|---|---|---|
| FR-01 | Thành viên shop sửa được thông tin shop của mình, không sửa được shop khác | A1 |
| FR-02 | Tên shop là duy nhất trong hệ thống (không phân biệt hoa thường) | A1 |
| FR-03 | Field chỉ dành cho admin (khóa, thứ tự hiển thị, ghi chú nội bộ) không sửa được từ vendor portal | A1 |
| FR-04 | Shop có 3 trạng thái hoạt động: **Đang bán**, **Tạm nghỉ** (do thành viên bật), **Bị khóa** (do admin). Bị khóa có ưu tiên cao hơn Tạm nghỉ | B1, C1 |
| FR-05 | Tạm nghỉ: shop và sản phẩm vẫn hiện, nhưng không thêm vào giỏ hàng và không đặt hàng được | B1 |
| FR-06 | Bị khóa: shop và sản phẩm không hiện trên storefront, không đặt hàng được, thành viên không sửa được thông tin shop và sản phẩm | C1 |
| FR-07 | Đơn đã đặt vẫn xử lý được khi shop tạm nghỉ hoặc bị khóa | B1, C1 |
| FR-08 | Khóa shop bắt buộc có lý do và gửi email cho mọi thành viên | C1 |
| FR-09 | Storefront không hiện email, số điện thoại, mã số thuế và địa chỉ kinh doanh của shop | D1 |
| FR-10 | Mọi thay đổi thông tin và trạng thái shop được ghi audit log, chỉ lưu tên field đã đổi | A1, B1, C1, C2 |

## 6. Yêu cầu phi chức năng

| ID | Yêu cầu |
|---|---|
| NFR-01 | Shop của người gọi luôn được xác định từ phiên đăng nhập |
| NFR-02 | File ảnh được kiểm tra định dạng thật (không chỉ đuôi file) và giới hạn dung lượng ở backend |
| NFR-03 | Nội dung mô tả và lời nhắn tạm nghỉ được mã hóa khi hiển thị, không chạy được HTML hay script |
| NFR-04 | API theo chuẩn `/api/v1`, lỗi dạng ProblemDetails, request ghi dữ liệu có CSRF |

## 7. Màn hình

| Màn hình | Đường dẫn | Ai dùng |
|---|---|---|
| Cài đặt shop | `/vendor/settings` | Thành viên shop |
| Thông báo shop bị khóa (banner trên mọi trang portal) | `/vendor/**` | Thành viên shop |
| Chi tiết vendor, có nút Khóa và Mở khóa | `/admin/vendors` | Admin |
| Trang shop | `/storefront/vendors/:id` | Khách |

## 8. Email

| Sự kiện | Người nhận | Nội dung chính |
|---|---|---|
| Shop bị khóa | Mọi thành viên shop | Lý do khóa, cách liên hệ hỗ trợ |
| Shop được mở khóa | Mọi thành viên shop | Shop đã bán lại được |

## 9. Quyết định

### Đã chốt

| # | Quyết định |
|---|---|
| D1 | Mọi thành viên shop ngang quyền sửa thông tin shop |
| D2 | Admin khóa shop bằng trạng thái riêng, tách khỏi chế độ tạm nghỉ của shop |

### Chờ chốt

| # | Câu hỏi | Đề xuất |
|---|---|---|
| Q1 | Đổi tên shop có cần admin duyệt không? | Không cần duyệt, nhưng ghi audit và giới hạn 1 lần mỗi 30 ngày để tránh lừa đảo |
| Q2 | Có hiện email hoặc số điện thoại shop cho khách không? | Không; khách liên hệ qua đơn hàng hoặc chat (module sau) |
| Q3 | Làm module Lưu file trước để có logo và ảnh bìa không? | Có, làm trước module Sản phẩm vendor vì sản phẩm cũng cần ảnh |
| Q4 | Tạm nghỉ có giới hạn thời gian tối đa không? | Không giới hạn |
