# GitHub Actions Setup — Chi tiết cấu hình

**Ba workflow vừa tạo:**
1. `build-and-push.yml` (backend) — Build + push API, migrator, proxy
2. `build-and-push.yml` (frontend) — Build + push web image
3. `deploy-azure.yml` (backend) — Deploy tự động lên Azure

---

## 📋 Phần 1: Chuẩn bị Azure Credentials

Để GitHub Actions có quyền deploy lên Azure, cần tạo **Service Principal**.

### Bước 1.1 — Tạo Service Principal qua Azure CLI

Chạy trên máy local (sau khi `az login`):

```bash
az ad sp create-for-rbac \
  --name "nomori-github-actions" \
  --role Contributor \
  --scopes /subscriptions/<SUBSCRIPTION_ID>
```

**Lấy SUBSCRIPTION_ID:**
```bash
az account show --query id -o tsv
```

**Output sẽ là:**
```json
{
  "appId": "xxxx-xxxx-xxxx",
  "displayName": "nomori-github-actions",
  "password": "xxxx-xxxx-xxxx",
  "tenant": "xxxx-xxxx-xxxx"
}
```

**Lưu giá trị này vào password manager!**

### Bước 1.2 — Tạo GitHub Secret

1. Vào **GitHub repo** → **Settings** → **Secrets and variables** → **Actions**
2. **New repository secret** → Tạo `AZURE_CREDENTIALS`
3. **Value** — Dán JSON (theo mẫu dưới), thay giá trị từ service principal vừa tạo:

```json
{
  "clientId": "<appId>",
  "clientSecret": "<password>",
  "subscriptionId": "<subscription-id>",
  "tenantId": "<tenant>"
}
```

Ví dụ:
```json
{
  "clientId": "12345678-1234-1234-1234-123456789abc",
  "clientSecret": "xyz~abcdefghijk",
  "subscriptionId": "87654321-4321-4321-4321-cba987654321",
  "tenantId": "11111111-2222-3333-4444-555555555555"
}
```

✅ Save

---

## 🔄 Phần 2: Build + Push (Tự động)

### Workflow: `build-and-push.yml` (Backend)

**Kích hoạt khi:**
- Push code vào `main` branch
- Sửa file trong `deploy/`, `src/`, `Nomori.Marketplace.*/`
- Hoặc chạy thủ công (workflow_dispatch)

**Output:**
- `ghcr.io/yourgithub/nomori-api:TAG`
- `ghcr.io/yourgithub/nomori-migrator:TAG`
- `ghcr.io/yourgithub/nomori-proxy:TAG`

**TAG là:**
- Nếu push tự động: `YYYY-MM-DD.HHMMSS` (ví dụ: `2026-10-27.143025`)
- Nếu workflow_dispatch: Nhập tay (ví dụ: `v1`, `v2`, `release-1.0`)
- Luôn có `latest`

### Workflow: `build-and-push.yml` (Frontend)

**Kích hoạt khi:**
- Push vào `master` hoặc `main` branch
- Sửa file trong `src/`, `package.json`, `Dockerfile`
- Hoặc chạy thủ công

**Output:**
- `ghcr.io/yourgithub/nomori-web:TAG`
- `ghcr.io/yourgithub/nomori-web:latest`

---

## 🚀 Phần 3: Deploy (Thủ công trigger)

### Workflow: `deploy-azure.yml`

**Kích hoạt:** Chỉ thủ công (workflow_dispatch) — không tự động

**Các bước:**
1. ✅ (Tuỳ chọn) Chạy migration
2. ✅ Update API image
3. ✅ Update web image
4. ✅ Update proxy image
5. ✅ Kiểm tra health check
6. ✅ Gửi notification

**Input:**
- `version`: Tag image cần deploy (ví dụ: `v1`, `latest`, `2026-10-27.143025`)
- `run_migration`: Có chạy migration không? (default: Yes)

---

## 📖 Cách sử dụng

### Lần 1: Test build + push

**Mục đích:** Kiểm tra workflow có chạy đúng không

**Cách:**
1. GitHub repo → **Actions** tab
2. **Build and Push Backend Images** (hoặc Frontend)
3. **Run workflow** → Manual trigger
   - Input `version`: `test-v1`
   - **Run workflow**
3. Chờ ~5–10 phút (build image)
4. Khi xanh ✅ → Vào GitHub Packages → kiểm tra image `nomori-api:test-v1` tồn tại

### Lần 2: Test deploy

**Mục đích:** Kiểm tra deploy workflow hoạt động

**Cách:**
1. GitHub repo → **Actions** tab
2. **Deploy to Azure Container Apps**
3. **Run workflow**
   - Input `version`: `latest` (hoặc tag vừa build)
   - Input `run_migration`: ✅ (checked)
   - **Run workflow**
4. Chờ ~2–5 phút (push + deploy)
5. Khi xanh ✅ → Xem output → copy URL (`https://nomori-proxy.xxxxx.azurecontainerapps.io`)
6. Mở URL → kiểm tra `/health/ready` = Healthy

### Lần 3+: Flow thực tế

```
[1] Push code vào main
         ↓
[2] build-and-push.yml tự động chạy (30–45 phút)
         ↓
[3] Image được push lên GHCR (tag = 2026-10-27.143025)
         ↓
[4] (Manual) Vào GitHub Actions → deploy-azure.yml
    → Input: version = 2026-10-27.143025, run_migration = yes
    → Run workflow
         ↓
[5] Deploy tự động lên Azure (2–5 phút)
         ↓
[6] ✅ Xong, URL ready
```

---

## 🔧 Cấu hình chi tiết

### Backend Workflow Triggers

Mặc định kích hoạt khi:
- Push vào `main`
- **VÀ** sửa file trong:
  - `deploy/**`
  - `src/**`
  - `Nomori.Marketplace.Api/**`
  - `Nomori.Marketplace.Core/**`
  - `Nomori.Marketplace.Data/**`
  - `Nomori.Marketplace.Services/**`
  - `Nomori.Marketplace.Web.Framework/**`
  - `.github/workflows/build-and-push.yml`

**Nếu sửa file khác mà không muốn build**, không cần chạy.

### Frontend Workflow Triggers

Mặc định kích hoạt khi:
- Push vào `master` hoặc `main`
- **VÀ** sửa file trong:
  - `src/**`
  - `public/**`
  - `package.json`
  - `package-lock.json`
  - `Dockerfile`
  - `.github/workflows/build-and-push.yml`

---

## 📊 Giám sát

### Xem log workflow

1. GitHub repo → **Actions** tab
2. Chọn workflow vừa chạy
3. Chọn job → xem log chi tiết

### GitHub Packages

1. Vào **GitHub** → Profile → **Packages**
2. Kiểm tra image:
   - `nomori-api:TAG`
   - `nomori-migrator:TAG`
   - `nomori-proxy:TAG`
   - `nomori-web:TAG`

### Azure Portal

1. **Container Apps** → `nomori-api` / `nomori-web` / `nomori-proxy`
2. **Revisions** → Kiểm tra revision mới đã tạo
3. **Logs** → Xem chi tiết nếu có lỗi

---

## 🆘 Troubleshooting

| Vấn đề | Nguyên nhân | Giải pháp |
|---|---|---|
| Build fail với lỗi Docker | Dockerfile có lỗi hoặc đường dẫn sai | Xem log GitHub Actions → xem dòng lỗi |
| Push fail: 403 Forbidden | GITHUB_TOKEN không có quyền write packages | Kiểm tra GitHub repo settings → Actions permissions |
| Deploy fail: "azure/login failed" | AZURE_CREDENTIALS secret không đúng | Kiểm tra JSON format, appId, clientSecret, subscriptionId |
| Deploy hang (không hoàn tất) | Migration chạy lâu hoặc app không khởi động | Chờ 10–15 phút; nếu vẫn fail, xem Azure Portal logs |
| Image không push lên GHCR | GHCR package không public | Vào GitHub Packages → Settings → Change visibility → Public |

---

## 🎯 Checklist cấu hình

- [ ] Tạo Azure Service Principal
- [ ] Tạo GitHub Secret `AZURE_CREDENTIALS`
- [ ] Push ba file workflow vào repo
- [ ] Test build-and-push (backend) thủ công
- [ ] Kiểm tra image trong GitHub Packages
- [ ] Test deploy-azure thủ công
- [ ] Kiểm tra URL deploy thành công
- [ ] (Tuỳ chọn) Config branch protection rules (require workflow pass)

---

## 📝 Lệnh nhanh

### Tạo Service Principal

```bash
az account show --query id -o tsv
# Copy subscription ID

az ad sp create-for-rbac \
  --name "nomori-github-actions" \
  --role Contributor \
  --scopes /subscriptions/<SUBSCRIPTION_ID>
```

### Test build locally (không cần GitHub)

```bash
# Backend
docker build -f deploy/api.Dockerfile --target api -t nomori-api:test .

# Frontend
cd ../nomori-marketplace-frontend
docker build -f Dockerfile -t nomori-web:test .
```

---

## ⏭️ Tiếp theo

1. **Cấu hình xong?** → Làm theo "Cách sử dụng" ở trên
2. **Muốn auto-deploy?** → Modify `deploy-azure.yml` trigger từ `workflow_dispatch` → `on: [push]`
3. **Muốn skip migration?** → Deploy job sẽ hỏi; chọn `run_migration: false`

---

**Chúc may mắn! 🚀**
