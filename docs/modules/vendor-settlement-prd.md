# PRD: Đối soát và hoa hồng (Vendor Settlement & Commission)

| | |
|---|---|
| **Sản phẩm** | Nomori Marketplace |
| **Module** | Vendor Settlement & Commission |
| **Cập nhật** | 2026-09-26 |
| **Phụ thuộc** | [Đơn hàng vendor](vendor-orders-prd.md) (đơn con `Completed`), module Thanh toán (hoàn tiền) |
| **Module liên quan** | [Cài đặt shop](vendor-shop-settings-prd.md) |

---

## 1. Mục đích

- Sàn thu tiền của khách. Với mỗi đơn con hoàn tất, sàn giữ lại **hoa hồng** và ghi nhận phần còn lại là **doanh thu của shop**.
- Shop xem rõ từng khoản: tiền hàng, hoa hồng, điều chỉnh, số dư.
- Sàn **chi trả** định kỳ số dư cho shop vào tài khoản ngân hàng của shop.
- Mọi con số đều truy vết được về từng đơn con.

## 2. Khái niệm

| Thuật ngữ | Nghĩa |
|---|---|
| **Tỷ lệ hoa hồng** | Phần trăm sàn giữ lại trên giá trị hàng của đơn con |
| **Hoa hồng** (`Commission`) | Số tiền sàn giữ lại của một đơn con = giá trị hàng × tỷ lệ hoa hồng |
| **Doanh thu shop** | Giá trị hàng + phí vận chuyển shop được hưởng − hoa hồng (Q3) |
| **Bút toán** | Một dòng cộng hoặc trừ vào số dư của shop, luôn gắn với nguồn gốc: đơn con, hoàn tiền, chi trả hoặc điều chỉnh thủ công |
| **Số dư chờ** | Doanh thu của đơn con đã giao nhưng chưa `Completed` (còn trong thời hạn khiếu nại). Chỉ để shop tham khảo, chưa rút được |
| **Số dư khả dụng** | Tổng bút toán đã ghi nhận mà chưa chi trả. Đây là số tiền sẽ được chi trả |
| **Kỳ đối soát** | Khoảng thời gian gom bút toán để chi trả, mặc định 1 tuần (Q2) |
| **Chi trả** (`Payout`) | Một lần sàn chuyển tiền cho shop, gồm các bút toán của một kỳ |

## 3. Phạm vi

### Trong phạm vi

- Cấu hình tỷ lệ hoa hồng: mặc định của sàn, và tỷ lệ riêng cho từng shop.
- Ghi nhận hoa hồng và doanh thu khi đơn con `Completed`.
- Bút toán điều chỉnh khi hoàn tiền sau đối soát, và điều chỉnh thủ công của admin.
- Số dư chờ, số dư khả dụng, lịch sử bút toán của shop.
- Tài khoản ngân hàng nhận tiền của shop.
- Tạo kỳ chi trả, xác nhận đã chuyển khoản, xử lý chi trả thất bại.
- Bảng đối soát theo kỳ, xuất được file CSV.

### Ngoài phạm vi

- Chuyển khoản tự động qua API ngân hàng. Admin chuyển khoản thủ công rồi xác nhận trên hệ thống.
- Hóa đơn điện tử, khấu trừ và kê khai thuế.
- Hoa hồng theo danh mục sản phẩm (Q1).
- Phí dịch vụ khác ngoài hoa hồng (phí thanh toán, phí quảng cáo).
- Voucher: phân bổ chi phí voucher giữa sàn và shop sẽ được bổ sung khi có module Voucher.

## 4. Vai trò

| Vai trò | Quyền trong module |
|---|---|
| **Thành viên shop** | Xem số dư, bút toán, kỳ chi trả của shop mình; quản lý tài khoản ngân hàng nhận tiền |
| **Admin** | Cấu hình hoa hồng; xem số dư mọi shop; tạo và xác nhận chi trả; tạo bút toán điều chỉnh |

## 5. Quy tắc tính tiền

1. **Thời điểm ghi nhận:** khi đơn con chuyển sang `Completed`, hệ thống tạo **một bút toán doanh thu** cho shop.
2. **Tỷ lệ áp dụng:** tỷ lệ riêng của shop nếu có, nếu không thì tỷ lệ mặc định của sàn. Tỷ lệ được **chốt tại thời điểm đơn con được tạo** và lưu vào đơn con; đổi tỷ lệ sau đó không ảnh hưởng đơn cũ.
3. **Công thức** cho mỗi đơn con:
   - `Giá trị hàng` = tổng (giá × số lượng) của các dòng hàng.
   - `Hoa hồng` = `Giá trị hàng` × `tỷ lệ`, làm tròn đến đơn vị đồng (làm tròn nửa lên).
   - `Doanh thu shop` = `Giá trị hàng` − `Hoa hồng` + `Phí vận chuyển shop được hưởng` (Q3).
4. **Đơn COD:** tiền do đơn vị vận chuyển thu hộ và chuyển về sàn; với shop, cách tính giống đơn thanh toán online.
5. **Hoàn tiền trước `Completed`:** đơn con bị hủy thì không phát sinh bút toán.
6. **Hoàn tiền sau `Completed`:** tạo **bút toán âm** bằng phần doanh thu shop tương ứng với khoản hoàn (hoa hồng tương ứng cũng được hoàn lại cho shop). Bút toán âm được trừ vào kỳ chi trả kế tiếp.
7. **Số dư âm:** nếu số dư khả dụng âm thì không chi trả; phần âm được trừ dần vào các kỳ sau.
8. **Bút toán không bao giờ bị sửa hoặc xóa.** Mọi sai sót được sửa bằng một bút toán điều chỉnh mới.

Ví dụ: đơn con có giá trị hàng 1.000.000đ, phí vận chuyển 30.000đ, tỷ lệ 8%.
Hoa hồng = 80.000đ. Doanh thu shop = 1.000.000 − 80.000 + 30.000 = 950.000đ.

## 6. User story và tiêu chí chấp nhận

### Epic A: Cấu hình hoa hồng

**US-A1.** Là admin, tôi muốn đặt tỷ lệ hoa hồng mặc định của sàn.

- [ ] Nhập tỷ lệ từ 0% đến 50%, tối đa 2 chữ số thập phân.
- [ ] Tỷ lệ mới chỉ áp dụng cho đơn con tạo sau thời điểm lưu.
- [ ] Lịch sử thay đổi tỷ lệ được lưu: giá trị cũ, giá trị mới, người đổi, thời điểm.

**US-A2.** Là admin, tôi muốn đặt tỷ lệ riêng cho một shop.

- [ ] Ở chi tiết shop, đặt hoặc bỏ tỷ lệ riêng, kèm ghi chú lý do.
- [ ] Mọi thành viên shop nhận email khi tỷ lệ của shop thay đổi, kèm ngày áp dụng.

**US-A3.** Là thành viên shop, tôi muốn biết tỷ lệ hoa hồng đang áp dụng.

- [ ] Trang "Tài chính" hiện tỷ lệ hiện hành của shop.
- [ ] Chi tiết mỗi đơn con hiện tỷ lệ và số tiền hoa hồng của đơn đó.

### Epic B: Tài khoản ngân hàng nhận tiền

**US-B1.** Là thành viên shop, tôi muốn khai báo tài khoản ngân hàng nhận tiền.

- [ ] Nhập: ngân hàng\* (chọn từ danh sách), số tài khoản\*, tên chủ tài khoản\*, chi nhánh.
- [ ] Đổi tài khoản ngân hàng bắt buộc nhập lại mật khẩu của người đang thao tác.
- [ ] Mọi thành viên shop nhận email ngay khi tài khoản ngân hàng thay đổi, kèm tên người đổi và 4 số cuối của tài khoản mới.
- [ ] Tài khoản mới chỉ được dùng cho chi trả sau **48 giờ** kể từ lúc đổi (Q4), để các thành viên kịp phát hiện nếu có thay đổi trái phép.
- [ ] Trên giao diện, số tài khoản chỉ hiện 4 số cuối.

**US-B2.** Là thành viên shop chưa khai báo tài khoản ngân hàng, tôi muốn được nhắc.

- [ ] Portal hiện cảnh báo "Chưa có tài khoản nhận tiền" cho đến khi khai báo.
- [ ] Shop chưa có tài khoản hợp lệ thì không được đưa vào kỳ chi trả; số dư được giữ lại.

### Epic C: Shop xem tài chính

**US-C1.** Là thành viên shop, tôi muốn xem tổng quan tài chính.

- [ ] Hiện số dư chờ, số dư khả dụng, tổng đã chi trả, và ngày chi trả dự kiến tiếp theo.
- [ ] Hiện tổng doanh thu và tổng hoa hồng theo tháng.

**US-C2.** Là thành viên shop, tôi muốn xem chi tiết từng bút toán.

- [ ] Danh sách bút toán: ngày, loại (Doanh thu đơn, Hoàn tiền, Điều chỉnh, Chi trả), mã đơn con liên quan, giá trị hàng, hoa hồng, số tiền, kỳ chi trả.
- [ ] Lọc theo loại, khoảng ngày, kỳ chi trả; tìm theo mã đơn con.
- [ ] Bấm vào bút toán doanh thu thì mở chi tiết đơn con.

**US-C3.** Là thành viên shop, tôi muốn xem các kỳ chi trả.

- [ ] Danh sách kỳ: khoảng thời gian, tổng doanh thu, tổng hoa hồng, tổng điều chỉnh, số tiền chi trả, trạng thái, mã giao dịch ngân hàng.
- [ ] Xuất bảng đối soát của một kỳ ra file CSV, gồm từng bút toán.

### Epic D: Admin chi trả

**US-D1.** Là admin, tôi muốn tạo kỳ chi trả.

- [ ] Chọn ngày chốt kỳ. Hệ thống gom mọi bút toán chưa chi trả đến ngày đó, theo từng shop.
- [ ] Chỉ tạo chi trả cho shop có số dư khả dụng ≥ mức tối thiểu (mặc định 100.000đ, Q5) và có tài khoản ngân hàng hợp lệ. Shop còn lại được giữ số dư sang kỳ sau.
- [ ] Mỗi shop có một bản ghi chi trả ở trạng thái `Pending`, gồm số tiền và tài khoản ngân hàng tại thời điểm tạo.
- [ ] Xuất được danh sách chi trả ra file CSV để chuyển khoản.

**US-D2.** Là admin, tôi muốn xác nhận đã chuyển khoản.

- [ ] Với từng chi trả, nhập mã giao dịch ngân hàng và bấm **Đã chuyển**. Chi trả chuyển sang `Paid`.
- [ ] Mọi thành viên shop nhận email báo đã chi trả, kèm số tiền và mã giao dịch.

**US-D3.** Là admin, tôi muốn xử lý chi trả thất bại.

- [ ] Đánh dấu **Thất bại** kèm lý do, ví dụ sai số tài khoản. Chi trả chuyển sang `Failed`.
- [ ] Số tiền được trả lại số dư khả dụng của shop và vào kỳ sau.
- [ ] Mọi thành viên shop nhận email kèm lý do, và được nhắc kiểm tra tài khoản ngân hàng.

**US-D4.** Là admin, tôi muốn tạo bút toán điều chỉnh.

- [ ] Nhập shop, số tiền (dương hoặc âm) và lý do bắt buộc.
- [ ] Bút toán hiện với shop kèm lý do.

**US-D5.** Là admin, tôi muốn xem tài chính toàn sàn.

- [ ] Xem số dư của mọi shop, lọc theo shop và trạng thái tài khoản ngân hàng.
- [ ] Xem tổng hoa hồng sàn thu được theo tháng.

## 7. Yêu cầu chức năng

| ID | Yêu cầu | Story |
|---|---|---|
| FR-01 | Tỷ lệ hoa hồng áp dụng = tỷ lệ riêng của shop nếu có, nếu không thì tỷ lệ mặc định | A1, A2 |
| FR-02 | Tỷ lệ được chốt và lưu vào đơn con tại thời điểm tạo đơn | A1, A2 |
| FR-03 | Bút toán doanh thu được tạo đúng một lần khi đơn con chuyển sang `Completed` | C2 |
| FR-04 | Hoa hồng và doanh thu tính theo công thức ở mục 5, làm tròn đến đơn vị đồng | C2 |
| FR-05 | Hoàn tiền sau `Completed` tạo bút toán âm; hoàn tiền trước `Completed` không tạo bút toán | C2 |
| FR-06 | Bút toán không sửa, không xóa được; sai sót được sửa bằng bút toán điều chỉnh có lý do | D4 |
| FR-07 | Số dư khả dụng = tổng các bút toán chưa thuộc chi trả nào ở trạng thái `Pending` hoặc `Paid` | C1, D1 |
| FR-08 | Chi trả chỉ tạo cho shop có số dư khả dụng ≥ mức tối thiểu và có tài khoản ngân hàng đã qua thời gian chờ | B1, D1 |
| FR-09 | Chi trả có 3 trạng thái: `Pending`, `Paid`, `Failed`. `Failed` trả số tiền về số dư khả dụng | D2, D3 |
| FR-10 | Đổi tài khoản ngân hàng bắt buộc nhập lại mật khẩu, gửi email cho mọi thành viên, và có thời gian chờ 48 giờ | B1 |
| FR-11 | Thành viên shop chỉ xem được tài chính của shop mình | C |
| FR-12 | Shop bị khóa vẫn xem được tài chính và vẫn được chi trả số dư | C, D |
| FR-13 | Mọi thao tác cấu hình hoa hồng, đổi tài khoản ngân hàng, tạo và xác nhận chi trả, điều chỉnh được ghi audit log | A, B, D |

## 8. Yêu cầu phi chức năng

| ID | Yêu cầu |
|---|---|
| NFR-01 | Tiền lưu dạng `decimal(18,2)`, đơn vị VND; không dùng số thực dấu phẩy động |
| NFR-02 | Tạo bút toán khi đơn con `Completed` là idempotent: chạy lại không tạo bút toán trùng |
| NFR-03 | Tạo kỳ chi trả chạy trong một transaction; một bút toán không bao giờ thuộc hai chi trả |
| NFR-04 | Số tài khoản ngân hàng được mã hóa khi lưu, chỉ hiện 4 số cuối trên giao diện và trong log |
| NFR-05 | Tổng các bút toán của một shop luôn khớp với số dư hiển thị; có báo cáo đối chiếu tổng tiền thu từ khách = tổng doanh thu shop + tổng hoa hồng + tổng hoàn tiền |
| NFR-06 | API theo chuẩn `/api/v1`, lỗi dạng ProblemDetails, request ghi dữ liệu có CSRF |

## 9. Màn hình

| Màn hình | Đường dẫn | Ai dùng |
|---|---|---|
| Tổng quan tài chính | `/vendor/finance` | Thành viên shop |
| Bút toán | `/vendor/finance/ledger` | Thành viên shop |
| Kỳ chi trả | `/vendor/finance/payouts` | Thành viên shop |
| Tài khoản nhận tiền | `/vendor/finance/bank-account` | Thành viên shop |
| Cấu hình hoa hồng | `/admin/finance/commission` | Admin |
| Số dư các shop | `/admin/finance/balances` | Admin |
| Chi trả | `/admin/finance/payouts` | Admin |

## 10. Email

| Sự kiện | Người nhận | Nội dung chính |
|---|---|---|
| Tỷ lệ hoa hồng của shop thay đổi | Mọi thành viên shop | Tỷ lệ cũ, tỷ lệ mới, ngày áp dụng |
| Tài khoản ngân hàng thay đổi | Mọi thành viên shop | Người đổi, 4 số cuối tài khoản mới, thời điểm có hiệu lực |
| Đã chi trả | Mọi thành viên shop | Số tiền, kỳ, mã giao dịch |
| Chi trả thất bại | Mọi thành viên shop | Lý do, nhắc kiểm tra tài khoản ngân hàng |

## 11. Quyết định

### Đã chốt

| # | Quyết định |
|---|---|
| D1 | Sàn thu tiền của khách và chi trả cho shop (theo `PAYMENT`, `COMMISSION`, `PAYOUT` trong database-v2) |
| D2 | Hoa hồng tính theo từng đơn con |
| D3 | Chuyển khoản do admin thực hiện thủ công, rồi xác nhận trên hệ thống |

### Chờ chốt

| # | Câu hỏi | Đề xuất |
|---|---|---|
| Q1 | Tỷ lệ hoa hồng tính theo đâu? | Một tỷ lệ mặc định cho cả sàn, cộng tỷ lệ riêng cho từng shop. Hoa hồng theo danh mục để sau |
| Q2 | Kỳ chi trả bao lâu một lần? | Hàng tuần, chốt vào thứ Hai |
| Q3 | Phí vận chuyển có tính hoa hồng không, và ai được hưởng? | Không tính hoa hồng trên phí vận chuyển; phí vận chuyển được cộng cho shop vì shop trả phí cho đơn vị vận chuyển |
| Q4 | Thời gian chờ sau khi đổi tài khoản ngân hàng? | 48 giờ |
| Q5 | Mức chi trả tối thiểu? | 100.000đ |
| Q6 | Vì mọi thành viên ngang quyền, có nên chỉ cho một số thành viên đổi tài khoản ngân hàng không? | Chưa tách quyền; dùng nhập lại mật khẩu, email cho mọi thành viên và thời gian chờ 48 giờ để giảm rủi ro. Xem xét lại khi làm phân quyền trong shop |
