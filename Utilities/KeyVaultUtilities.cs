namespace ProductivityInsights.Utilities
{
    using Azure.Identity;
    using Azure.Security.KeyVault.Certificates;
    using Azure.Security.KeyVault.Secrets;
    using System.Security.Cryptography.X509Certificates;

    public static class KeyVaultUtilities
    {
        public static async Task<X509Certificate2> GetCertificateAsync(string keyVaultUri, string certificateName)
        {
            if (((!string.IsNullOrWhiteSpace(_keyVaultUri) && _keyVaultUri.Equals(keyVaultUri))
                && (!string.IsNullOrWhiteSpace(_keyVaultCertificateName) && _keyVaultCertificateName.Equals(certificateName)))
                    && _keyVaultCertificate != null)
            {
                return _keyVaultCertificate;
            }

            _keyVaultUri = keyVaultUri;
            _keyVaultCertificateName = certificateName;

            try
            {
                // Use DefaultAzureCredential so the app can authenticate via managed identity or developer credentials
                var credential = new DefaultAzureCredential();

                // Accept either full vault URI or vault name
                Uri vaultUri;
                if (Uri.TryCreate(keyVaultUri, UriKind.Absolute, out var parsed) && parsed.Scheme.StartsWith("http"))
                {
                    vaultUri = parsed;
                }
                else
                {
                    vaultUri = new Uri($"https://{keyVaultUri}.vault.azure.net/");
                }

                var certClient = new CertificateClient(vaultUri, credential);
                var secretClient = new SecretClient(vaultUri, credential);

#if DEBUG
                Console.WriteLine($"🔑 Retrieving certificate metadata '{certificateName}' from Key Vault '{vaultUri.Host}'...");
#endif

                var certResponse = await certClient.GetCertificateAsync(certificateName);
                if (certResponse?.Value == null)
                    throw new InvalidOperationException($"Certificate '{certificateName}' not found in Key Vault '{vaultUri.Host}'.");

                // The certificate's secret (PFX) is usually stored under the same name
                var secretName = certResponse.Value.Name;

#if DEBUG
                Console.WriteLine($"🔐 Retrieving secret '{secretName}' (PFX) from Key Vault...");
#endif

                var secretResponse = await secretClient.GetSecretAsync(secretName);
                if (secretResponse?.Value == null)
                    throw new InvalidOperationException($"Secret for certificate '{certificateName}' not found in Key Vault '{vaultUri.Host}'.");

                var secretValue = secretResponse.Value.Value;
                if (string.IsNullOrWhiteSpace(secretValue))
                    throw new InvalidOperationException("Certificate secret is empty.");

                byte[] certBytes = Convert.FromBase64String(secretValue);

                _keyVaultCertificate = new X509Certificate2(certBytes, (string?)null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);

#if DEBUG
                Console.WriteLine($"✓ Successfully retrieved certificate '{certificateName}' from Key Vault '{vaultUri.Host}'.");
#endif

                return _keyVaultCertificate;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error retrieving certificate from Key Vault: {ex.Message}");
                throw;
            }
        }

        private static string _keyVaultUri = string.Empty;
        private static string _keyVaultCertificateName = string.Empty;
        private static X509Certificate2? _keyVaultCertificate = null;
    }
}
