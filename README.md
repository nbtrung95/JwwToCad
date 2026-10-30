# JwwToCad

Phần mềm chuyển bản vẽ Jw_cad (`.jww`) sang AutoCAD (`.dwg` / `.dxf`). Chiều ngược DWG → JWW sẽ làm ở giai đoạn sau.

## Trạng thái

| Phần | Trạng thái |
| --- | --- |
| Đọc JWW (Jw_cad 7.x / 8.x, chữ Shift-JIS và Unicode) | Xong, đã thử với 9 file mẫu |
| Ghi DWG/DXF (thư viện [ACadSharp](https://github.com/DomCR/ACadSharp), MIT) | Bản đầu: đường, cung/tròn/elip, điểm, chữ, tô đặc, kích thước (phá rời), block, layer, màu, kiểu nét |
| App desktop (WPF, kéo thả) | Chưa làm |
| DWG → JWW | Chưa làm |

Chưa chuyển: ảnh chèn, một số dấu đầu mũi tên của kích thước.

## Cách chuyển đổi

- **Thực tế (mặc định):** toạ độ nhân với tỉ lệ của từng nhóm lớp (ví dụ 1/40 → ×40), đơn vị mm, đo được kích thước thật trong AutoCAD.
- **Giấy (`--paper`):** giữ nguyên 1:1 như tờ giấy in ra.
- Layer DWG đặt tên `nhóm-lớp tên`, ví dụ `1-3 躯体線`.

## Dùng thử bằng dòng lệnh

```
dotnet run --project src/JwwToCad.Cli -- "C:\thu muc\file.jww"            # ra file.dwg cạnh file gốc
dotnet run --project src/JwwToCad.Cli -- "C:\thu muc jww" "C:\thu muc ra"   # chuyển cả thư mục
dotnet run --project src/JwwToCad.Cli -- file.jww --paper --dxf            # tỉ lệ giấy, xuất DXF
dotnet run --project src/JwwToCad.Cli -- preview file.dwg file.svg         # xem trước bản vẽ đã ghi
```

## Cấu trúc

- `src/JwwToCad.Core/Jww` – đọc định dạng JWW
- `src/JwwToCad.Core/Convert` – chuyển sang đối tượng AutoCAD
- `src/JwwToCad.Cli` – công cụ dòng lệnh để chuyển và kiểm tra

## Tự kiểm tra

Mỗi lần xuất, chương trình đọc lại file DWG và so từng đối tượng với file JWW gốc
(đường, cung, chữ, tô đặc, kích thước, ký hiệu, block, bật/tắt layer). Kết quả in ra là
`KHỚP` hoặc `CÒN n CHỖ LỆCH` kèm vị trí. Chạy offline, không cần Jw_cad hay AutoCAD.

- `jwwtocad verify <file.jww|thư mục> <file.dwg|thư mục> [--paper]`: kiểm tra lại file đã xuất.
- `jwwtocad selftest [out.dwg]`: tạo bản vẽ thử có đủ 18 ký hiệu SXF (11 mũi tên/chấm, 7 dấu điểm),
  chuyển đổi rồi kiểm tra; đồng thời thử làm hỏng bản vẽ để chắc bộ kiểm tra phát hiện được lỗi.

Ký hiệu điểm (đầu mút kích thước, đường dẫn) dùng mã SXF: mã dương 1-11 là mũi tên/chấm,
mã âm -1..-7 là dấu điểm; cỡ gốc 1,25 × hệ số (mm giấy), mũi tên dài 7,5 × hệ số.
