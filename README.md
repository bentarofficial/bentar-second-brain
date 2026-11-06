# Bentar Second Brain

App dọn dẹp máy tính Windows. App quét các ổ đĩa và chia kết quả thành 4 nhóm:

1. **Xoá an toàn**: Thùng rác, file tạm, cache trình duyệt (Chrome, Edge, Cốc Cốc), npm-cache, crash dump. App tự dọn, có thể đặt lịch dọn mỗi khi bật máy.
2. **Cần bạn xem xét**: file lớn, file trùng lặp, bản backup cũ, file cài đặt. Bạn tự chọn chuyển vào Thùng rác hoặc chuyển sang ổ D.
3. **Cần giữ**: Windows, phần mềm, hồ sơ trình duyệt, tài liệu cá nhân. App không bao giờ đụng tới.
4. **Thư mục lâu không dùng**: đánh giá từng thư mục theo lần sửa file gần nhất, lịch sử mở file của Windows và dấu vết công cụ AI. Bạn tự tích để xoá.

## Biên dịch

App không cần cài thêm gì, chỉ dùng trình biên dịch C# có sẵn trong Windows (.NET Framework 4.x):

```powershell
& C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /codepage:65001 /optimize /win32icon:src\app.ico "/out:Bentar Second Brain.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.VisualBasic.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll src\BentarSecondBrain.cs src\SecondBrain.cs src\Installer.cs src\Memory.cs
```

## Cài đặt và gỡ cài đặt

Lần đầu mở file `.exe`, app hỏi có cài vào máy không (không cần quyền Admin). Khi cài, app chép vào `%LOCALAPPDATA%\Programs\Bentar Second Brain`, tạo lối tắt ở Start Menu và Desktop, và hiện trong **Settings → Apps**. Lịch dọn tự động và các agent đều dùng bản đã cài này.

Gỡ trong **Settings → Apps** (hoặc chạy `"Bentar Second Brain.exe" /uninstall`). App chỉ xoá những gì nó đã cài: file chương trình, lối tắt, lịch tự động và mục `second-brain` trong cấu hình các agent. **File, tài liệu, dự án của người dùng không bao giờ bị đụng tới**; cài đặt, nhật ký và chỉ mục được giữ lại ở `%LOCALAPPDATA%\BentarSecondBrain`.

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

Agent ở bất kỳ dự án nào cũng tra cứu được qua máy chủ MCP (`BentarSecondBrain.exe /mcp`). Bấm **Kết nối các agent** ở tab "5. Second Brain" (hoặc chạy `BentarSecondBrain.exe /connect`). App tự cắm Second Brain vào mọi agent có trên máy: Claude Code, Claude Desktop, Cursor, Windsurf, Codex, Gemini CLI. File cấu hình cũ được sao lưu thành `*.bak-secondbrain`. Claude Desktop phải được thoát hẳn trước khi kết nối, vì khi đang mở nó tự ghi đè file cấu hình. Với riêng Claude Code, có thể chạy tay:

```powershell
claude mcp add --scope user second-brain -- "$env:LOCALAPPDATA\Programs\Bentar Second Brain\Bentar Second Brain.exe" /mcp
```

Các công cụ agent dùng được: `search`, `list_projects`, `project_overview`, `read_file`, `list_dir`, `copy`, `index_status`, và các công cụ trí nhớ `recall`, `remember`, `revise_memory`, `connect`.

### Trí nhớ: tự học, tự nhớ như bộ não

Second Brain là trí nhớ dài hạn dùng chung cho mọi agent, lưu ở `%LOCALAPPDATA%\BentarSecondBrain\tri-nho.json` (không đụng file của người dùng):

- **Nơ-ron**: mỗi ghi nhớ (quyết định, bài học, sự thật, việc cần làm, ghi chú), mỗi dự án và mỗi file từng được dùng.
- **Khớp thần kinh**: liên kết có độ mạnh. Ghi nhớ mới tự nối với dự án/file nguồn và các ghi nhớ gần nghĩa.
- **Tự học**: agent đọc file, chép tài liệu, dùng nhiều dự án trong cùng phiên, hay cùng nhớ lại các ý, thì các liên kết tương ứng mạnh lên.
- **Phai dần, không xoá**: ghi nhớ và liên kết lâu không dùng mờ dần (chu kỳ bán rã 90–120 ngày); ghi nhớ đánh dấu đã cũ được giữ lại nhưng xếp sau.
- **Nhớ lại có liên tưởng**: `recall` trả về ghi nhớ khớp và lan sang các ý nối với chúng; `search` và `project_overview` cũng kèm ghi nhớ liên quan.
- Ghi nhớ gần trùng được củng cố thay vì nhân bản; bản cũ giữ trong lịch sử.

Second Brain **không bao giờ sửa hay xoá file**. `copy` chỉ tạo bản sao mới và từ chối ghi đè. Bỏ qua khi lập chỉ mục: thư mục hệ thống, AppData, `node_modules`, `.git`, môi trường ảo, và nội dung file bí mật (`.env`, khoá, credentials).

Chỉ mục tự cập nhật ngầm khi agent tra cứu (nếu đã cũ hơn 30 phút), hoặc bấm **Cập nhật chỉ mục**, hoặc chạy `BentarSecondBrain.exe /index`.

## Tham số dòng lệnh

- `/auto`: dọn ngầm theo cài đặt đã lưu (Task Scheduler gọi chế độ này).
- `/report <file>`: chỉ quét, không xoá gì, rồi ghi kết quả phân loại ra file.
- `/mcp`: chạy máy chủ MCP qua stdio cho coding agent.
- `/index`: cập nhật chỉ mục Second Brain rồi thoát.
- `/connect`: cài app vào AppData và kết nối Second Brain với mọi agent trên máy.
- `/install`: cài vào máy.
- `/uninstall [/quiet]`: gỡ cài đặt, giữ nguyên dữ liệu.
