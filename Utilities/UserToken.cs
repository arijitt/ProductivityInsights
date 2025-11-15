namespace ProductivityInsights.Utilities
{

    using Azure.Core;
    using Azure.Identity;

    public static class UserToken
    {
        public static async Task<AccessToken> GetToken()
        {
            var defaultCredential = new DefaultAzureCredential();
            string[] devopsScopes = new[] { "499b84ac-1321-427f-aa17-267ca6975798/.default" };
            TokenRequestContext tokenContext = new TokenRequestContext(devopsScopes);
            AccessToken accessToken = await defaultCredential.GetTokenAsync(tokenContext);
            return accessToken;
        }
    }
}
