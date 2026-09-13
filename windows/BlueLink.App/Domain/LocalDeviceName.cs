using System.Text;

namespace BlueLink.Domain;

public static class LocalDeviceName
{
    public static string? ValidationError(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "设备名不能仅包含空格。";
        if (value.Length > 32) return "设备名最多 32 个字符。";
        if (value.Any(char.IsControl)) return "设备名不能包含换行或控制字符。";
        if (Encoding.UTF8.GetByteCount(value.Trim()) > 80)
            return "名称包含较多汉字或表情，请缩短后重试。";
        return null;
    }

    public static string Resolve(string? saved) => !string.IsNullOrWhiteSpace(saved) && ValidationError(saved) is null
        ? saved.Trim() : Environment.MachineName;
}
