# Kiểm thử smoke/golden

`run-smoke.ps1` build và chạy test harness nội bộ. Bộ test kiểm tra:

- F11 Android 9 stock ở trạng thái `Patchable` với profile modern;
- F11 Android 10, Realme C2 fixed và F7 API 27 fixed ở trạng thái `AlreadyPatched`;
- A5s/CPH1912 có thể phân tích trực tiếp từ RAR và ở trạng thái `Patchable`;
- ánh xạ catalog profile API 27/28/29 và từ chối API ngoài phạm vi;
- chuyển đổi raw/sparse khứ hồi không làm thay đổi dữ liệu;
- patch F11 tạo image mới, không đổi SHA-256 của stock và output phân tích lại thành `AlreadyPatched`.

Test harness dùng các fixture trong workspace và cùng helper bundle với bản release.

Các firmware OEM không được đưa vào repository công khai. Khi không có fixture, test tương ứng sẽ báo `SKIP`; test catalog và sparse vẫn chạy. Có thể đặt firmware do bạn sở hữu hợp pháp vào workspace để bật golden test.

```powershell
$env:VOLTE_OUTPUT = '.\\F11 ANDROID 9\\vendor_F11_Android9_VoLTE_patched_v1.img'
dotnet run --project .\\Tests\\VoLTEVendorPatcher.Tests\\VoLTEVendorPatcher.Tests.csproj -c Release
Remove-Item Env:VOLTE_OUTPUT
```

Để chạy golden test với sparse, hãy chuyển fixture bằng converter managed trước rồi dùng file `.simg`; GUI sử dụng cùng luồng xác minh output.
