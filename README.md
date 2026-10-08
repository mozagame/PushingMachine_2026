# CheckBarcode v2

Phần mềm kiểm tra mã vạch hộp/toa cho máy đóng hộp (camera Cognex DM262, PLC qua Modbus TCP),
có audit trail theo 21 CFR Part 11 / EU GMP Annex 11. Viết lại từ CheckBarcode v1.

## Cấu trúc thư mục

| Thư mục | Nội dung |
|---|---|
| `src/CheckBarcode.Domain` | Thực thể, enum, quyền, vai trò |
| `src/CheckBarcode.Application` | Nghiệp vụ: đăng nhập, user, recipe, lô, alarm, audit, báo cáo, TagEngine, ActionDispatcher |
| `src/CheckBarcode.Infrastructure` | SQLite, Modbus TCP, PDF, file/USB/backup, `AppHost` (khởi tạo toàn bộ) |
| `src/CheckBarcode.Devices.Cognex` | Kết nối camera Cognex DataMan |
| `src/CheckBarcode.Simulation` | Mô phỏng PLC và camera (dùng cho test, demo) |
| `src/CheckBarcode.Wpf` | Giao diện WPF 1920×1080 |
| `tools/CheckBarcode.PlcSimulator` | Chương trình mô phỏng PLC dùng khi FAT / đào tạo |
| `tests/CheckBarcode.Tests` | 70 test tự động |
| `config/` | `appsettings.json`, `tags.json` (bảng biến PLC), `actions.json` (nút nhấn + quyền + lý do + chữ ký), `lang/` |
| `docs/` | Kiến trúc, PLC interface, FAT, triển khai, đối chiếu Part 11, truy vết yêu cầu |
| `build/` | Script đóng gói Windows, installer, CI Linux |

## Build và chạy (Windows, Visual Studio 2022)

1. Mở `CheckBarcode.sln`, chọn project khởi động `CheckBarcode.Wpf`, cấu hình `Debug|x64`, nhấn F5.
2. Chưa có PLC/camera: trong `config/appsettings.json` đặt `"driver": "Simulator"` cho `plc` và từng camera.
3. Đăng nhập lần đầu: `admin` / `Abc@1234` → bắt buộc đổi mật khẩu.
4. Chạy test: `dotnet run --project tests/CheckBarcode.Tests` (kết quả phải là `0 failed`).
5. Đóng gói: `build\publish.ps1`, sau đó `iscc build\installer.iss` (xem `docs/DEPLOYMENT.md`).

## Mở rộng (không cần sửa code audit)

- **Thêm alarm / biến PLC:** thêm một dòng vào `config/tags.json`, khởi động lại phần mềm.
  Chạy `python3 build/tools/make_plc_doc.py` để cập nhật `docs/PLC_INTERFACE.md`.
- **Thêm nút nhấn cần audit:** thêm action vào `config/actions.json` (quyền, có cần lý do, có cần chữ ký điện tử),
  thêm text trong `build/tools/lang_*.py` rồi chạy `make_lang.py`, gọi `Dispatcher.ExecuteAsync("MÃ_ACTION", ...)` trong ViewModel.
- **Thêm vai trò:** Admin chỉnh ma trận quyền trên màn hình Users → Permissions.

## Tài liệu

- `PLAN.md` — kế hoạch, danh sách tính năng có mã (USR-, REC-, BAT-, …), schema DB.
- `docs/ARCHITECTURE.md` — kiến trúc, nguyên tắc mở rộng.
- `docs/PLC_INTERFACE.md` — bảng địa chỉ Modbus cho lập trình viên PLC.
- `docs/COMPLIANCE.md` — đối chiếu Part 11 / Annex 11.
- `docs/TRACEABILITY.md` — tính năng ↔ test tự động ↔ bước FAT.
- `docs/FAT.md` — quy trình nghiệm thu trên máy thật.
- `docs/DEPLOYMENT.md` — cài đặt, cấu hình, hardening Windows, rủi ro cần kiểm tra.
