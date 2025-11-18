using System.Text.Json;

namespace ProductivityInsights.Utilities
{
    public static class PrintUtilities
    {

        public static void PrintReadableToken(string accessToken)
        {
            var tokenParts = accessToken.Split('.');
            if (tokenParts.Length < 2)
            {
                Console.WriteLine("Invalid JWT format.");
                return;
            }
            string headerJson = FormatUtilities.DecodeBase64ToStringValue(tokenParts[0]);
            string payloadJson = FormatUtilities.DecodeBase64ToStringValue(tokenParts[1]);
            Console.WriteLine("Header:");
            PrintFormattedJson(headerJson);
            Console.WriteLine("Payload:");
            PrintFormattedJson(payloadJson);
        }

        public static void PrintFormattedJson(string jsonPayload)
        {
            try
            {
                var jsonDocument = JsonDocument.Parse(jsonPayload);
                string formatted = JsonSerializer.Serialize(jsonDocument, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(formatted);
            }
            catch
            {
                Console.WriteLine(jsonPayload);
            }
        }

        public static void PrintSingleDashSeparator()
        {
            string dashLine = new string('-', 128);
            Console.WriteLine(dashLine);
        }

        public static void PrintDoubleDashSeparator()
        {
            string dashLine = new string('=', 128);
            Console.WriteLine(dashLine);
        }
    }
}
