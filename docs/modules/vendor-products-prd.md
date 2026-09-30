# PRD: Quản lý sản phẩm của người bán (Vendor Products)

| | |
|---|---|
| **Sản phẩm** | Nomori Marketplace |
| **Module** | Vendor Products |
| **Cập nhật** | 2026-09-26 |
| **Phụ thuộc** | [Vendors](vendors-prd.md), Catalog (đã có), module Lưu file cho ảnh sản phẩm |
| **Module liên quan** | [Cài đặt shop](vendor-shop-settings-prd.md), [Đơn hàng vendor](vendor-orders-prd.md) |

---

## 1. Mục đích

- Thành viên shop tự đăng và quản lý sản phẩm của shop mình.
- Sản phẩm dùng chung cấu trúc catalog của sàn: danh mục, nhà sản xuất, thuộc tính, thông số, tag.
- Admin kiểm soát chất lượng: ẩn sản phẩm vi phạm và quản lý cấu trúc catalog dùng chung.

## 2. Phạm vi

### Trong phạm vi

- Thành viên shop tạo, sửa, xóa, đăng bán và ngừng bán sản phẩm của shop.
- Thông tin sản phẩm: tên, mô tả ngắn, mô tả chi tiết, giá, giá cũ, tồn kho, danh mục, nhà sản xuất, ảnh.
- Biến thể: gán thuộc tính (màu, size…), giá trị thuộc tính và tổ hợp thuộc tính, mỗi tổ hợp có tồn kho và giá riêng.
- Thông số kỹ thuật và tag của sản phẩm.
- Tìm kiếm, lọc và xem danh sách sản phẩm của shop.
- Admin ẩn và bỏ ẩn sản phẩm vi phạm, xem sản phẩm theo shop.
- Hiển thị tên shop trên sản phẩm ở storefront, và danh sách sản phẩm trên trang shop.

### Ngoài phạm vi

- Thành viên shop tạo danh mục, nhà sản xuất, định nghĩa thuộc tính hoặc nhóm thông số. Đây là cấu trúc dùng chung, chỉ admin quản lý.
- Nhập hoặc xuất sản phẩm hàng loạt bằng file Excel hay CSV.
- Voucher và chương trình khuyến mãi của shop.
- Đánh giá sản phẩm.
- Duyệt sản phẩm trước khi đăng bán (Q1).

## 3. Vai trò

| Vai trò | Quyền trong module |
|---|---|
| **Thành viên shop** | Quản lý mọi sản phẩm của shop mình |
| **Admin** | Quản lý cấu trúc catalog dùng chung; xem mọi sản phẩm; ẩn và bỏ ẩn sản phẩm; sửa sản phẩm khi cần |
| **Khách** | Xem sản phẩm đang bán |

## 4. User story và tiêu chí chấp nhận

### Epic A: Tạo và sửa sản phẩm

**US-A1.** Là thành viên shop, tôi muốn tạo sản phẩm mới.

- [ ] Form gồm: tên\*, mô tả ngắn, mô tả chi tiết (có định dạng cơ bản), giá\*, giá cũ, tồn kho\*, danh mục\* (chọn một hoặc nhiều từ danh mục của sàn), nhà sản xuất (chọn từ danh sách có sẵn).
- [ ] Giá > 0. Giá cũ, nếu có, phải lớn hơn giá. Tồn kho ≥ 0.
- [ ] Sản phẩm mới ở trạng thái **Nháp**, chưa hiện trên storefront.
- [ ] Sản phẩm luôn thuộc shop của người tạo; không chọn được shop khác.
- [ ] Không đặt được các field chỉ dành cho admin: hiện trên trang chủ, thứ tự hiển thị.

**US-A2.** Là thành viên shop, tôi muốn thêm ảnh sản phẩm.

- [ ] Tối đa 10 ảnh mỗi sản phẩm, định dạng JPG, PNG hoặc WebP, mỗi ảnh tối đa 5 MB.
- [ ] Sắp xếp được thứ tự ảnh; ảnh đầu tiên là ảnh đại diện.
- [ ] Sản phẩm phải có ít nhất 1 ảnh mới đăng bán được.

**US-A3.** Là thành viên shop, tôi muốn tạo biến thể cho sản phẩm.

- [ ] Chọn thuộc tính từ danh sách thuộc tính admin đã định nghĩa (ví dụ Màu, Size) và nhập giá trị (Đỏ, Xanh; S, M, L).
- [ ] Hệ thống tạo được các tổ hợp (Đỏ-S, Đỏ-M…). Mỗi tổ hợp có SKU, tồn kho và giá riêng (không bắt buộc; nếu trống thì dùng giá sản phẩm).
- [ ] Sản phẩm có biến thể thì tồn kho sản phẩm bằng tổng tồn kho các tổ hợp.
- [ ] Không xóa được giá trị thuộc tính đang có trong đơn hàng chưa hoàn tất.

**US-A4.** Là thành viên shop, tôi muốn gán thông số kỹ thuật và tag.

- [ ] Chọn thông số từ các nhóm thông số admin đã định nghĩa và chọn giá trị.
- [ ] Nhập tag tự do; tag trùng tên với tag đã có thì dùng lại tag đó.

**US-A5.** Là thành viên shop, tôi muốn sửa sản phẩm.

- [ ] Sửa được mọi field như khi tạo.
- [ ] Đổi giá không ảnh hưởng đơn đã đặt; đơn đã đặt giữ giá lúc đặt.

### Epic B: Đăng bán và ngừng bán

**US-B1.** Là thành viên shop, tôi muốn đăng bán sản phẩm.

- [ ] Chỉ đăng bán được khi đủ điều kiện: có tên, giá, ít nhất 1 danh mục và ít nhất 1 ảnh.
- [ ] Đăng bán xong thì sản phẩm hiện trên storefront ngay (Q1).
- [ ] Sản phẩm hết hàng (tồn kho = 0) vẫn hiện nhưng có nhãn "Hết hàng" và không mua được.

**US-B2.** Là thành viên shop, tôi muốn ngừng bán sản phẩm.

- [ ] Ngừng bán thì sản phẩm biến mất khỏi storefront và không thêm vào giỏ hàng được.
- [ ] Sản phẩm đã có trong giỏ hàng của khách bị đánh dấu "Không còn bán" khi khách mở giỏ.

**US-B3.** Là thành viên shop, tôi muốn xóa sản phẩm.

- [ ] Có hộp xác nhận trước khi xóa.
- [ ] Xóa là xóa mềm; sản phẩm đã xóa không hiện ở đâu, nhưng vẫn giữ trong các đơn hàng cũ.

### Epic C: Danh sách sản phẩm của shop

**US-C1.** Là thành viên shop, tôi muốn xem và tìm sản phẩm của shop.

- [ ] Lọc theo trạng thái: Nháp, Đang bán, Ngừng bán, Bị admin ẩn, Hết hàng.
- [ ] Tìm theo tên hoặc SKU; sắp xếp theo mới nhất, tên, giá, tồn kho.
- [ ] Mỗi dòng hiện ảnh đại diện, tên, giá, tồn kho, trạng thái.
- [ ] Có cảnh báo cho sản phẩm sắp hết hàng (tồn kho ≤ 5, cấu hình được).

### Epic D: Admin kiểm soát sản phẩm

**US-D1.** Là admin, tôi muốn ẩn sản phẩm vi phạm.

- [ ] Ẩn sản phẩm bắt buộc có lý do. Mọi thành viên shop nhận email kèm lý do.
- [ ] Sản phẩm bị ẩn không hiện trên storefront. Thành viên shop thấy trạng thái "Bị admin ẩn" và lý do; sửa được nội dung nhưng **không tự đăng bán lại được**.
- [ ] Thành viên sửa xong có thể bấm "Yêu cầu xem lại". Admin bỏ ẩn hoặc giữ nguyên.

**US-D2.** Là admin, tôi muốn xem sản phẩm theo shop.

- [ ] Danh sách sản phẩm ở admin lọc được theo shop và theo trạng thái.
- [ ] Admin sửa được mọi field, kể cả hiện trên trang chủ và thứ tự hiển thị.

### Epic E: Storefront

**US-E1.** Là khách, tôi muốn biết sản phẩm của shop nào.

- [ ] Trang chi tiết sản phẩm hiện tên shop, có link sang trang shop.
- [ ] Trang shop liệt kê sản phẩm đang bán của shop, có phân trang, lọc theo danh mục và sắp xếp.
- [ ] Sản phẩm của shop tạm nghỉ vẫn hiện nhưng không mua được; sản phẩm của shop bị khóa không hiện.

## 5. Yêu cầu chức năng

| ID | Yêu cầu | Story |
|---|---|---|
| FR-01 | Mỗi sản phẩm thuộc đúng một shop. Shop được lấy từ người tạo, không đổi được sau khi tạo | A1 |
| FR-02 | Thành viên chỉ thấy và thao tác được sản phẩm của shop mình | A–C |
| FR-03 | Thành viên chỉ dùng được danh mục, nhà sản xuất, thuộc tính và nhóm thông số do admin định nghĩa | A1, A3, A4 |
| FR-04 | Sản phẩm có 4 trạng thái: **Nháp**, **Đang bán**, **Ngừng bán**, **Bị admin ẩn** | B1–B3, D1 |
| FR-05 | Điều kiện đăng bán: có tên, giá > 0, ít nhất 1 danh mục, ít nhất 1 ảnh | B1 |
| FR-06 | Sản phẩm hiện trên storefront khi và chỉ khi: trạng thái Đang bán, shop không bị khóa và chưa xóa | B1, E1 |
| FR-07 | Mua được khi và chỉ khi: hiện trên storefront, còn hàng, và shop không tạm nghỉ | B1, E1 |
| FR-08 | Tồn kho được quản lý theo tổ hợp thuộc tính nếu sản phẩm có biến thể; nếu không thì theo sản phẩm | A3 |
| FR-09 | Đơn hàng lưu lại tên, giá và biến thể tại thời điểm đặt; sửa hoặc xóa sản phẩm không làm đổi đơn cũ | A5, B3 |
| FR-10 | Chỉ admin mới bỏ ẩn được sản phẩm bị ẩn | D1 |
| FR-11 | Ẩn sản phẩm bắt buộc có lý do và gửi email cho mọi thành viên shop | D1 |
| FR-12 | Field chỉ dành cho admin (hiện trên trang chủ, thứ tự hiển thị) không sửa được từ vendor portal | A1, D2 |
| FR-13 | Mọi thao tác tạo, sửa, đổi trạng thái, xóa sản phẩm được ghi audit log | A–D |

## 6. Yêu cầu phi chức năng

| ID | Yêu cầu |
|---|---|
| NFR-01 | Mọi API sản phẩm kiểm tra sản phẩm thuộc shop của người gọi. Sản phẩm của shop khác trả `404` |
| NFR-02 | Trừ tồn kho khi đặt hàng không được để tồn kho âm, kể cả khi nhiều khách đặt cùng lúc |
| NFR-03 | Mô tả chi tiết được lọc HTML an toàn (chỉ giữ thẻ định dạng cơ bản), không chạy được script |
| NFR-04 | Ảnh được kiểm tra định dạng thật, giới hạn dung lượng và tạo sẵn ảnh thu nhỏ |
| NFR-05 | Danh sách sản phẩm trên storefront trả về trong dưới 500 ms với 10.000 sản phẩm |
| NFR-06 | API theo chuẩn `/api/v1`, lỗi dạng ProblemDetails, request ghi dữ liệu có CSRF. Dùng chung route `/products` cho admin, thành viên và khách, giống cách làm ở module Vendors |

## 7. Màn hình

| Màn hình | Đường dẫn | Ai dùng |
|---|---|---|
| Danh sách sản phẩm của shop | `/vendor/products` | Thành viên shop |
| Tạo và sửa sản phẩm (các tab: Thông tin, Ảnh, Biến thể, Thông số và tag) | `/vendor/products/new`, `/vendor/products/:id` | Thành viên shop |
| Quản lý catalog, thêm bộ lọc theo shop và nút Ẩn, Bỏ ẩn | `/admin/catalog` | Admin |
| Chi tiết sản phẩm, có tên shop | `/storefront/products/:id` | Khách |
| Trang shop, có danh sách sản phẩm | `/storefront/vendors/:id` | Khách |

## 8. Email

| Sự kiện | Người nhận | Nội dung chính |
|---|---|---|
| Sản phẩm bị admin ẩn | Mọi thành viên shop | Tên sản phẩm, lý do |
| Sản phẩm được bỏ ẩn | Mọi thành viên shop | Tên sản phẩm |
| Thành viên yêu cầu xem lại | Admin (Q4) | Tên shop, tên sản phẩm |

## 9. Quyết định

### Đã chốt

| # | Quyết định |
|---|---|
| D1 | Cấu trúc catalog (danh mục, nhà sản xuất, thuộc tính, nhóm thông số) do admin quản lý, dùng chung cho mọi shop |
| D2 | Mọi thành viên shop ngang quyền quản lý sản phẩm |

### Chờ chốt

| # | Câu hỏi | Đề xuất |
|---|---|---|
| Q1 | Sản phẩm mới có cần admin duyệt trước khi hiện trên storefront không? | Không; đăng bán hiện ngay, admin ẩn sau nếu vi phạm |
| Q2 | Có giới hạn số sản phẩm mỗi shop không? | Không giới hạn trong giai đoạn đầu |
| Q3 | Thành viên có đề xuất danh mục mới cho admin được không? | Để sau |
| Q4 | Có gửi email cho admin khi có yêu cầu xem lại sản phẩm bị ẩn không? | Không; hiện ở danh sách "Chờ xem lại" trong trang admin |
