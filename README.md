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
