# Nomori Azure Deployment — Checklist nhanh

**Thời gian:** ~90 phút lần đầu

---

## ✅ Pre-flight (5 phút)

- [ ] Azure account tạo xong
- [ ] GitHub account sẵn sàng
- [ ] Docker Desktop chạy (WSL2)
- [ ] Hai repo cạnh nhau: `nomori-marketplace-backend`, `nomori-marketplace-frontend`
- [ ] Tạo GitHub PAT (write:packages) → lưu vào password manager
- [ ] Chuẩn bị ba secret:
  - `SQL_ADMIN_PASSWORD` (8+ ký tự, hoa/thường/số/ký tự đặc biệt, **không** dấu cách/`;="\\'$\`)
  - `SANDBOX_SECRET` (chuỗi ngẫu nhiên)
  - `SMTP_PASSWORD` (tuỳ chọn)
- [ ] Setup Budget alert trong Azure Portal

---

## 🔨 Build & Push (30–45 phút)

### Chuẩn bị biến (PowerShell)

```powershell
$GitHubUser = "yourgithub"
$GitHubToken = "ghp_..."
$Version = "v1"
$SqlServer = "nomori-demo-sql-XXXXX"    # thêm số ngẫu nhiên
$SqlPassword = "YourStrongPassword123!"
$SandboxSecret = "random-secret-key"
```

### Docker login

```powershell
$GitHubToken | docker login ghcr.io -u $GitHubUser --password-stdin
```

### Build 4 image

```powershell
cd nomori-marketplace-backend

# 1. API
docker build -f deploy/api.Dockerfile --target api -t "ghcr.io/$GitHubUser/nomori-api:$Version" .

# 2. Migrator
docker build -f deploy/api.Dockerfile --target migrator -t "ghcr.io/$GitHubUser/nomori-migrator:$Version" .

# 3. Tạo azure-proxy.Dockerfile nếu chưa có
@"
FROM caddy:2-alpine
COPY deploy/Caddyfile /etc/caddy/Caddyfile
"@ | Out-File -Encoding utf8 deploy/azure-proxy.Dockerfile

# 4. Proxy
docker build -f deploy/azure-proxy.Dockerfile -t "ghcr.io/$GitHubUser/nomori-proxy:$Version" .

# 5. Web
cd ..\nomori-marketplace-frontend
docker build -f Dockerfile -t "ghcr.io/$GitHubUser/nomori-web:$Version" .
cd ..\nomori-marketplace-backend
```

### Push 4 image

```powershell
docker push "ghcr.io/$GitHubUser/nomori-api:$Version"
docker push "ghcr.io/$GitHubUser/nomori-migrator:$Version"
docker push "ghcr.io/$GitHubUser/nomori-proxy:$Version"
docker push "ghcr.io/$GitHubUser/nomori-web:$Version"
```

### Đặt image Public (GitHub → Packages → Settings)

- [ ] nomori-api → Public
- [ ] nomori-migrator → Public
- [ ] nomori-proxy → Public
- [ ] nomori-web → Public

---

## 🗄️ Tạo SQL Database (10 phút)

### Qua Portal

1. **Create a resource** → **SQL Database** → **Create**
2. **Subscription**: Free Trial
3. **Resource group**: Create new → `nomori-demo-rg`
4. **Database name**: `NomoriMarketplace_Db`
5. **Server**: Create new
   - **Name**: `nomori-demo-sql-12345` (duy nhất)
   - **Location**: `Southeast Asia` (khớp Container Apps)
   - **Admin login**: `nomoriadmin`
   - **Password**: `$SqlPassword`
6. **Compute + storage**: Chọn **Azure SQL Database free offer**
   - ✅ **Behavior**: `Auto-pause until next month` (BẮT BUỘC)
7. **Networking**:
   - **Public endpoint**: Selected networks
   - Add your client IPv4
   - **Allow Azure services** → ON
8. **Create**

---

## ☁️ Tạo Container Apps (20 phút)

### Cloud Shell — Chuẩn bị biến Bash

```bash
RG="nomori-demo-rg"
LOC="southeastasia"
ENV="nomori-demo-env"
GH_USER="yourgithub"
VER="v1"
SQL_SERVER="nomori-demo-sql-12345"
SQL_ADMIN="nomoriadmin"
SQL_PASSWORD='YourStrongPassword123!'
SANDBOX_SECRET='random-secret-key'

DB_CONNECTION="Server=tcp:${SQL_SERVER}.database.windows.net,1433;Initial Catalog=NomoriMarketplace_Db;Persist Security Info=False;User ID=${SQL_ADMIN};Password=${SQL_PASSWORD};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;"
PUBLIC_URL_PLACEHOLDER="https://example.invalid"
```

### Update Azure CLI

```bash
az extension add --name containerapp --upgrade
az group create --name "$RG" --location "$LOC"
az containerapp env create --name "$ENV" --resource-group "$RG" --location "$LOC"
```

### Tạo 3 apps

#### API (internal)

```bash
az containerapp create \
  --name nomori-api --resource-group "$RG" --environment "$ENV" \
  --image "ghcr.io/$GH_USER/nomori-api:$VER" \
  --ingress internal --target-port 8080 --min-replicas 0 --max-replicas 1 \
  --cpu 0.25 --memory 0.5Gi \
  --secrets db-connection="$DB_CONNECTION" sandbox-secret="$SANDBOX_SECRET" \
  --env-vars \
    "ASPNETCORE_ENVIRONMENT=Production" \
    "ASPNETCORE_URLS=http://+:8080" \
    "ASPNETCORE_FORWARDEDHEADERS_ENABLED=true" \
    "Database__ConnectionString=secretref:db-connection" \
    "Payments__Sandbox__Secret=secretref:sandbox-secret" \
    "Jobs__Enabled=false" \
    "Email__Enabled=false"
```

#### Web (internal)

```bash
az containerapp create \
  --name nomori-web --resource-group "$RG" --environment "$ENV" \
  --image "ghcr.io/$GH_USER/nomori-web:$VER" \
  --ingress internal --target-port 4000 --min-replicas 0 --max-replicas 1 \
  --cpu 0.25 --memory 0.5Gi \
  --env-vars \
    "PORT=4000" \
    "API_ORIGIN=http://nomori-api" \
    "PUBLIC_URL=$PUBLIC_URL_PLACEHOLDER"
```

#### Proxy (public)

```bash
az containerapp create \
  --name nomori-proxy --resource-group "$RG" --environment "$ENV" \
  --image "ghcr.io/$GH_USER/nomori-proxy:$VER" \
  --ingress external --target-port 80 --min-replicas 0 --max-replicas 1 \
  --cpu 0.25 --memory 0.5Gi \
  --env-vars \
    "SITE_ADDRESS=:80" \
    "API_UPSTREAM=http://nomori-api" \
    "WEB_UPSTREAM=http://nomori-web"
```

### Lấy PUBLIC_URL

```bash
PROXY_FQDN=$(az containerapp show --name nomori-proxy --resource-group "$RG" \
  --query properties.configuration.ingress.fqdn --output tsv)
PUBLIC_URL="https://${PROXY_FQDN}"
echo "PUBLIC_URL=$PUBLIC_URL"
```

**Lưu URL này!** Ví dụ: `https://nomori-proxy.xxxxx.azurecontainerapps.io`

---

## 🔗 Cập nhật PUBLIC_URL (5 phút)

```bash
# API
az containerapp update --name nomori-api --resource-group "$RG" \
  --set-env-vars \
    "Seo__SiteBaseUrl=$PUBLIC_URL" \
    "Email__FrontendBaseUrl=$PUBLIC_URL" \
    "Payments__Sandbox__PageBaseUrl=$PUBLIC_URL"

# Web
az containerapp update --name nomori-web --resource-group "$RG" \
  --set-env-vars \
    "PORT=4000" \
    "API_ORIGIN=http://nomori-api" \
    "PUBLIC_URL=$PUBLIC_URL"
```

---

## 🗂️ Tạo & Chạy Migration (10 phút)

### Tạo job

```bash
az containerapp job create \
  --name nomori-migrate --resource-group "$RG" --environment "$ENV" \
  --trigger-type Manual --replica-timeout 1800 --replica-retry-limit 1 \
  --replica-completion-count 1 --parallelism 1 \
  --image "ghcr.io/$GH_USER/nomori-migrator:$VER" \
  --cpu 0.25 --memory 0.5Gi \
  --secrets db-connection="$DB_CONNECTION" \
  --env-vars "Database__ConnectionString=secretref:db-connection"
```

### Chạy

```bash
az containerapp job start --name nomori-migrate --resource-group "$RG"

# Chờ tới khi Succeeded
az containerapp job execution list --name nomori-migrate --resource-group "$RG" --output table
```

- [ ] Status: **Succeeded**

---

## 🧪 Smoke Test (5 phút)

Truy cập vào trình duyệt:

1. **Health check**: `$PUBLIC_URL/health/ready` → phải `Healthy`
   - [ ] Pass

2. **Trang cửa hàng**: `$PUBLIC_URL/storefront` → tải được
   - [ ] Pass

3. **Đăng ký/Đăng nhập**: Tạo tài khoản → không lỗi 500
   - [ ] Pass

4. **Xem nguồn HTML**: View Source → có `<title>`, canonical, meta
   - [ ] Pass

---

## 👤 Tạo Admin (5 phút)

### Đăng ký trên web

1. Vào `$PUBLIC_URL`
2. Sign up email `admin@test.local`
3. Xác thực (nếu Development → mã OTP trên màn hình)

### Cấp quyền qua SQL

Dùng Azure Data Studio / SSMS:

```sql
UPDATE Customer 
SET EmailVerified = 1, EmailVerifiedOnUtc = SYSUTCDATETIME() 
WHERE Email = 'admin@test.local';

INSERT INTO CustomerCustomerRoleMapping (CustomerId, CustomerRoleId)
SELECT c.Id, r.Id 
FROM Customer c CROSS JOIN CustomerRole r
WHERE c.Email = 'admin@test.local' 
  AND r.SystemName = 'Administrator'
  AND NOT EXISTS (SELECT 1 FROM CustomerCustomerRoleMapping m 
    WHERE m.CustomerId = c.Id AND m.CustomerRoleId = r.Id);
```

- [ ] Đăng xuất, đăng nhập lại → vào `/admin` thành công

---

## 📤 Gửi link cho team

**URL gửi**: `https://nomori-proxy.xxxxx.azurecontainerapps.io`

- [ ] Đã test link từ máy khác
- [ ] Admin account sẵn sàng
- [ ] Budget alert thiết lập

---

## 🧹 Dọn dẹp sau (nếu không dùng)

```bash
az group delete --name nomori-demo-rg --yes --no-wait
```

Hoặc Portal → Resource groups → `nomori-demo-rg` → Delete

- [ ] Resource group deleted
- [ ] Cost analysis không còn usage mới

---

## 🚨 Điều quan trọng

| ❌ **Đừng làm** | ✅ **Làm đúng** |
|---|---|
| Chọn SQL tier trả phí | Chọn **Free Offer** + **Auto-pause** |
| `min-replicas > 0` | `min-replicas 0`, `max-replicas 1` |
| Bật `Jobs__Enabled=true` | Bắt buộc **false** (tắt job) |
| Commit `.env`, password, PAT | Lưu vào password manager |
| Dùng dữ liệu nhạy cảm thật | Chỉ dữ liệu test |
| Quên Budget alert | Thiết lập ngay sau khi tạo account |

---

## 🔗 Tham khảo nhanh

| Cần làm gì? | Xem đâu? |
|---|---|
| Chi tiết từng bước | `docs/deployment-azure-step-by-step.md` |
| Troubleshoot | Phần 13 của step-by-step guide |
| Lần 2 update code | Phần 12: Update code and release |
| Xem log | `az containerapp logs show --name <app> --resource-group "$RG"` |
| Dừng lại | Xóa resource group (Phần 14) |

---

## Lệnh nhanh (copy-paste)

### Xem status

```bash
az containerapp list --resource-group nomori-demo-rg --output table
```

### Xem log

```bash
az containerapp logs show --name nomori-api --resource-group nomori-demo-rg --follow
```

### Force restart

```bash
az containerapp update --name nomori-api --resource-group nomori-demo-rg --force-restart
```

### Update image mới (sau release)

```bash
# Chuẩn bị: $Version = "v2"
az containerapp job update --name nomori-migrate --resource-group nomori-demo-rg --image "ghcr.io/yourgithub/nomori-migrator:v2"
az containerapp job start --name nomori-migrate --resource-group nomori-demo-rg
# (chờ Succeeded)
az containerapp update --name nomori-api --resource-group nomori-demo-rg --image "ghcr.io/yourgithub/nomori-api:v2"
az containerapp update --name nomori-web --resource-group nomori-demo-rg --image "ghcr.io/yourgithub/nomori-web:v2"
az containerapp update --name nomori-proxy --resource-group nomori-demo-rg --image "ghcr.io/yourgithub/nomori-proxy:v2"
```

---

**Chúc thành công! 🚀**
