# Build VoLTE Vendor Patcher

`publish.ps1` tạo bản phát hành Windows 10/11 x64 self-contained, single-file. Bản EXE không tải mạng khi chạy; helper native và payload đã được nhúng, sau đó kiểm tra SHA-256 trước khi thực thi.

## Build nhanh

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Build\publish.ps1
```

`bootstrap-tools.ps1` tải và kiểm tra:

- e2fsprogs 1.47.4 source từ kernel.org;
- AOSP `libsparse` source archive;
- Cygwin setup chính thức và gói `e2fsprogs` Windows, rồi chép `debugfs`, `e2fsck` cùng DLL cần thiết vào `VoLTEVendorPatcher/Assets/Tools`.

Các hash URL được khóa trong [tools.lock.json](tools.lock.json). Hash helper được sinh lại trong `Assets/Tools/tools.lock.json` và được ứng dụng kiểm tra trước mỗi lần chạy. Cygwin hiện chỉ phát hành gói Windows e2fsprogs 1.44.5-1; source 1.47.4 vẫn được tải/khóa để audit và là điểm thay thế native helper khi có bản MinGW 1.47.4 được đóng gói.

Sparse image được xử lý bằng converter managed trong ứng dụng (không cần WSL hoặc `simg2img` lúc chạy). Archive `libsparse` được khóa để có thể thay thế bằng native AOSP helper trong pipeline sau này mà không đổi hợp đồng GUI.

## Ký phát hành

Chưa có certificate Authenticode mặc định. Có thể ký trong pipeline nội bộ:

```powershell
$env:SIGNTOOL = 'C:\Program Files (x86)\Windows Kits\10\bin\x64\signtool.exe'
.\Build\publish.ps1 -SignCertificate .\release-cert.pfx
```

Luôn phân phối `SHA256SUMS.txt`; không dùng UPX/obfuscation/PowerShell ở runtime, không ghi Registry và không yêu cầu Administrator.
