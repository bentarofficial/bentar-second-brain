# Đánh giá kỹ thuật và backlog triển khai

**Dự án:** Bentar Second Brain  
**Phiên bản đọc khi review:** v2.9.0, commit `f24fd77`  
**Mục đích:** Chuyển các phát hiện review thành đầu việc có thể giao Claude Code. Đây là đánh giá tĩnh; chưa chạy ứng dụng trên Windows hoặc kiểm thử các luồng mạng/backup.

## Nhận xét tổng quan

Đây là ứng dụng desktop Windows có phạm vi sản phẩm rõ: dọn dẹp có xác nhận, tra cứu chỉ mục cục bộ cho nhiều agent, ghi nhớ dài hạn và backup GitHub. Ưu điểm nổi bật là không phụ thuộc nhiều thư viện ngoài, dữ liệu người dùng được tách khỏi chương trình, thao tác file nguy hiểm phần lớn chuyển vào Recycle Bin, và README giải thích được các chế độ dùng.

Rủi ro chính đến từ việc nhiều năng lực mạnh cùng nằm trong một executable: đọc file trên máy, copy theo yêu cầu agent, xóa/dời file, phục vụ HTTP cục bộ và khôi phục dữ liệu. Cần ưu tiên kiểm soát đường dẫn, giới hạn request, xử lý lỗi có thể quan sát được và bảo đảm quy trình backup không làm mất trạng thái tốt. Quy trình build/phát hành cũng nên có thể lặp lại và xác minh được.

## Việc ưu tiên cao

### P1 — Thêm timeout và giới hạn tài nguyên cho HTTP server

**Vị trí:** `src/PowerStation.cs`, `Serve`, `ReadRequest`, `HandleConnection` (khoảng dòng 150–360).

Server tạo một `Task` cho mỗi TCP connection và đọc header/body đồng bộ. `ReceiveTimeout = 15000` được gán sau khi lấy stream; không thấy giới hạn số kết nối đồng thời, timeout tường minh cho header/body, hoặc giới hạn kích thước JSON riêng sau khi parse. Một client cục bộ có thể giữ worker bằng cách gửi request chậm hoặc tạo nhiều kết nối. `Content-Length` sai định dạng hiện có thể biến thành 0 thay vì trả lỗi rõ ràng.

**Đề nghị:** đặt deadline tuyệt đối cho toàn request; kiểm tra method, request line, `Content-Length` hợp lệ và đúng giới hạn trước khi cấp phát; giới hạn số request đang xử lý; trả 400/413/408 phù hợp; đảm bảo mọi connection giải phóng tài nguyên khi lỗi.

**Hoàn thành khi:** request chậm, thiếu body, body quá cỡ, header lỗi và burst kết nối đều kết thúc trong giới hạn xác định; request hợp lệ MCP/REST tiếp tục hoạt động.

### P1 — Ràng buộc cây thư mục khi `copy`, bỏ qua reparse point

**Vị trí:** `src/SecondBrain.cs`, `Brain.Copy` (khoảng dòng 460–489).

`Directory.EnumerateDirectories/Files(..., AllDirectories)` đi theo cây thư mục nguồn mà chưa kiểm tra `FileAttributes.ReparsePoint`. Junction/symlink trong nguồn có thể khiến copy thoát khỏi cây người dùng chọn hoặc lặp qua dữ liệu ngoài dự kiến. Việc kiểm tra đích nằm bên trong nguồn dùng so sánh chuỗi đường dẫn, cần chuẩn hóa dấu phân cách và xử lý thư mục gốc/đường dẫn tương đương cẩn thận.

**Đề nghị:** duyệt cây thủ công, mặc định không đi theo reparse point; xác thực canonical source/destination theo thành phần đường dẫn; copy vào staging directory rồi đổi tên khi hoàn tất, và dọn staging nếu lỗi để không để lại cây dở dang. Giữ nguyên quy tắc không ghi đè.

**Hoàn thành khi:** fixture có junction ra ngoài, junction vòng lặp, đích bên trong nguồn, file đích đã tồn tại và lỗi giữa chừng cho kết quả an toàn, dễ hiểu.

### P1 — Bỏ ghép lệnh shell khi gọi GitHub CLI

**Vị trí:** `src/Backup.cs`, `Gh`, `HasBrain`, `CreateBackupRepo` và các lời gọi `Gh(...)` (khoảng dòng 139–175, 230–280).

Lệnh `gh` đang chạy qua `cmd.exe /c gh ` cộng với chuỗi đối số. Tên repo hoặc giá trị đầu vào ghép vào lệnh có thể bị hiểu thành cú pháp shell; dấu nháy và ký tự đặc biệt cũng dễ gây lỗi. Các thông báo lỗi từ tiến trình có thể chứa thông tin cần hạn chế hiển thị/ghi log.

**Đề nghị:** gọi `gh.exe` trực tiếp với `ProcessStartInfo` và đối số được quote/escape theo Windows; nếu framework cũ không có `ArgumentList`, tạo helper quoting duy nhất và kiểm tra bằng bộ test thuần cho khoảng trắng, dấu nháy, `&`, `|`, `%` và unicode. Tránh đưa secret vào command line, log hoặc lỗi UI.

**Hoàn thành khi:** tên repo hợp lệ có ký tự biên được xử lý đúng; input không thể sinh thêm command; timeout luôn kết thúc tiến trình và không treo đọc stdout/stderr.

### P1 — Khôi phục backup theo kiểu giao dịch, xác minh dữ liệu trước khi thay

**Vị trí:** `src/Backup.cs`, `Restore` (khoảng dòng 486–541), và `Memory.ReplaceAll`.

`Restore` clone repo rồi áp dụng dữ liệu, sau đó thay thư mục repo cục bộ. Cần xác minh chặt cấu trúc zip/JSON và mọi entry trước khi ghi; backup hiện có trước khi thay chỉ là bước cứu hộ, nhưng không có rollback tự động nếu cập nhật memory/settings thành công một phần rồi lỗi. Repo công khai có thể chứa dữ liệu thường theo README; giao diện khôi phục cần làm rõ mức tin cậy của repo và phạm vi dữ liệu sẽ nhập.

**Đề nghị:** tải và kiểm tra toàn bộ vào staging; giới hạn số entry/kích thước giải nén; chỉ cho phép tên entry dự kiến; deserialize và validate schema trước; ghi file tạm rồi atomic replace; snapshot đủ các file sẽ bị thay và rollback nếu bất kỳ bước commit nào thất bại. Hiển thị rõ repo và chế độ plain/encrypted trước khi xác nhận.

**Hoàn thành khi:** repo hỏng, zip giả mạo/path traversal, JSON sai schema, ổ đĩa đầy và lỗi ghi giữa chừng không làm mất dữ liệu hiện tại; khôi phục hợp lệ giữ được bản cũ để quay lại.

## Việc ưu tiên vừa

### P2 — Cấu hình HTTP không tiết lộ token qua URL và hạn chế CORS

**Vị trí:** `src/PowerStation.cs`, `HandleConnection` (khoảng dòng 290–294) và `Write`.

Ngoài Bearer header, server chấp nhận token qua query string. Query có thể xuất hiện trong lịch sử, log client/proxy và bản sao URL. Cần kiểm tra phản hồi CORS mặc định, `OPTIONS`, và chặn cross-origin trước khi API xử lý yêu cầu có tác dụng phụ. Kiểm tra Host hiện dùng `StartsWith`, vì vậy nên xác thực hostname/port theo cú pháp thay vì prefix.

**Đề nghị:** chỉ chấp nhận token qua header; từ chối cross-origin theo chính sách tường minh, không phản hồi `Access-Control-Allow-Origin: *`; xác thực Host chính xác cho loopback; trả security headers cần thiết. Nếu giữ query token cho tương thích, thêm lộ trình ngừng hỗ trợ và không ghi query vào log.

**Hoàn thành khi:** thử nghiệm từ trang web origin khác, Host giả, URL có token và request preflight không thể gọi API trái chính sách.

### P2 — Làm backup chống lỗi đồng thời và tăng độ tin cậy khi push

**Vị trí:** `src/Backup.cs`, `Run`, `Verify`, `Tick`.

`Run` và `Verify` có lock nội tiến trình, nhưng nhiều instance/app version hoặc tác vụ Git ngoài tiến trình vẫn có thể đụng cùng working tree. Quy trình pull/pack/commit/push nên xác thực nhánh và trạng thái repo; khi push thất bại cần giữ trạng thái local có thể retry thay vì ghi nhận thành công. `Tick` cũng nên tránh chồng các lần chạy chậm.

**Đề nghị:** dùng named mutex cho backup repo cục bộ; kiểm tra repo/branch remote; serialize tick; phân biệt lỗi mạng, xác thực, conflict và dữ liệu; retry có backoff cho lỗi tạm thời; kiểm tra backup từ commit đã push chứ không chỉ working tree.

**Hoàn thành khi:** hai tiến trình backup đồng thời và mạng rớt giữa push không tạo trạng thái báo sai hoặc mất khả năng retry.

### P2 — Tạo quy trình build/release tái lập

**Vị trí:** `README.md` và quy trình repo.

README hiện chỉ mô tả lệnh `csc` thủ công và phát hành bằng `gh release create`; không thấy manifest build, workflow CI, test tự động hoặc checksum artifact. Điều này làm khó tái lập bản phát hành và phát hiện build sai.

**Đề nghị:** thêm script PowerShell build duy nhất với kiểm tra mã thoát, phiên bản và output; thêm GitHub Actions build trên Windows, chạy kiểm tra khả thi với target .NET Framework, tạo artifact và ghi SHA-256; phát hành từ tag có version khớp AssemblyVersion. Thêm hướng dẫn thử tay các luồng GUI/backup mà CI không cover.

**Hoàn thành khi:** checkout sạch + script tạo đúng executable; CI lưu artifact/checksum; phát hành tag không dùng nhầm file exe cũ.

### P2 — Bổ sung test hồi quy cho phân loại và thao tác file

**Vị trí:** `src/BentarSecondBrain.cs`, `src/SecondBrain.cs`, `src/Memory.cs`.

Các quy tắc phân loại file, phát hiện dự án, loại trừ bí mật và ghi nhớ là cốt lõi nhưng chưa thấy harness test trong repo. Thay đổi heuristic có thể khiến file quan trọng bị xếp sai hoặc bị bỏ qua.

**Đề nghị:** tách logic thuần khỏi WinForms để test; tạo fixture Windows temp cho extension nhạy cảm, junction, tên tiếng Việt, đường dẫn dài, file đang mở, duplicate destination, memory JSON cũ/mới. Không dùng dữ liệu cá nhân thật trong fixture.

**Hoàn thành khi:** test chạy được trong CI Windows và các ca bảo vệ dữ liệu quan trọng được khóa bằng assertion.

## Gợi ý thứ tự triển khai

1. Thêm harness kiểm thử/fixture và xác nhận target framework có thể build trong CI.
2. Vá `copy` và giới hạn HTTP request; đây là hai bề mặt tác động trực tiếp tới máy người dùng.
3. Chuẩn hóa tiến trình `gh` và quy trình restore có rollback.
4. Thêm hardening CORS/token, backup đồng thời và kiểm thử hồi quy.
5. Hoàn thiện build/release tái lập và cập nhật README theo hành vi đã xác minh.

## Hướng dẫn cho Claude Code

- Đọc file này cùng `README.md` và mã nguồn trước khi sửa.
- Xử lý từng P1 thành commit nhỏ; không gộp refactor lớn với thay đổi hành vi.
- Bảo toàn target .NET Framework và khả năng build bằng compiler Windows đang dùng, trừ khi có đề xuất nâng cấp riêng kèm lý do.
- Không thay đổi dữ liệu người dùng hoặc chạy thao tác xóa thật trong lúc phát triển; dùng thư mục fixture tạm.
- Với mỗi mục, thêm kiểm thử phù hợp, chạy build/kiểm thử trên Windows và ghi rõ kết quả. Nếu không thể chạy một loại kiểm tra, nêu chính xác giới hạn đó.
- Xem các mục trong tài liệu là giả thuyết review cần xác minh trước khi sửa; giữ mục chỉ khi kiểm tra mã/fixture xác nhận rủi ro.

## Phạm vi và giới hạn

Review dựa trên mã nguồn được checkout và README tại thời điểm nêu trên. Chưa chạy app, build, CI, fuzz HTTP, mô phỏng GitHub, hoặc restore từ repo fixture; vì vậy đây là backlog có căn cứ mã nguồn, không phải xác nhận lỗi đã tái hiện.
