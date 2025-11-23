namespace ProductivityInsights.Utilities
{
    using Azure.Core;
    using Azure.Identity;

    public static class UserToken
    {
        public static async Task<AccessToken> GetToken()
        {
            if (IsTokenValid())
            {
                return _accessToken;
            }

            var defaultCredential = new DefaultAzureCredential();
            string[] devopsScopes = new[] { "499b84ac-1321-427f-aa17-267ca6975798/.default" };
            TokenRequestContext tokenContext = new TokenRequestContext(devopsScopes);
            _accessToken = await defaultCredential.GetTokenAsync(tokenContext);
            return _accessToken;
        }

        private static bool IsTokenValid()
        {
            // Check if token exists and has at least 15 minutes of lifespan left
            if (_accessToken.Token == null)
            {
                return false;
            }

            var expiresOn = _accessToken.ExpiresOn;
            var timeRemaining = expiresOn - DateTimeOffset.UtcNow;
            
            return timeRemaining.TotalMinutes >= 15;
        }

        private static AccessToken _accessToken;
    }
}
