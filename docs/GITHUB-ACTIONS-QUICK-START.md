# GitHub Actions — Bắt đầu nhanh

**Vừa tạo xong:** 3 workflow + 1 setup guide

---

## 🎯 Cái bạn có ngay bây giờ

| File | Tác dụng | Trigger |
|---|---|---|
| `backend/.github/workflows/build-and-push.yml` | Build API, migrator, proxy → push GHCR | Push main + path change, hoặc thủ công |
| `frontend/.github/workflows/build-and-push.yml` | Build web → push GHCR | Push main/master + path change, hoặc thủ công |
| `backend/.github/workflows/deploy-azure.yml` | Deploy image lên Azure | Thủ công (workflow_dispatch) |
| `docs/github-actions-setup.md` | Hướng dẫn cấu hình | (Đọc trước) |

---

## ⚡ 5 bước để chạy được

### Bước 1: Tạo Azure Service Principal

```bash
# Lấy subscription ID
az account show --query id -o tsv

# Tạo service principal
az ad sp create-for-rbac \
  --name "nomori-github-actions" \
  --role Contributor \
  --scopes /subscriptions/<SUBSCRIPTION_ID>
```

**Copy output** (một JSON với appId, password, tenant)

### Bước 2: Tạo GitHub Secret

1. GitHub repo → **Settings** → **Secrets and variables** → **Actions**
2. **New repository secret** → Tên: `AZURE_CREDENTIALS`
3. **Value**: Dán JSON từ output bước 1 (thay `password` thành `clientSecret`)

```json
{
  "clientId": "...",
  "clientSecret": "...",
  "subscriptionId": "...",
  "tenantId": "..."
}
```

✅ **Save**

### Bước 3: Commit 3 workflow file

```powershell
# Đã tạo sẵn ở:
# - backend/.github/workflows/build-and-push.yml
# - backend/.github/workflows/deploy-azure.yml
# - frontend/.github/workflows/build-and-push.yml

git add .github/
git commit -m "setup: add github actions workflows for build and deploy"
git push origin main
```

### Bước 4: Test build workflow

1. GitHub repo → **Actions** tab
2. **Build and Push Backend Images**
3. **Run workflow** → Input `version: test-v1` → **Run**
4. Chờ 5–10 phút xong ✅

### Bước 5: Test deploy workflow

1. GitHub repo → **Actions** tab
2. **Deploy to Azure Container Apps**
3. **Run workflow** → Input `version: latest` → **Run migration: Yes** → **Run**
4. Chờ 2–5 phút → Xem output → Copy URL
5. Mở URL → Check `/health/ready` = Healthy ✅

---

## 📊 Flow thực tế

```
[Ngày 1] Push code vào main
  ↓
[30–45 phút] GitHub Actions tự build & push image
  ↓
[Tùy chọn] Vào GitHub Actions → Deploy to Azure (thủ công)
  ↓
[2–5 phút] Deploy xong
  ↓
[✅] Live trên Azure
```

---

## 🎛️ Điều khiển

### Build Backend (thủ công)

```
GitHub → Actions → Build and Push Backend Images
→ Run workflow → Input version (ví dụ: v2)
→ Run → 🔄 Build & push
```

### Build Frontend (thủ công)

```
GitHub → Actions → Build and Push Frontend Images
→ Run workflow → Input version (ví dụ: v2)
→ Run → 🔄 Build & push
```

### Deploy to Azure (thủ công)

```
GitHub → Actions → Deploy to Azure Container Apps
→ Run workflow
  ├─ version: v2 (hoặc tag vừa build)
  └─ run_migration: Yes/No
→ Run → 🚀 Deploy
```

---

## 🔑 Cần tạo trước

❌ **Chưa có?**
- [ ] Azure Service Principal
- [ ] GitHub Secret `AZURE_CREDENTIALS`

❌ **Cần commit?**
- [ ] 3 file `.github/workflows/*.yml`

---

## 📚 Tài liệu

- **Chi tiết:** `docs/github-actions-setup.md` ← Đọc để hiểu đầy đủ
- **Flow diagram:** `docs/deployment-azure-step-by-step.md` (Phần 1, 2)

---

## 🚀 Sẵn sàng?

✅ Bước 1–5 xong → **Workflows đã chạy được!**

❓ Các câu hỏi?
- **Làm sao test?** → Xem bước 4–5
- **Làm sao update version?** → Workflow có input parameter
- **Deployment fail?** → Xem troubleshooting ở `github-actions-setup.md`

---

**Chúc thành công!** 🎉
