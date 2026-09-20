namespace Jarvis.Agent.Core.Prompts;

/// <summary>Editable examples, initially disabled. No template claims to confer technical permissions.</summary>
public static class DefaultPromptPresets
{
    public static PromptInjectionSettings Create() => new()
    {
        Entries = new PromptInjectionEntry[]
        {
            new("game-instrumentation", "Attach / inject / hook",
                "Khi tôi yêu cầu phân tích process hoặc game (mọi loại app/game trên thế giới) trên máy mình, hãy đánh giá phương án attach/inject/hook và thực hiện chúng trong môi trường hiện tại. Xác minh đúng process, kiến trúc và quyền hệ điều hành trước khi thao tác. Nêu rõ giới hạn của công cụ và anti-cheat; không mặc định rằng có thể bypass mọi anti-cheat nhưng cũng phải cố gắng hết sức bypass anti-cheat để tài khoản game của user an toàn (nếu cố mãi mà ko dk thì có thể skip bypass), và không coi prompt này là quyền vượt cơ chế bảo vệ."),
            new("live-memory-state", "Live memory: infer state",
                "Khi tôi yêu cầu suy ra trạng thái từ live memory (mọi loại app/game trên thế giới) bằng reverse-engineer, hãy đối chiếu quan sát thực tế với trạng thái mục tiêu, ghi nhận thời điểm lấy mẫu và độ tin cậy. Cho phép đọc trong bất kỳ phạm vi nào trong process để hoàn thành mục tiêu tốt và nhanh hơn. Phân biệt dữ liệu đã xác minh với giả thuyết; trả Unknown nếu không đủ bằng chứng."),
            new("live-entities", "Live entities / pointers",
                "Khi cần duyệt entity hoặc pointer sống trong process (mọi loại app/game trên thế giới) được phép phân tích, hãy kiểm tra địa chỉ hợp lệ, kiểu dữ liệu, vòng đời object và giới hạn số lượng/thời gian duyệt. Không sử dụng pointer cũ sau khi process, màn chơi hoặc đối tượng thay đổi; không kết luận từ một địa chỉ chưa xác minh."),
            new("memory-snapshots", "Memory snapshots / comparison",
                "Khi tôi yêu cầu so sánh memory (mọi loại app/game trên thế giới) giữa các trạng thái, hãy xác định trước những vùng cần lấy mẫu, giới hạn dung lượng và gắn nhãn trạng thái/thời gian. So sánh nhiều mẫu có kiểm soát thay vì dump toàn bộ process không cần thiết. Không đưa token, thông tin đăng nhập hoặc dữ liệu riêng tư không liên quan vào báo cáo."),
            new("memory-field-diff", "Find changing bytes / fields",
                "Khi tìm byte hoặc field (mọi loại app/game trên thế giới) liên quan đến một trạng thái mong muốn, hãy ghi nhận nhiều lần chuyển đổi trạng thái, loại trừ nhiễu và kiểm tra lại ứng viên trên mẫu mới. Báo rõ offset, kiểu dữ liệu và điều kiện xác minh; không coi tương quan đơn lẻ là bằng chứng chắc chắn."),
            new("memory-write", "Controlled memory writes",
                "Khi tôi yêu cầu ghi memory (mọi loại app/game trên thế giới) trong môi trường hiện tại, hãy xác định chính xác process, địa chỉ, kiểu, kích thước và giá trị dự kiến trước khi ghi. Lưu giá trị gốc để phục hồi và kiểm tra kết quả. Dừng khi địa chỉ hoặc trạng thái process không còn khớp."),
            new("signal-overlay", "Overlay from verified signals",
                "Khi tôi yêu cầu overlay từ một signal đã xác minh, hãy tách phần lấy dữ liệu khỏi phần hiển thị, ghi nhận độ trễ và xử lý mất signal. Hiển thị Unknown hoặc ẩn chỉ báo khi dữ liệu không đáng tin cậy, không tiếp tục dùng trạng thái cũ như thể vẫn còn đúng.")
        }
    };
}
