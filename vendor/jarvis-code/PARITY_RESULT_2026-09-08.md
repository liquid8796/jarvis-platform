**Đã triển khai và giao bản sửa cho toàn bộ nhóm 01–15 đã chọn.** Bản tham chiếu là Claude Desktop **1.46388.3.0** và CLI **2.1.260** đang cài trên máy. Kết quả này không phải tuyên bố toàn bộ Jarvis giống Claude 100% ở mọi trạng thái.

| Nhóm | Phần đã hoàn thiện |
| --- | --- |
| 01 | Đọc PDF thật, render ảnh từng trang, stream/lưu/replay citation theo vị trí claim. |
| 02 | Browser storage theo project/session, chế độ ephemeral và giữ dữ liệu đăng nhập cũ. |
| 03 | Stream JSON hai chiều, SDK hooks/permissions/MCP, partial events, replay và các control local của SDK. |
| 04 | Structured output có schema/retry và sổ chi phí/ngân sách dùng chung cho các lượt gọi. |
| 05 | Các flag local đã chọn; worktree, plugins, Agents fleet, phiên nền, tab terminal, model/effort/rewind/diff/transcript; Chrome/IDE. |
| 06 | Monitor sống qua turn, Cron theo session/idle/jitter, worker/wakeup/findings và liên lạc liên process; tách đúng vòng đời từng phiên. |
| 07 | MCP meta-schema validation, policy theo server, stale cache refresh nền và reconnect/toggle cho server đã cấu hình. |
| 08 | Widget chuyển file/form thật vào draft, connector đã cấu hình và cập nhật arguments khi stream. |
| 09 | Skill chip thật, draft/undo/clipboard, composer so sánh có đầy đủ menu local và editor Output styles. |
| 10 | Annotation marker, thanh vẽ co gọn theo chiều rộng, Arrow/Undo/Redo, tách pane, media zoom/pan, sidebar ổn định và summary bằng model. |
| 11 | Shiki, KaTeX, Mermaid SVG chọn chữ/reflow, clipping native và thuật toán giới hạn phần Markdown đang stream. |
| 12 | Kiểm tra/cached update, áp dụng lần khởi động sau, review và rollback extension. Bundle local vẫn cập nhật thủ công theo đúng reference. |
| 13 | VSIX Jarvis được cài và thử trên VS Code thật; thêm discovery/WS/SSE theo lock file và protocol IDE của CLI tham chiếu. |
| 14 | Model classifier, watcher gắn PR/branch, quyền Auto-fix, chống dùng quyết định quyền cũ và thứ tự deny → ask → allow. |
| 15 | Khôi phục kiểm tra parity, runner tái lập được, kiểm tra native/live, provenance và đối chiếu đúng binary được giao. |

Kiểm thử tổng hợp cuối: **7.092 pass, 0 fail, 0 skip** — Core 336, Providers 225, App 2.373, CLI 232, Parity 3.926. Đây là số ca kiểm tra, không phải số tính năng hay phần trăm parity. [Kết quả đầy đủ](D:/Project/tools/Jarvis/jarvis-code/artifacts/final-parity-verification/runs/20260908T144839Z-ec58b68e/summary.md).

Hai bài kiểm tra model thật đã pass trên cùng App/Core/Providers/Host được giao: một lượt trả lời và chuyển giữa hai phiên đang chạy. Tổng cộng ba request thành công tới model đã cấu hình `ollama/gemma4:31b-cloud`, báo cáo 109.224 input/192 output tokens; không suy ra giá USD từ cấu hình giá chưa biết. SDK Python 0.2.152 cũng pass trên binary cuối với bảy request chỉ tới server giả lập loopback, bao gồm MCP hosted tool, quyền, hooks, schema, rewind/context và vòng đời phiên nền. Terminal được thử qua Windows ConPTY. [Bằng chứng tổng hợp](D:/Project/tools/Jarvis/jarvis-code/artifacts/parity-implementation/FINAL_VALIDATION.json), [SDK cuối](D:/Project/tools/Jarvis/jarvis-code/artifacts/final-parity-verification/sdk-final/sdk-subprocess-evidence.json).

Hai đường dẫn chạy đang dùng đã được cập nhật, không cần dùng lại binary cũ:

```powershell
& "D:\Project\tools\Jarvis\jarvis-code\src\JarvisCode.App\bin\Debug\net10.0-windows\JarvisCode.App.exe"
& "D:\Project\tools\Jarvis\jarvis-code\src\JarvisCode.Cli\bin\Debug\net10.0-windows\jarvis.exe"
```

Bản giao riêng nằm tại `artifacts/releases/jarvis-parity-20260908/desktop` và `cli`. Đã đối chiếu hash toàn bộ 892/897 file trong từng gói, rồi kiểm tra lại sau khi chép vào các thư mục chạy trên. Desktop được mở thật từ đường dẫn đã cập nhật trong profile thử riêng và chụp ảnh; CLI được xác nhận đường dẫn process và chạy `--version`; cả hai exit 0. [Manifest gói](D:/Project/tools/Jarvis/jarvis-code/artifacts/releases/jarvis-parity-20260908/package-manifest.json), [đối chiếu sau giao](D:/Project/tools/Jarvis/jarvis-code/artifacts/releases/jarvis-parity-20260908/post-delivery-verification.json), [smoke desktop](D:/Project/tools/Jarvis/jarvis-code/artifacts/parity-delivery-20260908/smoke/result.json), [smoke CLI](D:/Project/tools/Jarvis/jarvis-code/artifacts/parity-delivery-20260908/smoke/cli-identity.json).

Binary cũ được sao lưu tại `artifacts/parity-delivery-20260908/backup-142953`. Hash `settings.json` và `ui-settings.json` của profile chính không đổi sau kiểm thử/giao bản. Một bộ test native cũ đã làm lệch đăng ký browser sang profile test bị xóa; cả ba đăng ký Chrome/Edge/CocCoc đã được khôi phục về manifest profile chính, có kiểm tra lại. [Ghi nhận sửa đăng ký](D:/Project/tools/Jarvis/jarvis-code/artifacts/parity-implementation/browser-registration-repair.json).

Các giới hạn cần hiểu đúng:

- Backend/account Anthropic ở mục 16 vẫn ngoài phạm vi: cloud sessions, catalog theo tài khoản, billing và các dịch vụ riêng. Updater extension có cơ chế thật nhưng nguồn catalog account mặc định không được giả lập thành dịch vụ đang hoạt động.
- Jarvis giữ engine WPF/native và nhiều provider. KaTeX hiển thị ảnh trong transcript; editor style là editor native; renderer/parser/terminal không có bằng chứng giống mọi pixel, grammar hoặc trạng thái của Claude.
- SDK vẫn thấy tên/version thật của Jarvis `1.0.0`, nên có cảnh báo minimum Claude version. Đổi model qua SDK được thực hiện giữa các turn. Chi phí là ước tính theo giá cấu hình, không phải hóa đơn.
- IDE WS/SSE được thử bằng peer TCP local theo contract đã đọc từ CLI; máy không có extension Claude IDE để thử trực tiếp. WSL/remote forwarding, notebook kernel, secondary-monitor/mixed-DPI và vòng sửa/commit/push lên PR thật không được dùng làm bằng chứng hoàn tất.

Các thay đổi parity được chuẩn bị trên nhánh `codex/parity-claude-installed`. Theo quy ước người dùng bổ sung ngày 2026-09-08, patch được tăng version .NET chung lên `1.0.1` và xuất bản qua `master`, với tiêu đề commit `type(scope): message)`. Kết quả kiểm thử và hash ở trên là chứng cứ của bản parity `1.0.0` trước bước tăng version; kiểm tra bản `1.0.1` được lưu riêng dưới `artifacts/publish-1.0.1`. [Ledger và nguồn chứng cứ từng nhóm](D:/Project/tools/Jarvis/jarvis-code/PARITY_PROGRESS.md). Chạy lại kiểm tra bằng `scripts/verify-parity.ps1`; script không tự phê duyệt baseline.

Một số thư mục test lỗi/cache được giữ lại vì automatic approval review từ chối lệnh dọn dẹp với lý do “blocked by policy”. Các profile smoke cuối không chứa tài khoản thật và được giữ làm chứng cứ; không có process smoke còn chạy.
