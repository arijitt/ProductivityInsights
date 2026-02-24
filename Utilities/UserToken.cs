namespace ProductivityInsights.Utilities
{
    using Azure.Core;
    using Azure.Identity;

    public static class UserToken
    {
        private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public static async Task<AccessToken> GetToken()
        {
            await _semaphore.WaitAsync();
            try
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
            finally
            {
                _semaphore.Release();
            }
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
