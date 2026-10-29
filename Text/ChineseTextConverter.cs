using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TreeTools.Text;

public static class ChineseTextConverter
{
    public static string ToTraditional(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0'))
            throw new ArgumentException("Localization text cannot contain a null character.", nameof(text));
        if (text.Length == 0)
            return string.Empty;

        var length = LCMapStringEx("zh-TW", TRADITIONAL_CHINESE_MAP_FLAG, text, -1, null, 0, nint.Zero, nint.Zero, nint.Zero);
        if (length == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to size the Traditional Chinese conversion.");

        var result = new StringBuilder(length);
        if (LCMapStringEx("zh-TW", TRADITIONAL_CHINESE_MAP_FLAG, text, -1, result, length, nint.Zero, nint.Zero, nint.Zero) != length)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to convert text to Traditional Chinese.");
        return result.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int LCMapStringEx
    (
        string         localeName,
        uint           mapFlags,
        string         source,
        int            sourceLength,
        StringBuilder? destination,
        int            destinationLength,
        nint           versionInformation,
        nint           reserved,
        nint           sortHandle
    );

    #region 常量

    private const uint TRADITIONAL_CHINESE_MAP_FLAG = 0x04000000;

    #endregion
}
