# Bentar Second Brain

App dọn dẹp máy tính Windows. App quét các ổ đĩa và chia kết quả thành 4 nhóm:

1. **Xoá an toàn**: Thùng rác, file tạm, cache trình duyệt (Chrome, Edge, Cốc Cốc), npm-cache, crash dump. App tự dọn, có thể đặt lịch dọn mỗi khi bật máy.
2. **Cần bạn xem xét**: file lớn, file trùng lặp, bản backup cũ, file cài đặt. Bạn tự chọn chuyển vào Thùng rác hoặc chuyển sang ổ D.
3. **Cần giữ**: Windows, phần mềm, hồ sơ trình duyệt, tài liệu cá nhân. App không bao giờ đụng tới.
4. **Thư mục lâu không dùng**: đánh giá từng thư mục theo lần sửa file gần nhất, lịch sử mở file của Windows và dấu vết công cụ AI. Bạn tự tích để xoá.

## Biên dịch

App không cần cài thêm gì, chỉ dùng trình biên dịch C# có sẵn trong Windows (.NET Framework 4.x):

```powershell
& C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /codepage:65001 /optimize /win32icon:src\app.ico "/out:Bentar Second Brain.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.VisualBasic.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.Security.dll src\*.cs
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

### Bản đồ bộ não

Tab **Bộ não** (mở đầu tiên) vẽ đúng các nơ-ron và khớp thần kinh trong trí nhớ:

- Bộ não 3D là đám mây hạt sáng nối thành lưới, xoay và lắc nhẹ trong không gian, có phối cảnh gần xa; mạng lưới sao phía sau xoay chậm hơn. Kéo chuột để xoay, lăn chuột để phóng to. Ghi nhớ của agent nằm ở thuỳ trán, màu theo loại (quyết định, bài học, sự thật...); dự án ở thuỳ đỉnh, file ở thuỳ thái dương.
- Nơ-ron to và sáng theo mức được dùng, mờ dần khi lâu không dùng; đường nối dày theo độ mạnh khớp thần kinh, nét chấm là các dự án chung thư mục.
- Xung điện chạy dọc đường nối; khi agent thật sự nhớ lại, đọc hay ghi, nơ-ron đó loé sáng và xung lan sang các ý liên quan.
- Gõ từ khoá rồi Enter để làm sáng các ý khớp và mở thẻ thông tin của ý khớp nhất; nhấp một nơ-ron để xem, nhấp đúp để mở vị trí trong Explorer.

Second Brain **không bao giờ sửa hay xoá file**. `copy` chỉ tạo bản sao mới và từ chối ghi đè. Bỏ qua khi lập chỉ mục: thư mục hệ thống, AppData, `node_modules`, `.git`, môi trường ảo, và nội dung file bí mật (`.env`, khoá, credentials).

Chỉ mục tự cập nhật ngầm khi agent tra cứu (nếu đã cũ hơn 30 phút), hoặc bấm **Cập nhật chỉ mục**, hoặc chạy `BentarSecondBrain.exe /index`.

## Trạm phát: một bộ não, mọi agent cắm vào

Second Brain là nguồn điện, các agent và công cụ là bóng đèn. Sau khi cài, trạm phát (`BentarSecondBrain.exe /serve`) tự chạy ngầm khi bật máy và mở ba kiểu ổ cắm dùng chung một bộ não:

| Ổ cắm | Dành cho | Địa chỉ |
|---|---|---|
| MCP stdio | Claude Code, Codex, Gemini CLI, Cursor... | `"Bentar Second Brain.exe" /mcp` |
| MCP qua HTTP | Agent hỗ trợ MCP từ xa | `POST http://127.0.0.1:7801/mcp` |
| REST + OpenAPI | n8n, script, tiện ích trình duyệt, agent tự xây | `GET/POST http://127.0.0.1:7801/api/<công cụ>`, mô tả ở `/openapi.json` |

Ổ cắm HTTP chỉ nghe trên máy này, mỗi yêu cầu cần mã khoá (`Authorization: Bearer <mã>`), lấy trong `%LOCALAPPDATA%\BentarSecondBrain\o-cam.json`; tên miền lạ trỏ về máy bị chặn. Đặt tên thiết bị bằng header `X-Client-Name`. Mọi lần cắm và dùng được ghi vào `hoat-dong.jsonl`.

## Giữ bộ não cả đời: sao lưu lên GitHub riêng tư

Lần đầu mở app, app hỏi: khôi phục bộ não cũ, tạo mới và sao lưu lên GitHub, hay để sau (app sẽ báo đỏ cho tới khi sao lưu).

- Bộ não được sao lưu vào một repo GitHub riêng tư; mỗi lần sao lưu là một commit, nên giữ được mọi phiên bản cả đời.
- Chỉ có dữ liệu đã mã hoá rời khỏi máy: `bo-nao.sbak` (trí nhớ JSON + bản Markdown đọc được), mã hoá bằng mã khôi phục; `khoa.enc` là mã khôi phục, mã hoá bằng mật khẩu của bạn. Chỉ cần mật khẩu hoặc mã khôi phục là mở được; mất cả hai thì không ai mở được.
- Định dạng chuẩn OpenSSL nên không cần app vẫn mở được: `openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -md sha256 -in bo-nao.sbak -out bo-nao.zip -pass pass:"MÃ-KHÔI-PHỤC"`.
- Trạm phát tự sao lưu khi trí nhớ thay đổi (kiểm tra 10 phút một lần) và mỗi tuần tự mở thử bản sao lưu; lỗi hoặc quá 3 ngày chưa sao lưu thì app báo đỏ.
- Đổi máy / mất máy: cài app, chọn **Khôi phục**, đăng nhập GitHub, chọn repo có dấu ★, nhập mật khẩu hoặc mã khôi phục. Chỉ mục file sẽ tự quét lại trên máy mới.

## Tham số dòng lệnh

- `/auto`: dọn ngầm theo cài đặt đã lưu (Task Scheduler gọi chế độ này).
- `/report <file>`: chỉ quét, không xoá gì, rồi ghi kết quả phân loại ra file.
- `/mcp`: chạy máy chủ MCP qua stdio cho coding agent.
- `/index`: cập nhật chỉ mục Second Brain rồi thoát.
- `/connect`: cài app vào AppData và kết nối Second Brain với mọi agent trên máy.
- `/install`: cài vào máy.
- `/uninstall [/quiet]`: gỡ cài đặt, giữ nguyên dữ liệu.
- `/serve`: chạy trạm phát (ổ cắm HTTP, tự sao lưu).
- `/backup-setup <repo> <mật khẩu>`, `/backup`, `/backup-verify`, `/restore <repo> <mật khẩu hoặc mã khôi phục>`: sao lưu và khôi phục bằng dòng lệnh.
