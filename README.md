# PC master cleand by Bentar

App dọn dẹp máy tính Windows. App quét các ổ đĩa và chia kết quả thành 4 nhóm:

1. **Xoá an toàn**: Thùng rác, file tạm, cache trình duyệt (Chrome, Edge, Cốc Cốc), npm-cache, crash dump. App tự dọn, có thể đặt lịch dọn mỗi khi bật máy.
2. **Cần bạn xem xét**: file lớn, file trùng lặp, bản backup cũ, file cài đặt. Bạn tự chọn chuyển vào Thùng rác hoặc chuyển sang ổ D.
3. **Cần giữ**: Windows, phần mềm, hồ sơ trình duyệt, tài liệu cá nhân. App không bao giờ đụng tới.
4. **Thư mục lâu không dùng**: đánh giá từng thư mục theo lần sửa file gần nhất, lịch sử mở file của Windows và dấu vết công cụ AI. Bạn tự tích để xoá.

## Biên dịch

App không cần cài thêm gì, chỉ dùng trình biên dịch C# có sẵn trong Windows (.NET Framework 4.x):

```powershell
& C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /codepage:65001 /optimize "/out:PC master cleand by Bentar.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.VisualBasic.dll src\PCMasterClean.cs
```

## Tham số dòng lệnh

- `/auto`: dọn ngầm theo cài đặt đã lưu (Task Scheduler gọi chế độ này).
- `/report <file>`: chỉ quét, không xoá gì, rồi ghi kết quả phân loại ra file.
