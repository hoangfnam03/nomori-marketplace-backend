# Nomori Marketplace: tiến độ theo feature map

Tài liệu theo dõi (trace) tiến độ so với `nopCommerce/docs/nomori-complete-feature-map.md`. Cập nhật mỗi khi xong hoặc hoãn một slice.

- **Cập nhật lần cuối:** 2026-10-12
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
| F07 | Localization, tiền tệ, đơn vị, múi giờ | Một phần | `modules/f07a-currency.md` | **F07-A xong** (chờ test tay và merge): tiền tệ (một tiền tệ chính, tiền tệ hiển thị kèm tỷ giá nhập tay), quy tắc số chữ số thập phân cho mọi giá, làm tròn và quy đổi, trang admin tiền tệ, bộ chọn tiền tệ ở storefront. Chưa có: đơn vị đo và múi giờ (F07-B), quốc gia và địa chỉ (F07-C), ngôn ngữ (F07-D), thuế (F07-E) |
| F08 | Media | Một phần | `modules/f08a-media.md` | **F08-A xong, đã merge** (ảnh, upload có kiểm soát). Còn F08-B (thumbnail, quét), F08-C (file riêng tư, download) |
| F09 | Taxonomy toàn sàn, manufacturer | Một phần | `modules/f09a-taxonomy.md` | **F09-A xong, đã merge**: toàn vẹn cây, luật xóa, cờ `RestrictFromVendors`, validator chọn danh mục cho seller, ràng buộc DB. Còn slug/SEO (F24), template và lọc (F13), localization (F07) |
| F10 | Sản phẩm, quyền sở hữu | Một phần | `modules/f10a-product-ownership.md`, `modules/f10b-product-lifecycle.md`, `modules/f10c-product-content.md` | **F10-A xong, đã merge** (`VendorId` bắt buộc, shop nền tảng, API seller, chuyển shop). **F10-B xong, đã merge** (chưa test tay trên DB thật): 4 trạng thái (nháp, đang bán, ngừng bán, bị admin ẩn), seller đăng bán/ngừng bán, admin ẩn kèm lý do và email, bỏ ẩn khôi phục trạng thái cũ, yêu cầu xem lại. **F10-C xong** (chờ test tay và merge, xem `modules/f10c-product-content.md`): mô tả HTML lọc an toàn, SKU/GTIN/MPN (SKU duy nhất theo shop), lịch đăng bán, sản phẩm liên quan (cùng shop, tối đa 12), copy sản phẩm. Cặp F10 đóng lại ở slice này |
| F11 | Biến thể, thuộc tính, thông số | Một phần | `modules/f11a-product-pictures.md`, `modules/f11b-product-variants-specs-tags.md` | **F11-B xong** (chờ test tay và merge): seller tự quản lý biến thể (tối đa 3 thuộc tính, 20 giá trị, 100 tổ hợp, SKU duy nhất theo shop, tồn kho sản phẩm = tổng tồn kho tổ hợp), thông số và tag; API công khai về thuộc tính/thông số/tag chỉ trả sản phẩm đang hiển thị; storefront chọn giá trị và hiện giá, tồn kho của tổ hợp. **F11-A xong, đã merge** (PR #13; chưa test tay trên DB thật): ảnh sản phẩm có thứ tự (tối đa 10, ảnh đầu là ảnh chính), đăng bán cần ít nhất 1 ảnh, ảnh hiện ở danh sách seller, storefront. Product attribute và specification attribute đã có. Chưa có: ảnh theo tổ hợp và copy kèm biến thể (F11-C), tier price (F14) |
| F12 | Tồn kho | Một phần | `modules/f12a-inventory.md` | **F12-A xong** (chờ test tay và merge): sổ cái tồn kho (`StockMovement`), điều chỉnh tồn kho nguyên tử không âm, giữ chỗ (reserve, release, commit, tự hết hạn) cho giỏ hàng và thanh toán, ngưỡng cảnh báo sắp hết, tuỳ chọn không theo dõi tồn kho, tồn kho khả dụng ở storefront. Chưa có: HTTP cho reserve/commit (làm cùng F16/F17), back in stock, backorder, nhiều kho (F12-B, F12-C) |
| F13 | Tìm kiếm, lọc | Một phần | `modules/f13a-catalog-search.md` | **F13-A xong** (chờ test tay và merge): tìm kiếm văn bản nhiều từ (tên, mô tả ngắn, SKU, tag, nhà sản xuất; ký tự `%`, `_` được coi là chữ thường), danh mục kèm danh mục con, lọc nhà sản xuất, khoảng giá, còn hàng, tag, thông số kỹ thuật; sắp xếp theo độ khớp; facet (số lượng) cho bảng lọc; gợi ý khi gõ. Chưa có: so sánh sản phẩm, đã xem gần đây, thống kê từ khoá (F13-B), search engine và cache (F13-C) |
| F14 | Giá, bảng giá | Một phần | `modules/f14a-pricing.md` | **F14-A xong** (chờ test tay và merge): giá đặc biệt có khung giờ, giá theo số lượng (tier), dịch vụ tính giá (`IPriceCalculationService`) dùng chung cho storefront, giỏ hàng và đơn, báo giá `GET /catalog/products/{id}/price`, bộ lọc và sắp xếp giá theo giá hiện hành, kiểm tra không hạ giá gốc xuống dưới giá đặc biệt/tier, audit đổi giá. Chưa có: giá theo nhóm khách/shop, bảng giá, giá do khách nhập (F14-B) |
| F16 | Giỏ hàng, wishlist | Một phần | `modules/f16a-cart.md` | **F16-A xong** (chờ test tay và merge): giỏ hàng của khách đã đăng nhập, nhiều shop trong một giỏ (nhóm theo shop), giá do máy chủ tính (F14-A), kiểm tra hiển thị và tồn kho (F12-A), cảnh báo giá đổi và sản phẩm không còn bán, chặn mua hàng của chính shop mình, trang `/storefront/cart`, số lượng trên header. **Giỏ không giữ chỗ tồn kho** (giữ chỗ khi bắt đầu thanh toán, F17). Chưa có: giỏ khách vãng lai và gộp giỏ, wishlist (F16-B), thuộc tính thanh toán (F16-C) |
| F15, F17–F31 | Khuyến mãi, đặt hàng, thanh toán, vận chuyển, đổi trả, thông báo, CMS, SEO, affiliate, báo cáo, import/export, plugin, job nền, theme, AI | Chưa làm | | Xem feature map |

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
5b. ~~F10-C Nội dung sản phẩm~~ (xong, chờ test tay và merge). 5c. ~~F11-B Biến thể, thông số, tag của seller~~ (xong, đã merge)
5d. ~~F12-A Tồn kho: sổ cái, giữ chỗ~~ (xong, đã merge)
5e. ~~F13-A Tìm kiếm, lọc, facet, gợi ý~~ (xong, đã merge)
5f. ~~F07-A Tiền tệ và quy tắc tiền~~ (xong, đã merge)
5g. ~~F14-A Giá đặc biệt, giá theo số lượng, dịch vụ tính giá~~ (xong, đã merge)
5h. ~~F16-A Giỏ hàng~~ (xong, chờ test tay và merge). Tiếp theo: **F18-A/F17-A** đơn hàng và thanh toán (cần địa chỉ, vận chuyển, thanh toán: F07-C địa chỉ, F20, F19 là điều kiện). F07-B đến F07-E làm khi cần (múi giờ trước F20/F22, thuế trước F17). Xem `modules/vendor-products-prd.md` (US-A3, A4).
6. F13-A Tìm kiếm.
7. F15 khuyến mãi, F18/F17 đơn hàng và thanh toán (F07-A, F14-A, F16-A đã xong).
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
| Logic khoá và đồng thời của tồn kho (`SqlInventoryStore`: `UPDLOCK, HOLDLOCK`, giữ chỗ cuối cùng) chỉ kiểm bằng tay trên SQL; unit test chỉ phủ `StockRules` và service | `modules/f12a-inventory.md` | F12-A (test tay), F26 |
| Endpoint admin thô `/admin/products/{id}/attributes/combinations` vẫn ghi tồn kho tổ hợp không qua sổ cái | `modules/f12a-inventory.md` | F12-B |
| Hàng giữ chỗ hết hạn chưa được dọn khỏi bảng `StockReservation` | `modules/f12a-inventory.md` | F29 |
| Mọi số tiền lưu ở tiền tệ chính (mặc định USD, cố định sau khi có sản phẩm); cart, đơn và thanh toán sau này phải lưu kèm mã tiền tệ | `modules/f07a-currency.md` | F16 đến F19 |
| Tiền tệ khách chọn chỉ lưu trong trình duyệt, tỷ giá nhập tay | `modules/f07a-currency.md` | F04, F28 |
| Giá theo nhóm khách/shop, bảng giá, giá khách nhập, làm tròn tiền mặt chưa có; dòng đơn chưa có bảng lưu snapshot giá (báo giá đã đủ trường) | `modules/f14a-pricing.md` | F14-B, F18 |
| Form admin sản phẩm chưa có trường giá đặc biệt và giá tier (chỉ seller) | `modules/f14a-pricing.md` | F10 |
| Giỏ chỉ có cho khách đã đăng nhập; chưa có giỏ vãng lai, gộp giỏ, wishlist, dọn giỏ cũ (F29); giỏ không giữ chỗ tồn kho nên người mua có thể thấy hết hàng lúc thanh toán | `modules/f16a-cart.md` | F16-B, F17, F29 |
| Test service (`Services.Tests`) không chạy được trên máy này do Windows App Control chặn DLL; code compile, cần chạy `dotnet test` trên máy khác hoặc CI | | Môi trường |
| Sản phẩm cũ chưa publish được chuyển thành "nháp" (không phân biệt được với "ngừng bán") | `modules/f10b-product-lifecycle.md` | Ghi chú triển khai |
| Tìm kiếm dùng `LIKE '%từ%'` (không dùng index), facet là nhiều truy vấn mỗi lần, chưa cache; `inStock` ở danh sách là tồn kho thực, không trừ hàng đang giữ chỗ | `modules/f13a-catalog-search.md` | F13-C |
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
| 2026-10-08 | `feat/inventory/foundation` | F12-A: migration `StockMovement`, `StockReservation`, `TrackInventory`, `LowStockThreshold`; `IInventoryStore`/`IInventoryService` (adjust, reserve, release, commit, availability) với `StockRules` thuần; API seller `GET .../inventory`, `POST .../stock-adjustments`, `PUT .../inventory-settings`, `GET .../stock-movements`, lọc `lowStock`; lưu sản phẩm của seller không còn sửa tồn kho (chỉ qua sổ cái), admin sửa qua delta; lưu biến thể bị chặn khi có hàng đang giữ chỗ; storefront hiện tồn kho khả dụng. Mục Inventory ở `/vendor/products/:id/details`, badge sắp hết hàng (BE + FE). Chưa commit |
| 2026-10-09 | `feat/search/foundation` | F13-A: `ICatalogSearchService`, bộ dựng SQL dùng chung `ProductFilter` (luật công khai luôn áp dụng, tham số hoá, escape `LIKE`), `GET /catalog/products` mở rộng (danh mục kèm con, nhà sản xuất, giá, còn hàng, tag, thông số, độ khớp), `GET /catalog/products/facets` và `/suggest`, migration index tag và thông số. Trang danh sách storefront có bảng lọc, chip bộ lọc, URL đồng bộ; ô tìm kiếm có gợi ý bằng bàn phím (BE + FE). **Sửa lỗi:** trang danh sách storefront trước đây không gửi từ khoá tìm kiếm lên API. Chưa commit |
| 2026-10-10 | `feat/localization/foundation` | F07-A: bảng `Currency` (một tiền tệ chính, seed USD), quyền `settings.manage`, `CurrencyRules` (làm tròn, quy đổi, số chữ số thập phân), `ICurrencyService`, API công khai `GET /currencies` và API admin CRUD + đặt tiền tệ chính (chia lại tỷ giá, bị khoá khi đã có sản phẩm), kiểm tra số chữ số thập phân cho giá sản phẩm, giá cũ, điều chỉnh và giá riêng của biến thể. Angular: `CurrencyService` thay 8 chỗ gắn cứng `$`, bộ chọn tiền tệ ở header, trang `/admin/currencies` (BE + FE). Chưa commit |
| 2026-10-11 | `feat/pricing/foundation` | F14-A: cột `SpecialPrice` + khung giờ, bảng `ProductTierPrice`, `PriceRules` thuần, `IPriceCalculationService` (đặc biệt, tier, điều chỉnh và giá riêng của biến thể), `IProductPricingService`, API seller `PUT .../pricing`, API công khai báo giá, `finalPrice` và `tierPrices` ở danh sách/chi tiết, lọc và sắp xếp giá theo giá hiện hành (F13-A), audit `product.price_changed`. Angular: mục Pricing ở trang Details, thẻ sản phẩm hiện giá sale kèm giá gạch, trang sản phẩm có số lượng, bảng giá theo số lượng và lấy giá từ máy chủ (xoá phép tính giá ở trình duyệt) (BE + FE). Chưa commit |
| 2026-10-12 | `feat/cart/foundation` | F16-A: bảng `CartItem` (duy nhất theo khách, sản phẩm, lựa chọn biến thể), `CartRules` thuần, `ICartService` (thêm, đổi số lượng, xoá, xoá hết, chấp nhận giá mới, đếm), API `/api/v1/cart` cho khách đã đăng nhập, giỏ nhóm theo shop với tổng tạm tính, cảnh báo `unavailable`, `variant_unavailable`, `out_of_stock`, `insufficient_stock`, `price_changed`, chặn mua sản phẩm của shop mình. Angular: trang giỏ hàng, nút Add to cart ở trang sản phẩm và thẻ sản phẩm (khách vãng lai được chuyển sang đăng nhập), số lượng trên header (BE + FE). Chưa commit |
