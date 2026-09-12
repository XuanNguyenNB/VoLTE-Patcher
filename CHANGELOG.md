# Nhật ký thay đổi

## v1.1.0

- Hỗ trợ kéo-thả và patch trực tiếp file RAR chứa một vendor image.
- Sửa nhận diện OPPO A5s/CPH1912 khi `ro.product.board` và volume label để trống.
- Bổ sung namespace nhận diện manufacturer/brand/device của vendor và ODM.
- Chấp nhận ext4 vendor có metadata `Last mounted on: /vendor` khi không có label.
- Tách cấu hình profile vào catalog để dễ thêm thế hệ vendor đã kiểm chứng.
- Báo chính xác binary hoặc init IMS còn thiếu.
- Thêm kiểm thử A5s/RAR, kiểm thử ánh xạ profile và workflow GitHub Actions.

## v1.0.0

- Bản phát hành đầu tiên cho OPPO/Realme MediaTek Android 8.1–10.
- Hỗ trợ raw ext4 và Android sparse image.
- Profile Legacy API 27 và Modern API 28–29.
