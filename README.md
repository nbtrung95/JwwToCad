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
(đường, cung kể cả điểm đầu/cuối, chữ, tô đặc, kích thước, ký hiệu, block, ảnh, bật/tắt layer),
cùng màu, kiểu nét, độ dày nét so với cài đặt lưu trong file JWW, và độ dài từng dòng chữ so với
điểm cuối Jw_cad lưu. Kết quả in ra là
`KHỚP` hoặc `CÒN n CHỖ LỆCH` kèm vị trí. Chạy offline, không cần Jw_cad hay AutoCAD.

- `jwwtocad verify <file.jww|thư mục> <file.dwg|thư mục> [--paper]`: kiểm tra lại file đã xuất.
- `jwwtocad selftest [out.dwg]`: tạo bản vẽ thử có đủ mọi loại đối tượng định dạng JWW cho phép
  (18 ký hiệu SXF, mọi màu 1–9 và SXF 100–356, mọi kiểu nét 1–9, 11–19, 30–62, độ dày nét, cung/elip,
  chữ ngang/dọc/giãn chữ/đậm/nghiêng/phông lạ/mã ^u ^d ^$ …/mã nhúng %f, tô đặc tứ giác và tròn mọi kiểu,
  ảnh trong file và ảnh liên kết, block lồng nhau có tỉ lệ/xoay/lật, kích thước, layer ẩn), chuyển đổi
  rồi kiểm tra; sau đó làm hỏng bản vẽ theo 7 cách và đòi bộ kiểm tra phải phát hiện từng cách.

## Những gì được chuyển theo đặc tả JWW

- Màu: bút 1–9 theo số màu Jw_cad; màu SXF 100–356 lấy đúng RGB lưu trong file (đen/trắng thành màu 7).
- Kiểu nét: nét SXF lấy độ dài mm lưu trong file; nét Jw_cad 2–9, 16–19 dựng từ mẫu bit trong file
  (1 bit = bước in × 1/32 mm, đúng với cả 15 nét SXF chuẩn). Nét ngẫu nhiên 11–15 vẽ bằng nét liền và có cảnh báo.
- Độ dày nét: độ dày riêng của đối tượng (1/100 mm), hoặc độ dày in của màu SXF.
- Chữ: giãn chữ (chữ nửa cỡ dùng nửa khoảng), chữ dọc 縦字, đậm/nghiêng (kiểu chữ +10000 nghiêng,
  +20000 đậm), phông theo tên, các mã ^ của Jw_cad, mã nhúng (tên file, tỉ lệ, ghi chú, ngày), ký tự %.
- Tô đặc tròn: hình quạt, viên phân, phần ngoài cung, tròn đặc, vành khuyên 105/106.
- Thứ tự vẽ: mảng tô đặc và ảnh luôn được vẽ trước (nằm dưới cùng), đường và chữ nằm trên; với nhóm lớp khác tỉ lệ, phần tô đặc nằm trong block riêng `..._TO_DAC`. Bộ kiểm tra báo lỗi nếu có đường/chữ nào bị tô đặc che.
- Màu bút 1–9 lấy theo màu màn hình lưu trong từng file JWW (mỗi file có thể khác nhau).
- Ẩn/hiện block: Jw_cad ẩn/hiện cả block theo layer nơi block được đặt; layer riêng của các nét bên trong không tính (nét vẽ trên layer ẩn vẫn hiện nếu block nằm trên layer đang hiện). Trong DWG các nét trong block nằm ở layer 0 để AutoCAD làm giống vậy.
- Ảnh: ảnh nằm trong file JWW được giải nén ra thư mục `<tên>_anh` cạnh file DWG và chèn vào bản vẽ.

Ký hiệu điểm (đầu mút kích thước, đường dẫn) dùng mã SXF: mã dương 1-11 là mũi tên/chấm,
mã âm -1..-7 là dấu điểm; cỡ gốc 1,25 × hệ số (mm giấy), mũi tên dài 7,5 × hệ số.
