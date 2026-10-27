# Nomori Azure Deployment — Quick Start

**Bạn đã chọn:** Phương án B (Azure SQL Free + Container Apps)

---

## 📚 Tài liệu cần đọc (theo thứ tự)

1. **File này** — Tóm tắt bắt đầu (2 phút)
2. **`deployment-azure-checklist.md`** — Checklist nhanh để in hoặc ghi nhớ (1 phút scan)
3. **`deployment-azure-step-by-step.md`** — Hướng dẫn chi tiết (làm theo từng bước, ~90 phút)

---

## ⚠️ Điều cốt lõi

**Sau khi Azure account tạo xong, ngay lập tức:**

1. **Tạo Budget alert** → Cost Management + Billing → Budgets
   - Đặt ngưỡng thấp (ví dụ 1 USD)
   - Email nhận cảnh báo
   - **Không bỏ qua bước này!**

2. **Đọc qua "Giới hạn chi phí" trong file step-by-step**
   - Phải hiểu tại sao không tạo VM, ACR, Azure Files
   - Phải hiểu `Auto-pause until next month` là **bắt buộc**

3. **Không dùng dữ liệu thật** — chỉ dữ liệu test/demo

---

## 🚀 Quy trình 90 phút (tóm tắt)

```
[1] Build & Push image (30–45 phút)
    ├─ Tạo GitHub PAT
    ├─ Build 4 image (API, migrator, proxy, web)
    └─ Push lên GHCR (GitHub Container Registry)

[2] Tạo SQL Database (10 phút)
    └─ Qua Portal: Free Offer + Auto-pause

[3] Tạo Container Apps (20 phút)
    ├─ Environment
    ├─ 3 apps: API, web, proxy
    └─ 1 job: migrator

[4] Migration & Config (15 phút)
    ├─ Chạy migration job
    ├─ Cập nhật PUBLIC_URL
    └─ Smoke test

[5] Tạo admin (5 phút)
    └─ Đăng ký + cấp quyền SQL
```

---

## 📋 Chuẩn bị trước

### Máy

- [ ] Docker Desktop chạy (WSL2)
- [ ] Git + hai repo cạnh nhau
- [ ] PowerShell / Bash
- [ ] Azure CLI (hoặc dùng Cloud Shell)

### Tài khoản

- [ ] Azure account (đã có)
- [ ] GitHub account
- [ ] GitHub PAT (write:packages)

### Secret (lưu vào password manager, **không commit**)

```
SQL_ADMIN_PASSWORD   = YourStrongPassword123!     (8+, hoa/thường/số/ký tự)
SANDBOX_SECRET       = random-key-32-chars        (random, dùng 1 lần)
SMTP_PASSWORD        = (tuỳ chọn, nếu dùng email)
```

**Yêu cầu password SQL:**
- ❌ Không: dấu cách, `;`, `=`, `"`, `'`, `$`, `\`
- ✅ Được: hoa/thường, số, `-`, `_`, `!`, `@`, `#`, `%`, `&`

---

## 🎯 Các file được chuẩn bị sẵn

✅ **Đã có trong repo:**
- `deploy/api.Dockerfile` — Package API + migrator
- `deploy/Caddyfile` — Reverse proxy config
- `deploy/docker-compose.yml` — Local development (không dùng cho Azure)
- `deploy/.env.example` — Mẫu biến môi trường
- `nomori-marketplace-frontend/Dockerfile` — Package web
- `nomori-marketplace-frontend/.dockerignore`

❌ **Cần tạo:**
- `deploy/azure-proxy.Dockerfile` — Nhỏ, tạo từ Caddyfile (hướng dẫn step-by-step phần 2.3)

---

## 🔑 Bước quan trọng nhất

### GitHub PAT

Cần để push image lên GitHub Container Registry:

1. <https://github.com/settings/tokens?type=beta>
2. **Generate new token (classic)**
3. Scopes: **write:packages**
4. Copy token (chỉ hiển thị 1 lần)
5. Lưu vào password manager

### SQL Password

Tạo mật khẩu mạnh (8+ ký tự, hoa/thường/số/special character):

```
Ví dụ: MyS3cur3P@ss!
```

Không được:
- ❌ Có dấu cách
- ❌ Chứa `;`, `=`, `"`, `'`, `$`, `\`

### Budget Alert

Ngay sau khi đăng ký Azure:

1. Portal → **Cost Management + Billing**
2. **Budgets** → **Add**
3. Đặt threshold 1–5 USD
4. Email nhận cảnh báo

**Nếu bỏ qua bước này, rủi ro bị tính tiền ngoài dự tính.**

---

## 💻 Chuỗi lệnh chính (PowerShell)

### 1. Đặt biến

```powershell
$GitHubUser = "yourgithub"
$GitHubToken = "ghp_..."
$Version = "v1"
$SqlServer = "nomori-demo-sql-12345"
$SqlPassword = "MyS3cur3P@ss!"
$SandboxSecret = "random-secret-key"
```

### 2. Login Docker & Build

```powershell
$GitHubToken | docker login ghcr.io -u $GitHubUser --password-stdin

cd nomori-marketplace-backend

docker build -f deploy/api.Dockerfile --target api -t "ghcr.io/$GitHubUser/nomori-api:$Version" .
docker build -f deploy/api.Dockerfile --target migrator -t "ghcr.io/$GitHubUser/nomori-migrator:$Version" .
docker build -f deploy/azure-proxy.Dockerfile -t "ghcr.io/$GitHubUser/nomori-proxy:$Version" .

cd ..\nomori-marketplace-frontend
docker build -f Dockerfile -t "ghcr.io/$GitHubUser/nomori-web:$Version" .
cd ..\nomori-marketplace-backend
```

### 3. Push

```powershell
docker push "ghcr.io/$GitHubUser/nomori-api:$Version"
docker push "ghcr.io/$GitHubUser/nomori-migrator:$Version"
docker push "ghcr.io/$GitHubUser/nomori-proxy:$Version"
docker push "ghcr.io/$GitHubUser/nomori-web:$Version"
```

### 4. Đặt image Public (GitHub)

Vào GitHub → Packages → mỗi package → Settings → **Change visibility** → **Public**

---

## ☁️ Chuỗi lệnh Azure (Bash/Cloud Shell)

### 1. Chuẩn bị biến

```bash
RG="nomori-demo-rg"
LOC="southeastasia"
ENV="nomori-demo-env"
GH_USER="yourgithub"
VER="v1"
SQL_SERVER="nomori-demo-sql-12345"
SQL_ADMIN="nomoriadmin"
SQL_PASSWORD='MyS3cur3P@ss!'
SANDBOX_SECRET='random-secret-key'

DB_CONNECTION="Server=tcp:${SQL_SERVER}.database.windows.net,1433;Initial Catalog=NomoriMarketplace_Db;Persist Security Info=False;User ID=${SQL_ADMIN};Password=${SQL_PASSWORD};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;"
```

### 2. Tạo environment

```bash
az extension add --name containerapp --upgrade
az group create --name "$RG" --location "$LOC"
az containerapp env create --name "$ENV" --resource-group "$RG" --location "$LOC"
```

### 3. Tạo 3 apps (xem chi tiết ở step-by-step)

```bash
# API
az containerapp create --name nomori-api ...

# Web
az containerapp create --name nomori-web ...

# Proxy
az containerapp create --name nomori-proxy ...
```

### 4. Lấy PUBLIC_URL

```bash
PROXY_FQDN=$(az containerapp show --name nomori-proxy --resource-group "$RG" \
  --query properties.configuration.ingress.fqdn --output tsv)
PUBLIC_URL="https://${PROXY_FQDN}"
echo "PUBLIC_URL=$PUBLIC_URL"
```

**Lưu PUBLIC_URL này!** Ví dụ: `https://nomori-proxy.xxxxx.azurecontainerapps.io`

### 5. Cập nhật URL

```bash
# API + Web
az containerapp update --name nomori-api --resource-group "$RG" \
  --set-env-vars "Seo__SiteBaseUrl=$PUBLIC_URL" "Email__FrontendBaseUrl=$PUBLIC_URL" "Payments__Sandbox__PageBaseUrl=$PUBLIC_URL"

az containerapp update --name nomori-web --resource-group "$RG" \
  --set-env-vars "PUBLIC_URL=$PUBLIC_URL"
```

### 6. Tạo & chạy migration

```bash
az containerapp job create --name nomori-migrate ...
az containerapp job start --name nomori-migrate --resource-group "$RG"

# Chờ Succeeded
az containerapp job execution list --name nomori-migrate --resource-group "$RG" --output table
```

---

## 🧪 Smoke Test (sau migration Succeeded)

Mở trình duyệt:

```
https://nomori-proxy.xxxxx.azurecontainerapps.io/health/ready
```

Kỳ vọng: `{"status":"Healthy",...}`

Nếu lỗi:
- Đợi 30 giây (container khởi động)
- Refresh
- Nếu vẫn lỗi → xem log: Portal → `nomori-proxy` → Logs

---

## 👤 Tạo admin

1. Truy cập `https://nomori-proxy.xxxxx.azurecontainerapps.io`
2. Đăng ký email `admin@test.local`
3. Xác thực (nếu Development → mã OTP trên màn hình)
4. Cấp quyền qua SQL (xem step-by-step phần 9.2)

---

## 🚨 Kiểm tra cuối cùng trước gửi link

- [ ] SQL là **Free Offer** + **Auto-pause until next month**
- [ ] 3 apps có `min=0, max=1`
- [ ] API có `Jobs__Enabled=false`
- [ ] Proxy là **external ingress** duy nhất; API/web là **internal**
- [ ] Budget alert đã thiết lập
- [ ] Smoke test xong (health check + trang web)
- [ ] Admin account sẵn sàng
- [ ] Không commit password / PAT / connection string
- [ ] Đã test link từ máy khác

---

## 📖 Khi gặp vấn đề

| Triệu chứng | Giải pháp |
|---|---|
| Không biết bước tiếp theo | Mở `deployment-azure-step-by-step.md` |
| Quên lệnh gì | Mở `deployment-azure-checklist.md` |
| App không lên | Xem log: `az containerapp logs show --name <app> --resource-group nomori-demo-rg --follow` |
| SQL connection error | Kiểm tra firewall, password, `Auto-pause` |
| Email không gửi | Cập nhật biến SMTP hoặc để trống (email nằm ở `/admin/emails`) |
| Hết free quota SQL | Bình thường; sẽ tự pause (nếu `Auto-pause` được chọn) |

---

## ✅ Sẵn sàng?

1. **Đã setup Budget alert?** → Nếu chưa, làm ngay trước khi bắt đầu
2. **Đã chuẩn bị secret?** → Lưu vào password manager
3. **Đã hiểu giới hạn chi phí?** → Đọc qua phần đó ở step-by-step

Nếu cả 3 câu trả lời "Có", bạn đã sẵn sàng! 🚀

**Bước tiếp theo:** Mở `deployment-azure-step-by-step.md` rồi làm theo từng phần.

---

## 📞 Liên hệ hỗ trợ

Nếu cần giúp đỡ:

1. **Xem phần Troubleshooting** ở step-by-step (Phần 13)
2. **Kiểm tra Cloud Shell log** để tìm error cụ thể
3. **Tham khảo tài liệu Microsoft chính thức** (link ở cuối step-by-step)

---

**Thời gian dự kiến:** 90 phút lần đầu (lần sau chỉ 10 phút khi update code)

**Hãy bắt đầu!** 🎯
