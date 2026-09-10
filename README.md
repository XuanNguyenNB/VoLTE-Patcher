# VoLTE Vendor Patcher v1

Ứng dụng WinForms .NET 8 cho Windows 10/11 x64, nhận một `vendor.img` raw ext4 hoặc Android sparse và tạo file mới `<tên>_VoLTE_patched.img`. Image gốc luôn chỉ đọc; output được ghi qua `.partial`, kiểm tra xong mới rename.

## Chạy bản build

Tải `VoLTEVendorPatcher.exe` từ mục [Releases](https://github.com/XuanNguyenNB/VoLTE-Patcher/releases), kéo thả/chọn image, bấm **Phân tích**, rồi **Bắt đầu & Lưu**. Không cần WSL, Python, Cygwin cài sẵn, .NET hay Administrator. Bản release hiện chưa ký Authenticode; đối chiếu file `SHA256SUMS.txt` đi kèm release.

Tool chỉ cho phép OPPO/Realme MediaTek Android 8.1/API 27 đến Android 10/API 29 khi volume là `vendor`, ext4 sạch, có SELinux xattr, đủ chỗ trống và đủ IMS stack. Android 8.0/API 26, Qualcomm, EROFS, `super.img`, `payload.bin` và image xung đột sẽ bị từ chối.

Profile legacy API 27 dùng namespace `persist.mtk.*` theo F7; profile modern API 28+ thêm `persist.vendor.mtk.*`, đặt state `3` và tắt dynamic switch theo F11/Realme C2. Cả hai dùng APK overlay hash `1010C1C7855C0150C204C7E6376F665FCB3B55C609BDFFAFA8114B0952B81694`, priority 999, bật VoLTE/VT/WFC/Enhanced 4G. Không chỉnh `build.prop` và không đụng `persist.sys.feedback.rooted`.

## Build và test từ source

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Build\publish.ps1
.\Tests\run-smoke.ps1
```

`Build/bootstrap-tools.ps1` tải archive nguồn có hash khóa, cài helper Cygwin chính thức khi cần và cập nhật bundle helper. Sparse conversion được thực hiện managed trong EXE, nên runtime không có lệnh shell hay tải mạng. Chi tiết third-party license và giới hạn version xem [Build/README.md](Build/README.md) và [Assets/LICENSES.txt](VoLTEVendorPatcher/Assets/LICENSES.txt).

## Lưu ý sử dụng

Patch chỉ mở capability trong vendor; IMS vẫn cần modem tương thích và SIM/nhà mạng đã provision VoLTE. Hãy giữ bản sao image gốc và xác minh SHA-256 trước khi flash bằng quy trình recovery của thiết bị.
