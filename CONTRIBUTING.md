# Đóng góp và bổ sung vendor

Mọi đóng góp đều được hoan nghênh. Không tải firmware dump, IMEI, NVRAM/NVDATA hoặc dữ liệu người dùng lên issue hay repository.

## Báo một vendor chưa hỗ trợ

Hãy cung cấp các thông tin không nhạy cảm sau:

- model, SoC, phiên bản Android và mã build;
- định dạng image: raw ext4, Android sparse hoặc RAR;
- toàn bộ thông báo lỗi và log của tool;
- danh sách tên file IMS trong `/bin` và `/etc/init` nếu có thể;
- các property liên quan `ro.product`, `ro.vendor`, `mtk`, `ims`, `volte` đã loại dữ liệu cá nhân.

Không đề nghị mở phạm vi API chỉ dựa trên tên model. Một profile mới cần vendor stock và vendor đã xác nhận VoLTE để so sánh, sau đó phải kiểm thử output trên thiết bị thật.

## Thêm profile đã kiểm chứng

1. Thêm định danh vào `PatchProfile` trong `Engine.cs`.
2. Thêm init template vào `VoLTEVendorPatcher/Assets/Payload`.
3. Thêm entry gồm phạm vi API, tên template, SHA-256 và các dòng bắt buộc vào `PatchProfiles.cs`.
4. Thêm fixture riêng vào test workspace và mở rộng smoke test. Firmware OEM không được commit.
5. Chạy:

```powershell
dotnet build .\VoLTEVendorPatcher\VoLTEVendorPatcher.csproj -c Release
dotnet run --project .\Tests\VoLTEVendorPatcher.Tests\VoLTEVendorPatcher.Tests.csproj -c Release
```

Pull request phải giữ nguyên nguyên tắc: không sửa input, không ghi đè payload xung đột, kiểm tra filesystem trước/sau và chỉ công bố output khi xác minh hoàn tất.
