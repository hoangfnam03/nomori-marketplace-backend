# Hướng dẫn triển khai Nomori lên Azure — Bước từng bước

**Dành cho:** Phương án B (Azure SQL Free + Container Apps)

**Thời gian:** ~60–90 phút lần đầu (download/build image mất lâu)

**Yêu cầu:** Azure account đã tạo, thẻ tín dụng xác minh (không bị tính tiền nếu tuân thủ hạn mức), hai repo cạnh nhau, Docker Desktop chạy

---

## 📋 Checklist chuẩn bị

- [ ] Azure account (đã có)
- [ ] Microsoft account đang đăng nhập Azure Portal
- [ ] GitHub account (có thể dùng account chứa source code)
- [ ] Docker Desktop trên Windows chạy (bật WSL2)
- [ ] Git hai repo: `nomori-marketplace-backend` và `nomori-marketplace-frontend` cạnh nhau
- [ ] PowerShell (Windows) hoặc Bash (Mac/Linux)
- [ ] Đã chuẩn bị ba giá trị bí mật (lưu vào password manager, **không commit**):
  - `SQL_ADMIN_PASSWORD`: mật khẩu SQL mạnh (hoa/thường/số/ký tự đặc biệt, không có dấu cách/`;="\\'$\`)
  - `SANDBOX_SECRET`: chuỗi ngẫu nhiên cho cổng thanh toán
  - `SMTP_PASSWORD`: (tuỳ chọn nếu bật email)

---

## 🛡️ **Giới hạn chi phí — ĐỌC KỸ**

Phương án này miễn phí nếu:

| Dịch vụ | Hạn mức/tháng | Cách giữ |
|---|---|---|
| Azure SQL Database Free Offer | 100.000 vCore-giây, 32 GB data, 32 GB backup | **Bắt buộc**: chọn `Auto-pause until next month` khi hết hạn |
| Container Apps (Consumption) | 180.000 vCPU-giây, 360.000 GiB-giây, 2M request | `min=0`, `max=1` replicas; scale-to-zero khi rảnh |
| GHCR (GitHub Container Registry) | Public image không tính phí | Đặt image là Public |

**Không tạo những service này** (có phí):
- ❌ Azure VM
- ❌ Azure Container Registry (dùng GHCR miễn phí)
- ❌ VNet / Application Gateway
- ❌ Azure Files (dùng container ephemeral storage)
- ❌ Log Analytics workspace
- ❌ Key Vault

**Ngay sau khi tạo Azure account:**
1. Vào **Cost Management + Billing** → **Budgets** → **Add**
2. Đặt ngưỡng cảnh báo thấp (ví dụ 1 USD)
3. Gửi thư cảnh báo về email

---

## Phần 1: Chuẩn bị các giá trị biến

### Bước 1.1 — Tạo PAT GitHub

Cần Personal Access Token để login GitHub Container Registry:

1. Vào <https://github.com/settings/tokens?type=beta>
2. **Generate new token** (classic)
3. Đặt:
   - **Token name**: `nomori-azure-deploy` (hoặc tên khác)
   - **Expiration**: 90 days (tuỳ chọn)
   - **Scopes**: Chọn `write:packages` (cần để push image)
4. Copy token (chỉ hiển thị một lần)

Lưu token vào password manager hoặc file tạm (xóa sau khi dùng xong).

### Bước 1.2 — Tạo biến môi trường cục bộ

Mở PowerShell trên **máy phát triển** (chứa Docker) và chuẩn bị các biến:

```powershell
# ===== BIẾN BẮTBUỘC — SỬA SỐ/TÊN THEO CLOUD/TÀI KHOẢN CỦA BẠN =====
$GitHubUser = "yourgithub"                    # GitHub username (chữ thường)
$GitHubToken = "ghp_..."                      # Personal Access Token vừa tạo
$Version = "v1"                               # Tag version cho image (v1, v2, 2026-10-27.1, ...)

# ===== BIẾN AZURE =====
$RG = "nomori-demo-rg"                        # Resource group
$Loc = "southeastasia"                        # Region (hoặc khác tùy chọn)
$Env = "nomori-demo-env"                      # Container Apps environment
$SqlServer = "nomori-demo-sql-12345"          # Phải duy nhất trên toàn Azure (thêm số ngẫu nhiên)
$SqlAdmin = "nomoriadmin"                     # SQL login name
$SqlPassword = "YourStrongPassword123!"       # SQL_ADMIN_PASSWORD (mạnh, không dấu cách)
$SandboxSecret = "your-random-secret-key"    # SANDBOX_SECRET (tối thiểu 32 ký tự, random)

# ===== BIẾN TÍNH TOÁN (KHÔNG SỬA) =====
$DbConnectionString = "Server=tcp:${SqlServer}.database.windows.net,1433;Initial Catalog=NomoriMarketplace_Db;Persist Security Info=False;User ID=${SqlAdmin};Password=${SqlPassword};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;"
$PublicUrlPlaceholder = "https://example.invalid"  # Chưa biết URL proxy, sẽ cập nhật sau

# Kiểm tra Docker chạy
docker version
```

**Lưu ý:**
- Không commit các biến này vào Git
- `$SqlPassword` không được chứa `;`, `=`, `"`, `'`, `$`, `\` hoặc dấu cách
- Sau khi deploy xong, không chia sẻ `$SqlPassword` hay `$SandboxSecret`

---

## Phần 2: Build và push image lên GHCR

### Bước 2.1 — Login Docker vào GHCR

```powershell
# Login GitHub Container Registry
$GitHubToken | docker login ghcr.io -u $GitHubUser --password-stdin
# Output mong đợi: "Login Succeeded"

# Kiểm tra config được lưu
cat $env:USERPROFILE\.docker\config.json | findstr ghcr.io
```

### Bước 2.2 — Build bốn image

Chạy từ thư mục `nomori-marketplace-backend`:

```powershell
cd nomori-marketplace-backend

# Build API image
docker build -f deploy/api.Dockerfile --target api `
  -t "ghcr.io/$GitHubUser/nomori-api:$Version" .
# Output: Successfully tagged ghcr.io/.../nomori-api:v1

# Build Migrator image (cùng Dockerfile, khác target)
docker build -f deploy/api.Dockerfile --target migrator `
  -t "ghcr.io/$GitHubUser/nomori-migrator:$Version" .

# Build Caddy proxy (tạo Dockerfile nhỏ nếu chưa có)
# Nếu chưa có deploy/azure-proxy.Dockerfile, tạo nó:
# (Xem Phần 2.3)

docker build -f deploy/azure-proxy.Dockerfile `
  -t "ghcr.io/$GitHubUser/nomori-proxy:$Version" .

# Build web (Angular)
cd ..\nomori-marketplace-frontend
docker build -f Dockerfile `
  -t "ghcr.io/$GitHubUser/nomori-web:$Version" .
cd ..\nomori-marketplace-backend

# Xác nhận bốn image đã build
docker images | findstr nomori
```

**Nếu Docker build lỗi:**
- Xem log chi tiết: `docker build ... 2>&1 | tee build.log`
- Kiểm tra Dockerfile tồn tại
- Kiểm tra thư mục repo không có `.git\`, `node_modules` rất lớn (dùng `.dockerignore`)

### Bước 2.3 — Tạo deploy/azure-proxy.Dockerfile (nếu chưa có)

Kiểm tra file tồn tại:

```powershell
if (Test-Path "deploy/azure-proxy.Dockerfile") {
    Write-Host "File tồn tại rồi"
} else {
    Write-Host "Tạo file mới..."
    @"
FROM caddy:2-alpine
COPY deploy/Caddyfile /etc/caddy/Caddyfile
"@ | Out-File -Encoding utf8 deploy/azure-proxy.Dockerfile
}
```

### Bước 2.4 — Push bốn image lên GHCR

```powershell
# Mỗi push mất 1–3 phút tùy kích cỡ image
docker push "ghcr.io/$GitHubUser/nomori-api:$Version"
docker push "ghcr.io/$GitHubUser/nomori-migrator:$Version"
docker push "ghcr.io/$GitHubUser/nomori-proxy:$Version"
docker push "ghcr.io/$GitHubUser/nomori-web:$Version"

# Xác nhận tất cả push thành công
# Vào https://github.com/$GitHubUser?tab=packages để xem
```

**Sau push thành công:**
- Vào GitHub Packages → Chọn mỗi package
- Mở **Package settings** → **Change visibility** → **Public**
- Azure Container Apps sẽ pull image mà không cần credentials

---

## Phần 3: Tạo database Azure SQL

### Bước 3.1 — Tạo trên Portal (UI, dễ nhất)

1. Vào <https://portal.azure.com/>
2. **Create a resource** → tìm **SQL Database** → **Create**
3. **Basics** tab:
   - **Subscription**: Free Trial / Free Account
   - **Resource group**: **Create new** → `nomori-demo-rg`
   - **Database name**: `NomoriMarketplace_Db`
   - **Server**: **Create new**
     - **Server name**: `nomori-demo-sql-12345` (phải duy nhất toàn Azure, thêm số ngẫu nhiên)
     - **Location**: `Southeast Asia` (phải khớp Container Apps)
     - **Authentication method**: SQL authentication
     - **Server admin login**: `nomoriadmin`
     - **Password**: `$SqlPassword` (mạnh, không dấu cách)
     - **Confirm password**: (lặp lại)
     - OK

4. **Compute + storage**: Mục **Configure database**
   - Tìm **Azure SQL Database free offer** → Chọn nó
   - Nếu không thấy → dừng, kiểm tra subscription/region, không chọn tier trả phí

5. **Free Database Offer** tab:
   - **Behavior when free limit reached**: `Auto-pause the database until next month` (**BẮT BUỘC**)
   - Max data storage: 32 GB (hoặc thấp hơn)
   - OK

6. **Networking** tab:
   - **Public network access**: `Selected networks`
   - **Firewall rules**:
     - Add **your client IPv4 address** (để máy bạn kết nối được SSMS/Azure Data Studio)
     - **Allow Azure services and resources** → ON (để Container Apps kết nối)
   - Save

7. **Review + Create** → Kiểm tra lại mọi thứ → **Create**

Chờ 1–2 phút để deployment hoàn tất.

### Bước 3.2 — Lấy thông tin SQL server

Deployment xong:
1. Portal → **Resource groups** → `nomori-demo-rg`
2. Mở resource `nomori-demo-sql-12345` (SQL server)
3. **Overview**: Lấy **Server name** (ví dụ `nomori-demo-sql-12345.database.windows.net`)

---

## Phần 4: Tạo Container Apps Environment

Dùng Azure CLI (Cloud Shell hoặc cài local). Chạy trên PowerShell / Bash:

### Bước 4.1 — Mở Cloud Shell

1. Portal → biểu tượng **Cloud Shell** (góc trên phải)
2. Chọn **Bash** (dễ hơn cmd)
3. Nếu hỏi tạo storage → **Create storage** (storage nhỏ, có thể có phí tối thiểu; nếu muốn tránh, cài Azure CLI local)

### Bước 4.2 — Chuẩn bị biến trong Cloud Shell

Copy-paste đoạn này vào Bash (không commit lên GitHub):

```bash
# ===== CẬP NHẬT THEO GIÁ TRỊ CỦA BẠN =====
RG="nomori-demo-rg"
LOC="southeastasia"
ENV="nomori-demo-env"
GH_USER="yourgithub"
VER="v1"
SQL_SERVER="nomori-demo-sql-12345"
SQL_ADMIN="nomoriadmin"
SQL_PASSWORD='YourStrongPassword123!'
PUBLIC_URL_PLACEHOLDER="https://example.invalid"

# ===== TẠO CONNECTION STRING =====
DB_CONNECTION="Server=tcp:${SQL_SERVER}.database.windows.net,1433;Initial Catalog=NomoriMarketplace_Db;Persist Security Info=False;User ID=${SQL_ADMIN};Password=${SQL_PASSWORD};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;"

# Kiểm tra biến
echo "RG=$RG"
echo "SQL_SERVER=$SQL_SERVER"
```

### Bước 4.3 — Update Azure CLI và tạo resource group + environment

```bash
# Update extension
az extension add --name containerapp --upgrade

# Tạo resource group (nếu chưa có)
az group create --name "$RG" --location "$LOC"

# Tạo Container Apps environment
az containerapp env create --name "$ENV" --resource-group "$RG" --location "$LOC"

# Chờ 1–2 phút
```

---

## Phần 5: Tạo ba Container Apps

### Bước 5.1 — Tạo API (internal ingress)

```bash
az containerapp create \
  --name nomori-api \
  --resource-group "$RG" \
  --environment "$ENV" \
  --image "ghcr.io/$GH_USER/nomori-api:$VER" \
  --ingress internal \
  --target-port 8080 \
  --min-replicas 0 \
  --max-replicas 1 \
  --cpu 0.25 \
  --memory 0.5Gi \
  --secrets db-connection="$DB_CONNECTION" sandbox-secret="$SANDBOX_SECRET" \
  --env-vars \
    "ASPNETCORE_ENVIRONMENT=Production" \
    "ASPNETCORE_URLS=http://+:8080" \
    "ASPNETCORE_FORWARDEDHEADERS_ENABLED=true" \
    "Database__ConnectionString=secretref:db-connection" \
    "Payments__Sandbox__Secret=secretref:sandbox-secret" \
    "Jobs__Enabled=false" \
    "Email__Enabled=false"

# Chờ tạo xong (1–2 phút)
```

### Bước 5.2 — Tạo web (internal ingress)

```bash
az containerapp create \
  --name nomori-web \
  --resource-group "$RG" \
  --environment "$ENV" \
  --image "ghcr.io/$GH_USER/nomori-web:$VER" \
  --ingress internal \
  --target-port 4000 \
  --min-replicas 0 \
  --max-replicas 1 \
  --cpu 0.25 \
  --memory 0.5Gi \
  --env-vars \
    "PORT=4000" \
    "API_ORIGIN=http://nomori-api" \
    "PUBLIC_URL=$PUBLIC_URL_PLACEHOLDER"

# Chờ tạo xong
```

### Bước 5.3 — Tạo proxy (public ingress)

```bash
az containerapp create \
  --name nomori-proxy \
  --resource-group "$RG" \
  --environment "$ENV" \
  --image "ghcr.io/$GH_USER/nomori-proxy:$VER" \
  --ingress external \
  --target-port 80 \
  --min-replicas 0 \
  --max-replicas 1 \
  --cpu 0.25 \
  --memory 0.5Gi \
  --env-vars \
    "SITE_ADDRESS=:80" \
    "API_UPSTREAM=http://nomori-api" \
    "WEB_UPSTREAM=http://nomori-web"

# Chờ tạo xong
```

### Bước 5.4 — Lấy URL công khai của proxy

```bash
# Lấy FQDN của proxy
PROXY_FQDN=$(az containerapp show \
  --name nomori-proxy \
  --resource-group "$RG" \
  --query properties.configuration.ingress.fqdn \
  --output tsv)

# Tạo PUBLIC_URL
PUBLIC_URL="https://${PROXY_FQDN}"
echo "PUBLIC_URL=$PUBLIC_URL"

# Lưu URL này để dùng sau
```

**Output sẽ giống như:**
```
PUBLIC_URL=https://nomori-proxy.someid.azurecontainerapps.io
```

---

## Phần 6: Cập nhật URL công khai cho API và web

Sử dụng `PUBLIC_URL` vừa lấy được:

```bash
# Cập nhật API
az containerapp update \
  --name nomori-api \
  --resource-group "$RG" \
  --set-env-vars \
    "Seo__SiteBaseUrl=$PUBLIC_URL" \
    "Email__FrontendBaseUrl=$PUBLIC_URL" \
    "Payments__Sandbox__PageBaseUrl=$PUBLIC_URL"

# Cập nhật web
az containerapp update \
  --name nomori-web \
  --resource-group "$RG" \
  --set-env-vars \
    "PORT=4000" \
    "API_ORIGIN=http://nomori-api" \
    "PUBLIC_URL=$PUBLIC_URL"

# Mỗi update tạo revision mới (tự khởi động)
```

---

## Phần 7: Tạo và chạy migration job

### Bước 7.1 — Tạo job

```bash
az containerapp job create \
  --name nomori-migrate \
  --resource-group "$RG" \
  --environment "$ENV" \
  --trigger-type Manual \
  --replica-timeout 1800 \
  --replica-retry-limit 1 \
  --replica-completion-count 1 \
  --parallelism 1 \
  --image "ghcr.io/$GH_USER/nomori-migrator:$VER" \
  --cpu 0.25 \
  --memory 0.5Gi \
  --secrets db-connection="$DB_CONNECTION" \
  --env-vars "Database__ConnectionString=secretref:db-connection"

# Chờ tạo xong
```

### Bước 7.2 — Chạy migration lần đầu

```bash
# Kích hoạt job
az containerapp job start --name nomori-migrate --resource-group "$RG"

# Xem lịch sử execution
az containerapp job execution list \
  --name nomori-migrate \
  --resource-group "$RG" \
  --output table

# Lệnh này sẽ in ra: Name, Status, Start time, End time
# Chờ status = Succeeded (khoảng 30 giây đến 2 phút)
```

**Nếu thất bại:**
- Portal → **Container Apps Jobs** → `nomori-migrate`
- Mở execution mới nhất → **Logs** → xem lỗi

Lỗi phổ biến:
- `Login failed`: SQL firewall chặn, hoặc password/connection string sai
- `Database X does not exist`: Schema chưa tạo → điều này bình thường, migration sẽ tạo nó

Khi migration **Succeeded**, database schema sẵn sàng.

---

## Phần 8: Kiểm tra ban đầu (Smoke test)

### Bước 8.1 — Kiểm tra API sống

Mở trình duyệt:

```
https://nomori-proxy.xxxxx.azurecontainerapps.io/health/ready
```

Kỳ vọng: `{"status":"Healthy",...}`

Nếu lỗi:
- Đợi 30 giây (container khởi động)
- Refresh lại
- Nếu vẫn lỗi → xem log: Portal → `nomori-proxy` → **Revisions and replicas** → Logs

### Bước 8.2 — Truy cập trang cửa hàng

```
https://nomori-proxy.xxxxx.azurecontainerapps.io/storefront
```

Kỳ vọng: Trang tải được, thấy sản phẩm (nếu có dữ liệu sẵn) hoặc danh mục trống

### Bước 8.3 — Kiểm tra Swagger (chỉ nếu chuyển sang Development)

```
https://nomori-proxy.xxxxx.azurecontainerapps.io/swagger
```

Nếu đang `Production` sẽ 404 (bình thường). Để xem Swagger:
- Cập nhật API: `az containerapp update --name nomori-api --resource-group "$RG" --set-env-vars "ASPNETCORE_ENVIRONMENT=Development"`
- Restart: Portal → `nomori-api` → **Revisions** → **Activate** revision mới

---

## Phần 9: Tạo admin

### Bước 9.1 — Đăng ký tài khoản

1. Truy cập `$PUBLIC_URL` (proxy URL)
2. Tìm nút **Sign up** hoặc **Đăng ký**
3. Nhập email (ví dụ `admin@test.local`), password
4. **Chưa nhận được email xác nhận?**
   - Nếu API là `Development` → mã OTP sẽ hiện trên màn hình
   - Nếu `Production` → email phải qua SMTP (chưa cấu hình) → email nằm ở `/admin/emails`

### Bước 9.2 — Cấp quyền admin qua SQL

Dùng Azure Data Studio hoặc SSMS từ máy (IP đã whitelist):

**Connection string:**
```
Server=tcp:nomori-demo-sql-12345.database.windows.net,1433;Initial Catalog=NomoriMarketplace_Db;User ID=nomoriadmin;Password=YourStrongPassword123!;
```

**Query:**
```sql
UPDATE Customer 
SET EmailVerified = 1, EmailVerifiedOnUtc = SYSUTCDATETIME() 
WHERE Email = 'admin@test.local';

INSERT INTO CustomerCustomerRoleMapping (CustomerId, CustomerRoleId)
SELECT c.Id, r.Id 
FROM Customer c 
CROSS JOIN CustomerRole r
WHERE c.Email = 'admin@test.local' 
  AND r.SystemName = 'Administrator'
  AND NOT EXISTS (
    SELECT 1 FROM CustomerCustomerRoleMapping m 
    WHERE m.CustomerId = c.Id AND m.CustomerRoleId = r.Id
  );
```

Logout rồi login lại → vào `/admin` sẽ thấy menu admin.

---

## Phần 10: Cấu hình bổ sung (tuỳ chọn)

### Email (nếu muốn gửi OTP/xác thực thật)

Chuẩn bị SMTP server (Gmail, Brevo, Mailtrap, v.v.), rồi cập nhật API:

```bash
az containerapp update \
  --name nomori-api \
  --resource-group "$RG" \
  --set-env-vars \
    "Email__Enabled=true" \
    "Email__SmtpHost=smtp.gmail.com" \
    "Email__SmtpPort=587" \
    "Email__SmtpUser=your-email@gmail.com" \
    "Email__UseAuthentication=true"
```

Thêm secret:

```bash
az containerapp secret set \
  --name nomori-api \
  --resource-group "$RG" \
  --secrets smtp-password="your-app-password"

az containerapp update \
  --name nomori-api \
  --resource-group "$RG" \
  --set-env-vars "Email__SmtpPassword=secretref:smtp-password"
```

### Chế độ Development (để xem mã OTP)

```bash
az containerapp update \
  --name nomori-api \
  --resource-group "$RG" \
  --set-env-vars "ASPNETCORE_ENVIRONMENT=Development"
```

**Cảnh báo:** Development hiện OTP, Swagger và không kiểm tra HTTPS nghiêm ngặt. Chỉ dùng cho demo nội bộ.

---

## Phần 11: Xem log và troubleshoot

### Xem log proxy

```bash
az containerapp logs show \
  --name nomori-proxy \
  --resource-group "$RG" \
  --follow
```

### Xem log API

```bash
az containerapp logs show \
  --name nomori-api \
  --resource-group "$RG" \
  --follow
```

### Restart một app

```bash
az containerapp update \
  --name nomori-api \
  --resource-group "$RG" \
  --force-restart
```

### Xem revisions và trạng thái

Portal → **Container Apps** → `nomori-api` → **Revisions and replicas**

---

## Phần 12: Cập nhật code và release lần 2, 3…

Quy trình:

1. **Merge code** vào main, test local
2. **Build image tag mới** (ví dụ `v2`):
   ```powershell
   $Version = "v2"
   docker build -f deploy/api.Dockerfile --target api -t "ghcr.io/$GitHubUser/nomori-api:$Version" .
   # (tương tự cho migrator, web, proxy)
   docker push "ghcr.io/$GitHubUser/nomori-api:$Version"
   # (tương tự push các image khác)
   ```

3. **Chạy migration trước API** (trong Cloud Shell):
   ```bash
   VER="v2"
   az containerapp job update --name nomori-migrate --resource-group "$RG" \
     --image "ghcr.io/$GH_USER/nomori-migrator:$VER"
   az containerapp job start --name nomori-migrate --resource-group "$RG"
   # Chờ Succeeded
   ```

4. **Update API rồi web rồi proxy**:
   ```bash
   az containerapp update --name nomori-api --resource-group "$RG" \
     --image "ghcr.io/$GH_USER/nomori-api:$VER"
   az containerapp update --name nomori-web --resource-group "$RG" \
     --image "ghcr.io/$GH_USER/nomori-web:$VER"
   az containerapp update --name nomori-proxy --resource-group "$RG" \
     --image "ghcr.io/$GH_USER/nomori-proxy:$VER"
   ```

5. **Smoke test**: Mở `$PUBLIC_URL`, kiểm tra chức năng cơ bản

---

## Phần 13: Sự cố thường gặp

| Triệu chứng | Nguyên nhân | Cách xử lý |
|---|---|---|
| Website 502/503 | App chưa khởi động hoặc crash | Đợi 30 giây, refresh; xem log `nomori-proxy` |
| `/health/ready` lỗi | API chưa khởi động, hoặc SQL pause | Chờ API scale up; kiểm tra SQL status: Portal → SQL database → Overview |
| Đăng nhập lỗi 500 | Request qua HTTP, không HTTPS | Dùng `https://` đúng; proxy phải báo `X-Forwarded-Proto: https` |
| Email không gửi | `Email__Enabled=false` hoặc SMTP sai | Cập nhật biến môi trường; kiểm tra `/admin/emails` |
| Job lỗi | Connection string sai, firewall chặn | Kiểm tra SQL firewall, password, Azure CLI biến |
| Hết free quota SQL | Chạy quá nhiều hoặc `Jobs__Enabled=true` | SQL sẽ tự pause; không bị tính phí nếu **Auto-pause** được chọn |
| Upload ảnh lỗi | Ảnh quá lớn (mặc định 5 MB) | Dùng ảnh nhỏ hơn; config MinIO/S3 là tuỳ chọn |
| Restart API → logout cả người | Bình thường, không có persistent key | Đăng nhập lại |

---

## Phần 14: Sau demo — Dọn dẹp

Khi chắc chắn không cần nữa:

```bash
# Xóa resource group (xóa mọi resource bên trong)
az group delete --name nomori-demo-rg --yes --no-wait
```

Hoặc qua Portal:
1. **Resource groups** → `nomori-demo-rg`
2. **Delete resource group** → gõ tên → **Delete**

Kiểm tra Cost Analysis không còn usage từ group.

**Tùy chọn:** Giữ Image trong GHCR (không tính phí) để deploy lại sau.

---

## Phần 15: Checklist cuối cùng

Trước khi gửi `$PUBLIC_URL` cho team:

- [ ] Azure SQL là **Free Offer** + **Auto-pause until next month**
- [ ] Không tạo: VM, ACR, VNet, Azure Files, Log Analytics, Key Vault
- [ ] Cả ba app (`nomori-api`, `nomori-web`, `nomori-proxy`) có `--min-replicas 0 --max-replicas 1`
- [ ] API có `Jobs__Enabled=false`
- [ ] `PUBLIC_URL` là URL `https://` của `nomori-proxy`
- [ ] API và web dùng **internal ingress**; chỉ proxy là **external**
- [ ] Smoke test xong: `/health/ready` = Healthy, `/storefront` tải được
- [ ] Có tài khoản admin hoạt động
- [ ] Budget alert đã thiết lập
- [ ] Không commit `.env`, password, PAT, connection string

---

## Tài liệu tham khảo

- [Azure SQL Database free offer](https://learn.microsoft.com/azure/azure-sql/database/free-offer)
- [Container Apps billing](https://learn.microsoft.com/azure/container-apps/billing)
- [Container Apps Jobs — Azure CLI](https://learn.microsoft.com/azure/container-apps/jobs-get-started-cli)
- [Quản lý secrets trong Container Apps](https://learn.microsoft.com/azure/container-apps/manage-secrets)

---

**Ghi chú cuối:** 
Đây là phiên bản demo, không dành cho production. Không nhập dữ liệu nhạy cảm thật. Nếu cần ổn định lâu dài, nâng cấp lên SQL Server thường + VM/App Service riêng biệt.
