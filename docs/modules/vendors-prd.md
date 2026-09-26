# PRD: Đăng ký và quản lý tài khoản người bán (Vendor)

| | |
|---|---|
| **Sản phẩm** | Nomori Marketplace |
| **Module** | Vendors, gồm đăng ký vendor và thành viên vendor |
| **Cập nhật** | 2026-09-26 |
| **Thiết kế kỹ thuật** | [vendors.vi.md](vendors.vi.md) / [vendors.md](vendors.md) |
| **Thiết kế API** | [vendors-api.md](vendors-api.md) |

---

## 1. Mục đích

- Người dùng tự đăng ký mở shop. Admin xét duyệt đơn.
- Khi đơn được duyệt, shop được tạo và người đăng ký quản lý shop qua vendor portal.
- Một shop có nhiều tài khoản thành viên, và mọi thành viên có quyền như nhau.
- Bất kỳ thành viên nào cũng tạo được tài khoản mới cho shop.

## 2. Phạm vi

### Trong phạm vi

- Nộp, sửa, hủy đơn đăng ký mở shop.
- Admin duyệt hoặc từ chối đơn.
- Vendor portal: xem thông tin shop, quản lý thành viên.
- Thành viên tạo tài khoản mới cho shop, gửi lại email kích hoạt, gỡ thành viên, tự rời shop.
- Admin xem, sửa, xóa shop, ghi chú nội bộ, xem và gỡ thành viên.
- Email thông báo và audit log.

### Ngoài phạm vi

- Phân quyền khác nhau trong shop (chủ shop, quản lý, nhân viên).
- Một tài khoản thuộc nhiều shop.
- Đưa một tài khoản đã có vào shop.
- Admin tạo shop hoặc tạo tài khoản thành viên.
- Upload giấy tờ khi đăng ký.
- Người bán tự sửa thông tin shop, quản lý sản phẩm, đơn hàng, đối soát và hoa hồng.

## 3. Vai trò

| Vai trò | Mô tả | Role trong hệ thống |
|---|---|---|
| **Khách hàng** | Người dùng đã có tài khoản | `Registered` |
| **Người nộp đơn** | Khách hàng đã gửi đơn mở shop | `Registered` |
| **Thành viên shop** | Tài khoản đang thuộc một shop | `Registered` + `Vendors` |
| **Admin** | Người vận hành sàn | `Administrator` |

Role được cộng dồn. Thành viên shop vẫn mua hàng như khách hàng bình thường.

## 4. User story và tiêu chí chấp nhận

### Epic A: Đăng ký mở shop

**US-A1.** Là khách hàng, tôi muốn nộp đơn mở shop, để bán hàng trên Nomori.

- [ ] Mục "Bán hàng cùng Nomori" dẫn tới trang đăng ký, chỉ hiện khi đã đăng nhập.
- [ ] Form gồm: tên shop\*, email liên hệ\*, số điện thoại\*, mô tả, mã số thuế, địa chỉ kinh doanh. Dấu \* là bắt buộc.
- [ ] Nếu chưa xác thực email, không gửi được đơn, và trang hiện link gửi lại email xác thực.
- [ ] Nếu đang thuộc một shop, không gửi được đơn.
- [ ] Nếu tên shop trùng với shop khác hoặc với đơn đang chờ duyệt, báo lỗi tại ô tên shop.
- [ ] Gửi thành công thì trang chuyển sang trạng thái "Đang chờ duyệt".

**US-A2.** Là người nộp đơn, tôi muốn xem, sửa hoặc hủy đơn khi đơn còn đang chờ duyệt.

- [ ] Khi đơn đang chờ duyệt, trang hiện thông tin đã gửi, kèm nút **Sửa** và **Hủy**.
- [ ] Mỗi tài khoản chỉ có một đơn đang chờ duyệt. Gửi lần hai bị chặn, kể cả khi bấm gửi hai lần liên tiếp.
- [ ] Đơn đã duyệt, đã từ chối hoặc đã hủy thì không sửa được.

**US-A3.** Là người nộp đơn bị từ chối, tôi muốn biết lý do và nộp lại.

- [ ] Nhận email báo từ chối, trong email có lý do.
- [ ] Trang đăng ký hiện lý do và nút **Nộp lại**. Form được điền sẵn thông tin của đơn cũ.
- [ ] Đơn cũ được giữ lại làm lịch sử.

**US-A4.** Là người nộp đơn được duyệt, tôi muốn vào quản lý shop ngay.

- [ ] Nhận email báo đã được duyệt, trong email có link tới vendor portal.
- [ ] Không cần đăng nhập lại, menu "Vendor portal" hiện ngay.
- [ ] Tôi là thành viên đầu tiên của shop.

### Epic B: Admin duyệt đơn

**US-B1.** Là admin, tôi muốn xem danh sách đơn đăng ký để xử lý.

- [ ] Lọc theo trạng thái: Chờ duyệt, Đã duyệt, Từ chối, Đã hủy. Mặc định hiện Chờ duyệt.
- [ ] Có tìm kiếm theo tên shop hoặc email, và có phân trang.
- [ ] Xem được chi tiết đơn, kèm email tài khoản người nộp.

**US-B2.** Là admin, tôi muốn duyệt đơn.

- [ ] Trước khi duyệt có hộp xác nhận. Trong hộp này tôi có thể sửa tên shop và thêm ghi chú nội bộ.
- [ ] Duyệt xong thì 3 việc cùng xảy ra: shop được tạo, người nộp trở thành thành viên, người nộp có quyền người bán. Nếu một việc lỗi thì cả ba đều không xảy ra.
- [ ] Nếu người nộp đã thuộc một shop, việc duyệt bị chặn và có thông báo.
- [ ] Không duyệt lại được đơn đã xử lý.

**US-B3.** Là admin, tôi muốn từ chối đơn kèm lý do.

- [ ] Nút **Từ chối** chỉ bấm được khi đã nhập lý do (tối đa 2000 ký tự).
- [ ] Người nộp nhận email có lý do đó.

### Epic C: Thành viên shop

**US-C1.** Là thành viên shop, tôi muốn tạo tài khoản mới cho người cùng vận hành shop.

- [ ] Trang "Thành viên" trong vendor portal có form: email\*, tên, họ.
- [ ] Nếu email đã có tài khoản trên Nomori, báo lỗi "Email đã được sử dụng".
- [ ] Khi shop đã có 20 thành viên, báo lỗi "Shop đã đủ thành viên".
- [ ] Tạo thành công thì người được tạo nhận email có link đặt mật khẩu, và trong danh sách người đó hiện trạng thái "Chờ kích hoạt".
- [ ] Người tạo không biết và không đặt được mật khẩu của người được tạo.

**US-C2.** Là người được thêm vào shop, tôi muốn kích hoạt tài khoản.

- [ ] Link trong email có hạn 72 giờ và chỉ dùng được một lần.
- [ ] Đặt mật khẩu xong thì email được coi là đã xác thực. Tôi đăng nhập được và thấy shop trong vendor portal.
- [ ] Nếu link hết hạn, bất kỳ thành viên nào của shop cũng bấm **Gửi lại email** được. Link cũ bị vô hiệu.

**US-C3.** Là thành viên shop, tôi muốn gỡ thành viên khác hoặc tự rời shop.

- [ ] Mọi thành viên đều gỡ được bất kỳ thành viên nào.
- [ ] Có hộp xác nhận trước khi gỡ.
- [ ] Không gỡ được thành viên cuối cùng, và thành viên cuối cùng không tự rời được.
- [ ] Người bị gỡ bị đăng xuất, mất quyền người bán, nhưng vẫn dùng tài khoản để mua hàng bình thường.
- [ ] Tự rời shop xong thì được đưa về trang chủ storefront.

### Epic D: Admin quản lý shop

**US-D1.** Là admin, tôi muốn xem và kiểm soát các shop.

- [ ] Xem danh sách shop, sửa thông tin shop, thêm ghi chú nội bộ.
- [ ] Xem được danh sách thành viên của từng shop. Gỡ được thành viên, trừ thành viên cuối cùng.
- [ ] Xóa shop thì mọi thành viên đều mất quyền người bán.
- [ ] Không có chức năng tạo shop, tạo tài khoản thành viên hay gán tài khoản vào shop.

## 5. Yêu cầu chức năng

| ID | Yêu cầu | Story |
|---|---|---|
| FR-01 | Chỉ khách hàng đã đăng nhập, đã xác thực email và chưa thuộc shop nào mới nộp được đơn | A1 |
| FR-02 | Mỗi tài khoản có tối đa 1 đơn đang chờ duyệt | A2 |
| FR-03 | Tên shop không được trùng với shop đang tồn tại hoặc đơn đang chờ duyệt (không phân biệt hoa thường) | A1 |
| FR-04 | Chỉ đơn đang chờ duyệt mới sửa, hủy, duyệt hoặc từ chối được | A2, B2, B3 |
| FR-05 | Duyệt đơn tạo shop, gán thành viên và cấp quyền trong cùng một thao tác: hoặc thành công cả ba, hoặc không có gì thay đổi | B2 |
| FR-06 | Từ chối bắt buộc có lý do, và lý do được gửi cho người nộp | B3 |
| FR-07 | Shop chỉ được tạo qua đơn đăng ký đã duyệt | B2, D1 |
| FR-08 | Mỗi tài khoản thuộc tối đa 1 shop | A1, B2, C1 |
| FR-09 | Thành viên chỉ tạo được tài khoản mới; email đã có tài khoản thì bị từ chối | C1 |
| FR-10 | Mỗi shop có tối đa 20 thành viên, giá trị này cấu hình được | C1 |
| FR-11 | Link đặt mật khẩu có hạn 72 giờ, dùng một lần, gửi lại được | C2 |
| FR-12 | Mọi thành viên ngang quyền, gồm tạo, gửi lại email và gỡ thành viên | C1–C3 |
| FR-13 | Shop luôn còn ít nhất 1 thành viên. Chỉ việc xóa shop mới làm shop hết thành viên | C3, D1 |
| FR-14 | Tài khoản có quyền người bán khi và chỉ khi đang thuộc một shop | A4, C3, D1 |
| FR-15 | Người bị gỡ khỏi shop bị đăng xuất khỏi các phiên đang mở | C3 |
| FR-16 | Gửi email khi đơn được duyệt, khi đơn bị từ chối, và khi được thêm vào shop | A3, A4, C1 |
| FR-17 | Mọi thao tác nộp, sửa, hủy, duyệt, từ chối đơn và thêm, gỡ thành viên đều được ghi audit log | B, C, D |

## 6. Yêu cầu phi chức năng

| ID | Yêu cầu |
|---|---|
| NFR-01 | **Bảo mật:** shop và tài khoản luôn được xác định từ phiên đăng nhập, không lấy từ dữ liệu client gửi lên. Thành viên không xem hay thao tác được với thành viên của shop khác, và cũng không dò được tài khoản nào thuộc shop khác |
| NFR-02 | **Bảo mật:** không ai ngoài chủ tài khoản biết mật khẩu. Link đặt mật khẩu được lưu dạng hash |
| NFR-03 | **Toàn vẹn dữ liệu:** các thao tác nhiều bước (duyệt đơn, thêm, gỡ thành viên, xóa shop) chạy trong một transaction |
| NFR-04 | **Chống trùng lặp:** bấm gửi đơn hai lần gần như cùng lúc cũng chỉ tạo ra 1 đơn |
| NFR-05 | **Quyền riêng tư:** audit log không lưu dữ liệu cá nhân như tên hay số điện thoại, chỉ lưu ID và tên field |
| NFR-06 | **Độ tin cậy:** gửi email lỗi không làm hỏng việc duyệt đơn hay tạo thành viên. Lỗi được ghi log và có thể gửi lại |
| NFR-07 | **Tương thích:** API theo chuẩn `/api/v1`, lỗi trả về dạng ProblemDetails, các request ghi dữ liệu có CSRF |
| NFR-08 | **Giao diện:** mọi trang có đủ trạng thái đang tải, trống, lỗi và không có quyền, và dùng được trên màn hình điện thoại |

## 7. Màn hình

| Màn hình | Đường dẫn | Ai dùng |
|---|---|---|
| Đăng ký mở shop | `/customer/become-vendor` | Khách hàng, người nộp đơn |
| Duyệt đơn vendor | `/admin/vendor-applications` | Admin |
| Quản lý vendor, có khung Thành viên | `/admin/vendors` | Admin |
| Vendor portal: thông tin shop | `/vendor` | Thành viên shop |
| Vendor portal: thành viên | `/vendor/members` | Thành viên shop |
| Đặt mật khẩu khi kích hoạt tài khoản | `/auth/reset-password?setup=1` | Người được thêm vào shop |

Các trạng thái của trang đăng ký mở shop:

| Trạng thái | Hiển thị |
|---|---|
| Chưa xác thực email | Thông báo và link gửi lại email xác thực |
| Chưa có đơn, hoặc đơn đã hủy | Form đăng ký |
| Đang chờ duyệt | Thông tin đơn, nút Sửa và Hủy |
| Bị từ chối | Lý do và nút Nộp lại |
| Đã duyệt | Link tới vendor portal |

## 8. Email

| Sự kiện | Người nhận | Nội dung chính |
|---|---|---|
| Đơn được duyệt | Người nộp đơn | Chúc mừng, kèm link tới vendor portal |
| Đơn bị từ chối | Người nộp đơn | Lý do từ chối, kèm link để nộp lại |
| Được thêm vào shop | Thành viên mới | Tên shop, kèm link đặt mật khẩu (hạn 72 giờ) |

## 9. Quyết định

### Đã chốt

| # | Quyết định |
|---|---|
| D1 | Một shop có nhiều tài khoản, và mọi thành viên có quyền như nhau |
| D2 | Bất kỳ thành viên nào cũng tạo được tài khoản mới cho shop |
| D3 | Mỗi tài khoản chỉ thuộc tối đa một shop |
| D4 | Thành viên gỡ được nhau và tự rời shop được, trừ thành viên cuối cùng |
| D5 | Tối đa 20 thành viên mỗi shop, cấu hình được |
| D6 | Link đặt mật khẩu có hạn 72 giờ và gửi lại được |
| D7 | Email đã có tài khoản thì báo lỗi. Không có chức năng đưa tài khoản đã có vào shop |
| D8 | Shop chỉ được tạo qua đơn đăng ký. Admin không tạo shop và không tạo tài khoản thành viên |
| D9 | Shop hoạt động và hiện trên storefront ngay sau khi được duyệt |
| D10 | Chưa có upload giấy tờ khi đăng ký; làm sau khi hệ thống có chức năng lưu file |
| D11 | Không gửi email báo admin khi có đơn mới; admin xem trên trang duyệt đơn |
| D12 | Bị từ chối thì được nộp lại ngay, không cần chờ |
| D13 | Duyệt đơn dùng chung permission `vendor.manage` với quản lý shop; permission này chỉ cấp cho role `Administrator` |
| D14 | Người bán tự sửa thông tin shop, quản lý sản phẩm, đơn hàng, đối soát và hoa hồng thuộc các module sau |
