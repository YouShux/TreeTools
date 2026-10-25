using System.Linq;
using TinyPinyin;

namespace TreeTools.Text;

public static class SearchText
{
    public static string Build(params string?[] fields)
    {
        var source = string.Join('\n', fields.Where(static field => !string.IsNullOrWhiteSpace(field)));
        if (source.Length == 0)
        {
            return string.Empty;
        }

        return $"{source}\n{PinyinHelper.GetPinyin(source, string.Empty)}\n{BuildPinyinInitials(source)}";
    }

    private static string BuildPinyinInitials(string text)
    {
        var buffer = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
        var length = 0;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            var pinyin = PinyinHelper.GetPinyin(character.ToString(), string.Empty);
            if (pinyin.Length > 0)
            {
                buffer[length++] = char.ToLowerInvariant(pinyin[0]);
            }
        }

        return length == 0 ? string.Empty : new string(buffer[..length]);
    }
}
