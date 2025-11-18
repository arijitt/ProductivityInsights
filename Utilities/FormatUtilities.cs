using System.Text;
using System.Text.Json;

namespace ProductivityInsights.Utilities
{
    public static class FormatUtilities
    {
        public static string? ToStringValue(JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.String => jsonElement.GetString(),
                JsonValueKind.Number => jsonElement.GetInt32().ToString(),
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                JsonValueKind.Null => null,
                _ => jsonElement.ToString()
            };
        }

        public static string ToStringValue(TimeSpan duration)
        {
            if (duration.TotalDays >= 1)
            {
                var days = (int)duration.TotalDays;
                var hours = duration.Hours;
                if (hours > 0)
                    return $"{days}d {hours}h";
                else
                    return $"{days}d";
            }
            else if (duration.TotalHours >= 1)
            {
                var hours = (int)duration.TotalHours;
                var minutes = duration.Minutes;
                if (minutes > 0)
                    return $"{hours}h {minutes}m";
                else
                    return $"{hours}h";
            }
            else if (duration.TotalMinutes >= 1)
            {
                var minutes = (int)duration.TotalMinutes;
                return $"{minutes}m";
            }
            else
            {
                var seconds = (int)duration.TotalSeconds;
                return $"{seconds}s";
            }
        }

        public static string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };

            if (bytes == 0)
                return "0 B";

            int suffixIndex = 0;
            double size = bytes;

            while (size >= 1024 && suffixIndex < suffixes.Length - 1)
            {
                size /= 1024;
                suffixIndex++;
            }

            return $"{size:F2} {suffixes[suffixIndex]}";
        }

        public static string DecodeBase64ToStringValue(string base64String)
        {
            string rightPadding = base64String.PadRight(base64String.Length + (4 - base64String.Length % 4) % 4, '=');
            var byteArray = Convert.FromBase64String(rightPadding.Replace('-', '+').Replace('_', '/'));
            return Encoding.UTF8.GetString(byteArray);
        }

        public static string GetSingleDashSeparator()
        {
            string dashLine = new string('-', 80);
            return dashLine;
        }

        public static string PrintDoubleDashSeparator()
        {
            string dashLine = new string('=', 80);
            return dashLine;
        }
    }
}
