# Giả lập shipper GHN cập nhật trạng thái đơn hàng (môi trường dev)

Hướng dẫn dựng môi trường local để thử luồng giao hàng Giao Hàng Nhanh (GHN) từ đầu đến cuối: đặt hàng, gửi vận đơn qua GHN, rồi **giả lập shipper** lấy hàng → đang giao → giao thành công, và thấy đơn trên Nomori tự đổi trạng thái.

Môi trường dev của GHN (staging) **không có shipper thật**, nên trạng thái vận đơn không bao giờ tự đổi. Chúng ta giả làm GHN, gửi webhook vào API của mình bằng Postman. API xử lý request đó y hệt như khi GHN thật gửi.

## 1. Luồng tổng quát

```
Production (có shipper thật)
  Shipper cập nhật trên app GHN ──► Máy chủ GHN ──► webhook ──► API Nomori ──► đơn đổi trạng thái

Dev (giả lập)
  Postman (giả làm GHN) ─────────────────────────► webhook ──► API Nomori ──► đơn đổi trạng thái
                                (qua ngrok hoặc localhost)
```

| Sự kiện webhook | Kết quả trên Nomori |
|---|---|
| `picked`, `storing`, `transporting`, `sorting`, `delivering`, `delivery_fail`, `cancel`, `return`… | Thêm một dòng hành trình "GHN: …" vào đơn |
| `delivered` | Thêm dòng "GHN: Giao hàng thành công" **và** chuyển đơn của shop sang **Đã giao** |
| Loại sự kiện khác (`create`, `update_weight`…), mã vận đơn lạ, shop GHN khác | Bỏ qua, vẫn trả `200` (`outcome: ignored` hoặc `handled`) |

## 2. Chuẩn bị

- Backend và frontend ở nhánh có tính năng GHN (`feat/shipping/ghn` hoặc `main`/`master` sau khi đã merge).
- SQL Server local đã chạy migration mới nhất, tức có `GhnMigration` và `GhnPickupMigration`. Chạy DbMigrator nếu chưa.
- Đã cài: **ngrok** (`ngrok.com`), **Postman**.
- Một tài khoản khách và một tài khoản thành viên shop trên Nomori local.

## 3. Tài khoản GHN dev

1. Đăng ký tài khoản tại **https://5sao.ghn.dev**.
2. **Token API**: vào **Chủ cửa hàng → Xem thông tin**, copy Token.
3. **ShopId**: vào **Quản lý cửa hàng**, lấy **ID của cửa hàng**.
   > Đừng nhầm với **Client ID** (mã tài khoản). Dùng Client ID thì GHN báo *"Lỗi lấy thông tin shop"*.
4. **Cập nhật địa chỉ cho cửa hàng** (sửa cửa hàng, nhập đủ tỉnh, quận, phường, số nhà). Cửa hàng mới tạo không có địa chỉ, và khi đó GHN từ chối mọi yêu cầu báo phí với lỗi *"Vui lòng cập nhật thông tin địa chỉ cửa hàng"*.
   > Mọi shop của Nomori dùng chung cửa hàng GHN này để gửi. Địa chỉ lấy hàng thật của từng đơn là địa chỉ lấy hàng mà mỗi shop tự khai trên Nomori (bước 5.1).

## 4. Cấu hình backend

1. Ở thư mục gốc `nomori-marketplace-backend`, copy `.env.example` thành `.env` và điền:
   ```env
   Shipping__Ghn__Enabled=true
   Shipping__Ghn__Token=<token GHN dev>
   Shipping__Ghn__ShopId=<shop id GHN dev>
   Shipping__Ghn__WebhookSecret=<chuỗi ngẫu nhiên tự đặt, ví dụ 32 ký tự>
   ```
   - `.env` đã nằm trong `.gitignore`. **Không commit file này**, và không ghi token vào `appsettings*.json` hay `launchSettings.json` (hai file đó có trong git).
   - Biến môi trường thật của Windows **được ưu tiên hơn** `.env`. Nếu trước đây đã `setx` các biến `Shipping__Ghn__*`, xoá chúng đi, hoặc nhớ rằng giá trị trong `.env` sẽ không có tác dụng.
2. Chạy API như bình thường. Profile `https` mở hai cổng `https://localhost:7014` và `http://localhost:5214`, và cổng 5214 tự chuyển hướng sang 7014.
3. Chạy frontend (`ng serve`, `http://localhost:4200`).
4. Kiểm tra: đăng nhập trên web rồi mở `http://localhost:4200/api/v1/shipping/ghn/status`. Phải thấy:
   ```json
   { "enabled": true }
   ```
   Nếu là `false` thì API chưa đọc được cấu hình. Xem mục 9.

## 5. Tạo một đơn đã gửi qua GHN

### 5.1. Shop bật GHN
Đăng nhập **thành viên shop**, vào `/vendor/shipping`, khung **Giao Hàng Nhanh (GHN)** → **Bật GHN**:
- Tên người gửi và số điện thoại được điền sẵn từ thông tin shop. Số điện thoại phải là số Việt Nam hợp lệ, ví dụ `0901234567`.
- Chọn tỉnh, quận, phường (danh sách lấy từ GHN), nhập số nhà và tên đường → **Lưu và bật GHN**.

Shop chưa bật GHN thì khách **không thấy** lựa chọn GHN cho hàng của shop đó.

### 5.2. Khách có địa chỉ theo GHN
Đăng nhập **khách**, vào `/customer/account-data`, thêm hoặc sửa một địa chỉ **Việt Nam** và chọn đủ **Tỉnh/Thành (GHN)**, **Quận/Huyện (GHN)**, **Phường/Xã (GHN)**.

### 5.3. Đặt hàng
Thêm sản phẩm của shop vào giỏ → Mua → ở checkout chọn địa chỉ vừa sửa → phần vận chuyển chọn **Giao Hàng Nhanh (GHN)** (phí do GHN tính, quy đổi sang tiền tệ chính) → đặt hàng.

### 5.4. Shop gửi qua GHN
Thành viên shop mở đơn ở `/vendor/orders/{id}` → **Xác nhận** → **Gửi qua GHN**.

- Đơn chuyển sang **Đã gửi**. Dòng **Vận đơn: Giao Hang Nhanh · `XXXXXX`** chứa **mã vận đơn GHN**, ghi lại để dùng ở bước 8.
- **In nhãn GHN** mở trang in nhãn A5 của GHN.
- Trên `5sao.ghn.dev` sẽ thấy vận đơn mới, người gửi là tên và địa chỉ của shop.

## 6. ngrok: cho máy chủ GHN gọi được vào máy local

Bước 6 và 7 chỉ cần khi muốn **GHN thật** gửi webhook về, ví dụ để kiểm tra webhook đã đăng ký đúng hay chưa. Nếu chỉ giả lập bằng Postman gửi thẳng vào `localhost` thì có thể bỏ qua, chuyển sang bước 8.

1. Đăng ký ngrok, lấy authtoken ở **https://dashboard.ngrok.com/get-started/your-authtoken**, rồi chạy:
   ```
   ngrok config add-authtoken <dán token thật vào đây>
   ```
   > Đừng chạy nguyên lệnh mẫu `ngrok config add-authtoken $YOUR_AUTHTOKEN`. Làm vậy ngrok sẽ lưu chữ `$YOUR_AUTHTOKEN` và báo lỗi `ERR_NGROK_105`.
2. Khi API đang chạy, mở terminal mới:
   ```
   ngrok http https://localhost:7014
   ```
   Trỏ thẳng vào cổng **7014**. Trỏ vào 5214 thì API chuyển hướng sang `localhost:7014` và request từ ngoài vào sẽ thất bại.
3. Lấy địa chỉ ở dòng **Forwarding**, ví dụ `https://abc-xyz.ngrok-free.dev`. URL webhook là:
   ```
   https://abc-xyz.ngrok-free.dev/api/v1/shipping/ghn/webhook
   ```
4. Trang **http://127.0.0.1:4040** hiển thị mọi request đi qua ngrok, kèm body và phản hồi của API.

> ngrok bản miễn phí **đổi địa chỉ mỗi lần chạy lại**. Muốn địa chỉ cố định, lấy một tên miền tĩnh miễn phí ở mục **Domains** trên dashboard ngrok rồi chạy `ngrok http https://localhost:7014 --url=<tên-miền>`. Địa chỉ đổi thì phải sửa lại URL ở bước 7.

## 7. Đăng ký webhook trên GHN

1. Mở **https://developer.ghn.dev/account/webhook**. Đây là Developer Portal của staging, không phải trang `5sao.ghn.dev`.
2. Tab **Order** → dán URL webhook ở bước 6.
3. **Custom headers**:
   | Header | Giá trị |
   |---|---|
   | `X-Nomori-Webhook-Secret` | đúng giá trị `Shipping__Ghn__WebhookSecret` trong `.env` |
   | `ngrok-skip-browser-warning` | `true` |
4. Bấm **Create webhook**. **Đợi khoảng 15 phút** để GHN áp dụng cấu hình.
5. Kiểm tra GHN gọi về được: **huỷ một vận đơn** trên `5sao.ghn.dev` (hoặc qua API huỷ đơn của GHN). Trang `http://127.0.0.1:4040` phải hiện một `POST /api/v1/shipping/ghn/webhook` từ IP của GHN (không phải IP nhà bạn), body có `"Type": "switch_status"` và `"Status": "cancel"`, phản hồi **200**.

> Production dùng tài khoản và portal riêng (`developer.ghn.vn`); cấu hình ở staging không tự chuyển sang. Khi deploy, đăng ký URL của server thật thay cho ngrok.

## 8. Postman: giả lập shipper cập nhật trạng thái

### 8.1. Tạo request
- **Method**: `POST`
- **URL**: một trong hai
  - qua ngrok: `https://abc-xyz.ngrok-free.dev/api/v1/shipping/ghn/webhook` (request hiện trên trang 4040);
  - hoặc thẳng vào máy: `http://localhost:4200/api/v1/shipping/ghn/webhook` (đi qua proxy của Angular, không cần ngrok).
- **Headers**:
  | Key | Value |
  |---|---|
  | `Content-Type` | `application/json` |
  | `X-Nomori-Webhook-Secret` | giá trị `Shipping__Ghn__WebhookSecret` |
  | `ngrok-skip-browser-warning` | `true` (chỉ cần khi gọi qua ngrok) |

### 8.2. Tự điền thời gian
`Time` phải là **giờ UTC hiện tại**. Nếu sớm hơn lúc đặt đơn hoặc ở tương lai, dòng hành trình sẽ bị bỏ qua. Để khỏi gõ tay, vào tab **Scripts → Pre-request** (Postman cũ gọi là *Pre-request Script*) và thêm:
```javascript
pm.variables.set('now', new Date().toISOString());
```

### 8.3. Body (raw, JSON)
```json
{
  "ShopID": 229760,
  "Type": "switch_status",
  "OrderCode": "{{orderCode}}",
  "Status": "{{status}}",
  "Time": "{{now}}"
}
```
- `ShopID`: ShopId GHN trong `.env`. Khác giá trị đó thì request bị bỏ qua.
- `orderCode`, `status`: tạo hai biến (Collection hoặc Environment) để mỗi lần chỉ phải đổi giá trị biến.
  - `orderCode`: mã vận đơn GHN ở bước 5.4. Đơn phải đang ở **Đã gửi**.
  - `status`: trạng thái muốn giả lập.
- Có thể thêm `"Reason": "Khách hẹn giao lại"` để hiện lý do, hữu ích khi thử `delivery_fail`.

### 8.4. Gửi lần lượt theo đúng thứ tự của shipper

| Lần | `status` | Ý nghĩa | Trên Nomori |
|---|---|---|---|
| 1 | `picked` | Shipper đã lấy hàng | Dòng "GHN: Đã lấy hàng" |
| 2 | `transporting` | Đang luân chuyển giữa các kho | Dòng "GHN: Đang luân chuyển" |
| 3 | `delivering` | Shipper đang giao tới khách | Dòng "GHN: Đang giao hàng" |
| 4 | `delivered` | Giao thành công | Dòng "GHN: Giao hàng thành công", đơn → **Đã giao** |

Nhánh giao thất bại: gửi `delivery_fail` (thêm dòng "Giao hàng thất bại", đơn vẫn **Đã gửi**), sau đó gửi lại `delivering` rồi `delivered`.

Mỗi lần gửi phải nhận **200** với:
```json
{ "outcome": "handled" }
```

### 8.5. Xem kết quả
Trang **không tự cập nhật**. Sau mỗi lần gửi, **tải lại (F5)**:
- Shop: `/vendor/orders/{id}`, phần hành trình kiện hàng.
- Khách: `/customer/orders/{id}`. Khi đơn ở **Đã giao**, khách bấm **Đã nhận được hàng** thì đơn chuyển sang **Hoàn thành**.

## 9. Xử lý sự cố

| Hiện tượng | Nguyên nhân thường gặp |
|---|---|
| `/shipping/ghn/status` trả `enabled: false` | API chưa đọc được cấu hình: thiếu `.env` ở thư mục gốc backend, `Enabled` khác `true`, hoặc API chưa được chạy lại sau khi sửa `.env`. Một biến môi trường Windows cũ cũng có thể đang ghi đè. |
| Checkout không có lựa chọn GHN | Shop chưa bật GHN (bước 5.1); địa chỉ khách thiếu tỉnh/quận/phường GHN; cửa hàng GHN chưa có địa chỉ; hoặc dùng Client ID thay cho ShopId. |
| "Gửi qua GHN" báo lỗi từ GHN | Đọc thông báo GHN hiện trên các nút. Hay gặp nhất là số điện thoại người nhận hoặc người gửi không hợp lệ. |
| Webhook trả **404** | Header `X-Nomori-Webhook-Secret` thiếu hoặc sai, hoặc `.env` chưa có `WebhookSecret`. 404 là cố ý, để giấu endpoint khỏi request không có mã bí mật. |
| Webhook trả `outcome: ignored` | `ShopID` khác ShopId trong `.env`, hoặc `OrderCode` không thuộc đơn nào đã "Gửi qua GHN". |
| `handled` nhưng không thêm dòng | Đơn không còn ở **Đã gửi** (đã giao hoặc đã huỷ); `Time` sớm hơn lúc đặt đơn hoặc ở tương lai; hoặc gửi lại đúng trạng thái và thời điểm đã có (bị coi là trùng). |
| Không thấy request của GHN trên trang 4040 | Chưa đủ 15 phút sau khi lưu webhook; URL khai sai; ngrok đã tắt hoặc đổi địa chỉ; thiếu `ngrok-skip-browser-warning`. |
| Postman báo lỗi chứng chỉ với `https://localhost:7014` | Tắt **SSL certificate verification** trong Settings của Postman, hoặc gọi qua `http://localhost:4200` hay địa chỉ ngrok. |

## 10. Giới hạn của bản thử nghiệm

- Sản phẩm chưa có cân nặng: mỗi sản phẩm tính **500 g** (`Shipping:Ghn:ItemWeightGrams`), kiện cố định 20×15×10 cm.
- Phí GHN (VND) quy đổi theo tỷ giá của tiền tệ VND nếu có, nếu không thì theo `Shipping:Ghn:VndPerPrimaryUnit` (mặc định 25.000).
- Khi GHN báo huỷ hoặc hoàn hàng, hệ thống **chỉ thêm dòng hành trình**; đơn vẫn **Đã gửi** và shop phải tự xử lý.
- Webhook không có chữ ký của GHN; bảo vệ duy nhất là header bí mật dùng chung.
