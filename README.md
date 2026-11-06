# Bentar Second Brain

App dọn dẹp máy tính Windows. App quét các ổ đĩa và chia kết quả thành 4 nhóm:

1. **Xoá an toàn**: Thùng rác, file tạm, cache trình duyệt (Chrome, Edge, Cốc Cốc), npm-cache, crash dump. App tự dọn, có thể đặt lịch dọn mỗi khi bật máy.
2. **Cần bạn xem xét**: file lớn, file trùng lặp, bản backup cũ, file cài đặt. Bạn tự chọn chuyển vào Thùng rác hoặc chuyển sang ổ D.
3. **Cần giữ**: Windows, phần mềm, hồ sơ trình duyệt, tài liệu cá nhân. App không bao giờ đụng tới.
4. **Thư mục lâu không dùng**: đánh giá từng thư mục theo lần sửa file gần nhất, lịch sử mở file của Windows và dấu vết công cụ AI. Bạn tự tích để xoá.

## Biên dịch

App không cần cài thêm gì, chỉ dùng trình biên dịch C# có sẵn trong Windows (.NET Framework 4.x):

```powershell
& C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /codepage:65001 /optimize /win32icon:src\app.ico "/out:Bentar Second Brain.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.VisualBasic.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll src\BentarSecondBrain.cs src\SecondBrain.cs
```

## Cập nhật

Bấm nút **Kiểm tra cập nhật** trong app. App sẽ đọc bản phát hành mới nhất trên GitHub Releases của repo này. Nếu có bản mới hơn, app tải file `.exe`, tự đóng, thay file cũ rồi mở lại. Mỗi lần mở, app cũng tự kiểm tra lặng lẽ: nếu có bản mới, nút sẽ chuyển sang màu vàng.

Cách phát hành một phiên bản mới:

1. Tăng số trong `AssemblyVersion` ở đầu file `src/BentarSecondBrain.cs`, ví dụ `2.3.0.0`.
2. Biên dịch lại theo lệnh ở mục trên.
3. Tạo bản phát hành có tag khớp với số phiên bản, và đính kèm file `.exe`:

```powershell
gh release create v2.3.0 "Bentar Second Brain.exe" --title "v2.3.0" --notes "Có gì mới..."
```

## Second Brain cho agent

App lập chỉ mục mọi ổ trên máy (ổ cứng, USB, thẻ nhớ): tên file và nội dung file chữ, mã nguồn, Word (.docx). Tìm không phân biệt dấu tiếng Việt. Khi USB/thẻ nhớ được rút ra, chỉ mục vẫn giữ những gì nó từng có.

Agent ở bất kỳ dự án nào cũng tra cứu được qua máy chủ MCP (`BentarSecondBrain.exe /mcp`). Bấm **Kết nối Claude Code** ở tab "5. Second Brain", hoặc chạy:

```powershell
claude mcp add --scope user second-brain -- "$env:LOCALAPPDATA\BentarSecondBrain\BentarSecondBrain.exe" /mcp
```

Các công cụ agent dùng được: `search`, `list_projects`, `project_overview`, `read_file`, `list_dir`, `copy`, `index_status`.

Second Brain **không bao giờ sửa hay xoá file**. `copy` chỉ tạo bản sao mới và từ chối ghi đè. Bỏ qua khi lập chỉ mục: thư mục hệ thống, AppData, `node_modules`, `.git`, môi trường ảo, và nội dung file bí mật (`.env`, khoá, credentials).

Chỉ mục tự cập nhật ngầm khi agent tra cứu (nếu đã cũ hơn 30 phút), hoặc bấm **Cập nhật chỉ mục**, hoặc chạy `BentarSecondBrain.exe /index`.

## Tham số dòng lệnh

- `/auto`: dọn ngầm theo cài đặt đã lưu (Task Scheduler gọi chế độ này).
- `/report <file>`: chỉ quét, không xoá gì, rồi ghi kết quả phân loại ra file.
- `/mcp`: chạy máy chủ MCP qua stdio cho coding agent.
- `/index`: cập nhật chỉ mục Second Brain rồi thoát.
