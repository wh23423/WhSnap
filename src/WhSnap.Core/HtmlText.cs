using System.Globalization;
using System.Text;

namespace WhSnap.Core;

/// <summary>
/// 从 HTML 剪贴板片段里提取可读纯文本。
/// 不做完整 HTML 渲染（会拖入 IE/WebView 依赖），只保留文字与换行，
/// 这是"从网页复制一段内容贴到屏幕上"最常见的用法。
/// </summary>
public static class HtmlText
{
    public static string ExtractPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        string text = html;

        // 剪贴板 HTML 常带 <!--StartFragment--> 分片标记，只取分片内容
        int start = text.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        int end = text.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start)
        {
            text = text[(start + "<!--StartFragment-->".Length)..end];
        }

        text = System.Text.RegularExpressions.Regex.Replace(text, @"<script[\s\S]*?</script>", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<style[\s\S]*?</style>", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"</(p|div|tr|li|h[1-6])>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", string.Empty);
        text = DecodeEntities(text);

        // 折叠多余空行与行内空白
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"[ \t\u00A0]+", " ").Trim())
            .ToList();

        while (lines.Count > 0 && lines[0].Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var builder = new System.Text.StringBuilder();
        bool lastBlank = false;

        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                if (lastBlank)
                {
                    continue;
                }

                lastBlank = true;
            }
            else
            {
                lastBlank = false;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
        }

        return builder.ToString();
    }

    private static string DecodeEntities(string text)
    {
        var map = new (string Entity, string Value)[]
        {
            ("&nbsp;", " "),
            ("&lt;", "<"),
            ("&gt;", ">"),
            ("&quot;", "\""),
            ("&#39;", "'"),
            ("&apos;", "'"),
            ("&amp;", "&"),
        };

        foreach (var (entity, value) in map)
        {
            text = text.Replace(entity, value, StringComparison.OrdinalIgnoreCase);
        }

        // 数值实体 &#123; / &#x1F600;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"&#x([0-9a-fA-F]+);", match =>
        {
            int code = Convert.ToInt32(match.Groups[1].Value, 16);
            return char.ConvertFromUtf32(code);
        });

        return System.Text.RegularExpressions.Regex.Replace(text, @"&#(\d+);", match =>
        {
            int code = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return code is > 0 and <= 0x10FFFF ? char.ConvertFromUtf32(code) : match.Value;
        });
    }
}
